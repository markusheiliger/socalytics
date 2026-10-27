import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

import { buildPlan, MAIN_MINUTES, packShards, TOOL_GROUPS } from './verification-plan.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(here, '..', '..', '..');
const temporary = mkdtempSync(path.join(tmpdir(), 'verification-plan-test-'));
after(() => rmSync(temporary, { recursive: true, force: true }));

let counter = 0;
const repository = () => {
  const directory = path.join(temporary, `repo-${counter++}`);
  mkdirSync(directory, { recursive: true });
  return directory;
};

function touch(root, relative, content = '') {
  mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
  writeFileSync(path.join(root, relative), content);
}

const declare = (root, folder, settings) => touch(root, `${folder}/verification.json`, JSON.stringify({ version: 1, ...settings }));

function plan(root, mode, changed, shard = '') {
  let changedFiles;
  if (changed !== undefined) {
    changedFiles = path.join(temporary, `changed-${counter++}.txt`);
    writeFileSync(changedFiles, changed.join('\n'));
  }
  return buildPlan({ root, mode, changedFiles, shard });
}

const component = (result, root) => result.components.find((item) => item.root === root);

describe('component discovery', () => {
  it('finds .NET solutions and npm packages by convention, without a declaration', () => {
    const root = repository();
    touch(root, 'src/platform/Platform.slnx');
    touch(root, 'src/clients/web/package.json', '{}');
    touch(root, 'src/tools/reporter/Reporter.sln');
    touch(root, 'src/clients/web/node_modules/dep/package.json', '{}');
    touch(root, 'src/platform/Api/bin/Debug/Copy.slnx');
    touch(root, 'src/platform/.hidden/Hidden.slnx');
    touch(root, 'src/analysts/manager/native/macos/Bridge/Package.swift');
    const result = plan(root, 'finalize', ['src/platform/a.cs']);
    assert.deepEqual(result.errors, []);
    assert.deepEqual(result.components.map((item) => [item.kind, item.root, item.manifest]), [
      ['npm-package', 'src/clients/web', 'package.json'],
      ['dotnet-solution', 'src/platform', 'Platform.slnx'],
      ['dotnet-solution', 'src/tools/reporter', 'Reporter.sln'],
    ]);
    assert.equal(component(result, 'src/tools/reporter').check, 'src/tools/reporter build and {tests}');
    assert.deepEqual(component(result, 'src/tools/reporter').tools, ['dotnet']);
  });

  it('reports several solutions or two kinds in one folder', () => {
    const root = repository();
    touch(root, 'src/a/One.slnx');
    touch(root, 'src/a/Two.sln');
    touch(root, 'src/b/B.slnx');
    touch(root, 'src/b/package.json', '{}');
    const result = plan(root, 'task', []);
    assert.equal(result.errors.length, 2);
    assert.match(result.errors[0], /^src\/a: several \.NET solutions \(One\.slnx, Two\.sln\)/);
    assert.match(result.errors[1], /^src\/b: a dotnet-solution and a npm-package share one folder/);
  });
});

