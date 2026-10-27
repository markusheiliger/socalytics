import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

import { buildPlan, TOOL_GROUPS } from './verification-plan.mjs';

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

function plan(root, mode, changed) {
  let changedFiles;
  if (changed !== undefined) {
    changedFiles = path.join(temporary, `changed-${counter++}.txt`);
    writeFileSync(changedFiles, changed.join('\n'));
  }
  return buildPlan({ root, mode, changedFiles });
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
    assert.equal(checks['src/clients/web'], 'Web UI build and tests');
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
