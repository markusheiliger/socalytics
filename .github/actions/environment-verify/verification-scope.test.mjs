import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

import { classify, globToRegExp, mentions, patternMatches } from './verification-scope.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const temporary = mkdtempSync(path.join(tmpdir(), 'verification-scope-test-'));
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

function changeList(changed) {
  const file = path.join(temporary, `changed-${counter++}.txt`);
  writeFileSync(file, changed.join('\n'));
  return file;
}

const scope = (root, changed) => classify(root, changeList(changed));

describe('documentation-only classification', () => {
  it('accepts a README in a test project that nothing reads', () => {
    const root = repository();
    touch(root, 'src/platform/Tests/E2E.Tests/E2E.Tests.csproj', '<Project><ItemGroup><None Include="TestAnalyst\\**" /></ItemGroup></Project>');
    touch(root, 'src/platform/Tests/E2E.Tests/README.md', '# E2E');
    const result = scope(root, ['src/platform/Tests/E2E.Tests/README.md']);
    assert.equal(result.documentationOnly, true, result.reason);
  });

  it('accepts documentation in every area, specs, and the repository root', () => {
    const root = repository();
    const files = [
      'README.md', 'AGENTS.md', 'docs/architecture/overview.md', 'specs/20260101-000000-x/spec.md',
      'specs/archive/20260101-000000-y/spec.md', 'src/platform/README.md', 'src/analysts/manager/README.md',
      'src/clients/web/README.md', 'src/analysts/sdk/README.md', 'src/agents/README.md', '.github/workflows/README.md',
    ];
    for (const file of files) touch(root, file, '# doc');
    touch(root, 'src/analysts/sdk/pyproject.toml', '[project]\nname = "sdk"\n[tool.hatch.build.targets.wheel]\npackages = ["src/sdk"]\n');
    touch(root, 'src/clients/web/vite.config.ts', '// see research.md § R11 and spec.md\n');
    assert.equal(scope(root, files).documentationOnly, true);
  });

  it('refuses a missing, unreadable, or empty change list', () => {
    const root = repository();
    assert.deepEqual(classify(root, ''), { documentationOnly: false, reason: 'no change list was given' });
    assert.match(classify(root, path.join(temporary, 'absent.txt')).reason, /cannot be read/);
    assert.deepEqual(scope(root, []), { documentationOnly: false, reason: 'the change list is empty' });
    assert.deepEqual(scope(root, ['', '  ']), { documentationOnly: false, reason: 'the change list is empty' });
  });

  it('refuses a change with any non-Markdown path', () => {
    const root = repository();
    for (const other of ['src/platform/a.cs', 'docs/diagram.png', 'specs/x/contracts/a.json', 'README.MD', 'notes.markdown']) {
      const result = scope(root, ['docs/a.md', other]);
      assert.equal(result.documentationOnly, false, other);
      assert.equal(result.reason, `${other} is not Markdown`);
    }
  });

  it('reports Spec Kit tooling files with the tooling reason', () => {
    const root = repository();
    for (const tooling of ['.github/scripts/speckit-implement-core.mjs', '.github/scripts/speckit-implement-core.test.mjs']) {
      const result = scope(root, [tooling]);
      assert.equal(result.documentationOnly, false, tooling);
      assert.equal(result.reason, `${tooling} is a Spec Kit tooling file covered by the Spec Kit tooling tests`);
    }
  });

  it('reports the four sample tooling files with the sample tooling reason', () => {
    const root = repository();
    for (const tooling of ['tools/samples.test.mjs', 'tools/samples-lib.mjs', 'tools/upload-samples.mjs', 'tools/download-samples.mjs']) {
      const result = scope(root, [tooling]);
      assert.equal(result.documentationOnly, false, tooling);
      assert.equal(result.reason, `${tooling} is a sample tooling file covered by the Sample tooling tests`);
    }
  });

  it('keeps other files under tools/ as not Markdown', () => {
    const root = repository();
    for (const other of ['tools/other.mjs', 'tools/samples.test.mjs.bak', 'tools/sub/samples-lib.mjs', 'tools/Samples-Lib.mjs']) {
      const result = scope(root, [other]);
      assert.equal(result.documentationOnly, false, other);
      assert.equal(result.reason, `${other} is not Markdown`);
    }
  });

  it('keeps Markdown under tools/ covered by the Markdown rule', () => {
    const root = repository();
    const readme = scope(root, ['tools/README.md']);
    assert.equal(readme.documentationOnly, true);
    assert.match(readme.reason, /^1 Markdown file\(s\) that no build, package, or test reads/);
    const mixed = scope(root, ['tools/README.md', 'tools/samples-lib.mjs']);
    assert.equal(mixed.documentationOnly, false);
    assert.equal(mixed.reason, 'tools/samples-lib.mjs is a sample tooling file covered by the Sample tooling tests');
  });

  it('reports the first non-Markdown file of a mixed list', () => {
    const root = repository();
    const tooling = '.github/scripts/speckit-implement-core.mjs';
    const first = scope(root, ['docs/a.md', tooling, 'src/platform/a.cs']);
    assert.equal(first.documentationOnly, false);
    assert.equal(first.reason, `${tooling} is a Spec Kit tooling file covered by the Spec Kit tooling tests`);
    const other = scope(root, ['docs/a.md', 'src/platform/a.cs', tooling]);
    assert.equal(other.documentationOnly, false);
    assert.equal(other.reason, 'src/platform/a.cs is not Markdown');
  });

  it('keeps other .github/scripts files as not Markdown', () => {
    const root = repository();
    for (const other of ['.github/scripts/helper.mjs', '.github/scripts/sub/speckit-x.mjs', '.github/scripts/speckit-x.sh']) {
      const result = scope(root, [other]);
      assert.equal(result.documentationOnly, false, other);
      assert.equal(result.reason, `${other} is not Markdown`);
    }
  });

  it('refuses Markdown that a project file includes through a glob, and ignores globs for other file types', () => {
    const root = repository();
    touch(root, 'src/platform/Tests/Contracts.Tests/Contracts.Tests.csproj',
      '<Project><ItemGroup><None Include="..\\..\\..\\..\\contracts\\**\\*" CopyToOutputDirectory="PreserveNewest" /></ItemGroup></Project>');
    touch(root, 'src/platform/Infrastructure/Infrastructure.csproj',
      '<Project><ItemGroup><EmbeddedResource Include="..\\..\\..\\contracts\\**\\*.schema.json;Seed/*.json" /></ItemGroup></Project>');
    touch(root, 'contracts/README.md');
    touch(root, 'contracts/analysis/README.md');
    const result = scope(root, ['contracts/analysis/README.md']);
    assert.equal(result.documentationOnly, false);
    assert.match(result.reason, /Contracts\.Tests\.csproj/);
    assert.doesNotMatch(result.reason, /Infrastructure\.csproj/);
  });

  it('refuses Markdown that a test names by a unique file name', () => {
    const root = repository();
    touch(root, 'docs/architecture/intelligence-agent-catalog.md');
    touch(root, 'src/platform/Tests/Integration.Tests/SeedTests.cs',
      'var lines = File.ReadAllLines(RepositoryFile("docs", "architecture", "intelligence-agent-catalog.md"));');
    const result = scope(root, ['docs/architecture/intelligence-agent-catalog.md']);
    assert.equal(result.documentationOnly, false);
    assert.match(result.reason, /SeedTests\.cs/);
  });

  it('ignores a shared file name named elsewhere, but not from the same folder', () => {
    const root = repository();
    touch(root, 'specs/a/research.md');
    touch(root, 'specs/b/research.md');
    touch(root, 'src/clients/web/vite.config.ts', '// research.md § R11');
    assert.equal(scope(root, ['specs/a/research.md']).documentationOnly, true);
    touch(root, 'src/analysts/sdk/README.md');
    touch(root, 'src/analysts/sdk/pyproject.toml', '[project]\nname = "sdk"\nreadme = "README.md"\n');
    const result = scope(root, ['src/analysts/sdk/README.md']);
    assert.equal(result.documentationOnly, false);
    assert.match(result.reason, /src\/analysts\/sdk\/pyproject\.toml/);
  });

  it('refuses Markdown named by its repository or relative path, but not a bare root file name', () => {
    const root = repository();
    touch(root, 'docs/guide.md');
    touch(root, 'docs/other/guide.md');
    touch(root, 'src/platform/Docs.Tests/Reader.cs', 'File.ReadAllText(Path.Combine(Root, "../../docs/guide.md"));');
    assert.equal(scope(root, ['docs/guide.md']).documentationOnly, false);
    assert.equal(scope(root, ['docs/other/guide.md']).documentationOnly, true);
    touch(root, 'README.md');
    touch(root, 'contracts/README.md');
    touch(root, 'src/platform/Contracts.Tests/Catalog.cs', 'File.ReadAllLines(Path.Combine(Root, "README.md"));');
    assert.equal(scope(root, ['README.md']).documentationOnly, true);
  });

  it('refuses Markdown inside a Python package path or a local path dependency', () => {
    const root = repository();
    touch(root, 'src/analysts/sdk/src/sdk/NOTES.md');
    touch(root, 'src/analysts/sdk/pyproject.toml', '[project]\nname = "sdk"\n[tool.hatch.build.targets.wheel]\npackages = ["src/sdk"]\n');
    assert.equal(scope(root, ['src/analysts/sdk/src/sdk/NOTES.md']).documentationOnly, false);
    touch(root, 'src/analysts/sdk/GUIDE.md');
    touch(root, 'src/analysts/sdk/other/GUIDE.md');
    touch(root, 'src/analysts/capabilities/probe/pyproject.toml', '[project]\nname = "probe"\n[tool.uv.sources]\nsdk = { path = "../../sdk" }\n');
    assert.equal(scope(root, ['src/analysts/sdk/GUIDE.md']).documentationOnly, false);
  });

  it('classifies a deleted Markdown file by the references that remain', () => {
    const root = repository();
    touch(root, 'src/platform/Tests/Reader.cs', 'Read("removed-catalog.md");');
    assert.equal(scope(root, ['docs/removed-catalog.md']).documentationOnly, false);
    assert.equal(scope(root, ['docs/removed-guide.md']).documentationOnly, true);
  });

  it('never counts tooling, specs, or generated folders as consumers', () => {
    const root = repository();
    touch(root, 'docs/unique-topic.md');
    touch(root, '.github/scripts/check.mjs', "read('docs/unique-topic.md')");
    touch(root, '.specify/scripts/x.py', "open('docs/unique-topic.md')");
    touch(root, 'specs/x/data.json', '"docs/unique-topic.md"');
    touch(root, 'src/clients/web/node_modules/pkg/index.js', "require('docs/unique-topic.md')");
    touch(root, 'src/platform/Api/bin/Debug/x.json', '"docs/unique-topic.md"');
    assert.equal(scope(root, ['docs/unique-topic.md']).documentationOnly, true);
  });

  it('matches path boundaries and globs exactly', () => {
    assert.equal(mentions('see "README.md"', 'README.md'), true);
    assert.equal(mentions('see src/x/README.md', 'README.md'), false);
    assert.equal(mentions('see src/x/README.md', 'README.md', { nested: true }), true);
    assert.equal(mentions('xREADME.md', 'README.md', { nested: true }), false);
    assert.equal(mentions('README.mdx', 'README.md'), false);
    assert.equal(mentions('"../docs/a.md"', 'docs/a.md'), true);
    assert.equal(globToRegExp('contracts/**/*').test('contracts/a/b/README.md'), true);
    assert.equal(globToRegExp('contracts/**/*').test('contracts/README.md'), true);
    assert.equal(globToRegExp('contracts/*.json').test('contracts/a/b.json'), false);
    assert.equal(globToRegExp('a/**').test('a/b/c.md'), true);
    assert.equal(patternMatches('src/p', '..\\..\\docs\\**', 'docs/a.md'), true);
    assert.equal(patternMatches('src/p', 'https://example.invalid/a.md', 'src/p/a.md'), false);
    assert.equal(patternMatches('', '../outside/**', 'outside/a.md'), false);
  });

  it('prints the decision and exits 0 only for documentation-only changes', () => {
    const root = repository();
    touch(root, 'docs/a.md');
    const script = path.join(here, 'verification-scope.mjs');
    const yes = spawnSync('node', [script, '--root', root, '--changed-files', changeList(['docs/a.md'])], { encoding: 'utf8' });
    assert.equal(yes.status, 0);
    assert.match(yes.stdout, /^documentation-only: 1 Markdown file/);
    const no = spawnSync('node', [script, '--root', root, '--changed-files', changeList(['docs/a.md', 'src/a.cs'])], { encoding: 'utf8' });
    assert.equal(no.status, 1);
    assert.equal(no.stdout.trim(), 'full: src/a.cs is not Markdown');
    const usage = spawnSync('node', [script, '--changed-files'], { encoding: 'utf8' });
    assert.equal(usage.status, 2);
  });
});