describe('selection and coverage', () => {
  function sample() {
    const root = repository();
    touch(root, 'src/platform/Platform.slnx');
    declare(root, 'src/platform', { check: 'platform build and {tests}', order: 10, covers: ['contracts/'] });
    touch(root, 'src/clients/web/package.json', '{}');
    declare(root, 'src/clients/web', { check: 'Web UI build and tests', order: 30, triggers: ['src/platform/Api/'] });
    touch(root, 'src/tools/reporter/Reporter.slnx');
    return root;
  }

  it('selects a component for its own folder, its covered folders, and its triggers', () => {
    const root = sample();
    const selected = (changed) => plan(root, 'task', changed).components.filter((item) => item.selected).map((item) => item.root);
    assert.deepEqual(selected(['src/platform/Domain/a.cs']), ['src/platform']);
    assert.deepEqual(selected(['contracts/common/a.json']), ['src/platform']);
    assert.deepEqual(selected(['src/platform/Api/a.cs']), ['src/platform', 'src/clients/web']);
    assert.deepEqual(selected(['src/tools/reporter/a.cs']), ['src/tools/reporter']);
    assert.deepEqual(selected(['src/platformer/a.cs', 'docs/a.txt']), []);
  });

  it('orders by declared order, then by folder, and tests affected or all by mode', () => {
    const root = sample();
    const task = plan(root, 'task', ['src/platform/a.cs', 'src/tools/reporter/a.cs']);
    assert.deepEqual(task.components.map((item) => item.root), ['src/platform', 'src/clients/web', 'src/tools/reporter']);
    assert.deepEqual(task.components.filter((item) => item.selected).map((item) => item.tests), ['affected', 'affected']);
    const finalize = plan(root, 'finalize', ['docs/a.txt']);
    assert.ok(finalize.components.every((item) => item.selected && item.tests === 'all' && item.reason === 'finalize'));
  });

  it('covers component folders and declared covers, but not triggers or unknown paths', () => {
    const root = sample();
    const result = plan(root, 'task', [
      'src/platform/a.cs', 'contracts/x.json', 'src/platform/Api/a.cs', 'src/clients/web/a.ts', 'src/tools/reporter/a.cs',
      'src/other/a.cs', 'docs/a.md', 'specs/x/plan.md', '.gitignore', '.github/actions/environment-verify/x.mjs', '.github/workflows/x.yml',
    ]);
    assert.deepEqual(result.uncovered, ['src/other/a.cs', '.github/workflows/x.yml']);
  });

  it('tests a component in full and keeps its declaration uncovered when the declaration changes', () => {
    const root = sample();
    const result = plan(root, 'task', ['src/platform/verification.json']);
    const platform = component(result, 'src/platform');
    assert.equal(platform.selected, true);
    assert.equal(platform.tests, 'all');
    assert.equal(platform.reason, 'declaration change: src/platform/verification.json');
    assert.deepEqual(result.uncovered, ['src/platform/verification.json']);
    const added = plan(root, 'task', ['src/tools/reporter/verification.json', 'src/stray/verification.json']);
    assert.equal(component(added, 'src/tools/reporter').tests, 'all');
    assert.deepEqual(added.uncovered, ['src/tools/reporter/verification.json', 'src/stray/verification.json']);
  });

  it('selects nothing for a documentation-only change, in every mode', () => {
    const root = sample();
    touch(root, 'src/platform/README.md');
    for (const mode of ['task', 'phase', 'finalize']) {
      const result = plan(root, mode, ['src/platform/README.md']);
      assert.equal(result.documentationOnly, true);
      assert.match(result.scope, /^documentation-only: /);
      assert.ok(result.components.every((item) => !item.selected));
      assert.deepEqual(result.uncovered, []);
    }
  });

  it('collects the tool groups of the selected work', () => {
    const root = sample();
    declare(root, 'src/tools/reporter', { tools: ['docker', 'softhsm'] });
    assert.deepEqual(plan(root, 'task', ['docs/a.txt']).tools, ['markdown', 'node']);
    assert.deepEqual(plan(root, 'task', ['src/tools/reporter/a.cs']).tools, ['docker', 'dotnet', 'markdown', 'node', 'softhsm']);
  });

  it('adds a default shard after the declared ones', () => {
    const root = sample();
    declare(root, 'src/tools/reporter', {
      tools: ['docker'],
      shards: [{ name: 'slow', tests: ['Tests/Slow.Tests/Slow.Tests.csproj'], runner: 'windows-x64', budgetMinutes: 40 }],
    });
    assert.deepEqual(component(plan(root, 'finalize', []), 'src/tools/reporter').shards, [
      { name: 'slow', tests: ['Tests/Slow.Tests/Slow.Tests.csproj'], runner: 'windows-x64', docker: false, budgetMinutes: 40 },
      { name: 'default', tests: [], runner: 'linux-x64', docker: true, budgetMinutes: null },
    ]);
  });
});

