import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { TWIN_LABEL } from './speckit-prepare-core.mjs';
import { renderImplementRequested, runSync } from './speckit-prepare.mjs';
import { UsageError, main, maintainFlakyTests, parseFlakyThreshold, resolveFolder, runOrchestrate, runRequest, runSelect, runStart } from './speckit-orchestrate.mjs';
import { decideForPull, phaseNotStarted, runContinue, runDecide, runPlanPhase, runSync as runSyncBase } from './speckit-spec.mjs';
import { ATTENTION_LABEL, BLOCKED_LABEL, CHECK_CONFLICT, CHECK_LIMIT, CHECK_MERGE, CHECK_PROGRESS, CHECK_RUN_NAME, CHECK_SYNC_CONFLICT, DONE_COMMENT_MARKER, RETRY_COMMENT_MARKER, START_COMMENT_MARKER, continuationOf, describeCommandEffect, parseGuidance, renderAttemptMarker, renderFixEvent, renderFlakyLines, renderFlakySpecMarker, renderRepairMarker, renderRetryComment } from './speckit-implement-core.mjs';
import { FakeGitHub, SPEC_TEMPLATE, envFor, makeRepo, silent } from './speckit-test-helpers.mjs';
import { CHECK_DIAGNOSING, DIAGNOSIS_STALE_MS, renderSyncComment, renderSyncBarrierMarker, renderSyncSkippedMarker, syncBarrier, unresolvedRepair } from './speckit-implement-core.mjs';

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
    assert.deepEqual(restart.ready, [{ number: twin.number, folder: 'a', requester: 'dev', reset: true, prerequisite: false }]);
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
    assert.deepEqual(result.ready, [{ number: a.number, folder: 'a', requester: 'dev', reset: false, prerequisite: false }]);
    assert.deepEqual(result.blocked.map((twin) => [twin.folder, twin.blockers.map((blocker) => blocker.number)]), [['b', [github.issues[3].number]]]);
    assert.deepEqual(result.inconsistent.map((twin) => twin.folder), ['c', 'e']);
    assert.match(result.inconsistent[1].reasons[0], /@outsider needs at least write access/);
    const output = readFileSync(path.join(root, 'output.txt'), 'utf8');
    assert.match(output, /count=1/);
    assert.match(output, new RegExp(`matrix=\\{"include":\\[\\{"number":${a.number},"folder":"a","requester":"dev","reset":false,"prerequisite":false\\}\\]\\}`));

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

test('request comments /speckit implement on a tasked twin with complete checklists, with the user\'s token', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: TASKS_OPEN }]);
  try {
    const lines = [];
    const git = fakeGit(remoteFiles('a'));
    github.commentAuthor = 'dev';
    const result = await runRequest({ client: github, git, rootDir: root, env: {}, folder: 'a', log: (line) => lines.push(line) });
    assert.equal(result.exitCode, 0);
    const issue = github.issues[0];
    assert.deepEqual(issue.labels.map((label) => label.name), [TWIN_LABEL, 'speckit:deps-pending', 'speckit:stage:tasked'], 'the command sets the label on GitHub, not the client');
    assert.equal(github.updates.length, 0);
    const posted = github.comments.at(-1);
    assert.deepEqual([posted.number, posted.body, posted.user.login], [issue.number, '/speckit implement', 'dev']);
    assert.equal(result.comment.html_url, posted.html_url);
    assert.deepEqual(git.calls[0], ['fetch', '--quiet', 'origin', 'main']);
    assert.match(lines.join('\n'), new RegExp(`Requested: posted \`/speckit implement\` on #${issue.number} \\(${posted.html_url.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')}\\)[\\s\\S]*No open blockers`));
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

    // The commands workflow accepted the request and set the label.
    a.labels = [...a.labels.filter((label) => !label.name.startsWith('speckit:stage:')), { name: IMPLEMENT }];
    github.updates = [];
    const comments = github.comments.length;
    const again = await run(remoteFiles('a'));
    assert.equal(again.code, 0);
    assert.match(again.text, /Already requested/);
    assert.equal(github.updates.length, 0);
    assert.equal(github.comments.length, comments, 'no second request');
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
    const stopped = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 3, 'a stop always continues so the chain can start its diagnosis');
    assert.match(stopped.text, /Continued/);
    await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 3, 'a running diagnosis is not started twice');

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

