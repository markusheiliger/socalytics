import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const installer = path.join(here, 'install-analyst-media.sh');
const realDeclaration = JSON.parse(readFileSync(path.join(here, 'analyst-media-toolchain.json'), 'utf8'));
const temporary = mkdtempSync(path.join(tmpdir(), 'toolchain-test-'));
after(() => rmSync(temporary, { recursive: true, force: true }));

let counter = 0;
const scratch = () => {
  const directory = path.join(temporary, `case-${counter++}`);
  mkdirSync(directory, { recursive: true });
  return directory;
};

const sha256 = (file) => createHash('sha256').update(readFileSync(file)).digest('hex');

function script(directory, relative, output) {
  const file = path.join(directory, relative);
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, `#!/usr/bin/env bash\necho "${output}"\n`);
  chmodSync(file, 0o755);
}

// Builds a fixture archive whose single tool prints the given version.
function fixtureArchive(directory, { root = 'pkg', version = 'fixture 1.0', extra } = {}) {
  const source = path.join(directory, 'source');
  rmSync(source, { recursive: true, force: true });
  script(source, `${root}/bin/fixture`, version);
  writeFileSync(path.join(source, root, 'LICENSE.txt'), 'fixture license\n');
  if (extra) extra(source);
  const archive = path.join(directory, 'fixture.tar.gz');
  execFileSync('tar', ['-czf', archive, '-C', source, '.']);
  return archive;
}

function declaration(archive, overrides = {}) {
  return {
    schemaVersion: 1,
    supportedHost: { os: 'Linux', architecture: 'x86_64' },
    installRoot: 'fixture-tools',
    tools: [{
      id: 'fixture',
      name: 'Fixture',
      version: '1.0',
      archive: {
        url: `file://${archive}`,
        fileName: 'fixture.tar.gz',
        format: 'tar.gz',
        sha256: sha256(archive),
      },
      extractedRoot: 'pkg',
      executables: { python: 'pkg/bin/fixture' },
      versionCheck: { executable: 'python', arguments: [], expectedOutput: 'fixture 1.0' },
      environmentVariable: 'SOCALYTICS_FIXTURE',
      pathDirectories: ['pkg/bin'],
      licenseLocations: ['pkg/LICENSE.txt'],
      provenanceLocation: 'https://example.invalid/provenance',
      ...overrides,
    }],
  };
}

function install(directory, toolchain, env = {}) {
  const file = path.join(directory, 'toolchain.json');
  writeFileSync(file, JSON.stringify(toolchain));
  const githubPath = path.join(directory, 'github-path');
  const githubEnv = path.join(directory, 'github-env');
  writeFileSync(githubPath, '');
  writeFileSync(githubEnv, '');
  const result = spawnSync('bash', [installer], {
    encoding: 'utf8',
    env: {
      ...process.env,
      ANALYST_MEDIA_TOOLCHAIN: file,
      ANALYST_MEDIA_INSTALL_ROOT: path.join(directory, 'install'),
      GITHUB_PATH: githubPath,
      GITHUB_ENV: githubEnv,
      ...env,
    },
  });
  return { ...result, githubPath: readFileSync(githubPath, 'utf8'), githubEnv: readFileSync(githubEnv, 'utf8') };
}

function assertFailure(result, tool, stage) {
  assert.notEqual(result.status, 0, result.stdout);
  assert.match(result.stderr, new RegExp(`tool=${tool} stage=${stage}:`));
  assert.match(result.stderr, /Recommended action: /);
  assert.equal((result.stderr.match(/Recommended action:/g) ?? []).length, 1);
}

describe('committed toolchain declaration', () => {
  it('pins exact archives with digests and no floating selectors', () => {
    assert.deepEqual(realDeclaration.supportedHost, { os: 'Linux', architecture: 'x86_64' });
    for (const tool of realDeclaration.tools) {
      assert.match(tool.archive.sha256, /^[0-9a-f]{64}$/);
      assert.doesNotMatch(tool.archive.url, /latest|nightly/i);
    }
  });
});