describe('declaration validation', () => {
  const cases = [
    ['not JSON', '{', /not valid JSON/],
    ['an array', '[]', /must be a JSON object/],
    ['a wrong version', { version: 2 }, /version must be 1/],
    ['an unknown setting', { version: 1, skip: true }, /unknown setting "skip"/],
    ['a check with a comma', { version: 1, check: 'a, b' }, /check must be a name/],
    ['a trigger outside src and contracts', { version: 1, triggers: ['.github/'] }, /triggers must list folders/],
    ['a cover without a trailing slash', { version: 1, covers: ['contracts'] }, /covers must list folders/],
    ['a parent cover', { version: 1, covers: ['src/../'] }, /covers must list folders/],
    ['an unknown tool group', { version: 1, tools: ['gcc'] }, /tools must list tool groups/],
    ['an absolute always-run project', { version: 1, alwaysRun: ['/Tests/A.csproj'] }, /alwaysRun must list test project files/],
    ['an escaping always-run project', { version: 1, alwaysRun: ['../A.csproj'] }, /alwaysRun must list test project files/],
    ['a pin that is no global.json', { version: 1, sdkPinOf: 'src/x.json' }, /sdkPinOf must name a global\.json/],
    ['a shard named default', { version: 1, shards: [{ name: 'default', tests: ['A.csproj'] }] }, /not "default"/],
    ['duplicate shards', { version: 1, shards: [{ name: 'a', tests: ['A.csproj'] }, { name: 'a', tests: ['B.csproj'] }] }, /duplicate name/],
    ['a shard without tests', { version: 1, shards: [{ name: 'a', tests: [] }] }, /tests must list test projects/],
    ['an unknown runner', { version: 1, shards: [{ name: 'a', tests: ['A.csproj'], runner: 'solaris' }] }, /runner must be one of/],
    ['a shard budget out of range', { version: 1, shards: [{ name: 'a', tests: ['A.csproj'], budgetMinutes: 0 }] }, /budgetMinutes must be/],
    ['an unknown shard setting', { version: 1, shards: [{ name: 'a', tests: ['A.csproj'], parallel: 2 }] }, /unknown setting "parallel"/],
  ];
  for (const [name, content, pattern] of cases) {
    it(`rejects ${name} and leaves the component out of the plan`, () => {
      const root = repository();
      touch(root, 'src/tools/reporter/Reporter.slnx');
      touch(root, 'src/tools/reporter/verification.json', typeof content === 'string' ? content : JSON.stringify(content));
      const result = plan(root, 'finalize', []);
      assert.equal(result.errors.length >= 1, true);
      assert.match(result.errors.join('\n'), pattern);
      assert.ok(result.errors.every((error) => error.startsWith('src/tools/reporter/verification.json: ')));
      assert.equal(component(result, 'src/tools/reporter'), undefined);
    });
  }

  it('rejects .NET settings on an npm package', () => {
    const root = repository();
    touch(root, 'src/clients/web/package.json', '{}');
    declare(root, 'src/clients/web', { alwaysRun: ['A.csproj'], sdkPinOf: 'src/platform/global.json' });
    assert.deepEqual(plan(root, 'task', []).errors, [
      'src/clients/web/verification.json: alwaysRun applies only to a .NET solution',
      'src/clients/web/verification.json: sdkPinOf applies only to a .NET solution',
    ]);
  });

  it('accepts the repository\'s own declarations and keeps their checks', () => {
    const result = plan(repositoryRoot, 'finalize', []);
    assert.deepEqual(result.errors, []);
    const checks = Object.fromEntries(result.components.map((item) => [item.root, item.check]));
    assert.equal(checks['src/platform'], 'platform build and {tests}');
    assert.equal(checks['src/analysts/manager'], 'Analyst Manager build and {tests}');
    assert.equal(checks['src/clients/web'], 'Web UI build and {tests}');
    assert.deepEqual(result.components.slice(0, 3).map((item) => item.root), ['src/platform', 'src/analysts/manager', 'src/clients/web']);
    for (const item of result.components) assert.ok(item.tools.every((tool) => TOOL_GROUPS.includes(tool)));
  });
});