describe('verification with a documentation-only change', () => {
  const actionText = readFileSync(path.join(here, 'action.yml'), 'utf8').replace(/\r\n/g, '\n');
  const script = actionText.split('      run: |\n')[1].split('\n').map((line) => line.slice(8)).join('\n');
  const python = spawnSync('python3', ['-c', 'import sys;print(sys.executable)'], { encoding: 'utf8' }).stdout.trim();

  // Stubs record every external command, so no solution, package index, or runner is contacted.
  function toolBin() {
    const bin = path.join(temporary, `bin-${counter++}`);
    mkdirSync(bin, { recursive: true });
    for (const tool of ['dotnet', 'npm', 'uv']) {
      writeFileSync(path.join(bin, tool), `#!/bin/bash\necho "${tool}|$*" >> "$TOOL_CALLS"\nexit 0\n`, { mode: 0o755 });
    }
    return bin;
  }

  const declaration = (root) => readFileSync(path.join(here, '..', '..', '..', root, 'verification.json'), 'utf8');

  function fullRepository() {
    const root = repository();
    touch(root, 'src/platform/SocAlytics.Platform.slnx');
    touch(root, 'src/platform/verification.json', declaration('src/platform'));
    touch(root, 'src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/SocAlytics.Platform.EndToEnd.Tests.csproj',
      '<Project><ItemGroup><None Include="TestAnalyst\\**" /></ItemGroup></Project>');
    touch(root, 'src/platform/Tests/SocAlytics.Platform.Contracts.Tests/SocAlytics.Platform.Contracts.Tests.csproj',
      '<Project><ItemGroup><None Include="..\\..\\..\\..\\contracts\\**\\*" /></ItemGroup></Project>');
    touch(root, 'src/platform/global.json', '{"sdk":{"version":"10.0.100"}}');
    touch(root, 'src/analysts/manager/SocAlytics.Analysts.Manager.slnx');
    touch(root, 'src/analysts/manager/verification.json', declaration('src/analysts/manager'));
    touch(root, 'src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/SocAlytics.Analysts.Manager.Tests.csproj');
    touch(root, 'src/analysts/manager/global.json', '{"sdk":{"version":"10.0.100"}}');
    touch(root, 'src/clients/web/package.json', '{"scripts":{"build":"b","test":"t","test:e2e":"e"}}');
    touch(root, 'src/clients/web/verification.json', declaration('src/clients/web'));
    touch(root, 'src/clients/web/node_modules/@playwright/test/package.json', '{"version":"1.63.0"}');
    touch(root, 'contracts/README.md');
    return root;
  }

  function run(root, { mode, changed, env = {} }) {
    const dir = path.join(temporary, `run-${counter++}`);
    mkdirSync(dir, { recursive: true });
    const files = {
      changed: path.join(dir, 'changed.txt'), log: path.join(dir, 'log.txt'), output: path.join(dir, 'output.txt'),
      summary: path.join(dir, 'summary.md'), calls: path.join(dir, 'calls.txt'), markdown: path.join(dir, 'markdown.txt'),
      stub: path.join(dir, 'markdown-stub.mjs'),
    };
    if (changed !== undefined) writeFileSync(files.changed, changed.join('\n'));
    writeFileSync(files.calls, '');
    writeFileSync(files.output, '');
    writeFileSync(files.markdown, '');
    writeFileSync(files.stub, `import { appendFileSync } from 'node:fs';\nappendFileSync(${JSON.stringify(files.markdown)}, ['markdown', ...process.argv.slice(2)].join(' ') + '\\n');\n`);
    const result = spawnSync('bash', ['-c', script], {
      encoding: 'utf8',
      env: {
        PATH: `${toolBin()}:${process.env.PATH}`, HOME: process.env.HOME,
        WORKSPACE: root, MODE: mode, CHANGED_FILES: changed === undefined ? '' : files.changed, LOG_FILE: files.log,
        MARKDOWN_CHECK: files.stub, ANALYST_PROJECTS: path.join(here, 'analyst-projects.py'),
        VERIFICATION_PLAN: path.join(here, 'verification-plan.mjs'),
        GITHUB_STEP_SUMMARY: files.summary, GITHUB_OUTPUT: files.output, TOOL_CALLS: files.calls, SOCALYTICS_PYTHON: python,
        ...env,
      },
    });
    const outputs = Object.fromEntries(readFileSync(files.output, 'utf8').split('\n').filter(Boolean).map((line) => line.split(/=(.*)/s).slice(0, 2)));
    return {
      status: result.status, outputs, log: readFileSync(files.log, 'utf8'), summary: readFileSync(files.summary, 'utf8'),
      calls: readFileSync(files.calls, 'utf8').split('\n').filter(Boolean),
      markdown: readFileSync(files.markdown, 'utf8').split('\n').filter(Boolean),
    };
  }

  const readme = 'src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/README.md';

  for (const mode of ['task', 'phase', 'finalize']) {
    it(`runs only the Markdown check for a test project README in ${mode} mode`, () => {
      const root = fullRepository();
      touch(root, readme, '# E2E');
      const result = run(root, { mode, changed: [readme] });
      assert.equal(result.status, 0, result.log);
      assert.equal(result.outputs.checks, 'Markdown check');
      assert.deepEqual(result.calls, []);
      assert.equal(result.outputs.uncovered, '');
      assert.match(result.log, /Verification scope: documentation-only: /);
      assert.match(result.summary, /### Verification scope/);
      assert.equal(result.markdown.length, 1);
      assert.match(result.markdown[0], mode === 'task' ? /^markdown --files \S+changed\.txt$/ : /^markdown$/);
    });
  }

  it('checks only the changed Markdown in task mode and the whole repository in phase and finalize mode', () => {
    const root = fullRepository();
    touch(root, 'docs/a.md');
    const task = run(root, { mode: 'task', changed: ['docs/a.md'] });
    assert.equal(task.markdown.length, 1);
    assert.match(task.markdown[0], /^markdown --files \S+changed\.txt$/);
    for (const mode of ['phase', 'finalize']) assert.deepEqual(run(root, { mode, changed: ['docs/a.md'] }).markdown, ['markdown']);
  });

  it('keeps every check for a mixed change', () => {
    const root = fullRepository();
    touch(root, readme);
    const result = run(root, { mode: 'finalize', changed: [readme, 'src/platform/a.cs'] });
    assert.equal(result.status, 0, result.log);
    assert.match(result.log, /Verification scope: full: src\/platform\/a\.cs is not Markdown/);
    assert.ok(result.outputs.checks.startsWith('platform build and tests,Analyst Manager build and tests,Web UI build and tests'));
    assert.ok(result.calls.some((call) => call.startsWith('dotnet|build')));
    assert.ok(result.calls.some((call) => call.startsWith('npm|run test:e2e')));
  });

  it('keeps every check for Markdown that a test project reads', () => {
    const root = fullRepository();
    const result = run(root, { mode: 'finalize', changed: ['contracts/README.md'] });
    assert.match(result.log, /Verification scope: full: contracts\/README\.md may be read by /);
    assert.ok(result.outputs.checks.includes('platform build and tests'));
    assert.ok(result.calls.some((call) => call.startsWith('dotnet|test')));
  });

  it('keeps every check without a change list', () => {
    const root = fullRepository();
    const result = run(root, { mode: 'finalize' });
    assert.match(result.log, /Verification scope: full: no change list was given/);
    assert.ok(result.outputs.checks.includes('platform build and tests'));
    const empty = run(root, { mode: 'finalize', changed: [] });
    assert.match(empty.log, /Verification scope: full: the change list is empty/);
    assert.ok(empty.outputs.checks.includes('Web UI build and tests'));
  });

  it('fails closed when the verification plan is not configured', () => {
    const root = fullRepository();
    touch(root, readme);
    const result = run(root, { mode: 'finalize', changed: [readme], env: { VERIFICATION_PLAN: '' } });
    assert.equal(result.status, 1);
    assert.match(result.log, /Verification scope: full: no verification plan/);
    assert.match(result.log, /Verification plan: the verification plan is not configured/);
    assert.equal(result.outputs.checks, 'Verification plan');
    assert.equal(result.outputs.uncovered, readme);
    assert.deepEqual(result.calls, []);
  });

  it('still fails an invalid Python project and still fails the Markdown check', () => {
    const root = fullRepository();
    touch(root, 'src/analysts/sdk/pyproject.toml', '[project');
    touch(root, 'docs/a.md');
    const invalid = run(root, { mode: 'finalize', changed: ['docs/a.md'] });
    assert.equal(invalid.status, 1);
    assert.equal(invalid.outputs.checks, 'Analyst Python projects');
    assert.deepEqual(invalid.calls, []);
    const failing = path.join(temporary, `failing-markdown-${counter++}.mjs`);
    writeFileSync(failing, 'process.exit(1);\n');
    const markdown = run(fullRepository(), { mode: 'phase', changed: ['docs/a.md'], env: { MARKDOWN_CHECK: failing } });
    assert.equal(markdown.status, 1);
    assert.equal(markdown.outputs.checks, 'Markdown check');
  });

  it('runs no environment action tests for a documentation-only change to an action README', () => {
    const root = fullRepository();
    const record = path.join(temporary, `env-record-${counter++}.txt`);
    const test = path.join(temporary, `env-${counter++}.test.mjs`);
    writeFileSync(test, `import { appendFileSync } from 'node:fs';\nappendFileSync(${JSON.stringify(record)}, 'ran\\n');\n`);
    const result = run(root, { mode: 'task', changed: ['.github/actions/environment-verify/README.md'], env: { ENVIRONMENT_TESTS: test } });
    assert.equal(result.outputs.checks, 'Markdown check');
    assert.throws(() => readFileSync(record));
  });

  it('is listed among the environment action tests', () => {
    assert.match(actionText.match(/ENVIRONMENT_TESTS: (.*)/)[1], /action_path \}\}\/verification-scope\.test\.mjs/);
    assert.match(actionText, /VERIFICATION_PLAN: \$\{\{ github\.action_path \}\}\/verification-plan\.mjs/);
  });
});