describe('install-analyst-media.sh', () => {
  it('never advises bypassing a check in any failure recommendation', () => {
    const text = readFileSync(installer, 'utf8');
    assert.doesNotMatch(text, /Recommended action: [^\n]*(skip|disable|ignore|--no-verify)/i);
  });

  it('installs a verified archive, retains notices, and publishes paths', () => {
    const directory = scratch();
    const result = install(directory, declaration(fixtureArchive(directory)));
    assert.equal(result.status, 0, result.stderr);
    const root = path.join(directory, 'install');
    assert.ok(existsSync(path.join(root, 'pkg/bin/fixture')));
    assert.equal(readFileSync(path.join(root, 'notices/fixture/LICENSE.txt'), 'utf8'), 'fixture license\n');
    assert.match(readFileSync(path.join(root, 'notices/fixture/PROVENANCE.txt'), 'utf8'), /example\.invalid/);
    assert.ok(!existsSync(path.join(root, 'downloads')));
    assert.equal(result.githubPath.trim(), path.join(root, 'pkg/bin'));
    assert.equal(result.githubEnv.trim(), `SOCALYTICS_FIXTURE=${path.join(root, 'pkg/bin/fixture')}`);
  });

  it('rejects an unsupported host before downloading', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory));
    toolchain.tools[0].archive.url = `file://${directory}/missing.tar.gz`;
    const result = install(directory, toolchain, { ANALYST_MEDIA_HOST_OS: 'Darwin' });
    assertFailure(result, 'host', 'platform');
    assert.match(result.stderr, /Darwin\/x86_64 is unsupported/);
    const arch = install(directory, toolchain, { ANALYST_MEDIA_HOST_ARCH: 'aarch64' });
    assertFailure(arch, 'host', 'platform');
  });

  it('rejects an invalid declaration', () => {
    const directory = scratch();
    const result = install(directory, { schemaVersion: 2, tools: [] });
    assertFailure(result, 'toolchain', 'declaration');
  });

  it('names the tool and stage when the download fails', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory));
    toolchain.tools[0].archive.url = `file://${directory}/missing.tar.gz`;
    assertFailure(install(directory, toolchain), 'fixture', 'download');
  });

  it('rejects a digest mismatch before extracting', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory));
    toolchain.tools[0].archive.sha256 = '0'.repeat(64);
    const result = install(directory, toolchain);
    assertFailure(result, 'fixture', 'integrity');
    assert.match(result.stderr, /never bypass/);
    assert.ok(!existsSync(path.join(directory, 'install/pkg')));
  });

  it('rejects a malformed digest', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory));
    toolchain.tools[0].archive.sha256 = 'abc';
    assertFailure(install(directory, toolchain), 'fixture', 'declaration');
  });

  it('rejects path traversal entries', () => {
    const directory = scratch();
    const source = path.join(directory, 'traversal');
    script(source, 'pkg/bin/fixture', 'fixture 1.0');
    writeFileSync(path.join(source, 'evil'), 'x');
    const archive = path.join(directory, 'fixture.tar.gz');
    execFileSync('tar', ['-czf', archive, '-C', source, '--transform', 's,^evil$,../evil,', 'pkg', 'evil']);
    assertFailure(install(directory, declaration(archive)), 'fixture', 'extract');
    assert.ok(!existsSync(path.join(directory, 'install/evil')));
  });

  it('rejects absolute entries', () => {
    const directory = scratch();
    const source = path.join(directory, 'absolute');
    script(source, 'pkg/bin/fixture', 'fixture 1.0');
    const archive = path.join(directory, 'fixture.tar.gz');
    execFileSync('tar', ['-czPf', archive, path.join(source, 'pkg')]);
    assertFailure(install(directory, declaration(archive)), 'fixture', 'extract');
  });

  it('rejects links that leave the archive root', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, {
      extra: (source) => symlinkSync('../../outside', path.join(source, 'pkg/escape')),
    });
    assertFailure(install(directory, declaration(archive)), 'fixture', 'extract');
    const absolute = fixtureArchive(directory, {
      extra: (source) => symlinkSync('/etc/passwd', path.join(source, 'pkg/escape')),
    });
    assertFailure(install(directory, declaration(absolute)), 'fixture', 'extract');
  });

  it('rejects an unexpected layout', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, { root: 'other' });
    assertFailure(install(directory, declaration(archive)), 'fixture', 'layout');
  });

  it('rejects a missing declared executable', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory), { executables: { python: 'pkg/bin/missing' } });
    assertFailure(install(directory, toolchain), 'fixture', 'layout');
  });

  it('rejects unsafe declared paths', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory), { executables: { python: '../pkg/bin/fixture' } });
    assertFailure(install(directory, toolchain), 'fixture', 'declaration');
  });

  it('rejects a missing license file', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory), { licenseLocations: ['pkg/NOTICE'] });
    assertFailure(install(directory, toolchain), 'fixture', 'notices');
  });

  it('rejects a wrong executable version', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, { version: 'fixture 2.0' });
    const result = install(directory, declaration(archive));
    assertFailure(result, 'fixture', 'version');
    assert.match(result.stderr, /fixture 2\.0/);
  });

  it('rejects an executable that fails to run', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, {
      extra: (source) => {
        writeFileSync(path.join(source, 'pkg/bin/fixture'), '#!/usr/bin/env bash\nexit 3\n');
      },
    });
    assertFailure(install(directory, declaration(archive)), 'fixture', 'version');
  });

  it('verifies additional executables of one tool', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, {
      extra: (source) => script(source, 'pkg/bin/probe', 'probe 9.9'),
    });
    const toolchain = declaration(archive, {
      executables: { python: 'pkg/bin/fixture', probe: 'pkg/bin/probe' },
      versionCheck: {
        executable: 'python',
        arguments: [],
        expectedOutput: 'fixture 1.0',
        additionalChecks: [{ executable: 'probe', arguments: [], expectedOutput: 'probe 1.0' }],
      },
    });
    assertFailure(install(directory, toolchain), 'fixture', 'version');
  });
});

