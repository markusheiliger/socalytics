import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { TWIN_LABEL } from './speckit-prepare-core.mjs';
import { runSync } from './speckit-prepare.mjs';
import { UsageError, main, resolveFolder, runOrchestrate, runRequest, runSelect, runStart } from './speckit-orchestrate.mjs';
import { phaseNotStarted, runContinue, runDecide, runPlanPhase, runSync as runSyncBase } from './speckit-spec.mjs';
import { ATTENTION_LABEL, BLOCKED_LABEL, CHECK_CONFLICT, CHECK_LIMIT, CHECK_MERGE, CHECK_PROGRESS, CHECK_RUN_NAME, CHECK_SYNC_CONFLICT, DONE_COMMENT_MARKER, RESUME_COMMENT_MARKER, RETRY_COMMENT_MARKER, START_COMMENT_MARKER, continuationOf, describeCommandEffect, parseGuidance, renderAttemptMarker, renderFixEvent, renderRepairMarker, renderResumeComment, renderRetryComment } from './speckit-implement-core.mjs';
import { FakeGitHub, SPEC_TEMPLATE, envFor, makeRepo, silent } from './speckit-test-helpers.mjs';

const IMPLEMENT = 'speckit:stage:implement';
const IMPLEMENTING = 'speckit:stage:implementing';
const stagesOf = (issue) => issue.labels.map((label) => label.name).filter((name) => name.startsWith('speckit:stage:'));
const TASKS_OPEN = '- [ ] T001 one\n- [ ] T002 two\n';

async function start(github, root, twin, { reset = false, requester = 'dev' } = {}) {
  return runStart({ client: github, rootDir: root, env: envFor(root), issueNumber: twin.number, folder: twin.folder ?? 'a', requester, reset, log: silent });
}

// A repository with one tasked twin "a", flagged by a writer.
async function flaggedRepo() {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: '## Phase 1: Setup\n\n- [ ] T001 [P] Create project\n\n### Implementation\n\n- [ ] T002 [US1] Build it\n' }]);
  github.permissions.dev = 'write';
  const twin = github.issues[0];
  flag(github, twin);
  return { root, github, twin };
}