describe('verification of a new component', () => {
  const actionText = readFileSync(path.join(here, 'action.yml'), 'utf8').replace(/\r\n/g, '\n');
  const script = actionText.split('      run: |\n')[1].split('\n').map((line) => line.slice(8)).join('\n');

  function run(root, { mode, changed, env = {} }) {
    const dir = path.join(temporary, `run-${counter++}`);
    const bin = path.join(dir, 'bin');
    mkdirSync(bin, { recursive: true });
    for (const tool of ['dotnet', 'npm']) writeFileSync(path.join(bin, tool), `#!/bin/bash\necho "$PWD|${tool}|$*" >> "$TOOL_CALLS"\nexit 0\n`, { mode: 0o755 });
    const files = { changed: path.join(dir, 'changed.txt'), log: path.join(dir, 'log.txt'), output: path.join(dir, 'output.txt'), calls: path.join(dir, 'calls.txt'), markdown: path.join(dir, 'markdown.mjs') };
    writeFileSync(files.changed, changed.join('\n'));
    writeFileSync(files.calls, '');
    writeFileSync(files.output, '');
    writeFileSync(files.markdown, '');
    const result = spawnSync('bash', ['-c', script], {
      encoding: 'utf8',
      env: {
        PATH: `${bin}:${process.env.PATH}`, HOME: process.env.HOME, WORKSPACE: root, MODE: mode, CHANGED_FILES: files.changed,
        LOG_FILE: files.log, MARKDOWN_CHECK: files.markdown, ANALYST_PROJECTS: path.join(here, 'analyst-projects.py'),
        VERIFICATION_PLAN: path.join(here, 'verification-plan.mjs'), GITHUB_STEP_SUMMARY: path.join(dir, 'summary.md'),
        GITHUB_OUTPUT: files.output, TOOL_CALLS: files.calls, ...env,
      },
    });
    const outputs = Object.fromEntries(readFileSync(files.output, 'utf8').split('\n').filter(Boolean).map((line) => line.split(/=(.*)/s).slice(0, 2)));
    return { status: result.status, outputs, log: readFileSync(files.log, 'utf8'), calls: readFileSync(files.calls, 'utf8').split('\n').filter(Boolean) };
  }

  it('builds and tests a new .NET solution from its own folder without any action change', () => {
    const root = repository();
    touch(root, 'src/tools/reporter/Reporter.slnx');
    touch(root, 'src/tools/reporter/Tests/Reporter.Tests/Reporter.Tests.csproj');
    const result = run(root, { mode: 'phase', changed: ['src/tools/reporter/Report.cs'] });
    assert.equal(result.status, 0, result.log);
    assert.equal(result.outputs.checks, 'src/tools/reporter build and tests');
    assert.equal(result.outputs.uncovered, '');
    assert.deepEqual(result.calls.map((call) => call.replace(/ --logger trx --results-directory \S+/, '')), [
      `${root}/src/tools/reporter|dotnet|restore Reporter.slnx`,
      `${root}/src/tools/reporter|dotnet|build Reporter.slnx --no-restore`,
      `${root}/src/tools/reporter|dotnet|test Tests/Reporter.Tests/Reporter.Tests.csproj --no-build`,
    ]);
    assert.match(readFileSync(result.outputs.plan, 'utf8'), /"root": "src\/tools\/reporter"/);
  });

  it('runs an npm package\'s declared scripts only', () => {
    const root = repository();
    touch(root, 'src/clients/kiosk/package.json', '{"scripts":{"build":"b"}}');
    const result = run(root, { mode: 'task', changed: ['src/clients/kiosk/a.ts'] });
    assert.equal(result.status, 0, result.log);
    assert.equal(result.outputs.checks, 'src/clients/kiosk build and affected tests');
    assert.deepEqual(result.calls, [`${root}/src/clients/kiosk|npm|ci`, `${root}/src/clients/kiosk|npm|run build`]);
  });

  it('fails the plan check for an invalid declaration and runs nothing else', () => {
    const root = repository();
    touch(root, 'src/tools/reporter/Reporter.slnx');
    touch(root, 'src/tools/reporter/verification.json', '{"version":1,"skip":true}');
    const result = run(root, { mode: 'finalize', changed: ['src/tools/reporter/a.cs'] });
    assert.equal(result.status, 1);
    assert.equal(result.outputs.checks, 'Verification plan');
    assert.match(result.log, /Verification plan: src\/tools\/reporter\/verification\.json: unknown setting "skip"/);
    assert.equal((result.log.match(/Recommended action:/g) ?? []).length, 1);
    assert.deepEqual(result.calls, []);
  });

  it('logs the slowest tests of each .NET test project and lists them after the timings', () => {
    const root = repository();
    touch(root, 'src/tools/reporter/Reporter.slnx');
    touch(root, 'src/tools/reporter/Tests/Reporter.Tests/Reporter.Tests.csproj');
    const dir = path.join(temporary, `slow-${counter++}`);
    const bin = path.join(dir, 'bin');
    mkdirSync(bin, { recursive: true });
    const trx = '<TestRun><Results>'
      + '<UnitTestResult testName="Ns.Fast" duration="00:00:00.5000000" outcome="Passed" />'
      + '<UnitTestResult testName="Ns.Slow(&quot;x&quot;)" duration="00:01:02.2500000" outcome="Passed" />'
      + '<UnitTestResult testName="Ns.Middle" duration="00:00:07.0000000" outcome="Passed" />'
      + '</Results></TestRun>';
    writeFileSync(path.join(bin, 'dotnet'), `#!/bin/bash
args=("$@"); for ((i = 0; i < \${#args[@]}; i++)); do [ "\${args[$i]}" = --results-directory ] && dir="\${args[$((i + 1))]}"; done
if [ "$1" = test ]; then mkdir -p "$dir"; printf '%s' '${trx}' > "$dir/r.trx"; fi
exit 0
`, { mode: 0o755 });
    const files = { changed: path.join(dir, 'changed.txt'), log: path.join(dir, 'log.txt'), output: path.join(dir, 'output.txt'), summary: path.join(dir, 'summary.md') };
    writeFileSync(files.changed, 'src/tools/reporter/a.cs');
    writeFileSync(files.output, '');
    const result = spawnSync('bash', ['-c', script], {
      encoding: 'utf8',
      env: {
        PATH: `${bin}:${process.env.PATH}`, HOME: process.env.HOME, WORKSPACE: root, MODE: 'phase', CHANGED_FILES: files.changed,
        LOG_FILE: files.log, MARKDOWN_CHECK: path.join(dir, 'none.mjs'), ANALYST_PROJECTS: path.join(here, 'analyst-projects.py'),
        VERIFICATION_PLAN: path.join(here, 'verification-plan.mjs'), GITHUB_STEP_SUMMARY: files.summary, GITHUB_OUTPUT: files.output,
      },
    });
    assert.equal(result.status, 0, readFileSync(files.log, 'utf8'));
    assert.match(readFileSync(files.log, 'utf8'), /Slowest tests of Tests\/Reporter\.Tests\/Reporter\.Tests\.csproj:\n {2}62\.3 s Ns\.Slow\("x"\)\n {2}7\.0 s Ns\.Middle\n {2}0\.5 s Ns\.Fast\n/);
    const summary = readFileSync(files.summary, 'utf8');
    assert.ok(summary.indexOf('### Slowest tests') > summary.lastIndexOf('| `dotnet test'), summary);
    assert.match(summary, /<details><summary>Slowest tests of Tests\/Reporter\.Tests\/Reporter\.Tests\.csproj<\/summary>\n\n- 62\.3 s Ns\.Slow\("x"\)/);
  });

  it('holds a changed declaration for review while testing the component in full', () => {
    const root = repository();
    touch(root, 'src/tools/reporter/Reporter.slnx');
    touch(root, 'src/tools/reporter/Tests/Reporter.Tests/Reporter.Tests.csproj');
    declare(root, 'src/tools/reporter', { check: 'reporter build and {tests}' });
    const result = run(root, { mode: 'task', changed: ['src/tools/reporter/verification.json'] });
    assert.equal(result.status, 0, result.log);
    assert.equal(result.outputs.checks, 'reporter build and tests');
    assert.equal(result.outputs.uncovered, 'src/tools/reporter/verification.json');
    assert.ok(result.calls.map((call) => call.replace(/ --logger trx --results-directory \S+/, ''))
      .includes(`${root}/src/tools/reporter|dotnet|test Tests/Reporter.Tests/Reporter.Tests.csproj --no-build`));
  });
});