const fakeFfmpeg = `#!/usr/bin/env bash
if [[ "$1 $2" == "-hide_banner -version" ]]; then echo "ffmpeg version fake"; exit 0; fi
echo "ffmpeg $*" >> "$FAKE_LOG"
mode="\${FAKE_MODE:-}"
out="\${@: -1}"
if [[ "$*" == *testsrc2* ]]; then
  [[ "$mode" == create ]] && { echo "create broke" >&2; exit 1; }
  if [[ "$mode" == create-empty ]]; then : > "$out"; else echo data > "$out"; fi
elif [[ "$*" == *"-f null"* ]]; then
  [[ "$mode" == decode ]] && { echo "decode broke" >&2; exit 1; }
else
  [[ "$mode" == extract ]] && { echo "extract broke" >&2; exit 1; }
  if [[ "$mode" == extract-empty ]]; then : > "$out"; else echo data > "$out"; fi
fi
exit 0
`;

const fakeFfprobe = `#!/usr/bin/env bash
if [[ "$1 $2" == "-hide_banner -version" ]]; then echo "ffprobe version fake"; exit 0; fi
echo "ffprobe $*" >> "$FAKE_LOG"
case "\${FAKE_PROBE:-good}" in
  good) echo '{"streams":[{"codec_type":"video"}],"format":{"duration":"1.000000"}}' ;;
  audio) echo '{"streams":[{"codec_type":"audio"}],"format":{"duration":"1.0"}}' ;;
  novideo) echo '{"streams":[],"format":{"duration":"1.0"}}' ;;
  zero) echo '{"streams":[{"codec_type":"video"}],"format":{"duration":"0.000000"}}' ;;
  nan) echo '{"streams":[{"codec_type":"video"}],"format":{"duration":"N/A"}}' ;;
  garbage) echo 'not json' ;;
  empty) ;;
  noduration) echo '{"streams":[{"codec_type":"video"}],"format":{}}' ;;
  infinite) echo '{"streams":[{"codec_type":"video"}],"format":{"duration":"inf"}}' ;;
  negative) echo '{"streams":[{"codec_type":"video"}],"format":{"duration":"-1.0"}}' ;;
  fail) echo 'probe broke' >&2; exit 1 ;;
esac
`;

function mediaInstall(env = {}) {
  const directory = scratch();
  const archive = fixtureArchive(directory, {
    version: 'unused',
    extra: (source) => {
      for (const [name, body] of [['ffmpeg', fakeFfmpeg], ['ffprobe', fakeFfprobe]]) {
        writeFileSync(path.join(source, 'pkg/bin', name), body);
        chmodSync(path.join(source, 'pkg/bin', name), 0o755);
      }
    },
  });
  const toolchain = declaration(archive, {
    id: 'ffmpeg',
    executables: { ffmpeg: 'pkg/bin/ffmpeg', ffprobe: 'pkg/bin/ffprobe' },
    versionCheck: {
      executable: 'ffmpeg',
      arguments: ['-hide_banner', '-version'],
      expectedOutput: 'ffmpeg version fake',
      additionalChecks: [{ executable: 'ffprobe', arguments: ['-hide_banner', '-version'], expectedOutput: 'ffprobe version fake' }],
    },
    environmentVariable: undefined,
  });
  const log = path.join(directory, 'fake.log');
  writeFileSync(log, '');
  const result = install(directory, toolchain, { FAKE_LOG: log, ...env });
  return { ...result, directory, log: readFileSync(log, 'utf8') };
}

