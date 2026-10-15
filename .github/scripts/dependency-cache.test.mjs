import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { cacheKey, dependencyInputs, toolPins } from '../actions/environment-setup/cache-keys.mjs';

const read = (file) => readFileSync(new URL(file, import.meta.url), 'utf8').replaceAll('\r\n', '\n');
const setup = read('../actions/environment-setup/action.yml');
const verify = read('../actions/environment-verify/action.yml');
const workflow = read('../workflows/dependency-cache.yml');
const copilot = read('../actions/speckit-copilot/action.yml');

test('NuGet keys cover both central package lists, projects, SDKs, targets, solutions, tools, and feeds', () => {
  const files = [
    'src/platform/Directory.Packages.props', 'src/analysts/manager/Directory.Packages.props',
    'src/platform/Directory.Build.targets', 'src/platform/global.json', 'src/analysts/manager/global.json',
    'src/platform/Api/Api.csproj', 'src/platform/Platform.slnx', 'NuGet.Config', '.config/dotnet-tools.json',
    'src/platform/Api/packages.lock.json', 'src/platform/Api/Program.cs',
    'src/clients/web/package-lock.json', 'README.md',
  ];
  assert.deepEqual(dependencyInputs(files, 'nuget'), files.slice(0, 10).sort());
});

test('npm keys cover dependency declarations and lockfiles, not source, build output, or secrets', () => {
  assert.deepEqual(dependencyInputs([
    'src/clients/web/package-lock.json', 'src/clients/web/package.json', '.npmrc',
    'npm-shrinkwrap.json', 'src/clients/web/src/app.tsx', '.env', 'credentials.json',
  ], 'npm'), ['.npmrc', 'npm-shrinkwrap.json', 'src/clients/web/package-lock.json', 'src/clients/web/package.json']);
});

test('download keys vary by ecosystem, OS, architecture, runtime, file content, and tool version, not checkout path', () => {
  const versions = { os: 'Linux', arch: 'X64', runtime: '10.0.100' };
  const inputs = [['src/platform/Directory.Packages.props', '<Project/>'], ['kiota-version', '1.36.0']];
  const key = cacheKey('nuget', versions, inputs);
  assert.equal(key, cacheKey('nuget', { ...versions }, inputs.map(input => [...input])));
  for (const modified of [
    cacheKey('npm', versions, inputs),
    cacheKey('nuget', { ...versions, os: 'Windows' }, inputs),
    cacheKey('nuget', { ...versions, arch: 'ARM64' }, inputs),
    cacheKey('nuget', { ...versions, runtime: '10.0.101' }, inputs),
    cacheKey('nuget', versions, [['src/platform/Directory.Packages.props', '<Project>changed</Project>'], inputs[1]]),
    cacheKey('nuget', versions, [inputs[0], ['kiota-version', '1.37.0']]),
  ]) assert.notEqual(modified, key);
  assert.match(key, /^socalytics-downloads-v1-nuget-Linux-X64-10\.0\.100-[a-f0-9]{64}$/);
});

test('population takes its tool pins from the same trusted actions as consumers', () => {
  const pins = toolPins(setup, copilot);
  assert.equal(pins.npm.length, 4);
  assert.ok(pins.npm.every(pin => /@\d+\.\d+\.\d+$/.test(pin)));
  const next = toolPins(setup.replace(/playwright@[\d.]+/, 'playwright@9.8.7'), copilot.replace(/(cli-version:[\s\S]*?default:)\s*[\d.]+/, '$1 9.8.6'));
  assert.ok(next.npm.includes('playwright@9.8.7'));
  assert.ok(next.npm.includes('@github/copilot@9.8.6'));
  assert.notEqual(cacheKey('npm', { os: 'Linux', arch: 'X64', runtime: '24' }, [['tools', pins.npm.join(' ')]]),
    cacheKey('npm', { os: 'Linux', arch: 'X64', runtime: '24' }, [['tools', next.npm.join(' ')]]));
  assert.throws(() => toolPins('', copilot), /Cannot read a pinned tool version/);
  assert.match(workflow, /NPM_TOOLS: \$\{\{ steps\.setup\.outputs\.npm-tools \}\}/);
  assert.match(workflow, /KIOTA_VERSION: \$\{\{ steps\.setup\.outputs\.kiota-version \}\}/);
});