describe('Web UI browser test tiers', () => {
  const actionText = readFileSync(path.join(here, 'action.yml'), 'utf8').replace(/\r\n/g, '\n');
  const script = actionText.split('      run: |\n')[1].split('\n').map((line) => line.slice(8)).join('\n');

  function web({ browserTests = 'tests/e2e/' } = {}) {
    const root = repository();
    touch(root, 'src/clients/web/package.json', JSON.stringify({ scripts: { build: 'b', test: 't', 'test:e2e': 'e' }, devDependencies: { '@playwright/test': '1.63.0' } }));
    touch(root, 'src/clients/web/node_modules/@playwright/test/package.json', '{"version":"1.63.0"}');
    declare(root, 'src/clients/web', { check: 'Web UI build and {tests}', ...(browserTests ? { browserTests } : {}) });
    touch(root, 'src/clients/web/tests/e2e/simulated/sign-in.spec.ts');
    touch(root, 'src/clients/web/tests/e2e/platform/triage.spec.ts');
    return root;
  }

  function npmCalls(root, mode, changed) {
    const dir = path.join(temporary, `web-${counter++}`);
    const bin = path.join(dir, 'bin');
    mkdirSync(bin, { recursive: true });
    writeFileSync(path.join(bin, 'npm'), '#!/bin/bash\necho "$*" >> "$TOOL_CALLS"\nexit 0\n', { mode: 0o755 });
    const files = { changed: path.join(dir, 'changed.txt'), log: path.join(dir, 'log.txt'), output: path.join(dir, 'output.txt'), calls: path.join(dir, 'calls.txt') };
    writeFileSync(files.changed, changed.join('\n'));
    for (const file of [files.calls, files.output]) writeFileSync(file, '');
    const result = spawnSync('bash', ['-c', script], {
      encoding: 'utf8',
      env: {
        PATH: `${bin}:${process.env.PATH}`, HOME: process.env.HOME, WORKSPACE: root, MODE: mode, CHANGED_FILES: files.changed,
        LOG_FILE: files.log, MARKDOWN_CHECK: path.join(dir, 'none.mjs'), ANALYST_PROJECTS: path.join(here, 'analyst-projects.py'),
        VERIFICATION_PLAN: path.join(here, 'verification-plan.mjs'), GITHUB_STEP_SUMMARY: path.join(dir, 'summary.md'),
        GITHUB_OUTPUT: files.output, TOOL_CALLS: files.calls,
      },
    });
    const outputs = Object.fromEntries(readFileSync(files.output, 'utf8').split('\n').filter(Boolean).map((line) => line.split(/=(.*)/s).slice(0, 2)));
    return { status: result.status, checks: outputs.checks, log: readFileSync(files.log, 'utf8'), calls: readFileSync(files.calls, 'utf8').split('\n').filter(Boolean) };
  }

  it('runs the build and all unit tests but no browser test when a task touches none', () => {
    const result = npmCalls(web(), 'task', ['src/clients/web/src/session.ts', 'src/platform/SocAlytics.Platform.Api/a.cs']);
    assert.equal(result.status, 0, result.log);
    assert.equal(result.checks, 'Web UI build and affected tests');
    assert.deepEqual(result.calls, ['ci', 'run build', 'test']);
    assert.match(result.log, /Web UI step: browser tests skipped: the change touched no browser test; the phase verification runs them all/);
  });

  it('runs only the browser spec files a task touched', () => {
    const result = npmCalls(web(), 'task', [
      'src/clients/web/src/session.ts', 'src/clients/web/tests/e2e/platform/triage.spec.ts',
      'src/clients/web/tests/e2e/simulated/sign-in.spec.ts', 'src/clients/web/tests/e2e/simulated/removed.spec.ts',
    ]);
    assert.equal(result.status, 0, result.log);
    assert.deepEqual(result.calls, ['ci', 'run build', 'test', 'run test:e2e -- --retries=1 tests/e2e/platform/triage.spec.ts tests/e2e/simulated/sign-in.spec.ts']);
    assert.match(result.log, /Web UI step: browser tests \(touched specs\)/);
  });

  for (const shared of ['src/clients/web/tests/e2e/support/fakeObjectStorage.mjs', 'src/clients/web/playwright.config.ts', 'src/clients/web/package-lock.json', 'src/clients/web/package.json']) {
    it(`runs every browser test when a task changes ${path.posix.basename(shared)}`, () => {
      const result = npmCalls(web(), 'task', ['src/clients/web/tests/e2e/simulated/sign-in.spec.ts', shared]);
      assert.equal(result.status, 0, result.log);
      assert.deepEqual(result.calls, ['ci', 'run build', 'test', 'run test:e2e -- --retries=1']);
    });
  }

  it('runs every browser test in phase and finalize mode and without a declared browser test folder', () => {
    for (const mode of ['phase', 'finalize']) {
      assert.deepEqual(npmCalls(web(), mode, ['src/clients/web/src/session.ts']).calls, ['ci', 'run build', 'test', 'run test:e2e -- --retries=1']);
    }
    assert.deepEqual(npmCalls(web({ browserTests: '' }), 'task', ['src/clients/web/src/session.ts']).calls, ['ci', 'run build', 'test', 'run test:e2e -- --retries=1']);
  });

  it('runs every browser test when the declaration changed', () => {
    const result = npmCalls(web(), 'task', ['src/clients/web/verification.json']);
    assert.equal(result.checks, 'Web UI build and tests');
    assert.deepEqual(result.calls, ['ci', 'run build', 'test', 'run test:e2e -- --retries=1']);
  });

  it('accepts browserTests only as a folder of an npm package', () => {
    const root = repository();
    touch(root, 'src/tools/reporter/Reporter.slnx');
    declare(root, 'src/tools/reporter', { browserTests: 'tests/e2e/' });
    touch(root, 'src/clients/web/package.json', '{}');
    declare(root, 'src/clients/web', { browserTests: '../e2e/' });
    assert.deepEqual(plan(root, 'task', []).errors.sort(), [
      'src/clients/web/verification.json: browserTests must name a folder relative to the component folder, ending with "/"',
      'src/tools/reporter/verification.json: browserTests applies only to an npm package',
    ]);
  });
});