describe('synthetic media smoke check', () => {
  it('creates, extracts, probes, and decodes with only the installed tools', () => {
    const result = mediaInstall();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Verified synthetic media smoke check/);
    const lines = result.log.trim().split('\n');
    assert.equal(lines.length, 4);
    assert.match(lines[0], /^ffmpeg .*testsrc2=size=64x64:rate=25:duration=2.* -c:v mpeg4 /);
    assert.match(lines[1], /^ffmpeg .*-c copy /);
    assert.match(lines[2], /^ffprobe .*-select_streams v:0/);
    assert.match(lines[3], /^ffmpeg .*-f null -$/);
    assert.deepEqual(readdirSync(path.join(result.directory, 'install')).filter((name) => name.startsWith('smoke.')), []);
  });

  it('does not run when no ffmpeg and ffprobe pair is installed', () => {
    const directory = scratch();
    const result = install(directory, declaration(fixtureArchive(directory)));
    assert.equal(result.status, 0, result.stderr);
    assert.doesNotMatch(result.stdout, /smoke check/);
  });

  for (const [mode, pattern] of [
    ['create', /creating the synthetic MP4 failed/],
    ['create-empty', /produced no output/],
    ['extract', /extracting a keyframe-compatible interval failed/],
    ['extract-empty', /extraction produced an empty or missing output/],
    ['decode', /decoding the extracted media failed/],
  ]) {
    it(`fails the smoke stage when ${mode} fails`, () => {
      const result = mediaInstall({ FAKE_MODE: mode });
      assertFailure(result, 'ffmpeg', 'smoke');
      assert.match(result.stderr, pattern);
      assert.doesNotMatch(result.stdout, /Verified synthetic media/);
      assert.deepEqual(readdirSync(path.join(result.directory, 'install')).filter((name) => name.startsWith('smoke.')), []);
    });
  }

  for (const probe of ['audio', 'novideo', 'zero', 'nan', 'garbage', 'fail', 'empty', 'noduration', 'infinite', 'negative']) {
    it(`fails the smoke stage for ${probe} probe evidence`, () => {
      const result = mediaInstall({ FAKE_PROBE: probe });
      assertFailure(result, 'ffprobe', 'smoke');
      assert.doesNotMatch(result.stdout, /Verified synthetic media/);
      assert.equal(result.githubPath, '');
      assert.deepEqual(readdirSync(path.join(result.directory, 'install')).filter((name) => name.startsWith('smoke.')), []);
    });
  }

  it('publishes no tool path when the smoke check fails', () => {
    const result = mediaInstall({ FAKE_MODE: 'decode' });
    assertFailure(result, 'ffmpeg', 'smoke');
    assert.equal(result.githubPath, '');
    assert.equal(result.githubEnv, '');
  });
});

describe('FFmpeg trust boundary', () => {
  const ffmpeg = realDeclaration.tools.find((tool) => tool.id === 'ffmpeg');

  it('keeps the mutable release tag distinct from the committed archive hash boundary', () => {
    assert.equal(ffmpeg.releaseImmutable, false);
    assert.equal(ffmpeg.integrityBoundary, 'archive.sha256');
    assert.match(ffmpeg.archive.sha256, /^[0-9a-f]{64}$/);
    assert.equal(ffmpeg.variant, 'lgpl');
  });

  it('claims no codec, model, or production support in the installer', () => {
    assert.doesNotMatch(readFileSync(installer, 'utf8'), /production[- ]ready|production support|codec support/i);
  });
});

// The composite action is checked textually so that no YAML dependency is needed.
const actionText = readFileSync(path.join(here, 'action.yml'), 'utf8');
const stepsText = actionText.slice(actionText.indexOf('\nruns:'));
const steps = stepsText
  .split(/\n(?=    - name: )/)
  .slice(1)
  .map((block) => ({
    name: /^    - name: (.+)$/m.exec(block)[1],
    condition: /^      if: (.+)$/m.exec(block)?.[1],
    block,
  }));