test('agent-adjacent setup only restores exact caches and never saves or evaluates workspace dependencies', () => {
  assert.equal((setup.match(/uses: actions\/cache\/restore@/g) || []).length, 2);
  assert.doesNotMatch(setup, /uses: actions\/cache(?:\/save)?@|restore-keys:|dotnet restore|npm ci/);
  assert.equal((setup.match(/continue-on-error: true/g) || []).length, 2);
  assert.match(setup, /default: "true"/);
  assert.equal((setup.match(/if: inputs\.install-tools == 'true'/g) || []).length, 4);
  assert.match(setup, /'_cacache'|npm-path/);
});

test('only guarded trusted population saves dependency caches, without agents, builds, tests, or lifecycle scripts', () => {
  const populate = workflow.split('  populate:\n')[1].split('  chromium-cold:\n')[0];
  assert.match(populate, /if: github\.ref == format\('refs\/heads\/\{0\}', github\.event\.repository\.default_branch\)/);
  assert.match(populate, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/);
  assert.match(populate, /persist-credentials: false/);
  assert.match(populate, /install-tools: "false"/);
  assert.doesNotMatch(populate, /copilot-requests:|uses: .*speckit-copilot|dotnet (?:build|test)|pull_request_target/);
  for (const line of populate.split('\n').filter(line => /^\s+npm (ci|install) /.test(line))) assert.match(line, /--ignore-scripts/);
  assert.equal((populate.match(/uses: actions\/cache\/save@/g) || []).length, 2);
  assert.match(populate, /if: steps\.setup\.outputs\.nuget-hit != 'true'/);
  assert.match(populate, /if: steps\.setup\.outputs\.npm-hit != 'true'/);
  assert.match(populate, /cache\.key === key && cache\.ref === process\.env\.GITHUB_REF/);
  assert.doesNotMatch(workflow.split('permissions: {}')[0], /ref:|pull_request/);
});