describe('shards', () => {
  // A platform with two declared shards and a default rest, a Web UI, and a small Manager, like the repository.
  function sharded() {
    const root = repository();
    touch(root, 'src/platform/Platform.slnx');
    for (const project of ['Architecture', 'Contracts', 'Integration', 'EndToEnd']) touch(root, `src/platform/Tests/${project}.Tests/${project}.Tests.csproj`);
    touch(root, 'src/platform/Tests/Integration.Tests/bin/Debug/Copy.Tests.csproj');
    declare(root, 'src/platform', {
      order: 10, budgetMinutes: 10,
      shards: [
        { name: 'integration', tests: ['Tests/Integration.Tests/Integration.Tests.csproj'], docker: true, budgetMinutes: 25 },
        { name: 'end-to-end', tests: ['Tests/EndToEnd.Tests/EndToEnd.Tests.csproj'], docker: true, budgetMinutes: 25 },
      ],
    });
    touch(root, 'src/clients/web/package.json', '{}');
    declare(root, 'src/clients/web', { order: 30, budgetMinutes: 21 });
    touch(root, 'src/analysts/manager/Manager.slnx');
    touch(root, 'src/analysts/manager/Tests/Manager.Tests/Manager.Tests.csproj');
    declare(root, 'src/analysts/manager', { order: 20, budgetMinutes: 2 });
    return root;
  }

  it('lists a .NET component\'s test projects outside bin and obj', () => {
    const platform = component(plan(sharded(), 'finalize', []), 'src/platform');
    assert.deepEqual(platform.testProjects, [
      'Tests/Architecture.Tests/Architecture.Tests.csproj', 'Tests/Contracts.Tests/Contracts.Tests.csproj',
      'Tests/EndToEnd.Tests/EndToEnd.Tests.csproj', 'Tests/Integration.Tests/Integration.Tests.csproj',
    ]);
  });

  it('packs the units largest first into the shard with the least work, the first shard carrying the shared checks', () => {
    const bins = plan(sharded(), 'finalize', [], '1/4').shards;
    assert.deepEqual(bins.map((bin) => [bin.index, bin.main, bin.minutes, bin.units.map((unit) => `${unit.root}#${unit.name}`)]), [
      [1, true, MAIN_MINUTES + 12, ['src/platform#default', 'src/analysts/manager#default']],
      [2, false, 25, ['src/platform#end-to-end']],
      [3, false, 25, ['src/platform#integration']],
      [4, false, 21, ['src/clients/web#default']],
    ]);
    assert.deepEqual(bins[0].units[0].tests, ['Tests/Architecture.Tests/Architecture.Tests.csproj', 'Tests/Contracts.Tests/Contracts.Tests.csproj']);
    assert.equal(bins[3].units[0].tests, null);
  });

  it('runs in each shard only its own components and test projects', () => {
    const root = sharded();
    const view = (shard) => plan(root, 'finalize', [], shard).components.filter((item) => item.run).map((item) => [item.root, item.allowedTests]);
    assert.deepEqual(view('1/4'), [
      ['src/platform', 'Tests/Architecture.Tests/Architecture.Tests.csproj;Tests/Contracts.Tests/Contracts.Tests.csproj'],
      ['src/analysts/manager', 'Tests/Manager.Tests/Manager.Tests.csproj'],
    ]);
    assert.deepEqual(view('2/4'), [['src/platform', 'Tests/EndToEnd.Tests/EndToEnd.Tests.csproj']]);
    assert.deepEqual(view('4/4'), [['src/clients/web', '*']]);
    assert.deepEqual(view(''), [['src/platform', '*'], ['src/analysts/manager', '*'], ['src/clients/web', '*']]);
    assert.equal(plan(root, 'finalize', [], '1/4').main, true);
    assert.equal(plan(root, 'finalize', [], '3/4').main, false);
    assert.equal(plan(root, 'finalize', []).main, true);
  });

  it('keeps every selected component in one unit for affected tests and leaves empty shards without work', () => {
    const bins = plan(sharded(), 'task', ['src/platform/Domain/a.cs'], '1/3').shards;
    assert.deepEqual(bins.map((bin) => bin.units.map((unit) => `${unit.root}#${unit.name}`)), [[], ['src/platform#default'], []]);
    assert.equal(bins[1].units[0].tests, null);
  });

  it('covers every test project exactly once across the shards', () => {
    const root = sharded();
    for (const count of [1, 2, 3, 4, 7]) {
      const runs = [];
      for (let index = 1; index <= count; index += 1) {
        for (const item of plan(root, 'finalize', [], `${index}/${count}`).components.filter((entry) => entry.run && entry.kind === 'dotnet-solution')) {
          runs.push(...item.allowedTests.split(';').map((test) => `${item.root}/${test}`));
        }
      }
      assert.deepEqual(runs.sort(), [
        'src/analysts/manager/Tests/Manager.Tests/Manager.Tests.csproj', 'src/platform/Tests/Architecture.Tests/Architecture.Tests.csproj',
        'src/platform/Tests/Contracts.Tests/Contracts.Tests.csproj', 'src/platform/Tests/EndToEnd.Tests/EndToEnd.Tests.csproj',
        'src/platform/Tests/Integration.Tests/Integration.Tests.csproj',
      ], `${count} shards`);
    }
  });

  it('rejects an invalid shard', () => {
    for (const shard of ['0/2', '3/2', '1/21', 'a/b', '1']) assert.throws(() => plan(sharded(), 'finalize', [], shard), /invalid shard/, shard);
  });

  it('never runs a shard declared for another runner and holds the change for review', () => {
    const root = sharded();
    declare(root, 'src/analysts/manager', {
      order: 20, shards: [{ name: 'windows-keys', tests: ['Tests/Manager.Tests/Manager.Tests.csproj'], runner: 'windows-x64' }],
    });
    const manager = component(plan(root, 'phase', ['src/analysts/manager/a.cs']), 'src/analysts/manager');
    assert.deepEqual(manager.excludedTests, ['Tests/Manager.Tests/Manager.Tests.csproj']);
    assert.equal(manager.allowedTests, '');
    assert.deepEqual(plan(root, 'phase', ['src/analysts/manager/a.cs']).uncovered, ['src/analysts/manager/verification.json']);
    for (const bin of plan(root, 'finalize', [], '1/2').shards) {
      assert.ok(bin.units.every((unit) => !(unit.tests ?? []).includes('Tests/Manager.Tests/Manager.Tests.csproj')));
    }
  });

  it('packs deterministically', () => {
    assert.deepEqual(packShards([], 3).map((bin) => bin.minutes), [MAIN_MINUTES, 0, 0]);
    const root = sharded();
    assert.deepEqual(plan(root, 'finalize', [], '2/4').shards, plan(root, 'finalize', [], '3/4').shards);
  });
});