const header = actionText.slice(0, actionText.indexOf('\nruns:'));
const names = (section) => [...section.matchAll(/^ {2}([a-z-]+):$/gm)].map((match) => match[1]);

describe('environment-setup action integration', () => {
  const installerStep = steps.find((step) => step.block.includes('install-analyst-media.sh'));
  const finishStep = steps.find((step) => step.name === 'Install the Analyst Python and media toolchain');

  it('installs the toolchain only when install-tools is true', () => {
    assert.ok(installerStep, 'the installer step exists');
    assert.equal(installerStep.name, 'Start the Analyst Python and media toolchain install');
    assert.equal(installerStep.condition, "inputs.install-tools == 'true'");
    assert.equal(finishStep.condition, "inputs.install-tools == 'true'");
    assert.equal(steps.filter((step) => step.block.includes('install-analyst-media.sh')).length, 1);
    assert.match(installerStep.block, /shell: bash/);
    assert.match(installerStep.block, /INSTALLER="\$GITHUB_ACTION_PATH\/install-analyst-media\.sh" setsid bash -c/);
  });

  it('starts the installer before the other tool installs, waits for it last, and publishes no new action output', () => {
    assert.equal(steps.at(-1), finishStep);
    assert.equal(steps.indexOf(installerStep), steps.findIndex((step) => step.name === 'Report dependency cache restores') + 1);
    const outputs = header.slice(header.indexOf('\noutputs:'));
    assert.deepEqual(names(outputs), [
      'nuget-key', 'npm-key', 'nuget-path', 'npm-path', 'nuget-hit', 'npm-hit', 'npm-tools', 'kiota-version',
    ]);
    assert.doesNotMatch(outputs, /python|ffmpeg|uv/i);
  });

  it('preserves the inputs and their defaults', () => {
    const inputs = header.slice(header.indexOf('\ninputs:'), header.indexOf('\noutputs:'));
    assert.deepEqual(names(inputs), ['workspace', 'install-tools']);
    assert.match(inputs, /workspace:[\s\S]*required: true/);
    assert.match(inputs, /install-tools:[\s\S]*default: "true"/);
  });

  it('keeps every existing executable tool step gated and in place', () => {
    const expected = [
      'Set up .NET',
      'Calculate dependency cache keys',
      'Restore NuGet downloads',
      'Restore npm downloads',
      'Report dependency cache restores',
      'Start the Analyst Python and media toolchain install',
      'Install the Markdown linters',
      'Install the SoftHSM2 PKCS#11 token',
      'Install the Kiota client generator',
      'Install the Playwright Chromium browser',
      'Install the Analyst Python and media toolchain',
    ];
    assert.deepEqual(steps.map((step) => step.name), expected);
    for (const step of steps.filter((candidate) => /^(Install|Start) /.test(candidate.name))) {
      assert.equal(step.condition, "inputs.install-tools == 'true'", step.name);
    }
    assert.match(steps.find((step) => step.name === 'Restore NuGet downloads').block, /actions\/cache\/restore@/);
  });

  // Runs the start and the finish step with a fake installer in place of install-analyst-media.sh.
  function runInstallSteps(installer, { start = true } = {}) {
    const directory = scratch();
    const actionPath = path.join(directory, 'action');
    mkdirSync(actionPath);
    writeFileSync(path.join(actionPath, 'install-analyst-media.sh'), installer);
    const runner = path.join(directory, 'runner');
    mkdirSync(runner);
    const files = { path: path.join(directory, 'github-path'), env: path.join(directory, 'github-env') };
    writeFileSync(files.path, '');
    writeFileSync(files.env, '');
    const body = (step) => step.block.split('      run: |\n')[1].split('\n').map((line) => line.slice(8)).join('\n');
    const env = { ...process.env, RUNNER_TEMP: runner, GITHUB_ACTION_PATH: actionPath, GITHUB_PATH: files.path, GITHUB_ENV: files.env };
    const started = Date.now();
    if (start) assert.equal(spawnSync('bash', ['-c', body(installerStep)], { env, encoding: 'utf8' }).status, 0);
    const startMs = Date.now() - started;
    const finished = spawnSync('bash', ['-c', body(finishStep)], { env, encoding: 'utf8' });
    return { startMs, finished, path: readFileSync(files.path, 'utf8'), env: readFileSync(files.env, 'utf8') };
  }

  it('returns from the start step at once and publishes the installer\'s path and environment when it finishes', () => {
    const result = runInstallSteps('sleep 2\necho "installed"\necho /opt/tools/bin >> "$GITHUB_PATH"\necho SOCALYTICS_PYTHON=/opt/tools/python >> "$GITHUB_ENV"\n');
    assert.ok(result.startMs < 1500, `the start step took ${result.startMs} ms`);
    assert.equal(result.finished.status, 0, result.finished.stdout + result.finished.stderr);
    assert.match(result.finished.stdout, /Waited \d+ s for the background install\.\ninstalled/);
    assert.equal(result.path, '/opt/tools/bin\n');
    assert.equal(result.env, 'SOCALYTICS_PYTHON=/opt/tools/python\n');
  });

  it('fails with the installer\'s exit code and log, and publishes nothing', () => {
    const result = runInstallSteps('echo /opt/tools/bin >> "$GITHUB_PATH"\necho "ERROR: Analyst media toolchain: tool=uv stage=integrity: mismatch" >&2\nexit 7\n');
    assert.equal(result.finished.status, 7);
    assert.match(result.finished.stdout, /stage=integrity: mismatch/);
    assert.equal(result.path, '');
    assert.equal(result.env, '');
  });

  it('fails when the install was never started', () => {
    const result = runInstallSteps('exit 0\n', { start: false });
    assert.equal(result.finished.status, 1);
    assert.match(result.finished.stdout, /stage=start: the background install was not started/);
  });

  it('never consults future project files', () => {
    assert.doesNotMatch(installerStep.block, /src\/analysts|pyproject|uv\.lock|workspace/);
    const installerText = readFileSync(installer, 'utf8');
    assert.doesNotMatch(installerText, /pyproject|uv\.lock|src\/analysts|GITHUB_WORKSPACE/);
  });

  it('adds no dependency-cache path, key input, or save step', () => {
    assert.doesNotMatch(actionText, /actions\/cache\/save|actions\/cache@/);
    assert.doesNotMatch(readFileSync(path.join(here, 'cache-keys.mjs'), 'utf8'), /analyst-media|toolchain/i);
  });
});