test('start prepares the linked branch, draft pull request, assignee, check run, and start comment', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    const result = await start(github, root, twin);
    assert.equal(result.exitCode, 0);
    const pull = github.repo.pulls[0];
    assert.deepEqual(github.repo.linked, [{ issueNodeId: twin.node_id, name: 'speckit/a' }]);
    assert.equal(await github.aheadBy('main', 'speckit/a'), 1);
    assert.equal(pull.draft, true);
    assert.equal(pull.title, 'Implement: a title');
    assert.equal(pull.base.ref, 'main');
    assert.match(pull.body, new RegExp(`^Closes #${twin.number}$`, 'm'));
    assert.match(pull.body, /^\*\*Spec\*\*: \[`specs\/a`\]/m);
    assert.match(pull.body, /## Tasks \(2\)\n\nShort form; the full task texts are in \[`tasks\.md`\]\([^)]+\)\.\n\n### Phase 1: Setup\n\n- \[ \] T001 \[P\] Create project\n\n#### Implementation\n\n- \[ \] T002 \[US1\] Build it/);
    assert.deepEqual(pull.assignees, ['dev']);
    assert.deepEqual(stagesOf(github.find(twin.number)), [IMPLEMENTING], 'implement >> implementing once the pull request is open');
    assert.deepEqual(github.repo.checkRuns.map((run) => [run.name, run.status, run.head_sha, run.output.title]), [[CHECK_RUN_NAME, 'queued', pull.head.sha, '0 of 2 tasks implemented']]);
    const comments = github.comments.filter((comment) => comment.number === pull.number);
    assert.equal(comments.length, 1);
    assert.match(comments[0].body, /2 task\(s\) queued/);
    assert.match(comments[0].body, /merged into its base branch `main` once it converged/);

    await start(github, root, twin);
    assert.equal(github.repo.pulls.length, 1);
    assert.equal(github.repo.checkRuns.length, 1);
    assert.equal(github.comments.filter((comment) => comment.body.includes(START_COMMENT_MARKER)).length, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('start bases the implementation on the default branch GitHub reports, whatever its name', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    github.repo.branches.trunk = github.repo.branches.main;
    delete github.repo.branches.main;
    await runStart({ client: github, rootDir: root, env: { ...envFor(root), SPECKIT_DEFAULT_BRANCH: 'trunk' }, issueNumber: twin.number, folder: 'a', requester: 'dev', reset: false, log: silent });
    const pull = github.repo.pulls[0];
    assert.equal(pull.base.ref, 'trunk');
    assert.equal(await github.aheadBy('trunk', 'speckit/a'), 1);
    assert.match(pull.body, /^\*\*Spec\*\*: \[`specs\/a`\]\([^)]*\/tree\/trunk\/specs\/a\)$/m);
    assert.match(github.comments.at(-1).body, /merged into its base branch `trunk`/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('start completes a partially prepared workspace and tolerates assignee and link failures', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    github.failLinkedBranch = true;
    github.failAssign = true;
    await github.createBranch('speckit/a', 'sha-main');
    const lines = [];
    await runStart({ client: github, rootDir: root, env: envFor(root), issueNumber: twin.number, folder: 'a', requester: 'dev', reset: false, log: (line) => lines.push(line) });
    assert.equal(github.repo.linked.length, 0);
    assert.equal(github.repo.pulls.length, 1);
    assert.match(lines.join('\n'), /Added the start commit[\s\S]*Warning: could not assign @dev/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('start reports why the pull request could not be created, and completes on the next run', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    github.failCreatePull = 'POST /pulls failed with HTTP 422: body is too long (maximum is 65536 characters)';
    await assert.rejects(() => start(github, root, twin), /Could not create or find the pull request for speckit\/a: POST \/pulls failed with HTTP 422: body is too long/);
    assert.equal(await github.aheadBy('main', 'speckit/a'), 1);

    github.failCreatePull = null;
    await start(github, root, twin);
    assert.equal(github.repo.pulls.length, 1);
    assert.equal(await github.aheadBy('main', 'speckit/a'), 1, 'the start commit is not added twice');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('start skips twins that are no longer flagged', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    github.find(twin.number).labels = github.find(twin.number).labels.filter((label) => label.name !== IMPLEMENT);
    await start(github, root, twin);
    assert.equal(github.repo.pulls.length, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('select tracks the pull request lifecycle: in progress, fallback, and a fresh restart', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const first = github.repo.pulls[0];
    const inProgress = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(inProgress.inProgress.map(({ number, folder, pull }) => ({ number, folder, pull })), [{ number: twin.number, folder: 'a', pull: first.number }]);
    assert.equal(inProgress.ready.length, 0);

    await github.addLabels(first.number, [BLOCKED_LABEL]);
    github.closePull(first.number);
    const fallback = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.ok(!first.labels.some((label) => label.name === BLOCKED_LABEL));
    assert.deepEqual(fallback.fallback.map((item) => item.pull), [first.number]);
    const issue = github.find(twin.number);
    assert.deepEqual(issue.labels.map((label) => label.name).filter((name) => name.startsWith('speckit:stage:')), ['speckit:stage:tasked']);
    assert.match(github.comments.at(-1).body, new RegExp(`#${first.number} was closed without merging[\\s\\S]*stage \`tasked\``));

    flag(github, issue);
    const restart = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(restart.ready, [{ number: twin.number, folder: 'a', requester: 'dev', reset: true, fix: false }]);
    const staleHead = github.repo.branches['speckit/a'];
    await start(github, root, twin, { reset: true });
    assert.equal(github.repo.pulls.length, 2);
    assert.notEqual(github.repo.branches['speckit/a'], staleHead);
    assert.equal(await github.aheadBy('main', 'speckit/a'), 1);
    assert.equal(github.repo.linked.length, 2);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a requested twin moves implement >> implementing, and sync keeps or revokes implementing like the flag', async () => {
  const { root, github, twin } = await flaggedRepo();
  const sync = () => runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
  try {
    await start(github, root, twin);
    const issue = github.find(twin.number);
    assert.deepEqual(stagesOf(issue), [IMPLEMENTING]);
    const comments = github.comments.length;
    await sync();
    assert.deepEqual(stagesOf(issue), [IMPLEMENTING], 'sync keeps a valid implementing stage');
    assert.equal(github.comments.length, comments);

    flag(github, issue);
    const reflagged = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(reflagged.inProgress.map((item) => [item.number, item.requester]), [[twin.number, 'dev']], 'the requester comes from the label history');
    assert.deepEqual(stagesOf(issue), [IMPLEMENTING], 'a flag set again while the pull request is open moves on to implementing');

    mkdirSync(path.join(root, 'specs', 'a', 'checklists'), { recursive: true });
    writeFileSync(path.join(root, 'specs', 'a', 'checklists', 'requirements.md'), '- [ ] Open item\n');
    await sync();
    assert.deepEqual(stagesOf(issue), ['speckit:stage:tasked'], 'sync revokes an implementing stage whose conditions no longer hold');
    assert.match(github.comments.at(-1).body, /^The `speckit:stage:implementing` stage was removed because:[\s\S]*checklist item/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a computed implementing stage from tasks checked on the default branch is no implementation request', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: '- [x] T001 one\n- [ ] T002 two\n' }]);
  try {
    const issue = github.issues[0];
    assert.deepEqual(stagesOf(issue), [IMPLEMENTING]);
    const result = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.equal(result.ready.length + result.inProgress.length + result.inconsistent.length + result.fallback.length, 0);
    const comments = github.comments.length;
    await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
    assert.deepEqual(stagesOf(issue), [IMPLEMENTING]);
    assert.equal(github.comments.length, comments, 'nothing is revoked');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('select finalizes a twin whose pull request a person merged while the twin is still open', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    await github.addLabels(github.repo.pulls[0].number, [BLOCKED_LABEL]);
    github.closePull(github.repo.pulls[0].number, { merged: true });
    const result = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.ok(!github.repo.pulls[0].labels.some((label) => label.name === BLOCKED_LABEL));
    assert.deepEqual(result.merged, [{ number: twin.number, folder: 'a', pull: github.repo.pulls[0].number, branchDeleted: true, note: null }]);
    assert.equal(result.ready.length + result.fallback.length, 0);
    const issue = github.find(twin.number);
    assert.deepEqual([issue.state, issue.state_reason], ['closed', 'completed']);
    assert.deepEqual(issue.labels.map((label) => label.name).filter((name) => name.startsWith('speckit:stage:')), ['speckit:stage:implemented']);
    assert.equal(github.repo.branches['speckit/a'], undefined);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('select finalizes a twin that GitHub closed when a person merged its held pull request, once', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.closePull(pull.number, { merged: true });
    Object.assign(github.find(twin.number), { state: 'closed', state_reason: 'completed' });
    const lines = [];
    const result = await runSelect({ client: github, rootDir: root, env: envFor(root), log: (line) => lines.push(line) });
    assert.equal(result.merged.length, 1);
    const issue = github.find(twin.number);
    assert.deepEqual(issue.labels.map((label) => label.name).filter((name) => name.startsWith('speckit:stage:')), ['speckit:stage:implemented']);
    assert.equal(github.repo.branches['speckit/a'], undefined);
    const comments = github.comments.filter((comment) => comment.number === twin.number);
    assert.equal(comments.length, 1);
    assert.match(comments[0].body, new RegExp(`#${pull.number} was merged into \`main\`, so the twin is now \`speckit:stage:implemented\` and the branch \`speckit/a\` was deleted`));
    assert.match(lines.join('\n'), new RegExp(`Finalized: #${twin.number} \`a\` merged in pull request #${pull.number}`));

    const updates = github.updates.length;
    const again = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.equal(again.merged.length, 0);
    assert.equal(github.updates.length, updates);
    assert.equal(github.comments.filter((comment) => comment.number === twin.number).length, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('select leaves closed twins alone unless their pull request was merged after the flag', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    Object.assign(github.find(twin.number), { state: 'closed', state_reason: 'completed' });
    const running = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.equal(running.merged.length + running.inProgress.length + running.ready.length, 0);

    github.closePull(github.repo.pulls[0].number, { merged: true });
    Object.assign(github.find(twin.number), { state: 'closed', state_reason: 'not_planned' });
    const notPlanned = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.equal(notPlanned.merged.length, 0);
    assert.ok(github.find(twin.number).labels.some((label) => label.name === IMPLEMENTING));
    assert.equal(github.repo.branches['speckit/a'] !== undefined, true);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

async function twinsFor(folders) {
  const root = makeRepo(folders);
  const github = new FakeGitHub();
  await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
  github.updates = [];
  return { root, github };
}

function flag(github, issue, login = 'dev') {
  issue.labels = issue.labels.filter((label) => !label.name.startsWith('speckit:stage:'));
  github.humanLabel(issue.number, IMPLEMENT, login);
}

// Fake git that serves files of one ref from a map of "path" -> content.
function fakeGit(files, { fetchStatus = 0 } = {}) {
  const calls = [];
  const git = (args) => {
    calls.push(args);
    if (args[0] === 'fetch') return { status: fetchStatus, stdout: '', stderr: fetchStatus ? 'network down' : '' };
    if (args[0] === 'show' || args[0] === 'cat-file') {
      const file = args.at(-1).replace(/^origin\/main:/, '');
      return file in files ? { status: 0, stdout: files[file], stderr: '' } : { status: 128, stdout: '', stderr: 'missing' };
    }
    if (args[0] === 'ls-tree') {
      assert.deepEqual(args.slice(0, 4), ['ls-tree', '-z', '--full-tree', '--name-only']);
      const prefix = args[6];
      const names = Object.keys(files).filter((file) => file.startsWith(prefix));
      return { status: 0, stdout: names.map((name) => `${name}\0`).join(''), stderr: '' };
    }
    throw new Error(`unexpected git ${args.join(' ')}`);
  };
  git.calls = calls;
  return git;
}

function remoteFiles(folder, { plan = true, tasks = TASKS_OPEN, checklists = { 'requirements.md': '- [x] ok\n' } } = {}) {
  const files = { [`specs/${folder}/spec.md`]: SPEC_TEMPLATE(folder) };
  if (plan) files[`specs/${folder}/plan.md`] = '# plan\n';
  if (tasks !== null) files[`specs/${folder}/tasks.md`] = tasks;
  for (const [name, content] of Object.entries(checklists)) files[`specs/${folder}/checklists/${name}`] = content;
  return files;
}

test('select reports ready, blocked, and inconsistent flagged twins', async () => {
  const { root, github } = await twinsFor([
    { folder: 'a', plan: true, tasks: TASKS_OPEN },
    { folder: 'b', plan: true, tasks: TASKS_OPEN },
    { folder: 'c', plan: true },
    { folder: 'd', plan: true, tasks: TASKS_OPEN },
    { folder: 'e', plan: true, tasks: TASKS_OPEN },
  ]);
  try {
    github.permissions.dev = 'write';
    github.permissions.outsider = 'triage';
    const [a, b, c, , e] = github.issues;
    for (const issue of [a, b, c]) flag(github, issue);
    flag(github, e, 'outsider');
    github.edges.push([b.number, github.issues[3].number]);
    const result = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(result.ready, [{ number: a.number, folder: 'a', requester: 'dev', reset: false, fix: false }]);
    assert.deepEqual(result.blocked.map((twin) => [twin.folder, twin.blockers.map((blocker) => blocker.number)]), [['b', [github.issues[3].number]]]);
    assert.deepEqual(result.inconsistent.map((twin) => twin.folder), ['c', 'e']);
    assert.match(result.inconsistent[1].reasons[0], /@outsider needs at least write access/);
    const output = readFileSync(path.join(root, 'output.txt'), 'utf8');
    assert.match(output, /count=1/);
    assert.match(output, new RegExp(`matrix=\\{"include":\\[\\{"number":${a.number},"folder":"a","requester":"dev","reset":false,"fix":false\\}\\]\\}`));

    github.find(github.issues[3].number).state = 'closed';
    const unblocked = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(unblocked.ready.map((twin) => twin.folder), ['a', 'b']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('select reports nothing when no twin is flagged', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: TASKS_OPEN }]);
  try {
    const result = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.equal(result.ready.length + result.blocked.length + result.inconsistent.length, 0);
    assert.match(readFileSync(path.join(root, 'summary.md'), 'utf8'), /No spec twin is flagged/);
    assert.match(readFileSync(path.join(root, 'output.txt'), 'utf8'), /matrix=\{"include":\[\]\}/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('resolves the folder from the argument, environment, or feature.json', () => {
  const root = makeRepo([]);
  try {
    assert.equal(resolveFolder({ folder: 'specs/x/', env: {}, rootDir: root }), 'x');
    assert.equal(resolveFolder({ env: { SPECIFY_FEATURE_DIRECTORY: 'specs\\y' }, rootDir: root }), 'y');
    assert.throws(() => resolveFolder({ env: {}, rootDir: root }), UsageError);
    mkdirSync(path.join(root, '.specify'));
    writeFileSync(path.join(root, '.specify', 'feature.json'), '{ "feature_directory": "specs/z" }');
    assert.equal(resolveFolder({ env: {}, rootDir: root }), 'z');
    writeFileSync(path.join(root, '.specify', 'feature.json'), '{ nope');
    assert.throws(() => resolveFolder({ env: {}, rootDir: root }), /not valid JSON/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('request flags a tasked twin with complete checklists in one update', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: TASKS_OPEN }]);
  try {
    const lines = [];
    const git = fakeGit(remoteFiles('a'));
    const result = await runRequest({ client: github, git, rootDir: root, env: {}, folder: 'a', log: (line) => lines.push(line) });
    assert.equal(result.exitCode, 0);
    const issue = github.issues[0];
    assert.deepEqual(issue.labels.map((label) => label.name), [TWIN_LABEL, 'speckit:deps-pending', IMPLEMENT]);
    assert.equal(github.updates.length, 1);
    assert.deepEqual(git.calls[0], ['fetch', '--quiet', 'origin', 'main']);
    assert.match(lines.join('\n'), /Requested: .*speckit:stage:implement[\s\S]*No open blockers/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('request reports blockers, already flagged twins, and failed pre-checks', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: TASKS_OPEN }, 'b']);
  try {
    const [a, b] = github.issues;
    github.edges.push([a.number, b.number]);
    const run = async (files, folder = 'a') => {
      const lines = [];
      const result = await runRequest({ client: github, git: fakeGit(files), rootDir: root, env: {}, folder, log: (line) => lines.push(line) });
      return { code: result.exitCode, text: lines.join('\n') };
    };

    const checklist = await run(remoteFiles('a', { checklists: { 'ux.md': '- [ ] open\n' } }));
    assert.equal(checklist.code, 1);
    assert.match(checklist.text, /1 checklist item\(s\)/);

    const planned = await run(remoteFiles('a', { tasks: null }));
    assert.equal(planned.code, 1);
    assert.match(planned.text, /stage `planned`/);

    const notMerged = await run({});
    assert.equal(notMerged.code, 1);
    assert.match(notMerged.text, /not merged to main/);

    const archived = await run({ 'specs/archive/a/spec.md': SPEC_TEMPLATE('a') });
    assert.equal(archived.code, 1);
    assert.match(archived.text, /specs\/a is already implemented and archived in specs\/archive\/a/);

    const noTwin = await run(remoteFiles('zz'), 'zz');
    assert.equal(noTwin.code, 1);
    assert.match(noTwin.text, /no open spec twin/);

    const requested = await run(remoteFiles('a'));
    assert.equal(requested.code, 0);
    assert.match(requested.text, new RegExp(`Open blockers: #${b.number}`));

    github.updates = [];
    const again = await run(remoteFiles('a'));
    assert.equal(again.code, 0);
    assert.match(again.text, /Already requested/);
    assert.equal(github.updates.length, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('request surfaces fetch failures and main validates commands', async () => {
  const root = makeRepo([]);
  try {
    const github = new FakeGitHub();
    await assert.rejects(
      () => runRequest({ client: github, git: fakeGit({}, { fetchStatus: 1 }), rootDir: root, env: {}, folder: 'a', log: silent }),
      (error) => error instanceof UsageError && /network down/.test(error.message),
    );
    await assert.rejects(() => main(['nope'], { env: {}, client: github, git: fakeGit({}) }), UsageError);
    await assert.rejects(() => main(['request', '--bogus'], { env: {}, client: github, git: fakeGit({}) }), /Unknown argument/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

const BRANCH_TASKS = (t1, t2) => `## Phase 1: Setup\n\n- [${t1}] T001 [P] Create project\n\n### Implementation\n\n- [${t2}] T002 [US1] Build it\n`;

async function orchestrate(github, root, env = {}) {
  const lines = [];
  const result = await runOrchestrate({ client: github, rootDir: root, env: { ...envFor(root), ...env }, log: (line) => lines.push(line), sleep: async () => {}, now: () => github.clock });
  return { ...result, text: lines.join('\n') };
}

const chainRuns = (github) => github.repo.runs.filter((run) => run.workflow === 'speckit-implement-spec.yml');
const limitCheck = (github, head) => github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'completed', conclusion: 'failure', external_id: CHECK_LIMIT });

test('orchestrate starts the implementation chain and continues it only when no run is active and there is work', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    const result = await orchestrate(github, root);
    const pull = github.repo.pulls[0];
    assert.equal(result.exitCode, 0);
    assert.deepEqual(chainRuns(github).map((run) => [run.display_title, run.ref, run.inputs]), [[`Spec Kit implement spec #${twin.number} · a title`, 'main', { twin: String(twin.number), pull: String(pull.number), title: 'a title' }]]);
    assert.match(result.text, new RegExp(`Started the implementation of #${twin.number} \\(pull request #${pull.number}\\)`));

    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    const active = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 1, 'an active chain is left alone');
    assert.match(active.text, /its implementation run is active/);

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    const broken = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 2, 'a chain that stopped with work left is continued');
    assert.match(broken.text, new RegExp(`Continued #${twin.number} \\(pull request #${pull.number}\\): next is T001 attempt 1`));
    assert.equal(chainRuns(github).at(-1).display_title, `Spec Kit implement spec #${twin.number} · a title`, 'the run is named after the spec');

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await limitCheck(github, github.repo.branches['speckit/a']);
    const stopped = await orchestrate(github, root, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.equal(chainRuns(github).length, 2, 'a stop waits for a person');
    assert.match(stopped.text, /stopped and waiting for a person/);
    await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 3, 'a stop that needs a diagnosis continues, so the chain starts it');

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await github.createComment(pull.number, `${DONE_COMMENT_MARKER}\n**All 2 tasks are implemented**`);
    const done = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 3);
    assert.match(done.text, /implemented and waiting for review/);
    const attention = () => (github.repo.pulls[0].labels ?? []).some((label) => label.name === ATTENTION_LABEL);
    assert.equal((await decide(github, root, twin)).outputs.action, 'done');
    assert.equal(attention(), true, 'an implementation held for review needs attention');

    github.closePull(pull.number, { merged: true });
    await orchestrate(github, root);
    assert.equal(attention(), false, 'a merged implementation no longer needs attention');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate does not continue an implementation that is blocked by its fix spec', async () => {
  const { root, github } = await flaggedRepo();
  try {
    await orchestrate(github, root);
    const pull = github.repo.pulls[0];
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await limitCheck(github, github.repo.branches['speckit/a']);
    const fixFolder = '20261016-000000-fix-x';
    github.repo.pulls.push({ number: 99, state: 'closed', merged_at: github.tick(), base: { ref: 'main' }, head: { ref: `speckit-fix/${fixFolder}`, sha: 'x' }, labels: [] });
    await github.createComment(pull.number, renderFixEvent('created', { pull: 99, folder: fixFolder }));
    await github.createComment(pull.number, renderFixEvent('merged', { pull: 99, folder: fixFolder }));
    const blocked = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 1, 'neither a diagnosis nor a retry starts while the fix is pending');
    assert.match(blocked.text, /blocked; it waits for its fix spec/);
    assert.ok(github.repo.pulls[0].labels.some((label) => label.name === BLOCKED_LABEL));
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:blocked \{"pull":99,"stage":"implement"\} -->\n\*\*Blocked\*\* by fix spec #99/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate starts no more than SPECKIT_MAX_ACTIVE_SPECS implementations', async () => {
  const tasks = '## Phase 1\n\n- [ ] T001 one\n';
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks }, { folder: 'b', plan: true, tasks }]);
  try {
    github.permissions.dev = 'write';
    for (const issue of github.issues) flag(github, issue);
    const [a, b] = github.issues;
    const first = await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.deepEqual(chainRuns(github).map((run) => run.display_title), [`Spec Kit implement spec #${a.number} · a title`]);
    assert.match(first.text, new RegExp(`Waiting: #${b.number} \`b\` starts when fewer than 1 implementations are active`));

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await github.createComment(github.repo.pulls[0].number, `${DONE_COMMENT_MARKER}\n**Done**`);
    await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.deepEqual(chainRuns(github).map((run) => run.display_title).at(-1), `Spec Kit implement spec #${b.number} · b title`, 'an implementation waiting for review frees its slot');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a fix spec gets no dependency inference and starts first, even at SPECKIT_MAX_ACTIVE_SPECS', async () => {
  const tasks = '## Phase 1\n\n- [ ] T001 one\n';
  const root = makeRepo([{ folder: 'a', plan: true, tasks }, { folder: 'b', plan: true, tasks }]);
  try {
    writeFileSync(path.join(root, 'specs', 'b', 'spec.md'), `${SPEC_TEMPLATE('b')}\n**Fix for**: #75 (implementation of \`specs/a\`)\n`);
    const github = new FakeGitHub();
    await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
    const [a, b] = github.issues;
    assert.ok(a.labels.some((label) => label.name === 'speckit:deps-pending'));
    assert.ok(!b.labels.some((label) => label.name === 'speckit:deps-pending'), 'inference could make the fix wait for the spec it unblocks');
    github.permissions.dev = 'write';
    for (const issue of github.issues) flag(github, issue);
    const first = await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.deepEqual(chainRuns(github).map((run) => run.display_title), [`Spec Kit implement spec #${b.number} · b title`], 'the fix spec goes first');
    assert.match(first.text, new RegExp(`Waiting: #${a.number} \`a\``));
    await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.equal(chainRuns(github).length, 1, 'the running fix spec holds the slot, and is not started twice');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate settles a merged amendment and continues the implementation with a fresh attempt count', async () => {
  const { root, github } = await flaggedRepo();
  try {
    await orchestrate(github, root, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await limitCheck(github, github.repo.branches['speckit/a']);
    const implementation = github.repo.pulls[0];
    const runs = chainRuns(github).length;

    // A person merged the amendment; the amendment branch's workflows did not report it.
    github.repo.pulls.push({ number: 77, node_id: 'PR_77', title: 'Amend: a', body: '', draft: false, state: 'closed', merged_at: github.tick(), closed_at: github.tick(), merged_by: { login: 'dev' }, base: { ref: 'speckit/a' }, head: { ref: 'speckit-amend/a', sha: 'sha-x' }, created_at: github.tick() });
    const settled = await orchestrate(github, root, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    const resume = github.comments.find((comment) => comment.number === implementation.number && comment.body.includes('<!-- speckit-amend:closed 77 -->'));
    assert.match(resume.body, /^<!-- speckit-implement:resume -->\n<!-- speckit-amend:closed 77 -->\n\*\*Amendment #77 was merged\*\* by @dev\./);
    assert.match(settled.text, /Amendment #77 was merged/);
    assert.equal(chainRuns(github).length, runs + 1, 'the resume continues the stopped implementation');

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await orchestrate(github, root, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.equal(github.comments.filter((comment) => comment.body.includes('<!-- speckit-amend:closed 77 -->')).length, 1, 'settled once');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// --- the implementation chain (Spec Kit implement spec and phase): decide, plan-phase, and continue ---

// Runs the decide job of the chain (or the plan job of the phase workflow) for the twin's pull request and returns its
// outputs.
async function decide(github, root, twin, env = {}, run = runDecide) {
  const output = path.join(root, `decide-${github.clock}.txt`);
  writeFileSync(output, '');
  const lines = [];
  const result = await run({
    client: github,
    env: { ...envFor(root), GITHUB_OUTPUT: output, SPECKIT_TWIN: String(twin.number), SPECKIT_PULL: String(github.repo.pulls[0].number), ...env },
    log: (line) => lines.push(line),
    now: () => github.clock,
  });
  const outputs = Object.fromEntries(readFileSync(output, 'utf8').trim().split('\n').filter(Boolean).map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]));
  return { ...result, outputs, text: lines.join('\n') };
}

const attemptComment = (github, pull, task, outcome, kind = 'task') => github.createComment(pull, `${renderAttemptMarker({ kind, task, attempt: 1, outcome })}\n**${task ?? kind}** ${outcome}`);

const planPhase = (github, root, twin, phase, env = {}) => decide(github, root, twin, { SPECKIT_FOLDER: 'a', SPECKIT_PHASE: phase, ...env }, runPlanPhase);

test('chain: decide picks the phase, and plan-phase plans its task order with prompts, verify modes, job names, and the first tasks\' check runs', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', '## Phase 1: Setup\n\n- [ ] T001 [P] Create project\n- [ ] T002 [P] Add docs\n\n### Implementation\n\n- [ ] T003 [US1] Build it\n\n## Phase 2\n\n- [ ] T004 Later\n');
    const progress = github.repo.checkRuns.at(-1);
    const chosen = await decide(github, root, twin);
    assert.deepEqual([chosen.outputs.action, chosen.outputs.continue, chosen.outputs.phase, chosen.outputs.job_name, chosen.outputs.base_branch], ['implement', 'true', 'Phase 1: Setup', 'Phase 1: Setup', 'main']);
    assert.match(chosen.text, new RegExp(`^Next: Spec Kit implement phase #${twin.number} · Phase 1: Setup\\.$`, 'm'));
    assert.match(chosen.text, new RegExp(`^## Spec Kit implement spec #${twin.number} · a title$`, 'm'));
    assert.equal(progress.external_id, CHECK_PROGRESS, 'decide starts no attempts for a phase');

    const stale = await planPhase(github, root, twin, 'Phase 2');
    assert.deepEqual([stale.outputs.count, stale.outputs.order], ['0', '[]'], 'a phase that is no longer next plans nothing');
    assert.match(stale.text, /Nothing to plan/);

    const decided = await planPhase(github, root, twin, chosen.outputs.phase);
    assert.deepEqual([decided.outputs.count, decided.outputs.phase_check], ['2', 'true'], 'a run that finishes the phase verifies it in its own job');
    assert.deepEqual(JSON.parse(decided.outputs.task_matrix), { include: [
      { index: 0, name: 'T001, T002 in parallel' },
      { index: 1, name: 'T003' },
    ] });
    const order = JSON.parse(decided.outputs.order);
    assert.deepEqual(order.map((entry) => entry.include.map((entry) => [entry.task, entry.attempt, entry.verify_mode])), [[['T001', 1, 'task'], ['T002', 1, 'task']], [['T003', 1, 'task']]], 'the last task is verified like every other; the phase is verified on its own');
    assert.match(decided.text, /- Then the full verification of the phase\./);
    assert.match(order[0].include[0].prompt, /^\/speckit-implement Implement only task T001 of the spec in `specs\/a\/` \(its `tasks\.md`\); ignore the other specs\./);
    assert.equal(order[1].include[0].summary, '[US1] Build it');
    assert.equal(progress.external_id, 'speckit:attempt:task:T001:1', 'the first attempt takes over the queued progress check run');
    assert.deepEqual(github.repo.checkRuns.filter((check) => String(check.external_id).startsWith('speckit:attempt:')).map((check) => [check.external_id, check.status]), [['speckit:attempt:task:T001:1', 'in_progress'], ['speckit:attempt:task:T002:1', 'in_progress']]);
    assert.equal(decided.outputs.head, github.repo.branches['speckit/a']);
    assert.match(decided.text, /- T001 attempt 1, T002 attempt 1 in parallel\n- T003 attempt 1\n/);

    // The run ended without reporting its attempts: they count as failed, and the next run retries them.
    const retried = await decide(github, root, twin);
    assert.match(retried.text, /Recorded 2 attempt\(s\) of an earlier run that ended without reporting as failed/);
    assert.match(github.comments.find((comment) => comment.body.includes('"task":"T001","attempt":1,"outcome":"failure"')).body, /T001 attempt 1 did not finish/);
    assert.deepEqual(JSON.parse((await planPhase(github, root, twin, 'Phase 1: Setup')).outputs.order)[0].include.map((entry) => entry.attempt), [2, 2]);

    github.setFile('speckit/a', 'specs/a/tasks.md', '## Phase 1: Setup\n\n- [x] T001 a\n- [ ] T002 [P] b\n- [ ] T003 [P] c\n');
    for (const check of github.repo.checkRuns) check.status = 'completed';
    const group = await planPhase(github, root, twin, 'Phase 1: Setup');
    assert.deepEqual([JSON.parse(group.outputs.task_matrix).include.length, group.outputs.phase_check], [1, 'true'], 'a phase that ends with [P] tasks gets the phase verification');
    assert.equal(pull.number, github.repo.pulls[0].number);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: a retry after the limit starts a new attempt window with its guidance', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    for (let attempt = 0; attempt < 3; attempt += 1) await attemptComment(github, pull.number, 'T001', 'failure');
    assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, 'stop');
    await github.createComment(pull.number, renderRetryComment('Implementation retried by @dev.', 'Keep the helper.'));
    const retried = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.equal(retried.outputs.action, 'implement');
    const [first] = JSON.parse((await planPhase(github, root, twin, retried.outputs.phase)).outputs.order);
    assert.deepEqual([first.include[0].task, first.include[0].attempt], ['T001', 1], 'a retry starts a new attempt window');
    assert.match(first.include[0].prompt, /Keep the helper\.$/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: a durable repair blocks normal planning until a writer authorizes guided repair', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
    const head = github.repo.branches['speckit/a'];
    await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'completed', conclusion: 'failure', external_id: CHECK_LIMIT, output: { title: 'Group failed', summary: 'combined tests failed' } });
    const repair = { id: `group:1:${head}`, state: 'pending', scope: 'group', tasks: ['T001', 'T002'], head, run: '1', artifact: 'speckit-group-verification', at: github.tick() };
    await github.createComment(pull.number, `${renderRepairMarker(repair)}\nrepair pending`);

    const continuation = await continuationOf(github, { folder: 'a', pull });
    assert.equal(continuation.repair.id, repair.id, 'checked tasks cannot make comments claim convergence is next');
    for (const command of ['retry', 'resume']) {
      const effect = describeCommandEffect(`/speckit ${command}`, { baseBranch: 'main', continuation });
      assert.match(effect, /reverifies without running a repair agent.*pending group verification/);
      assert.doesNotMatch(effect, /convergence|not repeated by itself|fresh attempt count/);
      assert.match(describeCommandEffect(`/speckit ${command} Investigate the logs`, { continuation }), /repair agent with the guidance, then reverifies/);
    }
    assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, 'stop', 'checked tasks cannot bypass pending repair');
    await github.createComment(pull.number, renderRetryComment('Implementation retried by @dev.', 'Fix the combined registration and rerun its tests.'));
    const authorized = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.equal(authorized.outputs.action, 'repair');
    assert.equal(JSON.parse(authorized.outputs.repair).id, repair.id);
    assert.match(authorized.outputs.prompt, /Writer-approved guidance: Fix the combined registration/);
    assert.equal(authorized.outputs.attempt, '1');
    assert.equal(authorized.outputs.verify_only, 'false');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

for (const restart of [renderResumeComment, renderRetryComment]) {
test(`chain: ${restart.name} reverifies a pending repair without guidance or a repair agent`, async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    const head = github.repo.branches['speckit/a'];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
    await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'completed', conclusion: 'failure', external_id: CHECK_LIMIT, output: { title: 'Phase failed', summary: 'transient failure' } });
    const repair = { id: `phase:1:${head}`, state: 'pending', scope: 'phase', tasks: ['T001', 'T002'], head, at: github.tick() };
    await github.createComment(pull.number, `${renderRepairMarker(repair)}\npending`);
    await github.createComment(pull.number, restart('Implementation restarted by @dev.'));
    const resumed = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.deepEqual([resumed.outputs.action, resumed.outputs.verify_only], ['repair', 'true']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
}

test('chain: decide stops at the attempt limit, resumes on request with guidance, and does nothing for unflagged twins', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    for (let attempt = 0; attempt < 3; attempt += 1) await attemptComment(github, pull.number, 'T001', 'failure');
    pull.draft = false;
    const limited = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.deepEqual([limited.outputs.action, limited.outputs.continue], ['stop', 'false']);
    assert.equal(pull.draft, true, 'a stop waiting for a person restores draft status');
    assert.equal(github.repo.checkRuns.at(-1).external_id, CHECK_LIMIT);
    assert.match(github.comments.at(-1).body, /Implementation needs attention\*\* @dev: T001 did not succeed in 3 attempts[\s\S]*```text\n\/speckit diagnose\n```[\s\S]*The implementation continues with T001, with a fresh attempt count[^\n]*That work merges `main` first anyway[\s\S]*\/speckit retry/, 'T001 opens a phase that has not started, so a retry merges main as well');
    assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, 'stop', 'a stop waits for a person');
    const attention = () => (github.repo.pulls[0].labels ?? []).some((label) => label.name === ATTENTION_LABEL);
    assert.equal(attention(), true, 'a stop without a diagnosis needs attention');

    pull.draft = false;
    const diagnosed = await decide(github, root, twin);
    assert.match(diagnosed.text, /Started a diagnosis/);
    assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.checkRuns.at(-1).external_id], ['speckit-diagnose.lock.yml', 'speckit:diagnosing']);
    assert.equal(pull.draft, true, 'starting a diagnosis restores draft status');
    assert.equal(attention(), false, 'a running diagnosis is processing');

    pull.draft = false;
    const diagnosing = await decide(github, root, twin);
    assert.deepEqual([diagnosing.outputs.action, diagnosing.outputs.continue], ['none', 'false']);
    assert.equal(pull.draft, true, 'waiting for a diagnosis result restores draft status');

    await github.addLabels(pull.number, [ATTENTION_LABEL]);
    pull.draft = false;
    const resumed = await decide(github, root, twin, { SPECKIT_RESUME: 'true', SPECKIT_GUIDANCE: 'Use the existing helper.' });
    assert.equal(resumed.outputs.action, 'implement');
    assert.equal(pull.draft, true, 'resumed implementation work restores draft status');
    assert.equal(attention(), false, 'a resumed implementation is processing');
    const comment = github.comments.find((item) => item.body.startsWith(RESUME_COMMENT_MARKER));
    assert.equal(parseGuidance(comment.body), 'Use the existing helper.');
    const [first, second] = JSON.parse((await planPhase(github, root, twin, resumed.outputs.phase)).outputs.order);
    assert.deepEqual([first.include[0].task, first.include[0].attempt], ['T001', 1], 'a resume starts a new attempt window');
    assert.match(first.include[0].prompt, /Guidance from the person who resumed this implementation: Use the existing helper\.$/);
    assert.doesNotMatch(second.include[0].prompt, /Guidance/, 'guidance applies to the stopped task only');

    github.mergeBranchOutcome = null;
    twin.labels = twin.labels.filter((label) => ![IMPLEMENT, IMPLEMENTING].includes(label.name));
    const unflagged = await decide(github, root, twin);
    assert.deepEqual([unflagged.outputs.action, unflagged.outputs.continue], ['none', 'false']);
    assert.match(unflagged.text, /is not open and flagged for implementation/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

const advanceRepairBase = (github) => {
  const sha = createHash('sha1').update(`repair base ${github.repo.nextSha++}`).digest('hex');
  github.repo.commits[sha] = { tree: github.repo.commits[github.repo.branches.main]?.tree, parents: [github.repo.branches.main], message: 'base moved' };
  github.repo.branches.main = sha;
  return sha;
};

async function restartedRepair(restart, guidance = '') {
  const fixture = await flaggedRepo();
  const { root, github, twin } = fixture;
  await start(github, root, twin);
  const pull = github.repo.pulls[0];
  github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
  await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: github.repo.branches['speckit/a'], status: 'completed', conclusion: 'failure', external_id: CHECK_LIMIT, output: { title: 'Phase failed' } });
  await github.createComment(pull.number, renderRepairMarker({ id: 'phase:sync', state: 'pending', scope: 'phase', head: github.repo.branches['speckit/a'], at: github.tick() }));
  await github.createComment(pull.number, restart('Implementation restarted by @dev.', guidance));
  return { ...fixture, pull };
}

test('chain: pending repair resume merges the base before verification-only work, while retry does not', async () => {
  for (const restart of [renderResumeComment, renderRetryComment]) {
    const { root, github, twin, pull } = await restartedRepair(restart);
    try {
      advanceRepairBase(github);
      const before = github.repo.branches['speckit/a'];
      const sync = () => decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' }, runSyncBase);
      assert.equal((await sync()).outputs.result, restart === renderResumeComment ? 'merged' : 'none');
      const next = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
      assert.deepEqual([next.outputs.action, next.outputs.verify_only], ['repair', 'true']);
      assert.equal(next.outputs.head, github.repo.branches['speckit/a']);
      assert.equal(next.outputs.head === before, restart === renderRetryComment);
      for (const check of github.repo.checkRuns) check.status = 'completed';
      await attemptComment(github, pull.number, null, 'failure', 'repair');
      advanceRepairBase(github);
      assert.equal((await sync()).outputs.result, 'none', 'a repair attempt consumes the restart authorization');
      assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, 'stop');
      for (const attempt of [2, 3]) {
        await github.createComment(pull.number, renderRetryComment('Retry without merging.'));
        assert.equal((await sync()).outputs.result, 'none', 'a later retry must not revive an earlier resume');
        const repair = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
        assert.deepEqual([repair.outputs.action, repair.outputs.attempt], ['repair', String(attempt)]);
        for (const check of github.repo.checkRuns) check.status = 'completed';
        await attemptComment(github, pull.number, null, 'failure', 'repair');
      }
      await github.createComment(pull.number, renderResumeComment('Try again.'));
      assert.equal((await sync()).outputs.result, 'merged', 'every restart gets a fresh repair attempt, and a resume merges the base first');
      const fresh = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
      assert.deepEqual([fresh.outputs.action, fresh.outputs.attempt], ['repair', '4'], 'restart resets the repair limit');
      for (const check of github.repo.checkRuns) check.status = 'completed';
      await attemptComment(github, pull.number, null, 'failure', 'repair');
      const comments = github.comments.length;
      for (let run = 0; run < 2; run += 1) {
        assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, 'stop');
      }
      assert.equal(github.comments.length, comments, 'a stop that was reported is not reported again');
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});

test('chain: resumed repair resolves sync conflicts first and keeps its authorization and guidance until the repair', async () => {
  for (const guidance of ['', 'Fix the failed scope.']) {
    for (const outcome of ['success', 'attention', 'failure']) {
      const { root, github, twin, pull } = await restartedRepair(renderResumeComment, guidance);
      try {
        advanceRepairBase(github);
        const sync = () => decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' }, runSyncBase);
        const next = () => decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
        const settle = () => { for (const check of github.repo.checkRuns) check.status = 'completed'; };
        github.mergeBranchOutcome = 'conflict';
        assert.equal((await sync()).outputs.result, 'conflict');
        assert.equal((await next()).outputs.action, 'resolve', 'repair must not override pending sync resolution');
        settle();
        await attemptComment(github, pull.number, null, 'failure', 'resolve');
        assert.equal((await sync()).outputs.result, 'none', 'do not attempt the same merge during resolution');
        assert.equal((await next()).outputs.action, 'resolve');
        settle();
        if (outcome === 'success') {
          github.mergeBranchOutcome = null;
          await github.mergeBranch('speckit/a', 'main', 'resolved base merge');
        }
        await attemptComment(github, pull.number, null, outcome, 'resolve');
        if (outcome === 'success') advanceRepairBase(github);
        assert.equal((await sync()).outputs.result, outcome === 'success' ? 'merged' : 'none', 'resolve does not consume the resume; given-up sync stays best effort');
        assert.equal((await sync()).outputs.result, 'none', 'a resolved or given-up sync cannot loop');
        const repair = await next();
        assert.deepEqual([repair.outputs.action, repair.outputs.attempt, repair.outputs.verify_only], ['repair', '1', String(!guidance)]);
        if (guidance) assert.match(repair.outputs.prompt, /Writer-approved guidance: Fix the failed scope\./);
        settle();
        await attemptComment(github, pull.number, null, 'failure', 'repair');
        assert.equal((await sync()).outputs.result, 'none');
        assert.equal((await next()).outputs.action, 'stop', 'an attempted repair still needs a new authorized restart');
        for (const attempt of [2, 3]) {
          const head = github.repo.branches['speckit/a'];
          await github.createComment(pull.number, renderRetryComment('Reverify unchanged without merging.'));
          assert.equal((await sync()).outputs.result, 'none', 'retry cannot revive the previous resume sync conflict');
          const retry = await next();
          assert.deepEqual([retry.outputs.action, retry.outputs.attempt, retry.outputs.verify_only], ['repair', String(attempt), 'true'], `${outcome}: old sync resolution history must not override retry`);
          assert.equal(github.repo.branches['speckit/a'], head, 'verification-only retry stays on the same head');
          settle();
          await attemptComment(github, pull.number, null, 'failure', 'repair');
        }
      } finally {
        rmSync(root, { recursive: true, force: true });
      }
    }
  }
});

test('chain: a resumed repair after the convergence gives up a conflicting base merge once instead of looping', async () => {
  const { root, github, twin, pull } = await restartedRepair(renderResumeComment);
  try {
    await attemptComment(github, pull.number, null, 'success', 'converge');
    const conflicted = advanceRepairBase(github);
    const sync = () => decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' }, runSyncBase);
    const next = () => decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    const settle = () => { for (const check of github.repo.checkRuns) check.status = 'completed'; };
    github.mergeBranchOutcome = 'conflict';
    assert.equal((await sync()).outputs.result, 'conflict');
    assert.equal((await next()).outputs.action, 'resolve');
    for (let attempt = 0; attempt < 2; attempt += 1) {
      settle();
      await attemptComment(github, pull.number, null, 'failure', 'resolve');
    }
    const checks = github.repo.checkRuns.filter((check) => check.external_id === CHECK_SYNC_CONFLICT).length;
    assert.equal((await sync()).outputs.result, 'none', 'the given-up base commit is not merged again before the repair');
    assert.equal(github.repo.checkRuns.filter((check) => check.external_id === CHECK_SYNC_CONFLICT).length, checks, 'no new conflict restarts the count');
    const repair = await next();
    assert.equal(repair.outputs.action, 'repair', 'the repair runs on the current base');
    assert.ok(github.comments.some((comment) => comment.body.includes(`<!-- speckit-implement:sync-skipped ${conflicted} -->`)), 'the skip is recorded');
    assert.equal((await sync()).outputs.result, 'none', 'the recorded skip holds');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: an explicit workflow resume syncs pending repair before persisting its restart comment', async () => {
  const { root, github, twin } = await restartedRepair(renderResumeComment);
  try {
    github.comments.pop();
    advanceRepairBase(github);
    assert.equal((await decide(github, root, twin, { SPECKIT_RESUME: 'true' }, runSyncBase)).outputs.result, 'merged');
    const next = await decide(github, root, twin, { SPECKIT_RESUME: 'true', SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.deepEqual([next.outputs.action, next.outputs.verify_only], ['repair', 'true']);
    assert.equal(next.outputs.head, github.repo.branches['speckit/a']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: every run pulls the base branch before a phase start, the convergence, the merge, and a resume, and resolves its conflicts in the same run', async () => {
  assert.equal(phaseNotStarted('## Phase 1\n\n- [ ] T001 a\n- [ ] T002 b\n'), true);
  assert.equal(phaseNotStarted('## Phase 1\n\n- [x] T001 a\n- [ ] T002 b\n'), false, 'a ticked task means the phase started');
  assert.equal(phaseNotStarted('## Phase 1\n\n- [x] T001 a\n\n## Phase 2\n\n- [ ] T002 b\n'), true, 'the next phase starts when the previous one is done');
  assert.equal(phaseNotStarted('## Phase 1\n\n- [x] T001 a\n'), false, 'no phase is left');
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    const advance = (branch) => {
      const sha = createHash('sha1').update(`commit ${github.repo.nextSha++}`).digest('hex');
      github.repo.commits[sha] = { tree: github.repo.commits[github.repo.branches[branch]]?.tree, parents: [github.repo.branches[branch]], message: `${branch} moved` };
      github.repo.branches[branch] = sha;
      return sha;
    };
    const head = () => github.repo.branches['speckit/a'];
    const lastComment = () => github.comments.at(-1).body;
    const sync = (env = {}) => decide(github, root, twin, env, runSyncBase);
    const settle = () => { for (const check of github.repo.checkRuns) check.status = 'completed'; };

    assert.equal((await sync()).outputs.result, 'none', 'nothing to merge');
    assert.equal((await decide(github, root, twin)).outputs.action, 'implement');

    advance('main');
    const before = head();
    const merged = await sync();
    assert.equal(merged.outputs.result, 'merged');
    assert.notEqual(head(), before);
    assert.match(merged.text, /Merged `main` at [0-9a-f]{7} into `speckit\/a` before the phase\./);
    assert.equal(await github.aheadBy('speckit/a', 'main'), 0);
    assert.equal((await decide(github, root, twin)).outputs.head, head(), 'the phase plans on the merged head');

    const conflicted = advance('main');
    github.mergeBranchOutcome = 'conflict';
    assert.equal((await sync()).outputs.result, 'conflict');
    const syncCheck = github.repo.checkRuns.find((check) => check.external_id === CHECK_SYNC_CONFLICT);
    assert.deepEqual([syncCheck.head_sha, syncCheck.status], [head(), 'completed']);
    assert.match(syncCheck.output.summary, new RegExp(conflicted));
    assert.match(lastComment(), /conflicts with `speckit\/a`\*\*: Copilot resolves the conflicts now, before the phase/);
    const resolve = await decide(github, root, twin);
    assert.deepEqual([resolve.outputs.action, resolve.outputs.job_name, JSON.parse(resolve.outputs.order)[0].include[0].verify_mode], ['resolve', 'Conflict resolution', 'finalize'], 'decide resolves in the same run');

    settle();
    await attemptComment(github, pull.number, null, 'failure', 'resolve');
    assert.equal((await decide(github, root, twin)).outputs.action, 'resolve', 'one failed attempt is retried');
    settle();
    await attemptComment(github, pull.number, null, 'failure', 'resolve');
    assert.equal((await sync()).outputs.result, 'none', 'a given-up head is not merged again');
    assert.equal((await decide(github, root, twin)).outputs.action, 'implement', 'the phase continues on the current base');
    assert.match(lastComment(), new RegExp(`^<!-- speckit-implement:sync-skipped ${conflicted} -->\\n\\*\\*\`main\` was not merged\\*\\*: resolving its conflicts`));
    const comments = github.comments.length;
    await decide(github, root, twin);
    assert.equal(github.comments.length, comments, 'the skip is reported once');

    advance('speckit/a');
    const skipped = await sync();
    assert.equal(skipped.outputs.result, 'none');
    assert.match(skipped.text, /was not merged before earlier work/, 'a skipped base commit is not tried again');

    github.mergeBranchOutcome = null;
    const refusedHead = head();
    const refused = advance('main');
    github.mergeBranchError = 'Resource not accessible by integration';
    assert.equal((await sync()).outputs.result, 'skipped');
    assert.equal(head(), refusedHead, 'a refused merge changes nothing');
    assert.match(lastComment(), new RegExp(`^<!-- speckit-implement:sync-skipped ${refused} -->\\n[^\\n]*GitHub refused the merge \\(Resource not accessible by integration\\)`));

    github.mergeBranchError = null;
    advance('main');
    assert.equal((await sync({ SPECKIT_SYNC_BASE: 'false' })).outputs.result, 'none', 'SPECKIT_SYNC_BASE=false turns it off');

    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', ' '));
    assert.equal((await sync()).outputs.result, 'none', 'a phase that has started is not merged into');
    assert.equal(head(), refusedHead);
    const resumedBefore = head();
    assert.equal((await sync({ SPECKIT_RESUME: 'true' })).outputs.result, 'merged', 'a resume merges the base branch first');
    assert.notEqual(head(), resumedBefore);
    advance('main');
    await github.createComment(pull.number, `${RESUME_COMMENT_MARKER}\nImplementation resumed by @dev.`);
    assert.equal((await sync()).outputs.result, 'merged', 'so does a pending resume comment (/speckit resume, a merged amendment)');
    await attemptComment(github, pull.number, 'T002', 'failure');
    advance('main');
    assert.equal((await sync()).outputs.result, 'none', 'once the resumed work started, its base stays');
    await github.createComment(pull.number, `${RETRY_COMMENT_MARKER}\nImplementation retried by @dev.`);
    assert.equal((await sync()).outputs.result, 'none', 'a retry (/speckit retry) restarts on the branch as it is');

    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
    await attemptComment(github, pull.number, 'T002', 'success');
    settle();
    const converging = await sync();
    assert.deepEqual([converging.outputs.result, /before the convergence/.test(converging.text)], ['merged', true]);
    assert.equal((await decide(github, root, twin)).outputs.action, 'converge');

    settle();
    await attemptComment(github, pull.number, null, 'success', 'converge');
    advance('main');
    github.mergeBranchOutcome = 'conflict';
    assert.equal((await sync()).outputs.result, 'conflict');
    assert.equal(github.repo.checkRuns.at(-1).external_id, CHECK_CONFLICT, 'before the merge the resolution is required');
    assert.match(lastComment(), /Copilot resolves the conflicts now, before the merge\.$/);
    const required = await decide(github, root, twin);
    assert.deepEqual([required.outputs.action, required.outputs.job_name], ['resolve', 'Conflict resolution']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
test('chain: decide converges, merges, and resolves conflicts', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
    await attemptComment(github, pull.number, 'T002', 'success');
    const converge = await decide(github, root, twin);
    assert.deepEqual([converge.outputs.action, converge.outputs.job_name], ['converge', 'Convergence']);
    assert.match(converge.text, new RegExp(`^Next: Spec Kit implement task #${twin.number} · Convergence\\.$`, 'm'));
    const [entry] = JSON.parse(converge.outputs.order);
    assert.deepEqual([entry.include[0].task, entry.include[0].attempt, entry.include[0].prompt.startsWith('/speckit-converge')], ['', 1, true]);
    assert.ok(github.repo.checkRuns.some((check) => check.external_id === 'speckit:attempt:converge::1' && check.status === 'in_progress'));

    for (const check of github.repo.checkRuns) check.status = 'completed';
    await attemptComment(github, pull.number, null, 'success', 'converge');
    const merge = await decide(github, root, twin);
    assert.deepEqual([merge.outputs.action, merge.outputs.continue, merge.outputs.attempt], ['merge', 'true', '1']);
    const mergeCheck = github.repo.checkRuns.find((check) => String(check.id) === merge.outputs.check_run);
    assert.deepEqual([mergeCheck.external_id, mergeCheck.status], [CHECK_MERGE, 'in_progress']);

    Object.assign(mergeCheck, { status: 'completed', conclusion: 'neutral', external_id: CHECK_CONFLICT });
    await github.createComment(pull.number, renderRetryComment('Implementation retried by @dev.', 'Keep both migration sequences and use the next free number.'));
    const resolve = await decide(github, root, twin);
    const resolveEntry = JSON.parse(resolve.outputs.order)[0].include[0];
    assert.deepEqual([resolve.outputs.action, resolve.outputs.job_name, resolveEntry.verify_mode], ['resolve', 'Conflict resolution', 'finalize']);
    assert.equal(resolveEntry.guidance, 'Keep both migration sequences and use the next free number.');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: continue starts the next run, and reports a failed phase verification as a stop', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    const env = { ...envFor(root), SPECKIT_TWIN: String(twin.number), SPECKIT_PULL: String(pull.number), SPECKIT_FOLDER: 'a', GITHUB_RUN_ID: '42' };
    await runContinue({ client: github, env: { ...env, SPECKIT_PHASE_CHECK: 'success' }, log: silent });
    assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-implement-spec.yml', { twin: String(twin.number), pull: String(pull.number), title: 'a title' }]);
    assert.equal(github.repo.runs.at(-1).display_title, `Spec Kit implement spec #${twin.number} · a title`);
    assert.equal(github.repo.checkRuns.some((check) => check.external_id === CHECK_LIMIT), false);

    await runContinue({ client: github, env: { ...env, SPECKIT_PHASE_CHECK: 'failure' }, log: silent });
    const stop = github.repo.checkRuns.at(-1);
    assert.deepEqual([stop.external_id, stop.conclusion, stop.output.title], [CHECK_LIMIT, 'failure', 'The phase verification failed']);
    assert.match(github.comments.at(-1).body, /Implementation needs attention\*\* @dev: the full verification at the end of the phase failed[\s\S]*actions\/runs\/42[\s\S]*covers only the tests it touched/);
    assert.equal(github.repo.runs.filter((run) => run.workflow === 'speckit-implement-spec.yml').length, 2, 'the next run reports the stop and starts a diagnosis');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
