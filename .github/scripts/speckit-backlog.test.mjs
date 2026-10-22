import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { BACKLOG_RETRY_MS, BACKLOG_WORKFLOW_FILE, checkLanded, landSpecs, maintainBacklog, parseBacklogSize, requestableSpecs, runAuthor, runLand } from './speckit-backlog.mjs';
import { discoverSpecs, runSync } from './speckit-prepare.mjs';
import { FakeGitHub, envFor, makeRepo, silent } from './speckit-test-helpers.mjs';

const TASKS = '- [ ] T001 one\n';
const IMPLEMENT = 'speckit:stage:implement';

async function twinsFor(folders) {
  const root = makeRepo(folders);
  const github = new FakeGitHub();
  await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
  return { root, github };
}

const backlogRuns = (github) => github.repo.runs.filter((run) => run.workflow === BACKLOG_WORKFLOW_FILE);

test('parses the refill size: off unless a positive whole number, capped at 10', () => {
  assert.equal(parseBacklogSize(undefined), 0);
  assert.equal(parseBacklogSize(''), 0);
  assert.equal(parseBacklogSize('0'), 0);
  assert.equal(parseBacklogSize('-2'), 0);
  assert.equal(parseBacklogSize('2.5'), 0);
  assert.equal(parseBacklogSize('5'), 5);
  assert.equal(parseBacklogSize(' 12 '), 10);
});