test('orchestrate does not continue an implementation that is blocked by its prerequisite spec', async () => {
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
    assert.equal(chainRuns(github).length, 1, 'neither a diagnosis nor a retry starts while the prerequisite is pending');
    assert.match(blocked.text, /blocked; it waits for its prerequisite spec/);
    assert.ok(github.repo.pulls[0].labels.some((label) => label.name === BLOCKED_LABEL));
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:blocked \{"pull":99,"stage":"implement"\} -->\n\*\*Blocked\*\* by spec #99/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// A stopped implementation of twin "a" waiting for the merged fix spec #99, linked to fix twin #77.
async function blockedByLinkedFix() {
  const { root, github, twin } = await flaggedRepo();
  await orchestrate(github, root);
  const pull = github.repo.pulls[0];
  github.completeRun(github.repo.runs.at(-1).id, 'success');
  await limitCheck(github, github.repo.branches['speckit/a']);
  const fixFolder = '20261016-000000-fix-x';
  github.repo.pulls.push({ number: 99, state: 'closed', merged_at: github.tick(), base: { ref: 'main' }, head: { ref: `speckit-fix/${fixFolder}`, sha: 'x' }, labels: [] });
  github.issues.push({ number: 77, id: 77000, node_id: 'I_77', state: 'open', state_reason: null, title: 'Fix x', body: '', labels: [] });
  await github.createComment(pull.number, renderFixEvent('created', { pull: 99, folder: fixFolder }));
  await github.createComment(pull.number, renderFixEvent('merged', { pull: 99, folder: fixFolder }));
  await github.createComment(pull.number, renderFixEvent('linked', { pull: 99, folder: fixFolder, twin: 77 }));
  await orchestrate(github, root);
  assert.ok(pull.labels.some((label) => label.name === BLOCKED_LABEL));
  return { root, github, twin, pull };
}

test('orchestrate lifts the block and retries the implementation in the run after its legacy fix spec landed', async () => {
  const { root, github, pull } = await blockedByLinkedFix();
  try {
    const before = chainRuns(github).length;
    Object.assign(github.find(77), { state: 'closed', state_reason: 'completed' });
    const result = await orchestrate(github, root);
    assert.equal(pull.labels.some((label) => label.name === BLOCKED_LABEL), false);
    assert.equal(chainRuns(github).length, before + 1, 'the implementation is retried once');
    assert.match(result.text, /The prerequisites of #\d+ are implemented \(#77\); lifted its block and retried it\./);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate lifts the block of a blocked implementation whose twin is not in progress', async () => {
  const { root, github, twin, pull } = await blockedByLinkedFix();
  try {
    // The twin lost its implementation request, so the twin loop skips it.
    twin.labels = twin.labels.filter((label) => !label.name.startsWith('speckit:stage:'));
    const before = chainRuns(github).length;
    Object.assign(github.find(77), { state: 'closed', state_reason: 'completed' });
    const result = await orchestrate(github, root);
    assert.equal(pull.labels.some((label) => label.name === BLOCKED_LABEL), false, 'the sweep lifts the block');
    assert.equal(chainRuns(github).length, before + 1, 'and retries the implementation');
    assert.match(result.text, new RegExp(`Lifted the block of #${pull.number} \\(retried\\)`));
    await orchestrate(github, root);
    assert.equal(chainRuns(github).length, before + 1, 'an unblocked pull request is not swept again');
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

// A running implementation of twin "a" whose twin a person made depend on the open issue #77.
async function blockedByHand() {
  const { root, github, twin } = await flaggedRepo();
  await orchestrate(github, root);
  const pull = github.repo.pulls[0];
  github.completeRun(github.repo.runs.at(-1).id, 'success');
  github.issues.push({ number: 77, id: 77000, node_id: 'I_77', state: 'open', state_reason: null, title: 'Other', body: '', labels: [] });
  github.edges.push([twin.number, 77]);
  return { root, github, twin, pull };
}

test('a running implementation with a hand-added open blocker is blocked, not continued, and retries once it is implemented', async () => {
  const { root, github, twin, pull } = await blockedByHand();
  try {
    const blocked = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 1, 'a blocked implementation is not continued');
    assert.ok(pull.labels.some((label) => label.name === BLOCKED_LABEL));
    assert.match(blocked.text, /blocked; it waits for its prerequisites #77/);
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:blocked \{"blockers":\[77\],"unrequested":\[77\]\} -->\n\*\*Blocked\*\* by prerequisite #77: [^\n]*waits instead of continuing\.\n\n\*\*Recommended next step\*\*: Comment the following on spec twin #77 to request its implementation\.\n\n```text\n\/speckit implement\n```[\s\S]*- \*\*Unblocks by itself\*\* once #77 is implemented/);
    const explained = github.comments.length;
    await orchestrate(github, root);
    assert.equal(github.comments.length, explained, 'the block is explained once');
    assert.equal((await decide(github, root, twin)).outputs.action, 'none', 'the chain itself stops before its next work');
    assert.equal(github.comments.length, explained);

    Object.assign(github.find(77), { state: 'closed', state_reason: 'completed' });
    const lifted = await orchestrate(github, root);
    assert.equal(pull.labels.some((label) => label.name === BLOCKED_LABEL), false);
    assert.equal(chainRuns(github).length, 2, 'the implementation is retried');
    assert.match(lifted.text, /The prerequisites of #\d+ are implemented \(#77\); lifted its block and retried it\./);
    const retry = github.comments.filter((comment) => comment.number === pull.number).at(-1);
    assert.ok(retry.body.startsWith(RETRY_COMMENT_MARKER), 'a fresh attempt count on the current phase base');
    assert.match(retry.body, /<!-- speckit-implement:unblocked -->\n\*\*The block is lifted\*\*: prerequisite #77 is implemented/);
    assert.match(parseGuidance(retry.body), /the spec twin\(s\) #77/);
    const retries = () => github.comments.filter((comment) => comment.body.includes('<!-- speckit-implement:unblocked -->')).length;
    await orchestrate(github, root);
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await github.addLabels(pull.number, [BLOCKED_LABEL]);
    await orchestrate(github, root);
    assert.equal(retries(), 1, 'retried only once, even when a stale label remains');
    assert.equal(pull.labels.some((label) => label.name === BLOCKED_LABEL), false);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a blocker closed as not planned lifts the block and asks for a person instead of retrying', async () => {
  const { root, github, pull } = await blockedByHand();
  try {
    await orchestrate(github, root);
    assert.ok(pull.labels.some((label) => label.name === BLOCKED_LABEL));
    Object.assign(github.find(77), { state: 'closed', state_reason: 'not_planned' });
    await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 1, 'no retry');
    assert.deepEqual(pull.labels.map((label) => label.name), [ATTENTION_LABEL]);
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:unblocked -->\n\*\*A prerequisite was not implemented\*\*: #77 was closed without being implemented\. The block is lifted[\s\S]*\*\*Recommended next step\*\*/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('an unrequested blocker of a running implementation never starts; once requested with /speckit implement it starts first at SPECKIT_MAX_ACTIVE_SPECS', async () => {
  const tasks = '## Phase 1\n\n- [ ] T001 one\n';
  const root = makeRepo([{ folder: 'a', plan: true, tasks }, { folder: 'b', plan: true, tasks }]);
  try {
    writeFileSync(path.join(root, 'specs', 'b', 'spec.md'), `${SPEC_TEMPLATE('b')}\n**Fix for**: #75 (implementation of \`specs/a\`)\n`);
    const github = new FakeGitHub();
    await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
    const [a, b] = github.issues;
    assert.ok(b.labels.some((label) => label.name === 'speckit:deps-pending'), 'a spec authored for a stop gets dependency inference like any spec, even with an earlier Fix for line');
    b.labels = b.labels.filter((label) => label.name !== 'speckit:deps-pending');
    github.permissions.dev = 'write';
    github.permissions.lead = 'write';
    flag(github, a);
    await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.equal(chainRuns(github).length, 1);
    github.edges.push([a.number, b.number]);

    const waiting = await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.deepEqual(stagesOf(github.find(b.number)), ['speckit:stage:tasked'], 'never requested automatically');
    assert.equal(github.comments.filter((comment) => comment.number === b.number).length, 0);
    assert.equal(chainRuns(github).length, 1, 'an unrequested blocker never starts');
    assert.doesNotMatch(waiting.text, /Requested prerequisite/);

    // Someone signs it off with /speckit implement on its twin (Spec Kit commands records it and sets the label).
    await github.createComment(b.number, `${renderImplementRequested({ by: 'lead', comment: 1 })}\n**Implementation requested** by @lead.`);
    github.find(b.number).labels = [...github.find(b.number).labels.filter((label) => !label.name.startsWith('speckit:stage:')), { name: IMPLEMENT }];
    const started = await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.equal(chainRuns(github).at(-1).display_title, `Spec Kit implement spec #${b.number} · b title`, 'a requested blocker of a running implementation starts although the limit is reached');
    assert.match(started.text, new RegExp(`Started the implementation of #${b.number}`));
    assert.deepEqual(github.repo.pulls.find((pull) => pull.head.ref === 'speckit/b').assignees, ['lead'], 'assigned to the person who signed it off');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate settles a merged amendment and continues the implementation with a fresh attempt count', async () => {
  const { root, github } = await flaggedRepo();
  try {
    await orchestrate(github, root);
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await limitCheck(github, github.repo.branches['speckit/a']);
    const implementation = github.repo.pulls[0];
    const runs = chainRuns(github).length;

    // A person merged the amendment; the amendment branch's workflows did not report it.
    github.repo.pulls.push({ number: 77, node_id: 'PR_77', title: 'Amend: a', body: '', draft: false, state: 'closed', merged_at: github.tick(), closed_at: github.tick(), merged_by: { login: 'dev' }, base: { ref: 'speckit/a' }, head: { ref: 'speckit-amend/a', sha: 'sha-x' }, created_at: github.tick() });
    const settled = await orchestrate(github, root);
    const retry = github.comments.find((comment) => comment.number === implementation.number && comment.body.includes('<!-- speckit-amend:closed 77 -->'));
    assert.match(retry.body, /^<!-- speckit-implement:retry -->\n<!-- speckit-amend:closed 77 -->\n\*\*Amendment #77 was merged\*\* by @dev\./);
    assert.match(settled.text, /Amendment #77 was merged/);
    assert.equal(chainRuns(github).length, runs + 1, 'the retry continues the stopped implementation');

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await orchestrate(github, root);
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
    assert.equal((await decide(github, root, twin)).outputs.action, 'stop');
    await github.createComment(pull.number, renderRetryComment('Implementation retried by @dev.', 'Keep the helper.'));
    const retried = await decide(github, root, twin);
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
    const effect = describeCommandEffect('/speckit retry', { baseBranch: 'main', continuation });
    assert.match(effect, /reverifies without running a repair agent.*pending group verification/);
    assert.doesNotMatch(effect, /convergence|not repeated by itself|fresh attempt count/);
    assert.match(describeCommandEffect('/speckit retry Investigate the logs', { continuation }), /repair agent with the guidance, then reverifies/);
    assert.equal((await decide(github, root, twin)).outputs.action, 'stop', 'checked tasks cannot bypass pending repair');
    await github.createComment(pull.number, renderRetryComment('Implementation retried by @dev.', 'Fix the combined registration and rerun its tests.'));
    const authorized = await decide(github, root, twin);
    assert.equal(authorized.outputs.action, 'repair');
    assert.equal(JSON.parse(authorized.outputs.repair).id, repair.id);
    assert.match(authorized.outputs.prompt, /Writer-approved guidance: Fix the combined registration/);
    assert.equal(authorized.outputs.attempt, '1');
    assert.equal(authorized.outputs.verify_only, 'false');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: a retry reverifies a pending repair without guidance or a repair agent', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    const head = github.repo.branches['speckit/a'];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
    await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'completed', conclusion: 'failure', external_id: CHECK_LIMIT, output: { title: 'Phase failed', summary: 'transient failure' } });
    const repair = { id: `phase:1:${head}`, state: 'pending', scope: 'phase', tasks: ['T001', 'T002'], head, at: github.tick() };
    await github.createComment(pull.number, `${renderRepairMarker(repair)}\npending`);
    await github.createComment(pull.number, renderRetryComment('Implementation retried by @dev.'));
    const retried = await decide(github, root, twin);
    assert.deepEqual([retried.outputs.action, retried.outputs.verify_only], ['repair', 'true']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: decide stops at the attempt limit, retries with guidance, and does nothing for unflagged twins', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    for (let attempt = 0; attempt < 3; attempt += 1) await attemptComment(github, pull.number, 'T001', 'failure');
    pull.draft = false;
    const limited = await decide(github, root, twin);
    assert.deepEqual([limited.outputs.action, limited.outputs.continue], ['stop', 'false']);
    assert.equal(pull.draft, true, 'a stop waiting for a person restores draft status');
    assert.equal(github.repo.checkRuns.at(-1).external_id, CHECK_DIAGNOSING);
    assert.match(github.comments.at(-1).body, /Implementation needs attention\*\* @dev: T001 did not succeed in 3 attempts[\s\S]*Wait for the diagnosis[\s\S]*The implementation continues with T001, with a fresh attempt count[^\n]*That work merges `main` first at its normal synchronization boundary[\s\S]*\/speckit retry/, 'T001 opens a phase that has not started, so the normal boundary merges main');
    assert.equal((await decide(github, root, twin)).outputs.action, 'none', 'a stop waits for its running diagnosis');
    const attention = () => (github.repo.pulls[0].labels ?? []).some((label) => label.name === ATTENTION_LABEL);
    assert.equal(attention(), false, 'a running diagnosis is processing');

    pull.draft = false;
    const diagnosing = await decide(github, root, twin);
    assert.deepEqual([diagnosing.outputs.action, diagnosing.outputs.continue], ['none', 'false']);
    assert.equal(pull.draft, true, 'waiting for a diagnosis result restores draft status');

    await github.addLabels(pull.number, [ATTENTION_LABEL]);
    pull.draft = false;
    await github.createComment(pull.number, renderRetryComment('Implementation retried by @dev.', 'Use the existing helper.'));
    const retried = await decide(github, root, twin);
    assert.equal(retried.outputs.action, 'implement');
    assert.equal(pull.draft, true, 'retried implementation work restores draft status');
    assert.equal(attention(), false, 'a retried implementation is processing');
    const comment = github.comments.find((item) => item.body.startsWith(RETRY_COMMENT_MARKER));
    assert.equal(parseGuidance(comment.body), 'Use the existing helper.');
    const [first, second] = JSON.parse((await planPhase(github, root, twin, retried.outputs.phase)).outputs.order);
    assert.deepEqual([first.include[0].task, first.include[0].attempt], ['T001', 1], 'a retry starts a new attempt window');
    assert.match(first.include[0].prompt, /Guidance from the person who retried this implementation: Use the existing helper\.$/);
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

test('required sync survives replacement, disabled/skipped automatic sync, and base movement before release', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    github.repo.branches.release = advanceRepairBase(github);
    pull.base.ref = 'release';
    await github.createComment(pull.number, renderSyncSkippedMarker(github.repo.branches.release));
    await github.createComment(pull.number, renderSyncComment('Sync required', 'Keep the release fix'));
    const request = syncBarrier(github.comments);
    const inspect = () => decideForPull(github, { twin: twin.number, folder: 'a', pullNumber: pull.number, now: github.clock });
    assert.equal((await decide(github, root, twin, { SPECKIT_SYNC_BASE: 'false' }, runSyncBase)).outputs.result, 'required');
    assert.equal((await decide(github, root, twin, { SPECKIT_SYNC_BASE: 'true' }, runSyncBase)).outputs.result, 'required', 'a previously skipped base cannot bypass required synchronization');
    for (let replacement = 0; replacement < 2; replacement++) {
      const decision = await inspect();
      assert.deepEqual([decision.action, decision.kind, decision.requiredSync.request], ['tasks', 'resolve', request.request]);
      assert.equal(decision.requiredSync.guidance, 'Keep the release fix');
    }
    assert.equal((await planPhase(github, root, twin, 'Phase 1: Setup')).outputs.count, '0', 'normal planning is blocked');
    await github.createComment(pull.number, renderRetryComment('Retry must not remove the barrier'));
    assert.equal((await inspect()).requiredSync.request, request.request);
    const baseSha = github.repo.branches.release;
    // The isolated work run merged and verified this exact head/base pair.
    await github.mergeBranch('speckit/a', 'release', 'verified sync');
    const head = github.repo.branches['speckit/a'];
    await github.createComment(pull.number, renderSyncBarrierMarker({ request: request.request, state: 'verified', head, base: 'release', baseSha }));
    const newer = advanceRepairBase(github);
    github.repo.branches.release = newer;
    assert.equal((await inspect()).kind, 'resolve', 'a moving base cannot release a stale verification');
    assert.ok(syncBarrier(github.comments));
    await github.mergeBranch('speckit/a', 'release', 'verified latest sync');
    await github.createComment(pull.number, renderSyncBarrierMarker({ request: request.request, state: 'verified', head: github.repo.branches['speckit/a'], base: 'release', baseSha: newer }));
    const compare = github.aheadBy.bind(github);
    github.aheadBy = async (...args) => {
      const result = await compare(...args);
      github.repo.branches.release = advanceRepairBase(github);
      return result;
    };
    assert.equal((await inspect()).kind, 'resolve', 'base movement during the release comparison also holds the barrier');
    github.aheadBy = compare;
    await github.mergeBranch('speckit/a', 'release', 'verified base after comparison race');
    await github.createComment(pull.number, renderSyncBarrierMarker({ request: request.request, state: 'verified', head: github.repo.branches['speckit/a'], base: 'release', baseSha: github.repo.branches.release }));
    const getBase = github.getBranchSha.bind(github);
    github.getBranchSha = async () => { throw new Error('comparison unavailable'); };
    assert.equal((await inspect()).kind, 'resolve', 'unknown base freshness cannot release the barrier');
    github.getBranchSha = getBase;
    assert.equal((await inspect()).kind, 'task');
    assert.equal(syncBarrier(github.comments), null);
    assert.match(github.comments.at(-1).body, /\*\*Required synchronization complete\*\*: `release` at `[0-9a-z-]{1,7}` is merged and verified/, 'the release is visible, not only a marker');
    assert.equal((await inspect()).kind, 'task', 'release is durable across later runs');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('automatic base integration rechecks a sync accepted while its merge was being prepared', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    advanceRepairBase(github);
    const before = github.repo.branches['speckit/a'];
    const getBase = github.getBranchSha.bind(github);
    github.getBranchSha = async (...args) => {
      const sha = await getBase(...args);
      await github.createComment(pull.number, renderSyncComment('Sync accepted during base comparison'));
      github.getBranchSha = getBase;
      return sha;
    };
    assert.equal((await decide(github, root, twin, {}, runSyncBase)).outputs.result, 'required');
    assert.equal(github.repo.branches['speckit/a'], before);
    assert.equal((await decide(github, root, twin)).outputs.action, 'resolve');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('clean sync preserves unconsumed guidance for the first continued task, while an agent resolution consumes it', async () => {
  for (const consumed of [false, true]) {
    const { root, github, twin } = await flaggedRepo();
    try {
      await start(github, root, twin);
      const pull = github.repo.pulls[0];
      github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
      await github.createComment(pull.number, renderSyncComment('Required sync', 'Preserve both registrations'));
      const barrier = syncBarrier(github.comments);
      await attemptComment(github, pull.number, null, 'success', 'resolve');
      await github.createComment(pull.number, renderSyncBarrierMarker({
        request: barrier.request, state: 'verified', head: github.repo.branches['speckit/a'],
        base: 'main', baseSha: github.repo.branches.main, guidanceConsumed: consumed,
      }));
      const chosen = await decide(github, root, twin);
      assert.equal(chosen.outputs.action, 'implement');
      const planned = await planPhase(github, root, twin, chosen.outputs.phase);
      const prompt = JSON.parse(planned.outputs.order)[0].include[0].prompt;
      assert.equal(prompt.includes('Guidance from the person who retried this implementation: Preserve both registrations'), !consumed);
      for (const check of github.repo.checkRuns) check.status = 'completed';
      await attemptComment(github, pull.number, 'T001', 'success');
      github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', ' '));
      const next = await planPhase(github, root, twin, chosen.outputs.phase);
      assert.doesNotMatch(JSON.parse(next.outputs.order)[0].include[0].prompt, /Preserve both registrations/, 'guidance ends after the first continued task succeeds');
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});

test('sync carries unconsumed guidance to a pending repair but does not repeat guidance consumed by resolution', async () => {
  for (const consumed of [false, true]) {
    const { root, github, twin, pull } = await restartedRepair(renderSyncComment, 'Keep the combined registrations');
    try {
      const barrier = syncBarrier(github.comments);
      await attemptComment(github, pull.number, null, 'success', 'resolve');
      await github.createComment(pull.number, renderSyncBarrierMarker({
        request: barrier.request, state: 'verified', head: github.repo.branches['speckit/a'],
        base: 'main', baseSha: github.repo.branches.main, guidanceConsumed: consumed,
      }));
      const next = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
      assert.equal(next.outputs.action, 'repair');
      assert.equal(next.outputs.verify_only, String(consumed));
      assert.equal(next.outputs.prompt.includes('Writer-approved guidance: Keep the combined registrations'), !consumed);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});

test('required sync without original-scope coverage proof preserves the pending repair and its attempt limit', async () => {
  for (const failures of [1, 3]) {
    const { root, github, twin, pull } = await restartedRepair(renderRetryComment);
    try {
      const original = unresolvedRepair(github.comments);
      for (let index = 0; index < failures; index++) await attemptComment(github, pull.number, null, 'failure', 'repair');
      await github.createComment(pull.number, renderSyncComment('Required sync', 'Preserve the registry entries'));
      const request = syncBarrier(github.comments);
      const inspect = () => decideForPull(github, { twin: twin.number, folder: 'a', pullNumber: pull.number, now: github.clock });
      assert.equal((await inspect()).kind, 'resolve');
      assert.deepEqual(unresolvedRepair(github.comments), original);
      await github.createComment(pull.number, renderSyncBarrierMarker({
        request: request.request, state: 'verified', head: github.repo.branches['speckit/a'], base: 'main', baseSha: github.repo.branches.main,
      }));
      const after = await inspect();
      assert.equal(after.action, failures === 3 ? 'failed' : 'repair');
      if (failures === 1) {
        assert.equal(after.attempt, 2, 'sync does not reset repair failures');
        assert.deepEqual(after.repair, original);
        assert.equal(after.guidance, 'Preserve the registry entries');
      }
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});

test('required sync can clear an exhausted repair only after authoritative verification of its original scope', async () => {
  for (const failures of [3, 5]) {
    const { root, github, twin, pull } = await restartedRepair(renderRetryComment);
    try {
      const original = unresolvedRepair(github.comments);
      for (let index = 0; index < failures; index++) await attemptComment(github, pull.number, null, 'failure', 'repair');
      await github.createComment(pull.number, renderSyncComment('Merge and verify the upstream fix'));
      const barrier = syncBarrier(github.comments);
      await github.createComment(pull.number, renderSyncBarrierMarker({
        request: barrier.request, state: 'verified', head: github.repo.branches['speckit/a'],
        base: 'main', baseSha: github.repo.branches.main, repair: { id: original.id, covered: true, snapshot: original },
      }));
      const next = await decideForPull(github, { twin: twin.number, folder: 'a', pullNumber: pull.number, now: github.clock });
      assert.notEqual(next.action, 'limit', 'successful authoritative finalize recovers even after the repair failure limit');
      assert.equal(next.action, 'tasks');
      assert.equal(next.kind, 'converge');
      assert.equal(unresolvedRepair(github.comments), null);
      assert.equal(syncBarrier(github.comments), null);
      assert.equal(next.ledger.filter((attempt) => attempt.kind === 'repair' && attempt.outcome === 'failure').length, failures, 'the old failures are not reset or replaced with an extra repair attempt');
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});

test('required sync detects cancelled, crashed and timed-out diagnoses instead of remaining permanently diagnosing', async () => {
  for (const outcome of ['cancelled', 'failure', 'timeout', 'running']) {
    const { root, github, twin } = await flaggedRepo();
    try {
      await start(github, root, twin);
      const pull = github.repo.pulls[0];
      github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
      await github.createComment(pull.number, renderSyncComment('Required sync'));
      const check = await github.createCheckRun({
        name: CHECK_RUN_NAME, head_sha: github.repo.branches['speckit/a'],
        external_id: CHECK_DIAGNOSING, status: 'in_progress',
      });
      await github.dispatchWorkflow('speckit-diagnose.lock.yml', 'main', { twin: String(twin.number), mode: 'diagnose' });
      if (['cancelled', 'failure'].includes(outcome)) github.completeRun(github.repo.runs.at(-1).id, outcome);
      if (outcome === 'timeout') github.clock += DIAGNOSIS_STALE_MS + 1;
      const inspected = await decideForPull(github, { twin: twin.number, folder: 'a', pullNumber: pull.number, now: github.clock });
      assert.equal(inspected.action, outcome === 'running' ? 'diagnosing' : 'failed');
      if (outcome !== 'running') {
        assert.equal(inspected.diagnosis.state, 'stale');
        const stopped = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
        assert.equal(stopped.outputs.action, 'stop');
        assert.equal(github.repo.checkRuns.find((item) => item.id === check.id).status, 'completed');
        assert.ok(syncBarrier(github.comments), 'a stopped diagnosis does not release required synchronization');
        await github.createComment(pull.number, renderRetryComment('Continue required synchronization'));
        assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, 'resolve');
      }
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});

test('required sync failure limits and protected-conflict attention never fall back to normal work', async () => {
  for (const outcome of ['failure', 'attention']) {
    const { root, github, twin } = await flaggedRepo();
    try {
      await start(github, root, twin);
      const pull = github.repo.pulls[0];
      github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
      await github.createComment(pull.number, renderSyncComment('Required sync'));
      for (let index = 0; index < (outcome === 'failure' ? 3 : 1); index++) await attemptComment(github, pull.number, null, outcome, 'resolve');
      const stopped = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
      assert.deepEqual([stopped.outputs.action, stopped.outputs.continue], ['stop', 'false']);
      assert.ok(syncBarrier(github.comments), 'a failed required sync cannot release the barrier');
      assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, outcome === 'failure' ? 'none' : 'stop', 'automatic diagnosis waits without resuming work; protected conflicts stay stopped');
      await github.createComment(pull.number, renderRetryComment('Try sync again'));
      assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, 'resolve', 'retry preserves and rearms required sync');
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});

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

test('chain: every run pulls the base branch before a phase start, the convergence, and the merge, and resolves its conflicts in the same run', async () => {
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
    advance('main');
    await github.createComment(pull.number, `${RETRY_COMMENT_MARKER}\nImplementation retried by @dev.`);
    assert.equal((await sync()).outputs.result, 'none', 'a retry preserves the started phase base');

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
    const comments = github.comments.length;
    await runContinue({ client: github, env: { ...env, SPECKIT_PHASE_CHECK: 'success', SPECKIT_PHASE: 'Phase 1: Setup', SPECKIT_VERIFY_FLAKY: 'Ns.A.Works' }, log: silent });
    assert.equal(github.comments.length, comments + 1, 'a passed phase verification reports only its flaky tests');
    assert.match(github.comments.at(-1).body, /^\*\*The phase verification passed\*\* for Phase 1: Setup on `[^`]+`\.\n\n- Flaky \(passed on rerun\): `Ns\.A\.Works`\n<!-- speckit-implement:flaky \{"tests":\["Ns\.A\.Works"\]\} -->$/);

    await runContinue({ client: github, env: { ...env, SPECKIT_PHASE_CHECK: 'failure', SPECKIT_VERIFY_FLAKY: 'Ns.B.Works' }, log: silent });
    const stop = github.repo.checkRuns.at(-1);
    assert.deepEqual([stop.external_id, stop.conclusion, stop.output.title], [CHECK_LIMIT, 'failure', 'The phase verification failed']);
    assert.match(github.comments.at(-1).body, /Implementation needs attention\*\* @dev: the full verification at the end of the phase failed[\s\S]*actions\/runs\/42[\s\S]*covers only the tests it touched[^\n]*\n\n- Flaky \(passed on rerun\): `Ns\.B\.Works`\n<!-- speckit-implement:flaky/);
    assert.equal(github.repo.runs.filter((run) => run.workflow === 'speckit-implement-spec.yml').length, 3, 'the next run reports the stop and starts a diagnosis');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the flaky-test ledger starts one non-blocking spec per test that is flaky in enough implementations', async () => {
  const github = new FakeGitHub([5, 6, 7].map((number) => ({ number, id: number * 1000, node_id: `I_${number}`, state: 'open', title: `T${number}`, body: '', labels: [] })));
  github.permissions.dev = 'write';
  github.humanLabel(6, IMPLEMENT, 'dev');
  const day = 24 * 60 * 60 * 1000;
  const pull = (number, ref, twin, fields = {}) => {
    github.repo.branches[ref] = `sha-${number}`;
    github.repo.pulls.push({ number, node_id: `PR_${number}`, title: `Implement: ${ref}`, body: `Closes #${twin}\n`, draft: true, state: 'open', merged_at: null, base: { ref: 'main', repo: { full_name: 'octo/repo' } }, head: { ref, sha: `sha-${number}`, repo: { full_name: 'octo/repo' } }, user: { login: 'dev' }, created_at: github.tick(), ...fields });
  };
  const flaky = (number, tests) => github.createComment(number, [`**T001 implemented** (attempt 1)`, '', ...renderFlakyLines(tests)].join('\n'));
  pull(20, 'speckit/a', 5);
  pull(22, 'speckit/c', 7, { state: 'closed', merged_at: new Date(github.clock - 2 * day).toISOString(), updated_at: new Date(github.clock - 2 * day).toISOString() });
  pull(23, 'speckit/d', 7, { state: 'closed', merged_at: new Date(github.clock - 20 * day).toISOString(), updated_at: new Date(github.clock - 20 * day).toISOString() });
  pull(24, 'speckit-amend/a', 5);
  pull(21, 'speckit/b', 6);
  for (const number of [23, 22, 20]) await flaky(number, ['Ns.Store.Saves', 'Ns.Covered.Test']);
  await flaky(24, ['Ns.Store.Saves']);
  github.commentAuthor = 'mallory';
  for (const number of [20, 21, 22]) await flaky(number, ['Ns.Forged.Test']);
  github.commentAuthor = undefined;
  await github.createComment(22, renderFlakySpecMarker('Ns.Covered.Test'));
  const env = { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main' };

  const below = await maintainFlakyTests(github, env, { now: github.clock });
  assert.deepEqual(below.started, [], 'only two implementations in the window (the old merge and the amendment do not count)');

  await flaky(21, ['Ns.Store.Saves', 'Ns.Covered.Test']);
  await flaky(20, ['Ns.Other.Test']);
  const result = await maintainFlakyTests(github, env, { now: github.clock });
  assert.deepEqual(result.started, [{ test: 'Ns.Store.Saves', pull: 21, pulls: [21, 20, 22] }], 'started on the open pull request where it was flaky most recently');
  assert.match(result.lines.join('\n'), /`Ns\.Store\.Saves` was flaky in #20, #21, #22; started a spec to make it deterministic on #21\./);
  const specify = github.repo.runs.filter((run) => run.workflow === 'speckit-specify.lock.yml');
  assert.equal(specify.length, 1);
  assert.deepEqual([specify[0].inputs.twin, specify[0].inputs.pull, specify[0].inputs.mode], ['6', '21', 'author']);
  assert.match(specify[0].inputs.guidance, /The test `Ns\.Store\.Saves` failed and then passed on its one rerun in the verifications of 3 implementation pull requests \(#20, #21, #22\)[\s\S]*Do not weaken, skip, or delete the test/);
  const onPull = github.comments.filter((comment) => comment.number === 21);
  assert.match(onPull.at(-2).body, /^<!-- speckit-fix:requested \{"by":"dev","stamp":"\d{8}-\d{6}","link":false\} -->/);
  assert.match(onPull.at(-1).body, /^<!-- speckit-implement:flaky-spec \{"test":"Ns\.Store\.Saves"\} -->\n\*\*Flaky test\*\* `Ns\.Store\.Saves` passed only on its rerun in 3 implementations/);
  assert.ok(!(github.repo.pulls.find((item) => item.number === 21).labels ?? []).some((label) => [BLOCKED_LABEL, ATTENTION_LABEL].includes(label.name)));

  github.repo.checkRuns.at(-1).status = 'completed';
  const again = await maintainFlakyTests(github, env, { now: github.clock });
  assert.deepEqual(again.started, [], 'a test is started once');
  assert.equal(github.repo.runs.filter((run) => run.workflow === 'speckit-specify.lock.yml').length, 1);
  assert.equal(parseFlakyThreshold('2'), 2);
  assert.equal(parseFlakyThreshold(''), 3);
  assert.equal(parseFlakyThreshold('x'), 3);
  const lower = await maintainFlakyTests(github, { ...env, SPECKIT_FLAKY_THRESHOLD: '1' }, { now: github.clock });
  assert.match(lower.lines.join('\n'), /`Ns\.Other\.Test` was flaky in #20; started a spec/);
});