test('Chromium benchmark is opt-in, measures fresh OS dependencies on both runners, and always cleans its isolated cache', () => {
  assert.match(workflow, /inputs\.benchmark-chromium == true/);
  assert.equal((workflow.match(/playwright" install --with-deps chromium/g) || []).length, 2);
  assert.match(workflow, /fail-on-cache-miss: true/);
  assert.match(workflow, /if: always\(\) && needs\.chromium-cold\.outputs\.cache-key != ''/);
  assert.match(workflow, /cache\.key !== process\.env\.CACHE_KEY \|\| cache\.ref !== process\.env\.GITHUB_REF/);
  assert.doesNotMatch(setup, /socalytics-chromium-benchmark|actions\/cache.*chromium/);
});

test('embedded workflow JavaScript and composite shell steps are syntactically valid', () => {
  const scripts = [...workflow.matchAll(/node --input-type=module <<'JS'\n([\s\S]*?)^\s+JS$/gm)];
  assert.equal(scripts.length, 3);
  for (const [, script] of scripts) {
    const result = spawnSync(process.execPath, ['--check', '--input-type=module'], { input: script, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
  }
  for (const action of [setup, verify]) {
    for (const [, script] of action.matchAll(/^      run: \|\n((?:^        .*\n|^\n)+)/gm)) {
      execFileSync('bash', ['-n'], { input: script.replace(/^        /gm, '') });
    }
  }
});

test('key calculation only reads dependency files, includes untracked additions, and works across checkout paths', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'dependency-cache-keys-'));
  const script = new URL('../actions/environment-setup/cache-keys.mjs', import.meta.url);
  try {
    const bin = path.join(directory, 'bin');
    mkdirSync(bin);
    for (const [name, version] of [['npm', '11.0.0']]) {
      writeFileSync(path.join(bin, name), `#!/bin/bash\n[ "$*" = "--version" ] || exit 9\nprintf '%s\\n' '${version}'\n`, { mode: 0o755 });
    }
    const calculate = (root) => {
      const output = path.join(directory, 'output');
      const environment = path.join(directory, 'environment');
      writeFileSync(output, '');
      writeFileSync(environment, '');
      execFileSync(process.execPath, [script.pathname, root], {
        env: {
          ...process.env, PATH: `${bin}:${process.env.PATH}`, RUNNER_OS: 'Linux', RUNNER_ARCH: 'X64',
          GITHUB_OUTPUT: output, GITHUB_ENV: environment, CACHE_DOTNET_VERSION: '10.0.100',
        },
      });
      return Object.fromEntries(readFileSync(output, 'utf8').trim().split('\n').map(line => {
        const index = line.indexOf('=');
        return [line.slice(0, index), line.slice(index + 1)];
      }));
    };
    const roots = ['tooling', 'workspace'].map(name => path.join(directory, name));
    for (const root of roots) {
      mkdirSync(path.join(root, 'src/platform'), { recursive: true });
      execFileSync('git', ['init', '--quiet', root]);
      writeFileSync(path.join(root, 'src/platform/Directory.Packages.props'), '<Project/>');
      writeFileSync(path.join(root, 'package-lock.json'), '{"lockfileVersion":3}');
      execFileSync('git', ['-C', root, 'add', '.']);
    }
    const initial = calculate(roots[0]);
    assert.deepEqual(calculate(roots[1]), initial);
    writeFileSync(path.join(roots[1], 'src/platform/New.csproj'), '<Project/>');
    assert.notEqual(calculate(roots[1])['nuget-key'], initial['nuget-key']);
    assert.equal(calculate(roots[1])['npm-key'], initial['npm-key']);
    writeFileSync(path.join(roots[1], 'package-lock.json'), '{"lockfileVersion":3,"packages":{}}');
    assert.notEqual(calculate(roots[1])['npm-key'], initial['npm-key']);
    assert.match(initial['npm-path'], /[/\\]\.npm[/\\]_cacache$/);
    assert.match(readFileSync(path.join(directory, 'environment'), 'utf8'), /NUGET_PACKAGES=.*[/\\]\.nuget[/\\]packages/);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('verification timings preserve command and pipeline failures and log every invocation', () => {
  const step = verify.match(/^        step\(\) \{\n([\s\S]*?)^        \}/m)[0].replace(/^        /gm, '');
  const directory = mkdtempSync(path.join(tmpdir(), 'dependency-cache-timing-'));
  try {
    const log = path.join(directory, 'log');
    const summary = path.join(directory, 'summary');
    const result = spawnSync('bash', ['-c', `set +e -uo pipefail\n${step}\nstep true\nok=$?\nstep bash -c 'exit 7'\nfailed=$?\n[ "$ok" = 0 ] && [ "$failed" = 7 ]`], {
      env: { ...process.env, LOG_FILE: log, GITHUB_STEP_SUMMARY: summary },
      encoding: 'utf8',
    });
    assert.equal(result.status, 0, result.stderr);
    assert.match(readFileSync(log, 'utf8'), /Duration: \d+ ms; exit: 0; command: true/);
    assert.match(readFileSync(log, 'utf8'), /Duration: \d+ ms; exit: 7;/);
    assert.match(readFileSync(summary, 'utf8'), /\| `true` \| \d+ \| 0 \|/);
    assert.match(readFileSync(summary, 'utf8'), /\| `bash -c exit 7` \| \d+ \| 7 \|/);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
