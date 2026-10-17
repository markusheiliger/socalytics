import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const helper = path.join(here, 'analyst-projects.py');
const temporary = mkdtempSync(path.join(tmpdir(), 'analyst-projects-test-'));
after(() => rmSync(temporary, { recursive: true, force: true }));

let counter = 0;
const repository = () => {
  const directory = path.join(temporary, `repo-${counter++}`);
  mkdirSync(directory, { recursive: true });
  return directory;
};

function manifest({ name, requiresPython = '>=3.11', dependencies = [], sources = {}, extra = '', build = true } = {}) {
  const lines = ['[project]'];
  if (name !== undefined) lines.push(`name = "${name}"`);
  if (requiresPython !== null) lines.push(`requires-python = "${requiresPython}"`);
  lines.push(`dependencies = [${dependencies.map((value) => `"${value}"`).join(', ')}]`);
  if (build) lines.push('', '[build-system]', 'requires = ["hatchling"]', 'build-backend = "hatchling.build"');
  if (Object.keys(sources).length) {
    lines.push('', '[tool.uv.sources]');
    for (const [key, value] of Object.entries(sources)) lines.push(`${key} = { path = "${value}" }`);
  }
  if (extra) lines.push('', extra);
  return `${lines.join('\n')}\n`;
}

function project(root, relative, options = {}, { lock = true } = {}) {
  const directory = path.join(root, relative);
  mkdirSync(directory, { recursive: true });
  writeFileSync(path.join(directory, 'pyproject.toml'), options.raw ?? manifest(options));
  if (lock) writeFileSync(path.join(directory, 'uv.lock'), 'version = 1\n');
}

function discover(root, args = []) {
  const result = spawnSync('python', [helper, 'discover', '--root', root, '--python-version', '3.13.5', ...args], { encoding: 'utf8' });
  return {
    status: result.status,
    stderr: result.stderr,
    projects: result.status === 0 ? JSON.parse(result.stdout).projects : undefined,
  };
}

function rejected(root, pattern) {
  const result = discover(root);
  assert.equal(result.status, 1, result.stderr);
  assert.match(result.stderr, pattern);
  assert.match(result.stderr, /Recommended action: /);
  assert.equal(result.stderr.match(/Recommended action: /g).length, result.stderr.trim().split('\n').length);
  return result;
}

describe('project discovery', () => {
  it('finds nothing when no recognized project exists', () => {
    const root = repository();
    mkdirSync(path.join(root, 'src/analysts/manager'), { recursive: true });
    writeFileSync(path.join(root, 'src/analysts/manager/pyproject.toml'), '[project]\nname = "ignored"\n');
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects, []);
  });

  it('recognizes the SDK and immediate capability roots, normalizing identities', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'SocAlytics_Analyst.SDK' });
    project(root, 'src/analysts/capabilities/segment', { name: 'segment' });
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects.map((entry) => [entry.root, entry.name]), [
      ['src/analysts/capabilities/segment', 'segment'],
      ['src/analysts/sdk', 'socalytics-analyst-sdk'],
    ]);
  });

  it('ignores nested, hidden, dependency-environment, and generated manifests', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/segment/nested', { name: 'nested' });
    project(root, 'src/analysts/capabilities/.venv', { name: 'venv' });
    project(root, 'src/analysts/capabilities/node_modules', { name: 'modules' });
    project(root, 'src/analysts/capabilities/build', { name: 'built' });
    project(root, 'src/analysts/sdk/.venv/lib', { name: 'env' });
    project(root, 'src/analysts/other', { name: 'other' });
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects, []);
  });

  it('does not follow a symbolic-link capability directory', () => {
    const root = repository();
    project(root, 'elsewhere', { name: 'linked' });
    mkdirSync(path.join(root, 'src/analysts/capabilities'), { recursive: true });
    symlinkSync(path.join(root, 'elsewhere'), path.join(root, 'src/analysts/capabilities/linked'));
    assert.deepEqual(discover(root).projects, []);
  });

  it('orders dependencies first and resolves local path dependencies', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['sdk'], sources: { sdk: '../../sdk' } });
    project(root, 'src/analysts/capabilities/b', { name: 'b', dependencies: ['a', 'sdk'], sources: { a: '../a', sdk: '../../sdk' } });
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects.map((entry) => entry.root), [
      'src/analysts/sdk',
      'src/analysts/capabilities/a',
      'src/analysts/capabilities/b',
    ]);
    assert.deepEqual(result.projects[2].dependencies, ['src/analysts/capabilities/a', 'src/analysts/sdk']);
  });
});

