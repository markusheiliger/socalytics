import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
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

function manifest({
  name, requiresPython = '>=3.11', dependencies = [], optionalDependencies = {}, dependencyGroups = {},
  sources = {}, extra = '', build = true,
} = {}) {
  const lines = ['[project]'];
  if (name !== undefined) lines.push(`name = "${name}"`);
  if (requiresPython !== null) lines.push(`requires-python = "${requiresPython}"`);
  lines.push(`dependencies = [${dependencies.map((value) => `"${value}"`).join(', ')}]`);
  if (Object.keys(optionalDependencies).length) {
    lines.push('', '[project.optional-dependencies]');
    for (const [key, values] of Object.entries(optionalDependencies)) lines.push(`${key} = [${values.map((value) => `"${value}"`).join(', ')}]`);
  }
  if (Object.keys(dependencyGroups).length) {
    lines.push('', '[dependency-groups]');
    for (const [key, values] of Object.entries(dependencyGroups)) lines.push(`${key} = [${values.map((value) => `"${value}"`).join(', ')}]`);
  }
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

  it('ignores hidden, dependency-environment, generated, and Manager manifests', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/.venv', { name: 'venv' });
    project(root, 'src/analysts/capabilities/node_modules', { name: 'modules' });
    project(root, 'src/analysts/capabilities/build', { name: 'built' });
    project(root, 'src/analysts/sdk/.venv/lib', { name: 'env' });
    project(root, 'src/analysts/other/__pycache__', { name: 'cache' });
    project(root, 'src/analysts/other/.hidden', { name: 'hidden' });
    project(root, 'src/analysts/manager', { name: 'manager' });
    project(root, 'src/analysts/manager/tools/helper', { name: 'helper' });
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects, []);
  });

  it('reports a nested capability manifest as an unsupported layout', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/segment/nested', { name: 'nested' });
    rejected(root, /project src\/analysts\/capabilities\/segment\/nested\/pyproject\.toml: project convention: unsupported layout/);
  });

  it('reports an unrecognized manifest under src/analysts and still validates recognized projects', () => {
    const root = repository();
    project(root, 'src/analysts/other', { name: 'other' });
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    const result = rejected(root, /project src\/analysts\/other\/pyproject\.toml: project convention: unsupported layout/);
    assert.equal(result.stderr.trim().split('\n').length, 1);
  });

  it('reports a manifest at the Analysts area root', () => {
    const root = repository();
    project(root, 'src/analysts', { name: 'area' });
    rejected(root, /project src\/analysts\/pyproject\.toml: project convention: unsupported layout/);
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
        MARKDOWN_CHECK: markdownStub, ANALYST_PROJECTS: helper, VERIFICATION_PLAN: path.join(here, 'verification-plan.mjs'),
        GITHUB_STEP_SUMMARY: files.summary, GITHUB_OUTPUT: files.output, UV_CALLS: files.calls,
        ...(withPython ? { SOCALYTICS_PYTHON: python } : {}), ...env,
      },
    });
    const outputs = Object.fromEntries(readFileSync(files.output, 'utf8').split('\n').filter(Boolean).map((line) => line.split(/=(.*)/s).slice(0, 2)));
    return {
      status: result.status, outputs, log: readFileSync(files.log, 'utf8'),
      summary: existsSync(files.summary) ? readFileSync(files.summary, 'utf8') : '',
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

  it('checks only the changed Markdown files in task mode and the whole repository otherwise', () => {
    const record = path.join(temporary, `markdown-${counter++}.txt`);
    const stub = path.join(temporary, `markdown-stub-${counter++}.mjs`);
    writeFileSync(stub, `import { appendFileSync } from 'node:fs';\nappendFileSync(${JSON.stringify(record)}, process.argv.slice(2).join(' ') + '\\n');\n`);
    const argsOf = (mode) => {
      writeFileSync(record, '');
      const result = run(repository(), { mode, changed: ['docs/guide.md'], env: { MARKDOWN_CHECK: stub } });
      assert.equal(result.status, 0, result.log);
      assert.match(result.outputs.checks, /Markdown check/);
      return readFileSync(record, 'utf8').trim();
    };
    assert.match(argsOf('task'), /^--files \S+changed\.txt$/);
    assert.equal(argsOf('phase'), '');
    assert.equal(argsOf('finalize'), '');
  });

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

  describe('cross-check regression', () => {
    const platformSolution = 'src/platform/SocAlytics.Platform.slnx';
    const platformTests = [
      'src/platform/Tests/SocAlytics.Platform.Architecture.Tests/SocAlytics.Platform.Architecture.Tests.csproj',
      'src/platform/Tests/SocAlytics.Platform.Integration.Tests/SocAlytics.Platform.Integration.Tests.csproj',
    ];
    // Components run from their own folder, so their commands name paths relative to it.
    const inPlatform = (file) => file.replace(/^src\/platform\//, '');
    const managerSolution = 'src/analysts/manager/SocAlytics.Analysts.Manager.slnx';
    const managerTest = 'src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/SocAlytics.Analysts.Manager.Tests.csproj';
    // The repository's own verification declarations, so the fixtures keep the real triggers, coverage, and pins.
    const declaration = (root) => readFileSync(path.join(here, '..', '..', '..', root, 'verification.json'), 'utf8');

    // Stubs for every external command the checks run, so no database, package index, or runner is contacted.
    function toolBin() {
      const bin = stubBin();
      for (const tool of ['dotnet', 'npm']) {
        writeFileSync(path.join(bin, tool), `#!/bin/bash
echo "$PWD|${tool}|$*" >> "$UV_CALLS"
echo "start ${tool} $*" >> "$UV_CALLS.events"
sleep 0.02
echo "end ${tool} $*" >> "$UV_CALLS.events"
if [ -n "$FAIL_TOOL" ] && [[ "$*" == *"$FAIL_TOOL"* ]]; then echo "stub failure"; exit 4; fi
exit 0
`, { mode: 0o755 });
      }
      return bin;
    }

    function touch(root, relative, content = '') {
      mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
      writeFileSync(path.join(root, relative), content);
    }

    function fullRepository({ platform = true, manager = true, web = true, python = true } = {}) {
      const root = repository();
      if (platform) {
        touch(root, platformSolution);
        for (const test of platformTests) touch(root, test);
        touch(root, 'src/platform/global.json', '{"sdk":{"version":"10.0.100"}}');
        touch(root, 'src/platform/verification.json', declaration('src/platform'));
      }
      if (manager) {
        touch(root, managerSolution);
        touch(root, managerTest);
        touch(root, 'src/analysts/manager/global.json', '{"sdk":{"version":"10.0.100"}}');
        touch(root, 'src/analysts/manager/verification.json', declaration('src/analysts/manager'));
      }
      if (web) {
        touch(root, 'src/clients/web/package.json', '{"scripts":{"build":"b","test":"t","test:e2e":"e"},"devDependencies":{"@playwright/test":"1.63.0"}}');
        touch(root, 'src/clients/web/node_modules/@playwright/test/package.json', '{"version":"1.63.0"}');
        touch(root, 'src/clients/web/verification.json', declaration('src/clients/web'));
      }
      if (python) project(root, sdkRoot, { name: 'sdk' });
      return root;
    }

    const names = {
      platform: 'platform build and tests',
      manager: 'Analyst Manager build and tests',
      web: 'Web UI build and tests',
      python: 'Analyst Python projects',
      markdown: 'Markdown check',
    };
    // The trx logger arguments name a temporary folder, so they are compared without it.
    const commands = (result, tool) => result.calls.filter((call) => call[1] === tool)
      .map((call) => call.slice(2).join('|').replace(/ --logger trx --results-directory \S+/, ''));

    it('keeps the established checks, order, and sequential platform tests in finalize mode', () => {
      const root = fullRepository();
      const result = run(root, { bin: toolBin() });
      assert.equal(result.status, 0, result.log);
      assert.equal(result.outputs.checks, Object.values(names).join(','));
      assert.deepEqual(commands(result, 'dotnet'), [
        'restore SocAlytics.Platform.slnx',
        'build SocAlytics.Platform.slnx --no-restore',
        ...platformTests.map((test) => `test ${inPlatform(test)} --no-build`),
        'restore SocAlytics.Analysts.Manager.slnx',
        'build SocAlytics.Analysts.Manager.slnx --no-restore',
        `test ${managerTest.replace('src/analysts/manager/', '')} --no-build`,
      ]);
      assert.deepEqual(commands(result, 'npm'), ['ci', 'run build', 'test', 'run test:e2e -- --retries=1']);
      assert.equal(result.outputs.uncovered, '');
      assert.equal(result.outputs.flaky, '');
      assert.ok(result.calls.filter((call) => call[1] === 'dotnet' && call[2].startsWith('test ')).every((call) => / --logger trx --results-directory \S+$/.test(call[2])));
    });

    it('never overlaps the platform test projects', () => {
      const root = fullRepository({ manager: false, web: false, python: false });
      const dir = path.join(temporary, `events-${counter++}`);
      mkdirSync(dir);
      const calls = path.join(dir, 'calls.txt');
      const result = run(root, { bin: toolBin(), env: { UV_CALLS: calls } });
      assert.equal(result.status, 0, result.log);
      const events = readFileSync(`${calls}.events`, 'utf8').split('\n').filter(Boolean).map((line) => line.split(' ')[0]);
      assert.ok(events.length > 0);
      events.forEach((event, index) => assert.equal(event, index % 2 === 0 ? 'start' : 'end'));
    });

    it('selects each check only for its own trigger in task mode', () => {
      const root = fullRepository();
      const bin = toolBin();
      const web = run(root, { mode: 'task', changed: ['src/clients/web/src/a.ts'], bin });
      assert.equal(web.outputs.checks, names.web.replace('tests', 'affected tests'));
      assert.equal(web.outputs.uncovered, '');
      const api = run(root, { mode: 'task', changed: ['src/platform/SocAlytics.Platform.Api/a.cs'], bin });
      assert.equal(api.outputs.checks, [names.platform.replace('tests', 'affected tests'), names.web.replace('tests', 'affected tests')].join(','));
      const fixtures = run(root, { mode: 'task', changed: ['contracts/analyst-manager/queue-depth/a.json'], bin });
      assert.equal(fixtures.outputs.checks, [names.platform.replace('tests', 'affected tests'), names.manager.replace('tests', 'affected tests')].join(','));
      assert.equal(fixtures.outputs.uncovered, '');
      const docs = run(root, { mode: 'task', changed: ['docs/a.md'], bin });
      assert.equal(docs.outputs.checks, names.markdown);
    });

    it('fails on a failing test project, finishes the remaining projects, and stops the later checks', () => {
      const root = fullRepository();
      const result = run(root, {
        bin: toolBin(), env: { FAIL_TOOL: 'Architecture.Tests.csproj' }, changed: ['src/platform/a.cs', 'src/analysts/other/x.py'],
      });
      assert.equal(result.status, 1);
      assert.equal(result.outputs.checks, names.platform);
      assert.equal(commands(result, 'dotnet').filter((command) => command.startsWith('test ')).length, 2);
      assert.equal(commands(result, 'npm').length, 0);
      assert.match(result.log, /stub failure/);
      assert.match(result.log, /exit: 4; command: dotnet test/);
      assert.equal(result.outputs.uncovered, 'src/analysts/other/x.py');
    });

    describe('reruns of failed tests', () => {
      const xml = (text) => text.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
      // A TRX log with the failed tests (fully qualified names; a display name may add arguments) and passed ones.
      function trx(failed, passed = ['Ns.Passing.Works']) {
        const tests = [...failed.map((name) => [name, 'Failed']), ...passed.map((name) => [name, 'Passed'])]
          .map(([name, outcome], index) => ({ id: `0000-${index}`, name, outcome, type: name.slice(0, name.lastIndexOf('.')), method: name.slice(name.lastIndexOf('.') + 1) }));
        return [
          '<?xml version="1.0" encoding="utf-8"?>',
          '<TestRun id="r" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">',
          '  <Results>',
          ...tests.map((test) => `    <UnitTestResult executionId="e${test.id}" testId="${test.id}" testName="${xml(test.name)}(value: 1)" computerName="c" outcome="${test.outcome}" />`),
          '  </Results>',
          '  <TestDefinitions>',
          ...tests.map((test) => `    <UnitTest name="${xml(test.name)}" storage="x.dll" id="${test.id}">\n      <Execution id="e${test.id}" />\n      <TestMethod codeBase="x.dll" adapterTypeName="executor://xunit/VsTestRunner3/netcore/" className="${xml(test.type)}, Tests" name="${xml(test.method)}" />\n    </UnitTest>`),
          '  </TestDefinitions>',
          '</TestRun>',
        ].join('\n');
      }

      // dotnet writes `trx` into its results directory and fails for projects matching TRX_FAIL; a rerun (a
      // FullyQualifiedName= filter) fails only with RERUN_FAIL. npm prints PLAYWRIGHT_OUTPUT for the browser tests.
      function rerunBin({ failed = [], passed } = {}) {
        const bin = toolBin();
        const log = path.join(bin, 'result.trx');
        writeFileSync(log, trx(failed, passed));
        writeFileSync(path.join(bin, 'dotnet'), `#!/bin/bash
echo "$PWD|dotnet|$*" >> "$UV_CALLS"
args=("$@")
dir=""
filter=""
for ((i = 0; i < \${#args[@]}; i++)); do
  case "\${args[$i]}" in
    --results-directory) dir="\${args[$((i + 1))]}" ;;
    --filter) filter="\${args[$((i + 1))]}" ;;
  esac
done
if [ "$1" = test ] && [[ "$filter" == FullyQualifiedName=* ]]; then
  [ -n "$RERUN_FAIL" ] && { echo "rerun failure"; exit 5; }
  exit 0
fi
if [ "$1" = test ] && [ -n "$TRX_FAIL" ] && [[ "$*" == *"$TRX_FAIL"* ]]; then
  mkdir -p "$dir/nested"
  cp ${JSON.stringify(log)} "$dir/nested/result.trx"
  echo "stub failure"
  exit 4
fi
exit 0
`, { mode: 0o755 });
        writeFileSync(path.join(bin, 'npm'), `#!/bin/bash
echo "$PWD|npm|$*" >> "$UV_CALLS"
if [ "$*" = "run test:e2e -- --retries=1" ] && [ -n "$PLAYWRIGHT_OUTPUT" ]; then printf '%b' "$PLAYWRIGHT_OUTPUT"; fi
exit 0
`, { mode: 0o755 });
        return bin;
      }
      const integration = inPlatform(platformTests[1]);
      const reruns = (result) => commands(result, 'dotnet').filter((command) => command.includes('--filter FullyQualifiedName='));

      it('reruns only the failed tests once, passes, and reports them as flaky', () => {
        const failed = ['Ns.Database.Connects', 'Ns.Outer+Inner.Retries'];
        const result = run(fullRepository({ manager: false, web: false, python: false }), {
          mode: 'phase', changed: ['src/platform/a.cs'], bin: rerunBin({ failed }), env: { TRX_FAIL: 'Integration.Tests.csproj' },
        });
        assert.equal(result.status, 0, result.log);
        assert.deepEqual(reruns(result), [`test ${integration} --no-build --filter FullyQualifiedName=Ns.Database.Connects|FullyQualifiedName=Ns.Outer+Inner.Retries`]);
        assert.equal(result.outputs.flaky, failed.join(','));
        assert.match(result.log, /Rerunning the 2 failed test\(s\) of \S+Integration\.Tests\.csproj once/);
        assert.match(result.log, /Flaky tests \(passed on rerun\): Ns\.Database\.Connects,Ns\.Outer\+Inner\.Retries/);
        assert.match(result.summary, /### Flaky tests \(passed on rerun\)\n- `Ns\.Database\.Connects`\n- `Ns\.Outer\+Inner\.Retries`/);
      });

      it('escapes filter characters and keeps the task filter on the first run', () => {
        const root = fullRepository({ manager: false, web: false, python: false });
        const testFile = 'src/platform/Tests/SocAlytics.Platform.Integration.Tests/StoreTests.cs';
        touch(root, testFile, 'public sealed class StoreTests\n{\n}\n');
        const result = run(root, {
          mode: 'task', changed: [testFile], bin: rerunBin({ failed: ['Ns.StoreTests.Is=Equal'] }), env: { TRX_FAIL: 'Integration.Tests.csproj' },
        });
        assert.equal(result.status, 0, result.log);
        assert.ok(commands(result, 'dotnet').includes(`test ${integration} --no-build --filter FullyQualifiedName~StoreTests`));
        assert.deepEqual(reruns(result), [`test ${integration} --no-build --filter FullyQualifiedName=Ns.StoreTests.Is\\=Equal`]);
        assert.equal(result.outputs.flaky, 'Ns.StoreTests.Is=Equal');
      });

      it('does not rerun more than 10 failed tests', () => {
        const failed = Array.from({ length: 11 }, (_, index) => `Ns.Many.Test${index}`);
        const result = run(fullRepository({ manager: false, web: false, python: false }), {
          mode: 'phase', changed: ['src/platform/a.cs'], bin: rerunBin({ failed }), env: { TRX_FAIL: 'Integration.Tests.csproj' },
        });
        assert.equal(result.status, 1);
        assert.deepEqual(reruns(result), []);
        assert.match(result.log, /No rerun of \S+Integration\.Tests\.csproj: 11 tests failed \(more than 10\)/);
        assert.equal(result.outputs.flaky, '');
      });

      it('fails when the rerun fails again, and still runs the later projects', () => {
        const result = run(fullRepository({ manager: false, web: false, python: false }), {
          mode: 'phase', changed: ['src/platform/a.cs'], bin: rerunBin({ failed: ['Ns.Database.Connects'] }), env: { TRX_FAIL: 'Architecture.Tests.csproj', RERUN_FAIL: '1' },
        });
        assert.equal(result.status, 1);
        assert.equal(result.outputs.checks, names.platform);
        assert.deepEqual(reruns(result), [`test ${inPlatform(platformTests[0])} --no-build --filter FullyQualifiedName=Ns.Database.Connects`]);
        assert.ok(commands(result, 'dotnet').includes(`test ${integration} --no-build`));
        assert.match(result.log, /rerun failure/);
        assert.equal(result.outputs.flaky, '');
        assert.doesNotMatch(result.summary, /Flaky tests/);
      });

      it('retries the browser tests once and reports the flaky ones Playwright lists', () => {
        const output = [
          'Running 4 tests using 1 worker',
          '',
          '  1 flaky',
          '    \\033[33m[simulated] › tests/sign-in.spec.ts:12:3 › signs in, then out ─────────────\\033[39m',
          '    [platform] › tests/upload.spec.ts:40:5 › uploads a recording',
          '  3 passed (12.4s)',
          '',
        ].join('\\n');
        const result = run(fullRepository({ platform: false, manager: false, python: false }), {
          mode: 'phase', changed: ['src/clients/web/a.ts'], bin: rerunBin(), env: { PLAYWRIGHT_OUTPUT: output },
        });
        assert.equal(result.status, 0, result.log);
        assert.deepEqual(commands(result, 'npm'), ['ci', 'run build', 'test', 'run test:e2e -- --retries=1']);
        assert.equal(result.outputs.flaky, '[simulated] › tests/sign-in.spec.ts:12:3 › signs in; then out,[platform] › tests/upload.spec.ts:40:5 › uploads a recording');
        assert.match(result.summary, /- `\[platform\] › tests\/upload\.spec\.ts:40:5 › uploads a recording`/);
      });
    });

    it('stops at the first failing Web UI step and still writes every output', () => {
      const root = fullRepository({ python: false });
      const result = run(root, { mode: 'phase', changed: ['src/clients/web/a.ts', 'src/clients/other/b.ts'], bin: toolBin(), env: { FAIL_TOOL: 'run build' } });
      assert.equal(result.status, 1);
      assert.equal(result.outputs.checks, names.web);
      assert.deepEqual(commands(result, 'npm'), ['ci', 'run build']);
      assert.match(result.log, /Web UI step failed: build/);
      assert.equal(result.outputs.uncovered, 'src/clients/other/b.ts');
    });

    it('leaves absent project paths uncovered and runs no check for them', () => {
      const root = fullRepository({ platform: false, manager: false, web: false, python: false });
      const changed = [
        'src/platform/a.cs', 'contracts/common/a.json', 'src/analysts/manager/a.cs', 'src/clients/web/a.ts',
        'src/analysts/sdk/a.py', 'src/analysts/capabilities/c/a.py', 'README.md', 'specs/x/tasks.md',
      ];
      const result = run(root, { mode: 'task', changed, bin: toolBin() });
      assert.equal(result.status, 0, result.log);
      assert.equal(result.outputs.uncovered, changed.filter((file) => !/\.md$|^specs\//.test(file)).join(','));
      assert.equal(result.outputs.checks, names.markdown);
      assert.equal(result.calls.length, 0);
    });

    it('leaves deleted or unowned Python project paths uncovered while other scopes stay covered', () => {
      const root = fullRepository({ python: false });
      project(root, 'src/analysts/capabilities/kept', { name: 'kept' });
      const changed = [
        'src/analysts/capabilities/deleted/pyproject.toml', 'src/analysts/capabilities/deleted/a.py',
        'src/analysts/other/a.py', 'src/analysts/capabilities/stray.py', 'src/platform/a.cs',
      ];
      const result = run(root, { mode: 'task', changed, bin: toolBin() });
      assert.equal(result.status, 0, result.log);
      assert.equal(result.outputs.uncovered, changed.slice(0, 4).join(','));
      assert.ok(result.outputs.checks.startsWith(names.platform.replace('tests', 'affected tests')));
      assert.ok(!result.outputs.checks.includes(names.python));
      assert.equal(result.calls.filter((call) => call[1] === 'never').length, 0);
    });

    it('covers contracts only while the platform solution exists', () => {
      const bin = toolBin();
      const present = run(fullRepository({ python: false }), { mode: 'task', changed: ['contracts/common/a.json'], bin });
      assert.equal(present.outputs.uncovered, '');
      assert.equal(present.outputs.checks, names.platform.replace('tests', 'affected tests'));
      const absent = run(fullRepository({ platform: false, python: false }), { mode: 'task', changed: ['contracts/common/a.json'], bin });
      assert.equal(absent.outputs.uncovered, 'contracts/common/a.json');
      assert.equal(absent.outputs.checks, '');
    });

    it('keeps the environment actions, Markdown, and Spec Kit artifacts covered without running a check', () => {
      const changed = ['.github/actions/environment-verify/action.yml', '.github/actions/environment-setup/x.sh', 'specs/a/spec.md', '.gitignore'];
      const result = run(fullRepository({ python: false }), { mode: 'task', changed: changed.filter((file) => !file.endsWith('.md')), bin: toolBin() });
      assert.equal(result.status, 0, result.log);
      assert.equal(result.outputs.checks, '');
      assert.equal(result.outputs.uncovered, '');
      assert.equal(result.calls.length, 0);
    });

    describe('environment action tests', () => {
      const name = 'Environment action tests';
      // Fixture test files record the marker they run under and pass or fail on demand.
      function fixtureTests(fail = false) {
        const dir = path.join(temporary, `env-tests-${counter++}`);
        mkdirSync(dir);
        const record = path.join(dir, 'record.txt');
        const files = ['first', 'second'].map((id) => {
          const file = path.join(dir, `${id}.test.mjs`);
          writeFileSync(file, `import { appendFileSync } from 'node:fs';
import { it } from 'node:test';
import assert from 'node:assert/strict';
it('${id}', () => {
  appendFileSync(${JSON.stringify(record)}, '${id}:' + process.env.SOCALYTICS_ENVIRONMENT_TESTS_RUNNING + '\\n');
  assert.ok(!${fail && id === 'second'});
});
`);
          return file;
        });
        return { env: { ENVIRONMENT_TESTS: files.join(' ') }, record: () => (existsSync(record) ? readFileSync(record, 'utf8').split('\n').filter(Boolean) : []) };
      }

      it('runs for a change to either environment action folder, without any Python project', () => {
        const root = fullRepository({ python: false });
        for (const changed of ['.github/actions/environment-setup/x.sh', '.github/actions/environment-verify/action.yml']) {
          const fixture = fixtureTests();
          const result = run(root, { mode: 'task', changed: [changed], bin: toolBin(), env: fixture.env });
          assert.equal(result.status, 0, result.log);
          assert.equal(result.outputs.checks, name);
          assert.equal(result.outputs.uncovered, '');
          assert.deepEqual(fixture.record().sort(), ['first:1', 'second:1']);
          assert.equal(result.calls.length, 0);
        }
      });

      it('runs in finalize mode after the Python projects and before the Markdown check', () => {
        const fixture = fixtureTests();
        const result = run(fullRepository(), { bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 0, result.log);
        assert.equal(result.outputs.checks, [names.platform, names.manager, names.web, names.python, name, names.markdown].join(','));
        assert.equal(fixture.record().length, 2);
        const noProject = fixtureTests();
        const bare = run(fullRepository({ platform: false, manager: false, web: false, python: false }), { env: noProject.env });
        assert.equal(bare.outputs.checks, [name, names.markdown].join(','));
      });

      it('does not run for phase or task changes outside the environment action folders', () => {
        const fixture = fixtureTests();
        const root = fullRepository({ python: false });
        const bin = toolBin();
        for (const mode of ['task', 'phase']) {
          const result = run(root, { mode, changed: ['src/clients/web/a.ts', 'src/platform/a.cs', '.github/actions/other/x.sh', 'docs/a.md'], bin, env: fixture.env });
          assert.ok(!result.outputs.checks.includes(name));
        }
        assert.equal(fixture.record().length, 0);
      });

      it('fails with one recommendation, runs every file, and still writes every output', () => {
        const fixture = fixtureTests(true);
        const result = run(fullRepository({ python: false }), { mode: 'task', changed: ['.github/actions/environment-verify/x.mjs', 'src/analysts/other/y.py'], bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 1);
        assert.equal(result.outputs.checks, name);
        assert.equal(result.outputs.uncovered, 'src/analysts/other/y.py');
        assert.equal(fixture.record().length, 2);
        const failure = result.log.split('\n').filter((line) => line.startsWith('Environment action tests failed'));
        assert.equal(failure.length, 1);
        assert.equal(failure[0].match(/Recommended action:/g).length, 1);
        assert.doesNotMatch(failure[0], /skip|ignore|disable|merge/i);
      });

      it('does not start the check again from a nested run and never touches a Python project', () => {
        const fixture = fixtureTests();
        const result = run(sdkRepository(), { changed: ['.github/actions/environment-setup/x.sh'], env: { ...fixture.env, SOCALYTICS_ENVIRONMENT_TESTS_RUNNING: '1' } });
        assert.equal(result.status, 0, result.log);
        assert.ok(!result.outputs.checks.includes(name));
        assert.equal(fixture.record().length, 0);
      });

      it('runs the changed action script for every component kind with stubbed tools', () => {
        const fixture = fixtureTests();
        const full = run(fullRepository(), { bin: toolBin(), env: fixture.env });
        assert.equal(full.status, 0, full.log);
        for (const kind of [names.platform, names.manager, names.web, names.python, names.markdown]) {
          assert.ok(full.outputs.checks.split(',').includes(kind), kind);
        }
        const root = fullRepository({ manager: false, web: false, python: false });
        writeFileSync(path.join(root, 'src/platform/verification.json'), '{ not json');
        const invalid = run(root, { bin: toolBin(), env: fixture.env });
        assert.equal(invalid.status, 1);
        assert.ok(invalid.outputs.checks.split(',').includes('Verification plan'));
      });

      describe('environment-tests-only', () => {
        const only = (fixture, extra = {}) => ({ ...fixture.env, ENVIRONMENT_TESTS_ONLY: 'true', ...extra });
        const recommendations = (result) => result.log.split('\n').filter((line) => /Recommended action:/.test(line));

        it('runs only the environment action tests in finalize mode', () => {
          const fixture = fixtureTests();
          const result = run(fullRepository(), { bin: toolBin(), env: only(fixture) });
          assert.equal(result.status, 0, result.log);
          assert.equal(result.outputs.checks, name);
          assert.deepEqual(fixture.record().sort(), ['first:1', 'second:1']);
          assert.equal(result.calls.length, 0);
          assert.ok(!('plan' in result.outputs));
          assert.equal(result.outputs.flaky, '');
          assert.match(result.log, /Verification scope: environment action tests only/);
        });

        it('fails with exactly one recommendation when a test fails', () => {
          const fixture = fixtureTests(true);
          const result = run(fullRepository(), { bin: toolBin(), env: only(fixture) });
          assert.equal(result.status, 1);
          assert.equal(result.outputs.checks, name);
          assert.equal(fixture.record().length, 2);
          assert.equal(recommendations(result).length, 1);
          assert.match(recommendations(result)[0], /^Environment action tests failed/);
        });

        it('fails with exactly one recommendation when no tests are configured', () => {
          const fixture = fixtureTests();
          const result = run(fullRepository(), { bin: toolBin(), env: only(fixture, { ENVIRONMENT_TESTS: '' }) });
          assert.equal(result.status, 1);
          assert.equal(recommendations(result).length, 1);
          assert.equal(fixture.record().length, 0);
          assert.equal(result.calls.length, 0);
        });

        it('passes without running the tests in a nested run', () => {
          const fixture = fixtureTests();
          const result = run(fullRepository(), { bin: toolBin(), env: only(fixture, { SOCALYTICS_ENVIRONMENT_TESTS_RUNNING: '1' }) });
          assert.equal(result.status, 0, result.log);
          assert.equal(fixture.record().length, 0);
          assert.equal(result.calls.length, 0);
        });

        it('lists every changed file as uncovered', () => {
          const fixture = fixtureTests();
          const changed = ['src/platform/a.cs', 'docs/a.md', '.github/actions/environment-verify/x.mjs'];
          const result = run(fullRepository(), { mode: 'task', changed, bin: toolBin(), env: only(fixture) });
          assert.equal(result.status, 0, result.log);
          assert.equal(result.outputs.uncovered, changed.join(','));
          assert.equal(run(fullRepository(), { bin: toolBin(), env: only(fixture) }).outputs.uncovered, '');
        });

        it('keeps the unchanged full verification for "false" and empty values', () => {
          for (const value of ['false', '']) {
            const fixture = fixtureTests();
            const result = run(fullRepository(), { bin: toolBin(), env: { ...fixture.env, ENVIRONMENT_TESTS_ONLY: value } });
            assert.equal(result.status, 0, result.log);
            assert.equal(result.outputs.checks, [names.platform, names.manager, names.web, names.python, name, names.markdown].join(','));
            assert.ok('plan' in result.outputs);
          }
        });

        it('runs the tests for a shard other than the first outside finalize mode', () => {
          const fixture = fixtureTests();
          const result = run(fullRepository(), { mode: 'phase', changed: ['src/platform/a.cs'], bin: toolBin(), env: only(fixture, { SHARD: '2/2' }) });
          assert.equal(result.status, 0, result.log);
          assert.equal(result.outputs.checks, name);
          assert.equal(fixture.record().length, 2);
        });

        it('declares the input with default "false" and keeps the outputs', () => {
          const inputs = actionText.split('inputs:')[1].split('outputs:')[0];
          assert.match(inputs, /environment-tests-only:[\s\S]*?default: "false"/);
          const outputs = actionText.split('outputs:')[1].split('runs:')[0];
          assert.deepEqual([...outputs.matchAll(/^  (\w+):/gm)].map((match) => match[1]), ['checks', 'uncovered', 'flaky', 'plan']);
        });
      });

      it('points at the two Node test files next to the actions and adds no workflow input', () => {
        const declared = actionText.match(/ENVIRONMENT_TESTS: (.*)/)[1];
        assert.match(declared, /action_path \}\}\/analyst-projects\.test\.mjs/);
        assert.match(declared, /action_path \}\}\/\.\.\/environment-setup\/toolchain\.test\.mjs/);
        assert.ok(!/^  (node|tests?)[\w-]*:/m.test(actionText.split('inputs:')[1].split('outputs:')[0]));
      });
    });

    describe('Spec Kit tooling tests', () => {
      const name = 'Spec Kit tooling tests';
      const folder = '.github/scripts';
      const failureMessage = 'Spec Kit tooling tests failed (command: node --test .github/scripts/speckit-a.test.mjs .github/scripts/speckit-b.test.mjs). Recommended action: reproduce this command, correct the failing Spec Kit tooling script or its test without weakening the test, and retry verification';
      // Stub test files in the workspace record the marker they run under and pass or fail on demand.
      function toolingFixture(root, { fail = false, ids = ['a', 'b'] } = {}) {
        mkdirSync(path.join(root, folder), { recursive: true });
        const record = path.join(root, `tooling-record-${counter++}.txt`);
        for (const id of ids) {
          writeFileSync(path.join(root, folder, `speckit-${id}.test.mjs`), `import { appendFileSync } from 'node:fs';
import { it } from 'node:test';
import assert from 'node:assert/strict';
it('${id}', () => {
  appendFileSync(${JSON.stringify(record)}, '${id}:' + process.env.SOCALYTICS_ENVIRONMENT_TESTS_RUNNING + '\\n');
  assert.ok(!${fail && id === 'b'});
});
`);
        }
        return { env: { SPECKIT_TOOLING_TESTS: folder }, record: () => (existsSync(record) ? readFileSync(record, 'utf8').split('\n').filter(Boolean).sort() : []) };
      }
      const toolingRepository = (options) => {
        const root = fullRepository({ python: false });
        return { root, fixture: toolingFixture(root, options) };
      };

      it('runs and succeeds for a changed speckit script and a changed speckit test', () => {
        for (const changed of ['.github/scripts/speckit-implement-core.mjs', '.github/scripts/speckit-implement-core.test.mjs']) {
          const { root, fixture } = toolingRepository();
          const result = run(root, { mode: 'task', changed: [changed], bin: toolBin(), env: fixture.env });
          assert.equal(result.status, 0, result.log);
          assert.equal(result.outputs.checks, name);
          assert.equal(result.outputs.uncovered, '');
          assert.deepEqual(fixture.record(), ['a:1', 'b:1']);
        }
      });

      it('runs for a deleted tooling file', () => {
        const { root, fixture } = toolingRepository();
        const result = run(root, { mode: 'task', changed: ['.github/scripts/speckit-gone.mjs'], bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 0, result.log);
        assert.equal(result.outputs.checks, name);
        assert.deepEqual(fixture.record(), ['a:1', 'b:1']);
      });

      it('runs for the task-named change list with no change and leaves nothing uncovered', () => {
        const { root, fixture } = toolingRepository();
        const named = ['.github/scripts/speckit-implement-core.mjs', '.github/scripts/speckit-implement-work.mjs'];
        const result = run(root, { mode: 'task', changed: named, bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 0, result.log);
        assert.ok(result.outputs.checks.split(',').includes(name));
        assert.equal(result.outputs.uncovered, '');
        assert.deepEqual(fixture.record(), ['a:1', 'b:1']);
      });

      it('runs in finalize mode with nothing changed', () => {
        const { root, fixture } = toolingRepository();
        const result = run(root, { mode: 'finalize', bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 0, result.log);
        assert.ok(result.outputs.checks.split(',').includes(name));
        assert.deepEqual(fixture.record(), ['a:1', 'b:1']);
      });

      it('fails with the exact one-line message including the reproduce command and still writes every output', () => {
        const { root, fixture } = toolingRepository({ fail: true });
        const result = run(root, { mode: 'task', changed: ['.github/scripts/speckit-x.mjs'], bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 1);
        assert.equal(result.outputs.checks, name);
        assert.equal(result.outputs.uncovered, '');
        assert.deepEqual(fixture.record(), ['a:1', 'b:1']);
        const failure = result.log.split('\n').filter((line) => line.startsWith('Spec Kit tooling tests failed'));
        assert.deepEqual(failure, [failureMessage]);
      });

      it('fails when no speckit test file exists', () => {
        const root = fullRepository({ python: false });
        mkdirSync(path.join(root, folder), { recursive: true });
        writeFileSync(path.join(root, folder, 'helper.test.mjs'), '');
        const result = run(root, { mode: 'task', changed: ['.github/scripts/speckit-x.mjs'], bin: toolBin(), env: { SPECKIT_TOOLING_TESTS: folder } });
        assert.equal(result.status, 1);
        assert.equal(result.outputs.checks, name);
        assert.match(result.log, /Spec Kit tooling tests: no \.github\/scripts\/speckit-\*\.test\.mjs file exists\. Recommended action: restore the Spec Kit tooling tests and retry verification/);
      });

      it('does not run from a nested run', () => {
        const { root, fixture } = toolingRepository();
        const result = run(root, { mode: 'task', changed: ['.github/scripts/speckit-x.mjs'], bin: toolBin(), env: { ...fixture.env, SOCALYTICS_ENVIRONMENT_TESTS_RUNNING: '1' } });
        assert.equal(result.status, 0, result.log);
        assert.ok(!result.outputs.checks.includes(name));
        assert.equal(fixture.record().length, 0);
      });

      it('skips an unrelated change, a non-speckit script, and a documentation-only change', () => {
        for (const changed of [['src/clients/web/a.ts'], ['.github/scripts/helper.mjs'], ['.github/scripts/sub/speckit-x.mjs'], ['docs/a.md']]) {
          const { root, fixture } = toolingRepository();
          const result = run(root, { mode: 'task', changed, bin: toolBin(), env: fixture.env });
          assert.equal(result.status, 0, result.log);
          assert.ok(!result.outputs.checks.includes(name), changed.join());
          assert.equal(fixture.record().length, 0);
        }
      });

      it('belongs to the first shard only', () => {
        const { root, fixture } = toolingRepository();
        const second = run(root, { mode: 'finalize', bin: toolBin(), env: { ...fixture.env, SHARD: '2/2' } });
        assert.equal(second.status, 0, second.log);
        assert.ok(!second.outputs.checks.includes(name));
        assert.equal(fixture.record().length, 0);
        const first = run(root, { mode: 'finalize', bin: toolBin(), env: { ...fixture.env, SHARD: '1/2' } });
        assert.equal(first.status, 0, first.log);
        assert.ok(first.outputs.checks.split(',').includes(name));
        assert.deepEqual(fixture.record(), ['a:1', 'b:1']);
      });

      it('does not run while SPECKIT_TOOLING_TESTS is unset', () => {
        const { root } = toolingRepository();
        const result = run(root, { mode: 'task', changed: ['.github/scripts/speckit-x.mjs'], bin: toolBin() });
        assert.ok(!result.outputs.checks.includes(name));
      });
    });

    describe('Sample tooling tests', () => {
      const name = 'Sample tooling tests';
      const testFile = 'tools/samples.test.mjs';
      const paths = ['tools/samples.test.mjs', 'tools/samples-lib.mjs', 'tools/upload-samples.mjs', 'tools/download-samples.mjs'];
      const nonMatching = ['tools/other.mjs', 'tools/samples.test.mjs.bak', 'tools/sub/samples-lib.mjs', 'tools/Samples-Lib.mjs'];
      const failureMessage = 'Sample tooling tests failed (command: node --test tools/samples.test.mjs). Recommended action: reproduce this command, correct the failing sample tooling file or its test without weakening the test, and retry verification';
      // A stub test file in the workspace records the marker it runs under and passes or fails on demand.
      function sampleFixture(root, { fail = false } = {}) {
        mkdirSync(path.join(root, 'tools'), { recursive: true });
        const record = path.join(root, `sample-record-${counter++}.txt`);
        writeFileSync(path.join(root, testFile), `import { appendFileSync } from 'node:fs';
import { it } from 'node:test';
import assert from 'node:assert/strict';
it('sample', () => {
  appendFileSync(${JSON.stringify(record)}, 'samples:' + process.env.SOCALYTICS_ENVIRONMENT_TESTS_RUNNING + '\\n');
  assert.ok(!${fail});
});
`);
        return { env: { SAMPLE_TOOLING_TESTS: testFile }, record: () => (existsSync(record) ? readFileSync(record, 'utf8').split('\n').filter(Boolean) : []) };
      }
      const sampleRepository = (options) => {
        const root = fullRepository({ python: false });
        return { root, fixture: sampleFixture(root, options) };
      };
      const checksOf = (result) => result.outputs.checks.split(',');

      it('runs the exact command and lists the check for each of the four paths, a deletion included', () => {
        for (const changed of paths) {
          const { root, fixture } = sampleRepository();
          const result = run(root, { mode: 'task', changed: [changed], bin: toolBin(), env: fixture.env });
          assert.equal(result.status, 0, `${changed}: ${result.log}`);
          assert.ok(checksOf(result).includes(name), changed);
          assert.match(result.log, /\$ node --test tools\/samples\.test\.mjs|\$ env SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1 node --test tools\/samples\.test\.mjs/);
          assert.deepEqual(fixture.record(), ['samples:1'], changed);
        }
        const { root, fixture } = sampleRepository();
        const result = run(root, { mode: 'phase', changed: ['tools/upload-samples.mjs'], bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 0, result.log);
        assert.ok(checksOf(result).includes(name));
        assert.deepEqual(fixture.record(), ['samples:1']);
      });

      it('fails with the exact one-line reproduce-command message and still writes every output', () => {
        const { root, fixture } = sampleRepository({ fail: true });
        const result = run(root, { mode: 'task', changed: ['tools/samples-lib.mjs'], bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 1);
        assert.ok(checksOf(result).includes(name));
        assert.ok('uncovered' in result.outputs && 'flaky' in result.outputs);
        assert.deepEqual(fixture.record(), ['samples:1']);
        assert.deepEqual(result.log.split('\n').filter((line) => line.startsWith('Sample tooling tests failed')), [failureMessage]);
      });

      it('fails visibly with the missing-file message while another of the four files changes', () => {
        const root = fullRepository({ python: false });
        const result = run(root, { mode: 'task', changed: ['tools/download-samples.mjs'], bin: toolBin(), env: { SAMPLE_TOOLING_TESTS: testFile } });
        assert.equal(result.status, 1);
        assert.ok(checksOf(result).includes(name));
        assert.deepEqual(
          result.log.split('\n').filter((line) => line.startsWith('Sample tooling tests:')),
          ['Sample tooling tests: tools/samples.test.mjs does not exist. Recommended action: restore tools/samples.test.mjs and retry verification'],
        );
      });

      it('fails with the node-required message without Node.js', () => {
        const { root, fixture } = sampleRepository();
        // The plan itself needs node, so only the availability probe of the check is hidden.
        const hide = '() { if [ "$1" = -v ] && [ "$2" = node ]; then return 1; fi; builtin command "$@"; }';
        const result = run(root, { mode: 'task', changed: ['tools/samples.test.mjs'], bin: toolBin(), env: { ...fixture.env, 'BASH_FUNC_command%%': hide } });
        assert.equal(result.status, 1, result.log);
        assert.ok(checksOf(result).includes(name));
        assert.ok(result.log.split('\n').includes('Sample tooling tests: node is required. Recommended action: run environment-setup before verification'));
        assert.equal(fixture.record().length, 0);
      });

      it('skips and does not list the check for similar paths in task and phase mode', () => {
        for (const mode of ['task', 'phase']) {
          for (const changed of nonMatching) {
            const { root, fixture } = sampleRepository();
            const result = run(root, { mode, changed: [changed], bin: toolBin(), env: fixture.env });
            assert.ok(!result.outputs.checks.includes(name), `${mode} ${changed}: ${result.log}`);
            assert.equal(fixture.record().length, 0, `${mode} ${changed}`);
          }
        }
      });

      it('runs in finalize mode without a changed sample tooling file', () => {
        const { root, fixture } = sampleRepository();
        const result = run(root, { mode: 'finalize', bin: toolBin(), env: fixture.env });
        assert.equal(result.status, 0, result.log);
        assert.ok(checksOf(result).includes(name));
        assert.deepEqual(fixture.record(), ['samples:1']);
      });

      it('does not run while the nested-run marker is set or SAMPLE_TOOLING_TESTS is unset', () => {
        const nested = sampleRepository();
        const nestedResult = run(nested.root, { mode: 'task', changed: ['tools/samples.test.mjs'], bin: toolBin(), env: { ...nested.fixture.env, SOCALYTICS_ENVIRONMENT_TESTS_RUNNING: '1' } });
        assert.equal(nestedResult.status, 0, nestedResult.log);
        assert.ok(!nestedResult.outputs.checks.includes(name));
        assert.equal(nested.fixture.record().length, 0);
        const unset = sampleRepository();
        const unsetResult = run(unset.root, { mode: 'task', changed: ['tools/samples.test.mjs'], bin: toolBin() });
        assert.ok(!unsetResult.outputs.checks.includes(name));
        assert.equal(unset.fixture.record().length, 0);
      });
    });

    describe('agents area coverage and regression', () => {
      const bypass = /--no-locked|--frozen|--upgrade|skip|ignore|disable|merge|unlock|bypass|weaken/i;
      const recommendations = (log) => log.split('\n').filter((line) => line.includes('Recommended action:'));

      it('keeps deleted, area-root, nested, and unowned agents-area paths uncovered while valid roots stay covered', () => {
        const root = fullRepository({ platform: false, manager: false, web: false, python: false });
        project(root, 'src/agents/kept', { name: 'kept' });
        const changed = [
          'src/agents/deleted/pyproject.toml', 'src/agents/deleted/a.py', 'src/agents/pyproject.toml',
          'src/agents/stray.py', 'src/agents/kept/a.py', 'src/agents/README.md',
        ];
        const result = run(root, { mode: 'task', changed, bin: toolBin() });
        assert.equal(result.status, 0, result.log);
        assert.equal(result.outputs.uncovered, 'src/agents/deleted/pyproject.toml,src/agents/deleted/a.py,src/agents/pyproject.toml,src/agents/stray.py');
        assert.equal(result.outputs.checks, [names.python.replace('Analyst', 'Agent'), names.markdown].join(','));
      });

      it('covers src/agents/README.md only through the Markdown rule', () => {
        const absent = run(fullRepository({ platform: false, manager: false, web: false, python: false }), { mode: 'task', changed: ['src/agents/README.md'], bin: toolBin() });
        assert.equal(absent.status, 0, absent.log);
        assert.equal(absent.outputs.uncovered, '');
        assert.equal(absent.outputs.checks, names.markdown);
        const root = fullRepository({ platform: false, manager: false, web: false, python: false });
        project(root, 'src/agents/kept', { name: 'kept' });
        const present = run(root, { mode: 'task', changed: ['src/agents/README.md'], bin: toolBin() });
        assert.equal(present.outputs.uncovered, '');
        assert.equal(present.outputs.checks, names.markdown);
        assert.equal(present.calls.length, 0);
      });

      it('matches agent coverage roots literally when the folder name contains ERE metacharacters', () => {
        const root = fullRepository({ platform: false, manager: false, web: false, python: false });
        project(root, 'src/agents/mcp+server', { name: 'mcp-server' });
        const result = run(root, {
          mode: 'task',
          changed: ['src/agents/mcp+server/agent.py', 'src/agents/mcpserver/unowned.py'],
        });
        assert.equal(result.status, 0, result.log);
        assert.equal(result.outputs.checks, 'Agent Python projects');
        assert.equal(result.outputs.uncovered, 'src/agents/mcpserver/unowned.py');
      });

      const invalid = [
        ['a missing lock', 'src/agents/mcp', (root) => project(root, 'src/agents/mcp', { name: 'mcp' }, { lock: false })],
        ['the area-root manifest', 'src/agents/pyproject.toml', (root) => { project(root, 'src/agents', { name: 'area' }); project(root, 'src/agents/mcp', { name: 'mcp' }); }],
        ['a nested manifest', 'src/agents/mcp/nested/pyproject.toml', (root) => { project(root, 'src/agents/mcp', { name: 'mcp' }); project(root, 'src/agents/mcp/nested', { name: 'nested' }); }],
        ['an Analyst dependency', 'src/agents/mcp', (root) => { project(root, 'src/analysts/sdk', { name: 'sdk' }); project(root, 'src/agents/mcp', { name: 'mcp', dependencies: ['sdk'], sources: { sdk: '../../analysts/sdk' } }); }],
        ['a name repeating an Analyst name', 'src/agents/mcp', (root) => { project(root, 'src/analysts/sdk', { name: 'sdk' }); project(root, 'src/agents/mcp', { name: 'sdk' }); }],
        ['a malformed manifest', 'src/agents/mcp', (root) => project(root, 'src/agents/mcp', { raw: '[project\n' })],
      ];
      for (const [label, named, build] of invalid) {
        it(`fails ${label} in every mode with the named project and exactly one safe recommendation, covering no agent root`, () => {
          for (const mode of ['task', 'phase', 'finalize']) {
            const root = fullRepository({ platform: false, manager: false, web: false, python: false });
            build(root);
            const changed = ['src/clients/other/b.ts', 'src/agents/mcp/a.py'];
            const result = run(root, { mode, changed: mode === 'finalize' ? [] : changed, bin: toolBin() });
            assert.equal(result.status, 1, `${mode}: ${result.log}`);
            assert.match(result.log, /Agent Python projects: discovery failed/);
            assert.ok(result.log.includes(named), `${mode}: ${named}`);
            assert.ok(result.outputs.checks.split(',').includes('Agent Python projects'));
            assert.ok(!result.outputs.checks.includes('Markdown'));
            const advice = recommendations(result.log);
            assert.equal(advice.length, 1, result.log);
            assert.doesNotMatch(advice[0], bypass);
            assert.equal(result.calls.filter((call) => call[0].includes('src/agents')).length, 0);
            if (mode !== 'finalize') assert.equal(result.outputs.uncovered, 'src/clients/other/b.ts,src/agents/mcp/a.py');
          }
        });
      }

      it('fails an invalid agent project even when the changed paths do not touch it', () => {
        const root = fullRepository({ platform: false, manager: false, web: false, python: false });
        project(root, 'src/agents/mcp', { name: 'mcp' }, { lock: false });
        const result = run(root, { mode: 'task', changed: ['src/clients/other/b.ts'], bin: toolBin() });
        assert.equal(result.status, 1);
        assert.equal(result.outputs.checks, 'Agent Python projects');
        assert.equal(result.outputs.uncovered, 'src/clients/other/b.ts');
        assert.match(result.log, /project src\/agents\/mcp: locked sync: uv\.lock is missing/);
      });

      it('keeps the log, checks, and uncovered outputs for an agent stage failure alongside platform checks', () => {
        const root = fullRepository({ web: false, python: false });
        project(root, 'src/agents/mcp', { name: 'mcp' });
        const result = run(root, {
          mode: 'phase', changed: ['src/agents/mcp/a.py', 'src/agents/gone/a.py', 'src/platform/a.cs'],
          env: { FAIL_STAGE: stages[3] }, bin: toolBin(),
        });
        assert.equal(result.status, 1);
        assert.equal(result.outputs.checks, [names.platform, 'Agent Python projects'].join(','));
        assert.equal(result.outputs.uncovered, 'src/agents/gone/a.py');
        const advice = recommendations(result.log).filter((line) => line.startsWith('Agent Python stage failed'));
        assert.equal(advice.length, 1);
        assert.match(advice[0], /Agent Python stage failed: tests in src\/agents\/mcp \(command: uv run --no-sync pytest\)/);
        assert.doesNotMatch(advice[0], /--no-locked|--frozen|--upgrade|skip|ignore|disable|merge|unlock|bypass/i);
      });

      it('runs every check in the documented order in finalize mode', () => {
        const fixtureDir = path.join(temporary, `env-tests-${counter++}`);
        mkdirSync(fixtureDir);
        const file = path.join(fixtureDir, 'pass.test.mjs');
        writeFileSync(file, "import { it } from 'node:test';\nit('passes', () => {});\n");
        const root = fullRepository();
        project(root, 'src/agents/mcp', { name: 'mcp' });
        mkdirSync(path.join(root, '.github/scripts'), { recursive: true });
        writeFileSync(path.join(root, '.github/scripts/speckit-a.test.mjs'), "import { it } from 'node:test';\nit('passes', () => {});\n");
        mkdirSync(path.join(root, 'tools'), { recursive: true });
        writeFileSync(path.join(root, 'tools/samples.test.mjs'), "import { it } from 'node:test';\nit('passes', () => {});\n");
        const result = run(root, { bin: toolBin(), env: { ENVIRONMENT_TESTS: file, SPECKIT_TOOLING_TESTS: '.github/scripts', SAMPLE_TOOLING_TESTS: 'tools/samples.test.mjs' } });
        assert.equal(result.status, 0, result.log);
        assert.equal(result.outputs.checks, [
          names.platform, names.manager, names.web, names.python, 'Agent Python projects', 'Environment action tests', 'Spec Kit tooling tests', 'Sample tooling tests', names.markdown,
        ].join(','));
        assert.equal(result.outputs.uncovered, '');
      });
    });

    it('keeps the log, checks, and uncovered outputs for an Analyst failure alongside platform checks', () => {
      const root = fullRepository({ web: false });
      const result = run(root, {
        mode: 'phase', changed: ['src/analysts/sdk/a.py', 'src/analysts/capabilities/gone/a.py'],
        env: { FAIL_STAGE: stages[2] }, bin: toolBin(),
      });
      assert.equal(result.status, 1);
      assert.equal(result.outputs.checks, names.python);
      assert.equal(result.outputs.uncovered, 'src/analysts/capabilities/gone/a.py');
      assert.match(result.log, /Analyst Python stage failed: wheel build in src\/analysts\/sdk/);
      assert.equal(commands(result, 'dotnet').length, 0);
    });
  });

  describe('agent projects', () => {
    const mcpRoot = 'src/agents/mcp';
    const agentRepository = () => {
      const root = repository();
      project(root, mcpRoot, { name: 'mcp' });
      return root;
    };

    it('runs the four exact stages in order with only the download and interpreter variables', () => {
      const result = run(agentRepository());
      assert.equal(result.status, 0, result.log);
      assert.deepEqual(result.calls.map((call) => call[3]), stages);
      for (const call of result.calls) {
        assert.ok(call[0].endsWith(mcpRoot));
        assert.equal(call[1], 'never');
        assert.equal(call[2], python);
      }
      assert.equal(result.outputs.checks, 'Agent Python projects,Markdown check');
      const lines = result.log.split('\n');
      assert.ok(lines.includes(`Agent Python project: ${mcpRoot}`));
      for (const stage of ['bootstrap sync', 'project sync', 'wheel build', 'tests']) {
        assert.ok(lines.includes(`Agent Python stage: ${stage} (${mcpRoot})`), stage);
      }
      assert.doesNotMatch(result.log, /Analyst Python/);
    });

    it('adds only the download and interpreter variables to the stage commands', () => {
      const result = run(agentRepository());
      assert.equal(result.status, 0, result.log);
      const commands = result.log.split('\n').filter((line) => line.startsWith('$ env '));
      assert.equal(commands.length, 4);
      for (const line of commands) assert.match(line, new RegExp(`^\\$ env UV_PYTHON_DOWNLOADS=never UV_PYTHON=${python.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')} uv `));
    });

    for (const [index, stage] of ['bootstrap sync', 'project sync', 'wheel build', 'tests'].entries()) {
      it(`fails the ${stage} stage with the project, command, and one recommendation`, () => {
        const result = run(agentRepository(), { env: { FAIL_STAGE: stages[index] } });
        assert.equal(result.status, 1);
        assert.deepEqual(result.calls.map((call) => call[3]), stages.slice(0, index + 1));
        assert.equal(result.outputs.checks, 'Agent Python projects');
        const failure = result.log.split('\n').filter((line) => line.startsWith('Agent Python stage failed: '));
        assert.equal(failure.length, 1);
        assert.match(failure[0], new RegExp(`^Agent Python stage failed: ${stage} in ${mcpRoot} \\(command: uv ${stages[index].replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\)\\. Recommended action: `));
        assert.equal(failure[0].match(/Recommended action:/g).length, 1);
        assert.doesNotMatch(failure[0], /--no-locked|--frozen|--upgrade|skip|ignore|disable|merge/i);
      });
    }

    it('orders dependencies first and stops at the first failing project', () => {
      const root = repository();
      project(root, 'src/agents/mcp-server', { name: 'mcp-server', dependencies: ['mcp'], sources: { mcp: '../mcp' } });
      project(root, mcpRoot, { name: 'mcp' });
      const ok = run(root);
      assert.equal(ok.status, 0, ok.log);
      assert.deepEqual([...new Set(ok.calls.map((call) => path.relative(root, call[0])))], [mcpRoot, 'src/agents/mcp-server']);
      const failed = run(root, { env: { FAIL_STAGE: stages[0] } });
      assert.equal(failed.status, 1);
      assert.equal(failed.calls.length, 1);
    });

    it('fails explicitly without the installed interpreter or uv', () => {
      const result = run(agentRepository(), { withPython: false });
      assert.equal(result.status, 1);
      assert.match(result.log, /Agent Python projects: environment setup: .*Recommended action: run environment-setup/);
      assert.equal(result.calls.length, 0);
      assert.equal(result.outputs.checks, 'Agent Python projects');
    });

    it('covers an agent root in task mode and leaves other agent paths uncovered', () => {
      const result = run(agentRepository(), { mode: 'task', changed: [`${mcpRoot}/src/x.py`, 'src/agents/mcp-other/y.py'] });
      assert.equal(result.status, 0, result.log);
      assert.equal(result.calls.length, 4);
      assert.equal(result.outputs.uncovered, 'src/agents/mcp-other/y.py');
    });

    it('runs no agent check when no agent project exists', () => {
      const result = run(sdkRepository());
      assert.equal(result.status, 0, result.log);
      assert.ok(!result.outputs.checks.includes('Agent Python projects'));
      assert.ok(!/Agent Python/.test(result.log));
      const empty = run(repository(), { mode: 'task', changed: ['src/agents/x/a.py'] });
      assert.equal(empty.outputs.checks, '');
      assert.equal(empty.outputs.uncovered, 'src/agents/x/a.py');
    });

    it('runs the Analyst check before the agent check', () => {
      const root = sdkRepository();
      project(root, mcpRoot, { name: 'mcp' });
      const result = run(root);
      assert.equal(result.status, 0, result.log);
      assert.equal(result.outputs.checks, 'Analyst Python projects,Agent Python projects,Markdown check');
      assert.deepEqual([...new Set(result.calls.map((call) => path.relative(root, call[0])))], [sdkRoot, mcpRoot]);
    });

    it('runs only the check of the area that changed', () => {
      const root = sdkRepository();
      project(root, mcpRoot, { name: 'mcp' });
      const agent = run(root, { mode: 'task', changed: [`${mcpRoot}/a.py`] });
      assert.equal(agent.status, 0, agent.log);
      assert.equal(agent.outputs.checks, 'Agent Python projects');
      assert.deepEqual([...new Set(agent.calls.map((call) => path.relative(root, call[0])))], [mcpRoot]);
      const analyst = run(root, { mode: 'task', changed: [`${sdkRoot}/a.py`] });
      assert.equal(analyst.outputs.checks, 'Analyst Python projects');
      assert.deepEqual([...new Set(analyst.calls.map((call) => path.relative(root, call[0])))], [sdkRoot]);
      const analysis = run(root, { mode: 'task', changed: ['contracts/analysis/x.json'] });
      assert.equal(analysis.outputs.checks, 'Analyst Python projects');
      assert.deepEqual([...new Set(analysis.calls.map((call) => path.relative(root, call[0])))], [sdkRoot]);
    });

    it('fails only the agent check for an agent discovery failure and keeps the Analyst check', () => {
      const root = sdkRepository();
      project(root, mcpRoot, { name: 'mcp', dependencies: ['sdk'], sources: { sdk: '../../analysts/sdk' } });
      const result = run(root);
      assert.equal(result.status, 1);
      assert.equal(result.outputs.checks, 'Analyst Python projects,Agent Python projects');
      assert.match(result.log, /Agent Python projects: discovery failed/);
      assert.equal(result.log.match(/Recommended action:/g).length, 1);
      assert.match(result.log, /remove the dependency on the Analyst project/);
      assert.deepEqual([...new Set(result.calls.map((call) => path.relative(root, call[0])))], [sdkRoot]);
      assert.doesNotMatch(result.log, /Analyst Python projects: discovery failed/);
    });

    it('keeps the Analyst check unchanged while an invalid agent project exists', () => {
      const root = sdkRepository();
      project(root, mcpRoot, { name: 'sdk' });
      const result = run(root, { mode: 'task', changed: [`${sdkRoot}/a.py`] });
      assert.equal(result.status, 1);
      assert.equal(result.outputs.checks, 'Analyst Python projects,Agent Python projects');
      assert.equal(result.calls.length, 4);
      assert.match(result.log, /Analyst Python stage: tests \(src\/analysts\/sdk\)/);
      assert.match(result.log, /project src\/agents\/mcp: project convention: project name 'sdk' duplicates Analyst project src\/analysts\/sdk/);
    });
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

  it('passes every stage for a real isolated agents-area package without changing declarations', { skip: !uvReal }, () => {
    const root = repository();
    const directory = path.join(root, 'src/agents/demo');
    mkdirSync(path.join(directory, 'src/demo_agent'), { recursive: true });
    mkdirSync(path.join(directory, 'tests'), { recursive: true });
    writeFileSync(path.join(directory, 'pyproject.toml'), `[project]
name = "demo-agent"
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
    writeFileSync(path.join(directory, 'src/demo_agent/__init__.py'), 'VALUE = 1\n');
    writeFileSync(path.join(directory, 'tests/test_demo.py'), 'import demo_agent\n\n\ndef test_value():\n    assert demo_agent.VALUE == 1\n');
    const locked = spawnSync('uv', ['lock'], { cwd: directory, encoding: 'utf8', env: { ...process.env, UV_PYTHON: python } });
    if (locked.status !== 0) return; // package index unreachable: the real fixture cannot be locked
    const before = readFileSync(path.join(directory, 'pyproject.toml'), 'utf8') + readFileSync(path.join(directory, 'uv.lock'), 'utf8');
    const bin = path.join(temporary, `bin-${counter++}`);
    mkdirSync(bin);
    symlinkSync(spawnSync('which', ['uv'], { encoding: 'utf8' }).stdout.trim(), path.join(bin, 'uv'));
    const passed = run(root, { bin });
    assert.equal(passed.status, 0, passed.log);
    assert.equal(passed.outputs.checks, 'Agent Python projects,Markdown check');
    assert.equal(before, readFileSync(path.join(directory, 'pyproject.toml'), 'utf8') + readFileSync(path.join(directory, 'uv.lock'), 'utf8'));
    writeFileSync(path.join(directory, 'tests/test_demo.py'), 'def test_value():\n    assert False\n');
    assert.equal(run(root, { bin }).status, 1);
  });
});

describe('agents area discovery', () => {
  const agents = (root, args = []) => discover(root, ['--area', 'agents', ...args]);
  const rejectedAgents = (root, pattern) => {
    const result = agents(root);
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.stderr, pattern);
    assert.match(result.stderr, /Recommended action: /);
    return result;
  };

  it('finds nothing without an agents area or project', () => {
    const root = repository();
    assert.deepEqual(agents(root).projects, []);
    mkdirSync(path.join(root, 'src/agents/empty'), { recursive: true });
    assert.deepEqual(agents(root).projects, []);
  });

  it('recognizes several agent projects with dependencies first', () => {
    const root = repository();
    project(root, 'src/agents/mcp-server', { name: 'mcp-server', dependencies: ['mcp'], sources: { mcp: '../mcp' } });
    project(root, 'src/agents/mcp', { name: 'MCP' });
    const result = agents(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects.map((entry) => entry.root), ['src/agents/mcp', 'src/agents/mcp-server']);
    assert.deepEqual(result.projects[1].dependencies, ['src/agents/mcp']);
  });

  it('skips hidden, ignored, and symbolic-link folders and leaves the Analyst area unchanged', () => {
    const root = repository();
    for (const name of ['.hidden', '.venv', 'venv', 'node_modules', '__pycache__', '.git', 'build', 'dist', 'site-packages']) {
      project(root, `src/agents/${name}`, { name: `x-${name}` });
    }
    project(root, 'elsewhere', { name: 'linked' });
    symlinkSync(path.join(root, 'elsewhere'), path.join(root, 'src/agents/linked'));
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    assert.deepEqual(agents(root).projects, []);
    assert.deepEqual(discover(root).projects.map((entry) => entry.root), ['src/analysts/sdk']);
  });

  it('fails the area root and nested manifests as an unsupported layout', () => {
    const root = repository();
    project(root, 'src/agents', { name: 'area' });
    project(root, 'src/agents/one/nested', { name: 'nested' });
    project(root, 'src/agents/one', { name: 'one' });
    const result = rejectedAgents(root, /project src\/agents\/pyproject\.toml: project convention: unsupported layout/);
    assert.match(result.stderr, /project src\/agents\/one\/nested\/pyproject\.toml: project convention: unsupported layout/);
    assert.match(result.stderr, /Recommended action: move the project to src\/agents\/<name>, or author the required environment extension, before retrying/);
  });

  it('applies every convention check and names the agent project', () => {
    const cases = [
      [{ raw: '[project\n' }, /malformed/],
      [{ name: 'a', requiresPython: '<3.0' }, /runtime compatibility: requires-python/],
      [{ name: 'a', build: false }, /\[build-system\]/],
      [{ name: 'a', extra: '[tool.uv]\npackage = false' }, /package = false/],
      [{ name: 'a', extra: '[tool.uv.workspace]\nmembers = ["x"]' }, /workspace/],
      [{ name: 'a', dependencies: ['other'], extra: '[tool.uv.sources]\nother = { git = "https://example.test/x.git" }' }, /must be a path/],
      [{ name: 'a', sources: { other: '../other' } }, /not in the project requirements/],
      [{ name: 'a', dependencies: ['other'], sources: { other: '../missing' } }, /has no project/],
      [{ name: 'a', dependencies: ['sdk @ file:///repo/src/analysts/sdk'] }, /direct reference requirement/],
      [{ name: 'a', optionalDependencies: { test: ['sdk @ file:///repo/src/analysts/sdk'] } }, /direct reference requirement/],
      [{ name: 'a', dependencyGroups: { dev: ['sdk @ file:///repo/src/analysts/sdk'] } }, /direct reference requirement/],
    ];
    for (const [options, pattern] of cases) {
      const root = repository();
      project(root, 'src/agents/a', options);
      rejectedAgents(root, new RegExp(`project src/agents/a: .*${pattern.source}`));
    }
    const root = repository();
    project(root, 'src/agents/a', { name: 'a' }, { lock: false });
    rejectedAgents(root, /project src\/agents\/a: locked sync: uv\.lock is missing/);
  });

  it('rejects duplicate names and cycles', () => {
    const root = repository();
    project(root, 'src/agents/a', { name: 'same' });
    project(root, 'src/agents/b', { name: 'Same' });
    rejectedAgents(root, /project src\/agents\/b: project convention: project name 'same' duplicates src\/agents\/a/);
    const cyclic = repository();
    project(cyclic, 'src/agents/a', { name: 'a', dependencies: ['b'], sources: { b: '../b' } });
    project(cyclic, 'src/agents/b', { name: 'b', dependencies: ['a'], sources: { a: '../a' } });
    rejectedAgents(cyclic, /local dependencies form a cycle/);
  });

  it('rejects dependencies outside the agents roots', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/agents/a', { name: 'a', dependencies: ['sdk'], sources: { sdk: '../../analysts/sdk' } });
    const result = rejectedAgents(root, /project src\/agents\/a: project convention: local dependency 'sdk' at src\/analysts\/sdk is outside the permitted ownership roots/);
    assert.match(result.stderr, /Recommended action: remove the dependency on the Analyst project or move the shared code into an agent project under src\/agents\/<name>, before retrying/);
    const capability = repository();
    project(capability, 'src/analysts/capabilities/seg', { name: 'seg' });
    project(capability, 'src/agents/a', { name: 'a', dependencies: ['seg'], sources: { seg: '../../analysts/capabilities/seg' } });
    rejectedAgents(capability, /local dependency 'seg' at src\/analysts\/capabilities\/seg is outside/);
  });

  it('keeps an Analyst dependency on an agent project failing as before', () => {
    const root = repository();
    project(root, 'src/agents/a', { name: 'a' });
    project(root, 'src/analysts/sdk', { name: 'sdk', dependencies: ['a'], sources: { a: '../../agents/a' } });
    const result = rejected(root, /project src\/analysts\/sdk: project convention: local dependency 'a' at src\/agents\/a is outside the permitted ownership roots/);
    assert.match(result.stderr, /Recommended action: correct the named manifest to the documented Python product convention/);
  });

  it('fails an agent name that repeats an Analyst name and names both projects', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/seg', { name: 'Seg_Core' });
    project(root, 'src/agents/a', { name: 'seg-core' });
    const result = rejectedAgents(root, /project src\/agents\/a: project convention: project name 'seg-core' duplicates Analyst project src\/analysts\/capabilities\/seg/);
    assert.equal(result.stderr.trim().split('\n').length, 1);
    assert.equal(discover(root).status, 0);
  });

  it('reads only static Analyst names and skips unreadable Analyst manifests', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { raw: '[project\n' });
    project(root, 'src/analysts/capabilities/dyn', { raw: '[project]\ndynamic = ["name"]\nname = "a"\n' });
    project(root, 'src/analysts/capabilities/noname', { raw: '[project]\nrequires-python = ">=3.11"\n' });
    project(root, 'src/agents/a', { name: 'a' });
    const result = agents(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects.map((entry) => entry.root), ['src/agents/a']);
  });

  it('leaves Analyst output and errors unchanged while agent projects exist', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/agents/a', { name: 'sdk' });
    project(root, 'src/agents/b', { raw: '[project\n' });
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects.map((entry) => entry.root), ['src/analysts/sdk']);
  });
});

describe('agents area selection', () => {
  const mcp = 'src/agents/mcp';
  const server = 'src/agents/mcp-server';
  const hermes = 'src/agents/hermes';

  function graph() {
    const root = repository();
    project(root, mcp, { name: 'mcp' });
    project(root, server, { name: 'mcp-server', dependencies: ['mcp'], sources: { mcp: '../mcp' } });
    project(root, hermes, { name: 'hermes' });
    return root;
  }

  function select(root, mode, changed = []) {
    const list = path.join(root, 'changed.txt');
    writeFileSync(list, changed.join('\n'));
    const result = spawnSync('python', [helper, 'select', '--area', 'agents', '--root', root, '--python-version', '3.13.5', '--mode', mode, '--changed-files', list], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    const parsed = JSON.parse(result.stdout);
    return { selected: parsed.projects.map((entry) => [entry.root, entry.reason]), covered: parsed.covered };
  }

  it('selects an own-source change and its reverse local-dependency closure once, in order', () => {
    const root = graph();
    for (const mode of ['task', 'phase']) {
      assert.deepEqual(select(root, mode, [`${mcp}/src/x.py`]).selected, [[mcp, 'direct change'], [server, 'local-dependency change']]);
      assert.deepEqual(select(root, mode, [`${server}/a.py`, `${mcp}/b.py`, `${server}/c.py`]).selected, [[mcp, 'direct change'], [server, 'direct change']]);
      assert.deepEqual(select(root, mode, [`${hermes}/pyproject.toml`]).selected, [[hermes, 'direct change']]);
    }
  });

  it('never matches prefix-similar project folders', () => {
    const root = graph();
    assert.deepEqual(select(root, 'task', [`${server}/x.py`]).selected, [[server, 'direct change']]);
    assert.deepEqual(select(root, 'task', ['src/agents/mcp-other/x.py']).selected, []);
  });

  it('selects every agent project for shared contract families', () => {
    const root = graph();
    const all = [[hermes, 'shared contract change'], [mcp, 'shared contract change'], [server, 'shared contract change']];
    for (const changed of [
      'contracts/agent-catalog/schema.json', 'contracts/agent-orchestration/x/y.json', 'contracts/agent-tools/a.json',
      'contracts/agent-x/new.json', 'contracts/common/shared.json',
    ]) {
      for (const mode of ['task', 'phase']) assert.deepEqual(select(root, mode, [changed]).selected, all, changed);
    }
  });

  it('selects nothing for unrelated, lookalike, and Analyst changes', () => {
    const root = graph();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    for (const changed of [
      'contracts/agentic/x.json', 'contracts/agent-x.md', 'contracts/analysis/x.json', 'src/analysts/sdk/x.py',
      'src/analysts/manager/x.cs', 'src/platform/x.cs', 'README.md', 'src/agents/README.md',
    ]) {
      assert.deepEqual(select(root, 'task', [changed]).selected, [], changed);
    }
  });

  it('selects every agent project for finalize and environment changes', () => {
    const root = graph();
    const expected = (reason) => [[hermes, reason], [mcp, reason], [server, reason]];
    assert.deepEqual(select(root, 'finalize').selected, expected('finalize'));
    for (const changed of ['.github/actions/environment-verify/action.yml', '.github/actions/environment-setup/scripts/x.sh']) {
      assert.deepEqual(select(root, 'task', [changed]).selected, expected('environment change'), changed);
    }
  });

  it('covers the existing valid agent roots only', () => {
    const root = graph();
    assert.deepEqual(select(root, 'task', []).covered, [hermes, mcp, server]);
    assert.deepEqual(select(repository(), 'task', []).covered, []);
  });

  it('keeps Analyst selection unchanged by agent contract folders', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    const list = path.join(root, 'changed.txt');
    writeFileSync(list, 'contracts/agent-tools/a.json\ncontracts/analysis/x.json');
    const result = spawnSync('python', [helper, 'select', '--root', root, '--python-version', '3.13.5', '--mode', 'task', '--changed-files', list], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout).projects.map((entry) => entry.reason), ['shared contract change']);
  });
});