test('requestable specs are tasked, fully checked, not archived, and not requested yet', async () => {
  const { root, github } = await twinsFor([
    { folder: 'a', plan: true, tasks: TASKS },
    { folder: 'b', plan: true },
    { folder: 'c', plan: true, tasks: TASKS, checklists: { 'requirements.md': '- [ ] open\n' } },
    { folder: 'd', plan: true, tasks: '- [x] T001 one\n- [ ] T002 two\n' },
    { folder: 'e', plan: true, tasks: TASKS },
  ]);
  try {
    // A folder without a twin yet (Spec Kit prepare creates it) is requestable.
    mkdirSync(path.join(root, 'specs', 'f'));
    writeFileSync(path.join(root, 'specs', 'f', 'spec.md'), '# Feature Specification: f\n');
    writeFileSync(path.join(root, 'specs', 'f', 'plan.md'), '# plan\n');
    writeFileSync(path.join(root, 'specs', 'f', 'tasks.md'), TASKS);
    const twins = await github.listTwinIssues('speckit:spec');
    const byFolder = new Map(twins.map((issue) => [issue.body.match(/specs\/([\w-]+)/)[1], issue]));
    assert.deepEqual(requestableSpecs(discoverSpecs(root), byFolder), ['a', 'e', 'f']);
    byFolder.get('e').labels.push({ name: IMPLEMENT });
    byFolder.get('a').state = 'closed';
    assert.deepEqual(requestableSpecs(discoverSpecs(root), byFolder), ['f']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the refill starts only when no spec is left to request, one at a time, and a day after a failure', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: TASKS }]);
  const env = { ...envFor(root), SPECKIT_BACKLOG_SPECS: '5' };
  try {
    assert.equal((await maintainBacklog(github, { ...env, SPECKIT_BACKLOG_SPECS: '' }, { rootDir: root, now: github.clock })).started, false, 'off without the variable');

    let result = await maintainBacklog(github, env, { rootDir: root, now: github.clock });
    assert.equal(result.started, false);
    assert.match(result.lines[0], /1 spec\(s\) can be requested/);

    github.find(github.issues[0].number).labels.push({ name: IMPLEMENT });
    result = await maintainBacklog(github, env, { rootDir: root, now: github.clock });
    assert.equal(result.started, true);
    assert.deepEqual(backlogRuns(github).map((run) => [run.ref, run.inputs]), [['main', { count: '5' }]]);

    result = await maintainBacklog(github, env, { rootDir: root, now: github.clock });
    assert.equal(result.started, false, 'one refill at a time');
    assert.match(result.lines[0], /is authoring new specs/);

    github.completeRun(backlogRuns(github)[0].id, 'failure');
    result = await maintainBacklog(github, env, { rootDir: root, now: github.clock });
    assert.equal(result.started, false, 'no automatic retry right after a failure');
    assert.match(result.lines[0], /ended `failure`/);

    result = await maintainBacklog(github, env, { rootDir: root, now: github.clock + BACKLOG_RETRY_MS + 1000 });
    assert.equal(result.started, true, 'a day later it starts again');
    assert.equal(backlogRuns(github).length, 2);

    github.completeRun(backlogRuns(github)[1].id, 'success');
    result = await maintainBacklog(github, env, { rootDir: root, now: github.clock + BACKLOG_RETRY_MS + 2000 });
    assert.equal(result.started, true, 'after a successful refill whose specs were all requested');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// ── author and land against real git repositories ──────────────────────────────────────────────────────────────────

function gitIn(cwd) {
  return (args) => {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8' });
    return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  };
}

function must(git, args) {
  const result = git(args);
  assert.equal(result.status, 0, `git ${args.join(' ')}: ${result.stderr}`);
  return result.stdout;
}

function initRepo(dir) {
  mkdirSync(dir, { recursive: true });
  const git = gitIn(dir);
  must(git, ['init', '--quiet', '-b', 'main']);
  must(git, ['config', 'user.name', 'test']);
  must(git, ['config', 'user.email', 'test@example.com']);
  mkdirSync(path.join(dir, 'docs', 'architecture'), { recursive: true });
  writeFileSync(path.join(dir, 'docs', 'architecture', 'README.md'), '# Architecture\n');
  mkdirSync(path.join(dir, 'specs'), { recursive: true });
  writeFileSync(path.join(dir, 'specs', 'README.md'), '# Specs\n');
  must(git, ['add', '-A']);
  must(git, ['commit', '--quiet', '-m', 'init']);
  return git;
}

// A fake `specify workflow run author`: commits one spec per call, like a run with publish=commit, and leaves the
// engine's state the way the real run does; `fail` makes the given call fail.
function fakeSpecify(root, git, { fail = 0 } = {}) {
  let calls = 0;
  return (args) => {
    calls += 1;
    assert.deepEqual(args.slice(0, 2), ['workflow', 'run']);
    assert.ok(args.includes('mode=unattended') && args.includes('publish=commit'));
    const run = `run${calls}`;
    const runPath = path.join(root, '.specify', 'workflows', 'runs', run);
    mkdirSync(runPath, { recursive: true });
    const folder = `20261022-10000${calls}-spec-${calls}`;
    if (calls === fail) {
      writeFileSync(path.join(runPath, 'author.json'), JSON.stringify({ folder }));
      writeFileSync(path.join(runPath, 'state.json'), JSON.stringify({ status: 'failed', current_step_id: 'specify', error: 'the agent wrote no spec' }));
      return { status: 1, stdout: JSON.stringify({ run_id: run }) };
    }
    mkdirSync(path.join(root, 'specs', folder));
    writeFileSync(path.join(root, 'specs', folder, 'spec.md'), `# Feature Specification: Spec ${calls}\n`);
    writeFileSync(path.join(root, 'specs', folder, 'tasks.md'), TASKS);
    writeFileSync(path.join(root, 'docs', 'architecture', 'README.md'), `# Architecture\n\n${'- spec\n'.repeat(calls)}`);
    must(git, ['add', '-A', '--', `specs/${folder}`, 'docs/architecture']);
    must(git, ['commit', '--quiet', '-m', `Spec: Spec ${calls}`]);
    writeFileSync(path.join(runPath, 'author.json'), JSON.stringify({ folder, title: `Spec ${calls}`, architecture: ['docs/architecture/README.md'] }));
    writeFileSync(path.join(runPath, 'state.json'), JSON.stringify({
      status: 'completed',
      current_step_id: 'result',
      step_results: { 'publish-input': { output: { data: { action: 'commit', folder, committed: true, pushed: false } } } },
    }));
    return { status: 0, stdout: JSON.stringify({ run_id: run }) };
  };
}

function setup() {
  const base = mkdtempSync(path.join(tmpdir(), 'speckit-backlog-'));
  const origin = path.join(base, 'origin.git');
  initRepo(origin);
  must(gitIn(origin), ['config', 'receive.denyCurrentBranch', 'ignore']);
  const workspace = path.join(base, 'workspace');
  must(gitIn(base), ['clone', '--quiet', origin, workspace]);
  const git = gitIn(workspace);
  must(git, ['config', 'user.name', 'bot']);
  must(git, ['config', 'user.email', 'bot@example.com']);
  const land = path.join(base, 'land');
  must(gitIn(base), ['clone', '--quiet', origin, land]);
  must(gitIn(land), ['config', 'user.name', 'person']);
  must(gitIn(land), ['config', 'user.email', 'person@example.com']);
  return { base, origin, workspace, git, land, landGit: gitIn(land), result: path.join(base, 'result') };
}

// The land job pushes to `<server>/<repository>.git`; the tests point it at the local origin.
function landEnv(ctx, extra = {}) {
  return {
    ...envFor(ctx.base),
    GITHUB_SERVER_URL: `file://${path.dirname(ctx.origin)}`,
    GITHUB_REPOSITORY: 'origin',
    GITHUB_TOKEN: 'token',
    SPECKIT_RESULT_DIR: ctx.result,
    ...extra,
  };
}

test('author runs the Spec Kit workflow author n times and packages every committed spec', async () => {
  const ctx = setup();
  try {
    const env = { SPECKIT_BACKLOG_COUNT: '3', SPECKIT_TOOLING: ctx.workspace, SPECKIT_RESULT_DIR: ctx.result };
    await runAuthor({ env, log: silent, git: ctx.git, rootDir: ctx.workspace, specify: fakeSpecify(ctx.workspace, ctx.git) });
    const result = JSON.parse(readFileSync(path.join(ctx.result, 'result.json'), 'utf8'));
    assert.equal(result.error, null);
    assert.deepEqual(result.specs.map((spec) => [spec.folder, spec.title]), [
      ['20261022-100001-spec-1', 'Spec 1'],
      ['20261022-100002-spec-2', 'Spec 2'],
      ['20261022-100003-spec-3', 'Spec 3'],
    ]);
    assert.equal((readFileSync(path.join(ctx.result, 'specs.patch'), 'utf8').match(/^Subject: /gm) ?? []).length, 3);

    // Land: someone pushed to the default branch meanwhile; the specs are applied on the new head and pushed.
    writeFileSync(path.join(ctx.land, 'README.md'), 'other\n');
    must(ctx.landGit, ['add', '-A']);
    must(ctx.landGit, ['commit', '--quiet', '-m', 'other']);
    must(ctx.landGit, ['push', '--quiet', 'origin', 'HEAD:main']);
    must(ctx.landGit, ['reset', '--quiet', '--hard', 'HEAD~1']);
    const github = new FakeGitHub();
    const lines = [];
    const landed = await runLand({ client: github, env: landEnv(ctx), log: (line) => lines.push(line), git: ctx.landGit });
    assert.equal(landed.exitCode, 0, lines.join('\n'));
    const log = must(gitIn(ctx.origin), ['log', '--format=%s', 'main']).trim().split('\n');
    assert.deepEqual(log, ['Spec: Spec 3', 'Spec: Spec 2', 'Spec: Spec 1', 'other', 'init']);
    assert.deepEqual(github.repo.runs.map((run) => [run.workflow, run.inputs]), [['speckit-prepare.yml', { dry_run: 'false' }]]);
    assert.match(lines.join('\n'), /Published 3 new spec\(s\) on `main`/);
  } finally {
    rmSync(ctx.base, { recursive: true, force: true });
  }
});

test('author stops at the first failed run; land publishes the specs before it and fails the run', async () => {
  const ctx = setup();
  try {
    const env = { SPECKIT_BACKLOG_COUNT: '5', SPECKIT_TOOLING: ctx.workspace, SPECKIT_RESULT_DIR: ctx.result };
    await runAuthor({ env, log: silent, git: ctx.git, rootDir: ctx.workspace, specify: fakeSpecify(ctx.workspace, ctx.git, { fail: 2 }) });
    const result = JSON.parse(readFileSync(path.join(ctx.result, 'result.json'), 'utf8'));
    assert.equal(result.specs.length, 1);
    assert.equal(result.runs.length, 2, 'no run after the failed one');
    assert.match(result.error, /^Spec 2 of 5 \(specs\/20261022-100002-spec-2\): the agent wrote no spec/);
    const lines = [];
    const landed = await runLand({ client: new FakeGitHub(), env: landEnv(ctx), log: (line) => lines.push(line), git: ctx.landGit });
    assert.equal(landed.exitCode, 1);
    assert.match(lines.join('\n'), /Published 1 new spec\(s\)/);
    assert.match(lines.join('\n'), /\*\*Stopped\*\*: Spec 2 of 5/);
    assert.equal(must(gitIn(ctx.origin), ['log', '-1', '--format=%s', 'main']).trim(), 'Spec: Spec 1');
  } finally {
    rmSync(ctx.base, { recursive: true, force: true });
  }
});

test('author stops before a run that does not fit into the time budget', async () => {
  const ctx = setup();
  try {
    let clock = 0;
    const specify = fakeSpecify(ctx.workspace, ctx.git);
    const env = { SPECKIT_BACKLOG_COUNT: '5', SPECKIT_TOOLING: ctx.workspace, SPECKIT_RESULT_DIR: ctx.result, SPECKIT_BACKLOG_BUDGET_MINUTES: '150', SPECKIT_BACKLOG_RUN_MINUTES: '60' };
    await runAuthor({ env, log: silent, git: ctx.git, rootDir: ctx.workspace, now: () => clock, specify: (args) => { clock += 50 * 60 * 1000; return specify(args); } });
    const result = JSON.parse(readFileSync(path.join(ctx.result, 'result.json'), 'utf8'));
    assert.equal(result.specs.length, 2);
    assert.match(result.stopped, /no room for spec 3 of 5/);
  } finally {
    rmSync(ctx.base, { recursive: true, force: true });
  }
});

test('land refuses specs that change files outside new spec folders and docs/architecture', () => {
  const ctx = setup();
  try {
    const base = must(ctx.git, ['rev-parse', 'HEAD']).trim();
    mkdirSync(path.join(ctx.workspace, 'specs', '20261022-100001-x'));
    writeFileSync(path.join(ctx.workspace, 'specs', '20261022-100001-x', 'spec.md'), '# x\n');
    writeFileSync(path.join(ctx.workspace, 'specs', '20261022-100001-x', 'tasks.md'), TASKS);
    writeFileSync(path.join(ctx.workspace, 'specs', 'README.md'), '# changed\n');
    must(ctx.git, ['add', '-A']);
    must(ctx.git, ['commit', '--quiet', '-m', 'Spec: x']);
    assert.match(checkLanded(ctx.git, base, ['20261022-100001-x']), /outside their new folders.*specs\/README\.md/);
    assert.match(checkLanded(ctx.git, base, ['20261022-100001-y']), /outside their new folders/);
    must(ctx.git, ['checkout', '--quiet', 'HEAD~1', '--', 'specs/README.md']);
    must(ctx.git, ['commit', '--quiet', '-m', 'revert readme']);
    assert.equal(checkLanded(ctx.git, base, ['20261022-100001-x']), null);
    assert.match(checkLanded(ctx.git, 'HEAD', ['20261022-100001-x']), /already exists on the default branch/);

    mkdirSync(ctx.result, { recursive: true });
    writeFileSync(path.join(ctx.result, 'specs.patch'), must(ctx.git, ['format-patch', '--stdout', `${base}..HEAD~1`]));
    const landed = landSpecs({ env: landEnv(ctx), git: ctx.landGit, dir: ctx.result, folders: ['20261022-100001-x'] });
    assert.match(landed.error, /specs\/README\.md/);
    assert.equal(must(gitIn(ctx.origin), ['log', '-1', '--format=%s', 'main']).trim(), 'init', 'nothing was pushed');
  } finally {
    rmSync(ctx.base, { recursive: true, force: true });
  }
});