describe('project validation', () => {
  it('rejects malformed manifests', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { raw: '[project\nname = ' });
    rejected(root, /project src\/analysts\/sdk: project convention: pyproject\.toml is malformed/);
  });

  it('rejects a missing project table, name, requires-python, and build system', () => {
    for (const [options, pattern] of [
      [{ raw: '[tool.x]\na = 1\n' }, /\[project\] table is missing/],
      [{ raw: '[project]\nrequires-python = ">=3.11"\n[build-system]\nrequires = ["h"]\nbuild-backend = "h"\n' }, /name must be a static/],
      [{ name: 'sdk', requiresPython: null }, /requires-python is missing/],
      [{ name: 'sdk', build: false }, /\[build-system\] with requires and build-backend is missing/],
    ]) {
      const root = repository();
      project(root, 'src/analysts/sdk', options);
      rejected(root, pattern);
    }
  });

  it('rejects an incompatible or unsupported requires-python', () => {
    let root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk', requiresPython: '>=3.14' });
    rejected(root, /runtime compatibility: requires-python >=3\.14 excludes the installed interpreter 3\.13\.5/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk', requiresPython: '<3.13' });
    rejected(root, /runtime compatibility/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk', requiresPython: 'banana' });
    rejected(root, /not a supported specifier/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk', requiresPython: '>=3.11,<3.14,!=3.12.*' });
    assert.equal(discover(root).status, 0);
  });

  it('requires a committed lock', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' }, { lock: false });
    rejected(root, /locked sync: uv\.lock is missing/);
  });

  it('rejects workspace and layout declarations', () => {
    for (const [extra, pattern] of [
      ['[tool.uv.workspace]\nmembers = ["x"]', /\[tool\.uv\.workspace\] is not supported/],
      ['[tool.uv]\npackage = false', /package = false/],
    ]) {
      const root = repository();
      project(root, 'src/analysts/sdk', { name: 'sdk', extra });
      rejected(root, pattern);
    }
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', {
      name: 'a', dependencies: ['sdk'], extra: '[tool.uv.sources]\nsdk = { workspace = true }',
    });
    rejected(root, /uses workspace/);
  });

  it('rejects non-path sources and sources missing from requirements', () => {
    let root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', {
      name: 'a', dependencies: ['x'], extra: '[tool.uv.sources]\nx = { git = "https://example.invalid/x" }',
    });
    rejected(root, /must be a path to a recognized project/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', sources: { sdk: '../../sdk' } });
    rejected(root, /not in the project requirements/);
  });

  it('rejects a missing local project', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['sdk'], sources: { sdk: '../../sdk' } });
    rejected(root, /local dependency 'sdk' points to src\/analysts\/sdk, which has no project/);
  });

  it('rejects local dependencies outside the repository and the permitted ownership roots', () => {
    let root = repository();
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['x'], sources: { x: '../../../../../x' } });
    rejected(root, /points outside the repository/);
    root = repository();
    project(root, 'src/analysts/manager', { name: 'manager' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['manager'], sources: { manager: '../../manager' } });
    rejected(root, /outside the permitted ownership roots/);
    root = repository();
    project(root, 'src/analysts/capabilities/b/deep', { name: 'deep' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['deep'], sources: { deep: '../b/deep' } });
    rejected(root, /outside the permitted ownership roots/);
  });

  it('rejects a dependency whose name differs from its project and one that is itself invalid', () => {
    let root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['other'], sources: { other: '../../sdk' } });
    rejected(root, /points to project 'sdk'/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' }, { lock: false });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['sdk'], sources: { sdk: '../../sdk' } });
    const result = rejected(root, /not a valid recognized project/);
    assert.match(result.stderr, /uv\.lock is missing/);
  });

  it('rejects duplicate identities', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'Shared_Name' });
    project(root, 'src/analysts/capabilities/a', { name: 'shared-name' });
    rejected(root, /project name 'shared-name' duplicates src\/analysts\/capabilities\/a/);
  });

  it('rejects dependency cycles once', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['b'], sources: { b: '../b' } });
    project(root, 'src/analysts/capabilities/b', { name: 'b', dependencies: ['a'], sources: { a: '../a' } });
    const result = rejected(root, /local dependencies form a cycle: .*capabilities\/a -> .*capabilities\/b -> .*capabilities\/a/);
    assert.equal(result.stderr.match(/cycle/g).length, 1);
  });
});