describe('sharded verification', () => {
  const actionText = readFileSync(path.join(here, 'action.yml'), 'utf8').replace(/\r\n/g, '\n');
  const script = actionText.split('      run: |\n')[1].split('\n').map((line) => line.slice(8)).join('\n');

  function shardRun(root, shard) {
    const dir = path.join(temporary, `shard-${counter++}`);
    const bin = path.join(dir, 'bin');
    mkdirSync(bin, { recursive: true });
    for (const tool of ['dotnet', 'npm']) writeFileSync(path.join(bin, tool), `#!/bin/bash\necho "\${PWD##*/src/}|${tool}|$*" >> "$TOOL_CALLS"\nexit 0\n`, { mode: 0o755 });
    const files = { changed: path.join(dir, 'changed.txt'), log: path.join(dir, 'log.txt'), output: path.join(dir, 'output.txt'), calls: path.join(dir, 'calls.txt'), markdown: path.join(dir, 'markdown.cjs') };
    writeFileSync(files.changed, 'src/platform/a.cs\ndocs/a.md');
    for (const file of [files.calls, files.output]) writeFileSync(file, '');
    writeFileSync(files.markdown, `require('fs').appendFileSync(${JSON.stringify(files.calls)}, 'markdown\\n');\n`);
    const result = spawnSync('bash', ['-c', script], {
      encoding: 'utf8',
      env: {
        PATH: `${bin}:${process.env.PATH}`, HOME: process.env.HOME, WORKSPACE: root, MODE: 'finalize', CHANGED_FILES: files.changed, SHARD: shard,
        LOG_FILE: files.log, MARKDOWN_CHECK: files.markdown, ANALYST_PROJECTS: path.join(here, 'analyst-projects.py'),
        VERIFICATION_PLAN: path.join(here, 'verification-plan.mjs'), GITHUB_STEP_SUMMARY: path.join(dir, 'summary.md'),
        GITHUB_OUTPUT: files.output, TOOL_CALLS: files.calls,
      },
    });
    const outputs = Object.fromEntries(readFileSync(files.output, 'utf8').split('\n').filter(Boolean).map((line) => line.split(/=(.*)/s).slice(0, 2)));
    return {
      status: result.status, checks: outputs.checks, uncovered: outputs.uncovered, log: readFileSync(files.log, 'utf8'),
      calls: readFileSync(files.calls, 'utf8').split('\n').filter(Boolean).map((call) => call.replace(/ --logger trx --results-directory \S+/, '')),
    };
  }

  function twoProjects() {
    const root = repository();
    touch(root, 'src/platform/Platform.slnx');
    for (const project of ['Architecture', 'Integration']) touch(root, `src/platform/Tests/${project}.Tests/${project}.Tests.csproj`);
    declare(root, 'src/platform', {
      check: 'platform build and {tests}', budgetMinutes: 3,
      shards: [{ name: 'integration', tests: ['Tests/Integration.Tests/Integration.Tests.csproj'], budgetMinutes: 25 }],
    });
    return root;
  }

  it('runs a later shard\'s test projects only, without the shared checks', () => {
    const result = shardRun(twoProjects(), '2/2');
    assert.equal(result.status, 0, result.log);
    assert.equal(result.checks, 'platform build and tests');
    assert.deepEqual(result.calls, [
      'platform|dotnet|restore Platform.slnx', 'platform|dotnet|build Platform.slnx --no-restore',
      'platform|dotnet|test Tests/Integration.Tests/Integration.Tests.csproj --no-build',
    ]);
    assert.match(result.log, /Verification shard 2\/2: src\/platform#integration \(25 min\)\n/);
    assert.equal(result.uncovered, '');
  });

  it('runs the rest and the shared checks in the first shard', () => {
    const result = shardRun(twoProjects(), '1/2');
    assert.equal(result.status, 0, result.log);
    assert.equal(result.checks, 'platform build and tests,Markdown check');
    assert.deepEqual(result.calls, [
      'platform|dotnet|restore Platform.slnx', 'platform|dotnet|build Platform.slnx --no-restore',
      'platform|dotnet|test Tests/Architecture.Tests/Architecture.Tests.csproj --no-build', 'markdown',
    ]);
    assert.match(result.log, /Verification shard 1\/2: src\/platform#default \(3 min\); also the Python projects, environment action tests, and Markdown check/);
  });

  it('runs nothing in a shard without work and still passes', () => {
    const result = shardRun(twoProjects(), '4/4');
    assert.equal(result.status, 0, result.log);
    assert.equal(result.checks, '');
    assert.deepEqual(result.calls, []);
    assert.match(result.log, /Verification shard 4\/4: no component work/);
  });

  it('fails every shard of an invalid shard input', () => {
    const result = shardRun(twoProjects(), '4/3');
    assert.equal(result.status, 1);
    assert.equal(result.checks, 'Verification plan');
    assert.match(result.log, /Verification plan: invalid shard 4\/3/);
  });
});