describe('real installation', { skip: process.env.ANALYST_MEDIA_REAL_INSTALL !== '1' }, () => {
  it('installs and verifies CPython, uv, and FFmpeg on the supported Linux runner', () => {
    const directory = scratch();
    const githubPath = path.join(directory, 'github-path');
    const githubEnv = path.join(directory, 'github-env');
    writeFileSync(githubPath, '');
    writeFileSync(githubEnv, '');
    const result = spawnSync('bash', [installer], {
      encoding: 'utf8',
      timeout: 900000,
      env: {
        ...process.env,
        ANALYST_MEDIA_INSTALL_ROOT: path.join(directory, 'install'),
        GITHUB_PATH: githubPath,
        GITHUB_ENV: githubEnv,
      },
    });
    assert.equal(result.status, 0, result.stderr);
    const root = path.join(directory, 'install');
    const python = readFileSync(githubEnv, 'utf8').trim();
    assert.equal(python, `SOCALYTICS_PYTHON=${path.join(root, 'python/bin/python3.12')}`);
    assert.equal(readFileSync(githubPath, 'utf8').trim().split('\n').length, 3);
    for (const tool of realDeclaration.tools) {
      assert.ok(existsSync(path.join(root, 'notices', tool.id, 'PROVENANCE.txt')), tool.id);
    }
    assert.match(result.stdout, /Verified synthetic media smoke check/);
    for (const tool of realDeclaration.tools) {
      const checks = [tool.versionCheck, ...(tool.versionCheck.additionalChecks ?? [])];
      for (const check of checks) {
        const executable = path.join(root, tool.executables[check.executable]);
        const output = execFileSync(executable, check.arguments, { encoding: 'utf8' });
        assert.ok(output.startsWith(check.expectedOutput), `${tool.id} ${check.executable}: ${output.split('\n')[0]}`);
      }
    }
    assert.deepEqual(readdirSync(root).filter((name) => name.startsWith('smoke.')), []);
    assert.match(execFileSync(python.split('=')[1], ['--version'], { encoding: 'utf8' }), /^Python 3\.12\.15/);
  });
});