describe('verification selection', () => {
  const sdk = 'src/analysts/sdk';
  const a = 'src/analysts/capabilities/a';
  const b = 'src/analysts/capabilities/b';
  const c = 'src/analysts/capabilities/c';

  function graph() {
    const root = repository();
    project(root, sdk, { name: 'sdk' });
    project(root, a, { name: 'a', dependencies: ['sdk'], sources: { sdk: '../../sdk' } });
    project(root, b, { name: 'b', dependencies: ['a'], sources: { a: '../a' } });
    project(root, c, { name: 'c' });
    return root;
  }

  function select(root, mode, changed = []) {
    const list = path.join(root, 'changed.txt');
    writeFileSync(list, changed.join('\n'));
    const result = spawnSync('python', [helper, 'select', '--root', root, '--python-version', '3.13.5', '--mode', mode, '--changed-files', list], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    const parsed = JSON.parse(result.stdout);
    return { selected: parsed.projects.map((entry) => [entry.root, entry.reason]), covered: parsed.covered };
  }

  it('selects an own-source change and its transitive consumers in dependency order', () => {
    const root = graph();
    assert.deepEqual(select(root, 'task', [`${sdk}/src/x.py`]).selected, [
      [sdk, 'direct change'], [a, 'local-dependency change'], [b, 'local-dependency change'],
    ]);
    assert.deepEqual(select(root, 'phase', [`${a}/tests/test_a.py`]).selected, [
      [a, 'direct change'], [b, 'local-dependency change'],
    ]);
    assert.deepEqual(select(root, 'task', [`${c}/pyproject.toml`]).selected, [[c, 'direct change']]);
  });

  it('selects each project once when several changes overlap', () => {
    const root = graph();
    const { selected } = select(root, 'task', [`${sdk}/a.py`, `${a}/b.py`, `${b}/c.py`, `${a}/d.py`]);
    assert.deepEqual(selected, [[sdk, 'direct change'], [a, 'direct change'], [b, 'direct change']]);
  });

  it('selects every project for shared contracts, either environment folder, and finalize', () => {
    const root = graph();
    const every = [sdk, a, b, c].sort((left, right) => left.localeCompare(right));
    for (const [mode, changed, reason] of [
      ['task', ['contracts/analysis/run/schema.json'], 'shared contract change'],
      ['phase', ['contracts/common/error.json'], 'shared contract change'],
      ['task', ['.github/actions/environment-setup/action.yml'], 'environment change'],
      ['task', ['.github/actions/environment-verify/analyst-projects.py'], 'environment change'],
      ['finalize', [], 'finalize'],
      ['finalize', ['README.md'], 'finalize'],
    ]) {
      const { selected } = select(root, mode, changed);
      assert.deepEqual(selected.map((entry) => entry[0]).sort(), [...every].sort(), `${mode} ${changed}`);
      assert.ok(selected.every((entry) => entry[1] === reason), `${mode} ${changed}`);
    }
    const ordered = select(root, 'finalize').selected.map((entry) => entry[0]);
    assert.ok(ordered.indexOf(sdk) < ordered.indexOf(a) && ordered.indexOf(a) < ordered.indexOf(b));
  });

  it('selects nothing for unrelated changes, other contracts, and the Manager', () => {
    const root = graph();
    for (const changed of [
      ['README.md'], ['contracts/analyst-manager/x.json'], ['contracts/recordings/x.json'], ['src/analysts/manager/a.cs'],
      ['src/platform/x.cs'], ['.github/actions/environment-other/x'], [],
    ]) {
      assert.deepEqual(select(root, 'task', changed).selected, [], changed.join());
    }
  });

  it('matches by complete path components, never by prefix', () => {
    const root = graph();
    for (const changed of [
      ['src/analysts/sdk-extra/x.py'], ['src/analysts/sdkx'], ['src/analysts/capabilities/a2/x.py'], ['src/analysts/capabilities/ab'],
      ['contracts/analysis-extra/x.json'], ['contracts/commons/x.json'], ['.github/actions/environment-setup-more/x'],
    ]) {
      assert.deepEqual(select(root, 'task', changed).selected, [], changed.join());
    }
    assert.deepEqual(select(root, 'task', [`./${a}//x.py`]).selected.map((entry) => entry[0]), [a, b]);
  });

  it('reports existing project roots as covered and nothing when none exists', () => {
    const root = graph();
    assert.deepEqual(select(root, 'task', ['README.md']).covered.sort(), [a, b, c, sdk].sort());
    const empty = repository();
    assert.deepEqual(select(empty, 'finalize'), { selected: [], covered: [] });
    assert.deepEqual(select(empty, 'task', ['src/analysts/sdk/x.py', 'contracts/common/x.json']), { selected: [], covered: [] });
  });

  it('leaves a deleted manifest and unowned source uncovered and unselected', () => {
    const root = graph();
    unlinkSync(path.join(root, c, 'pyproject.toml'));
    const result = select(root, 'task', [`${c}/pyproject.toml`, 'src/analysts/other/x.py', 'src/analysts/capabilities/stray.py']);
    assert.deepEqual(result.selected, []);
    assert.ok(!result.covered.includes(c));
    assert.ok(!result.selected.some((entry) => entry[0] === c));
    assert.ok(!select(root, 'finalize').covered.includes(c));
  });

  it('still fails on an invalid existing project regardless of the changes', () => {
    const root = graph();
    project(root, 'src/analysts/capabilities/bad', { raw: '[project' });
    const result = spawnSync('python', [helper, 'select', '--root', root, '--python-version', '3.13.5', '--mode', 'task'], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Recommended action: /);
  });
});

describe('verification execution', () => {
  const actionText = readFileSync(path.join(here, 'action.yml'), 'utf8');
  const script = actionText.split('      run: |\n')[1].split('\n').map((line) => line.slice(8)).join('\n');
  const python = spawnSync('python3', ['-c', 'import sys;print(sys.executable)'], { encoding: 'utf8' }).stdout.trim();
  const sdkRoot = 'src/analysts/sdk';
  const markdownStub = path.join(temporary, 'markdown-stub.mjs');
  writeFileSync(markdownStub, '');

  // A stub uv records each invocation and fails the stage named in FAIL_STAGE.
  function stubBin(extra = '') {
    const bin = path.join(temporary, `bin-${counter++}`);
    mkdirSync(bin, { recursive: true });
    writeFileSync(path.join(bin, 'uv'), `#!/bin/bash
echo "$PWD|$UV_PYTHON_DOWNLOADS|$UV_PYTHON|$*" >> "$UV_CALLS"
if [ -n "$FAIL_STAGE" ] && [[ "$*" == *"$FAIL_STAGE"* ]]; then echo "stub failure"; exit 3; fi
${extra}
exit 0
`, { mode: 0o755 });
    return bin;
  }

  function run(root, { mode = 'finalize', changed = [], env = {}, withPython = true, bin = stubBin() } = {}) {
    const dir = path.join(temporary, `run-${counter++}`);
    mkdirSync(dir, { recursive: true });
    const files = {
      changed: path.join(dir, 'changed.txt'), log: path.join(dir, 'log.txt'), output: path.join(dir, 'output.txt'),
      summary: path.join(dir, 'summary.md'), calls: path.join(dir, 'calls.txt'),
    };
    writeFileSync(files.changed, changed.join('\n'));
    writeFileSync(files.calls, '');
    writeFileSync(files.output, '');
    const result = spawnSync('bash', ['-c', script], {
      encoding: 'utf8',
      env: {
        PATH: `${bin}:${process.env.PATH}`, HOME: process.env.HOME,
        WORKSPACE: root, MODE: mode, CHANGED_FILES: files.changed, LOG_FILE: files.log,
        MARKDOWN_CHECK: markdownStub, ANALYST_PROJECTS: helper,
        GITHUB_STEP_SUMMARY: files.summary, GITHUB_OUTPUT: files.output, UV_CALLS: files.calls,
        ...(withPython ? { SOCALYTICS_PYTHON: python } : {}), ...env,
      },
    });
    const outputs = Object.fromEntries(readFileSync(files.output, 'utf8').split('\n').filter(Boolean).map((line) => line.split(/=(.*)/s).slice(0, 2)));
    return {
      status: result.status, outputs, log: readFileSync(files.log, 'utf8'),
      calls: readFileSync(files.calls, 'utf8').split('\n').filter(Boolean).map((line) => line.split('|')),
    };
  }

  const stages = [
    'sync --locked --all-groups --no-install-local --no-build',
    'sync --locked --all-groups --no-build-isolation',
    'build --wheel --no-build-isolation --python .venv/bin/python',
    'run --no-sync pytest',
  ];

  const sdkRepository = () => {
    const root = repository();
    project(root, sdkRoot, { name: 'sdk' });
    return root;
  };

  it('runs the four exact stages in order with downloads disabled and the installed interpreter', () => {
    const root = sdkRepository();
    const result = run(root);
    assert.equal(result.status, 0, result.log);
    assert.deepEqual(result.calls.map((call) => call[3]), stages);
    for (const call of result.calls) {
      assert.ok(call[0].endsWith(sdkRoot));
      assert.equal(call[1], 'never');
      assert.equal(call[2], python);
    }
    assert.equal(result.outputs.checks, 'Analyst Python projects,Markdown check');
  });

  for (const [index, stage] of ['missing or stale lock', 'incompatible build/external wheel', 'failed build', 'failing, missing, or empty tests'].entries()) {
    it(`fails and stops after the stage for ${stage}`, () => {
      const root = sdkRepository();
      const result = run(root, { env: { FAIL_STAGE: stages[index] } });
      assert.equal(result.status, 1);
      assert.deepEqual(result.calls.map((call) => call[3]), stages.slice(0, index + 1));
      assert.match(result.log, /Analyst Python stage failed: /);
      assert.equal(result.outputs.checks, 'Analyst Python projects');
    });

    it(`names the project, stage, command, and one recommendation for ${stage}`, () => {
      const root = sdkRepository();
      const result = run(root, { env: { FAIL_STAGE: stages[index] } });
      const failure = result.log.split('\n').filter((line) => line.startsWith('Analyst Python stage failed: '));
      assert.equal(failure.length, 1);
      assert.match(failure[0], new RegExp(`in ${sdkRoot} \\(command: uv ${stages[index].replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\)\\. Recommended action: `));
      assert.equal(failure[0].match(/Recommended action:/g).length, 1);
      assert.match(failure[0], index === 0 ? /commit them/ : /reproduce this command in the named project/);
      assert.doesNotMatch(failure[0], /--no-locked|--frozen|--upgrade|skip|ignore|disable|merge/i);
    });
  }

  it('orders dependencies first and stops at the first failing project', () => {
    const root = repository();
    project(root, sdkRoot, { name: 'sdk' });
    project(root, 'src/analysts/capabilities/segment', {
      name: 'segment', dependencies: ['sdk'], sources: { sdk: '../../sdk' },
    });
    const ok = run(root);
    assert.equal(ok.status, 0, ok.log);
    assert.deepEqual([...new Set(ok.calls.map((call) => path.relative(root, call[0])))], [sdkRoot, 'src/analysts/capabilities/segment']);
    const failed = run(root, { env: { FAIL_STAGE: stages[0] } });
    assert.equal(failed.status, 1);
    assert.equal(failed.calls.length, 1);
  });

  it('runs nothing when no project exists or the change is unrelated', () => {
    const empty = run(repository(), { mode: 'task', changed: ['src/platform/x.cs'] });
    assert.equal(empty.status, 0, empty.log);
    assert.equal(empty.calls.length, 0);
    assert.equal(empty.outputs.checks, '');
    const unrelated = run(sdkRepository(), { mode: 'task', changed: ['src/platform/x.cs'] });
    assert.equal(unrelated.calls.length, 0);
    assert.equal(unrelated.outputs.checks, '');
  });

  it('selects a project in task mode for its own source and covers its root', () => {
    const root = sdkRepository();
    const result = run(root, { mode: 'task', changed: [`${sdkRoot}/src/x.py`, 'src/analysts/sdk-extra/y.py'] });
    assert.equal(result.status, 0, result.log);
    assert.equal(result.calls.length, 4);
    assert.equal(result.outputs.uncovered, 'src/analysts/sdk-extra/y.py');
  });

  it('fails explicitly without the installed interpreter or uv', () => {
    const root = sdkRepository();
    const result = run(root, { withPython: false });
    assert.equal(result.status, 1);
    assert.match(result.log, /Recommended action: run environment-setup/);
    assert.equal(result.calls.length, 0);
  });

  it('gives one recommendation when discovery itself cannot run', () => {
    const root = sdkRepository();
    const result = run(root, { env: { ANALYST_PROJECTS: path.join(temporary, 'missing-helper.py') } });
    assert.equal(result.status, 1);
    assert.match(result.log, /discovery failed/);
    assert.equal((result.log.match(/Recommended action:/g) ?? []).length, 1);
    assert.equal(result.calls.length, 0);
  });

  it('fails for an invalid recognized project and reports the helper recommendation', () => {
    const root = repository();
    project(root, sdkRoot, { raw: '[project' });
    const result = run(root, { mode: 'task', changed: ['README.md'] });
    assert.equal(result.status, 1);
    assert.match(result.log, /Recommended action: /);
    assert.equal(result.calls.length, 0);
    assert.equal(result.outputs.checks, 'Analyst Python projects');
  });

  const uvReal = spawnSync('uv', ['--version']).status === 0;
  it('passes every stage for a real isolated package fixture without changing declarations', { skip: !uvReal }, () => {
    const root = repository();
    const directory = path.join(root, sdkRoot);
    mkdirSync(path.join(directory, 'src/demo_sdk'), { recursive: true });
    mkdirSync(path.join(directory, 'tests'), { recursive: true });
    writeFileSync(path.join(directory, 'pyproject.toml'), `[project]
name = "demo-sdk"
version = "0.1.0"
requires-python = ">=3.11"
dependencies = []

[build-system]
requires = ["setuptools>=68"]
build-backend = "setuptools.build_meta"

[tool.setuptools.packages.find]
where = ["src"]

[dependency-groups]
dev = ["pytest>=8", "setuptools>=68"]
`);
    writeFileSync(path.join(directory, 'src/demo_sdk/__init__.py'), 'VALUE = 1\n');
    writeFileSync(path.join(directory, 'tests/test_demo.py'), 'import demo_sdk\n\n\ndef test_value():\n    assert demo_sdk.VALUE == 1\n');
    const locked = spawnSync('uv', ['lock'], { cwd: directory, encoding: 'utf8', env: { ...process.env, UV_PYTHON: python } });
    if (locked.status !== 0) return; // package index unreachable: the real fixture cannot be locked
    const before = readFileSync(path.join(directory, 'pyproject.toml'), 'utf8') + readFileSync(path.join(directory, 'uv.lock'), 'utf8');
    const bin = path.join(temporary, `bin-${counter++}`);
    mkdirSync(bin);
    symlinkSync(spawnSync('which', ['uv'], { encoding: 'utf8' }).stdout.trim(), path.join(bin, 'uv'));
    const passed = run(root, { bin });
    assert.equal(passed.status, 0, passed.log);
    assert.equal(before, readFileSync(path.join(directory, 'pyproject.toml'), 'utf8') + readFileSync(path.join(directory, 'uv.lock'), 'utf8'));
    writeFileSync(path.join(directory, 'tests/test_demo.py'), 'def test_value():\n    assert False\n');
    assert.equal(run(root, { bin }).status, 1);
    writeFileSync(path.join(directory, 'tests/test_demo.py'), '');
    assert.equal(run(root, { bin }).status, 1);
    writeFileSync(path.join(directory, 'pyproject.toml'), before.split('[dependency-groups]')[0].replace('version = "0.1.0"', 'version = "0.2.0"') + '[dependency-groups]\ndev = ["pytest>=8", "setuptools>=68", "iniconfig"]\n');
    const stale = run(root, { bin });
    assert.equal(stale.status, 1);
    assert.match(stale.log, /Analyst Python stage failed: bootstrap sync/);
  });
});
