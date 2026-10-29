import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { TWIN_LABEL } from './speckit-prepare-core.mjs';
import { renderImplementRequested, runSync } from './speckit-prepare.mjs';
import { UsageError, main, maintainFlakyTests, parseFlakyThreshold, resolveFolder, runOrchestrate, runRequest, runSelect, runStart } from './speckit-orchestrate.mjs';
import { decideForPull, phaseNotStarted, reconcileImplementationAttention, runContinue, runDecide, runSync as runSyncBase, runVerifyReport } from './speckit-spec.mjs';
import { ATTENTION_LABEL, BLOCKED_LABEL, CHECK_CONFLICT, CHECK_LIMIT, CHECK_MERGE, CHECK_PROGRESS, CHECK_RUN_NAME, CHECK_SYNC_CONFLICT, DONE_COMMENT_MARKER, RETRY_COMMENT_MARKER, START_COMMENT_MARKER, continuationOf, describeCommandEffect, parseGuidance, renderAttemptMarker, renderFixEvent, renderFlakyLines, renderFlakySpecMarker, renderRepairMarker, renderRetryComment } from './speckit-implement-core.mjs';
import { FakeGitHub, SPEC_TEMPLATE, diagnosisRepo, envFor, makeRepo, silent } from './speckit-test-helpers.mjs';
import { CHECK_DIAGNOSED, CHECK_DIAGNOSING, COPILOT_HANDOFF_MARKER, DIAGNOSIS_STALE_MS, FIX_CHECK_NAME, renderSyncComment, renderSyncBarrierMarker, renderSyncSkippedMarker, syncBarrier, unresolvedRepair } from './speckit-implement-core.mjs';

const IMPLEMENT = 'speckit:stage:implement';
const IMPLEMENTING = 'speckit:stage:implementing';
const stagesOf = (issue) => issue.labels.map((label) => label.name).filter((name) => name.startsWith('speckit:stage:'));
const TASKS_OPEN = '- [ ] T001 one\n- [ ] T002 two\n';

test('implementation attention follows the current decision, not old comments, unrelated activity, or dependent age', async () => {
  const github = diagnosisRepo({ amendment: false });
  const pull = await github.getPullRequest(9);
  await github.createComment(9, '<!-- speckit-implement:diagnosis -->\n**Recommended next step**: Make a decision.');
  const decision = { action: 'failed', diagnosis: { state: 'reported' }, pull, comments: await github.listIssueComments(9) };
  const attention = () => github.repo.pulls[0].labels.some((label) => label.name === ATTENTION_LABEL);
  await reconcileImplementationAttention(github, decision);
  assert.equal(attention(), true, 'no aged dependents or new comment is required');
  await github.removeLabel(9, ATTENTION_LABEL);
  const count = github.comments.length;
  await reconcileImplementationAttention(github, decision);
  await reconcileImplementationAttention(github, decision);
  assert.equal(attention(), true);
  assert.equal(github.comments.length, count);
  for (const action of ['tasks', 'repair', 'verify', 'merge', 'wait', 'diagnosing']) {
    await github.addLabels(9, [ATTENTION_LABEL]);
    await reconcileImplementationAttention(github, { ...decision, action });
    assert.equal(attention(), false, action);
  }
  await reconcileImplementationAttention(github, { ...decision, action: 'done' });
  assert.equal(attention(), true, 'review holds need a person');
  await reconcileImplementationAttention(github, { ...decision, action: 'tasks' }, { blocked: true });
  assert.equal(attention(), true, 'dependency-request labels belong to the blocked lifecycle');
  const child = diagnosisRepo().repo.pulls.find((item) => item.number === 11);
  github.repo.pulls.push(child);
  await reconcileImplementationAttention(github, decision);
  assert.equal(attention(), false, 'the review action belongs on the amendment');
});

test('accepted amendment, authoring, and Copilot work suppress an obsolete implementation request', async () => {
  for (const kind of ['amend', 'author', 'copilot']) {
    const github = diagnosisRepo({ amendment: false });
    const pull = await github.getPullRequest(9);
    await github.addLabels(9, [ATTENTION_LABEL]);
    await github.createComment(9, '<!-- speckit-implement:diagnosis -->\n**Recommended next step**: Decide.');
    if (kind === 'amend') await github.createComment(9, '<!-- speckit-amend:request {"comment":501,"head":"sha-head","guidance":"Make the probes manual."} -->');
    if (kind === 'copilot') await github.createComment(9, `${COPILOT_HANDOFF_MARKER}\nCopilot is working on the accepted request.`);
    if (kind === 'author') {
      await github.createComment(9, renderFixEvent('requested', { by: 'dev', stamp: '20261001-000000' }));
      await github.createCheckRun({ name: FIX_CHECK_NAME, head_sha: pull.head.sha, status: 'in_progress' });
    }
    await reconcileImplementationAttention(github, {
      action: 'failed', diagnosis: { state: 'reported' }, pull, comments: await github.listIssueComments(9),
    }, { now: github.clock });
    assert.equal(github.repo.pulls[0].labels.some((label) => label.name === ATTENTION_LABEL), false, kind);
  }
});

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

test('scheduler restores a reported diagnosis label without a new comment or any dependent implementation', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile(pull.head.sha, 'specs/a/tasks.md', TASKS_OPEN);
    await github.createCheckRun({
      name: CHECK_RUN_NAME, head_sha: pull.head.sha, status: 'completed', conclusion: 'neutral', external_id: CHECK_DIAGNOSED,
    });
    await github.createComment(pull.number, '<!-- speckit-implement:diagnosis -->\n**Recommended next step**: Authorize the artifact decision.');
    const comments = github.comments.length;
    await orchestrate(github, root);
    assert.ok(pull.labels.some((label) => label.name === ATTENTION_LABEL));
    await github.removeLabel(pull.number, ATTENTION_LABEL);
    await orchestrate(github, root);
    await orchestrate(github, root);
    assert.ok(pull.labels.some((label) => label.name === ATTENTION_LABEL));
    assert.equal(github.comments.length, comments);
    assert.equal(chainRuns(github).length, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

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
    assert.deepEqual(restart.ready, [{ number: twin.number, folder: 'a', requester: 'dev', reset: true, prerequisite: false, unblocks: 0 }]);
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
    assert.deepEqual(result.ready, [{ number: a.number, folder: 'a', requester: 'dev', reset: false, prerequisite: false, unblocks: 0 }]);
    assert.deepEqual(result.blocked.map((twin) => [twin.folder, twin.blockers.map((blocker) => blocker.number)]), [['b', [github.issues[3].number]]]);
    assert.deepEqual(result.inconsistent.map((twin) => twin.folder), ['c', 'e']);
    assert.match(result.inconsistent[1].reasons[0], /@outsider needs at least write access/);
    const output = readFileSync(path.join(root, 'output.txt'), 'utf8');
    assert.match(output, /count=1/);
    assert.match(output, new RegExp(`matrix=\\{"include":\\[\\{"number":${a.number},"folder":"a","requester":"dev","reset":false,"prerequisite":false,"unblocks":0\\}\\]\\}`));

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

// Runs of the chain, in the workflow of each kind of work and Spec Kit implement decide.
  const chainRuns = (github) => github.repo.runs.filter((run) => /^speckit-implement-(?:decide|group|verify|repair|merge)\.yml$/.test(run.workflow));
const limitCheck = (github, head) => github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'completed', conclusion: 'failure', external_id: CHECK_LIMIT });

test('orchestrate starts the implementation chain and continues it only when no run is active and there is work', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    const result = await orchestrate(github, root);
    const pull = github.repo.pulls[0];
    assert.equal(result.exitCode, 0);
    assert.deepEqual(chainRuns(github).map((run) => [run.display_title, run.ref, run.inputs]), [[`Spec Kit implement spec #${twin.number} · a title`, 'main', { issue: String(twin.number), pull: String(pull.number), title: 'a title' }]]);
    assert.match(result.text, new RegExp(`Started the implementation of #${twin.number} \\(pull request #${pull.number}\\)`));

    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    const active = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 1, 'an active chain is left alone');
    assert.match(active.text, /its implementation run is active/);

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    const broken = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 2, 'a chain that stopped with work left is continued');
    assert.match(broken.text, new RegExp(`Continued #${twin.number} \\(pull request #${pull.number}\\): next is T001 attempt 1`));
    assert.equal(chainRuns(github).at(-1).display_title, `Spec Kit implement spec #${twin.number} · a title · Phase 1: Setup · T001 [P] Create project`, 'the run is named after the spec and what it will work on');

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
    github.setFile(pull.head.sha, 'specs/a/tasks.md', TASKS_OPEN);
    await orchestrate(github, root);
    assert.ok(pull.labels.some((label) => label.name === BLOCKED_LABEL));
    Object.assign(github.find(77), { state: 'closed', state_reason: 'not_planned' });
    const stopped = await orchestrate(github, root);
    assert.equal(stopped.selection.inProgress[0].decision.action, 'failed');
    assert.equal(chainRuns(github).length, 1, 'no retry');
    assert.deepEqual(pull.labels.map((label) => label.name), [ATTENTION_LABEL]);
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:unblocked -->\n\*\*A prerequisite was not implemented\*\*: #77 was closed without being implemented\. The block is lifted[\s\S]*\*\*Recommended next step\*\*/);
    const count = github.comments.length;
    await github.removeLabel(pull.number, ATTENTION_LABEL);
    await orchestrate(github, root);
    assert.deepEqual(pull.labels.map((label) => label.name), [ATTENTION_LABEL]);
    assert.equal(github.comments.length, count);
    assert.equal(chainRuns(github).length, 1, 'a newer accepted action is still required');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a blocked implementation holds its slot, so a new spec waits instead of starting at SPECKIT_MAX_ACTIVE_SPECS', async () => {
  const tasks = '## Phase 1\n\n- [ ] T001 one\n';
  const root = makeRepo([{ folder: 'a', plan: true, tasks }, { folder: 'b', plan: true, tasks }, { folder: 'c', plan: true, tasks }]);
  try {
    const github = new FakeGitHub();
    await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
    const [a, b, c] = github.issues;
    for (const issue of github.issues) issue.labels = issue.labels.filter((label) => label.name !== 'speckit:deps-pending');
    github.permissions.dev = 'write';
    flag(github, a);
    await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.equal(chainRuns(github).length, 1);
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    // a now waits for c, which nobody requested: it is blocked and has no running chain.
    github.edges.push([a.number, c.number]);
    flag(github, b);
    const waiting = await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.ok(github.repo.pulls.find((pull) => pull.head.ref === 'speckit/a').labels.some((label) => label.name === BLOCKED_LABEL));
    assert.match(waiting.text, new RegExp(`Waiting: #${b.number} \`b\` starts when fewer than 1 implementations are active`));
    assert.equal(github.repo.pulls.some((pull) => pull.head.ref === 'speckit/b'), false, 'the new spec did not start');
    // Without the dependency the block is lifted, a runs again, and b still waits for the slot.
    github.edges.splice(github.edges.findIndex(([from, to]) => from === a.number && to === c.number), 1);
    await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.equal(github.repo.pulls.some((pull) => pull.head.ref === 'speckit/b'), false);
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

    const implementationA = github.repo.pulls.find((pull) => pull.head.ref === 'speckit/a');
    const implementationB = github.repo.pulls.find((pull) => pull.head.ref === 'speckit/b');
    const blockedComments = () => github.comments.filter((comment) => comment.number === implementationA.number && comment.body.includes('speckit-implement:blocked'));
    assert.doesNotMatch(blockedComments().at(-1).body, /to finish first/, 'no implementation pull request of the blocker existed yet');
    assert.match(blockedComments().at(-1).body, new RegExp(`\\*\\*Requested, starting by itself\\*\\*:\\n\\n- #${b.number}: requested for implementation;`), 'a requested blocker without a pull request is starting, not waiting for a request');
    assert.doesNotMatch(blockedComments().at(-1).body, /Recommended next step/, 'nothing is left to request');
    await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    const explained = blockedComments().at(-1).body;
    assert.match(explained, new RegExp(`^<!-- speckit-implement:blocked \\{"blockers":\\[${b.number}\\],"pulls":\\[${implementationB.number}\\]\\} -->`));
    assert.match(explained, new RegExp(`\\*\\*Implementation pull request to finish first\\*\\*:\\n\\n- #${implementationB.number} implements #${b.number}; this implementation resumes once it is merged\\.`));
    const count = blockedComments().length;
    await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.equal(blockedComments().length, count, 'the blocking pull request is explained once');
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

// The finished phase was verified (Spec Kit implement verify's report), so the chain moves on to the next phase or the convergence.
const phaseVerified = (github, pull, phase = 'Phase 1: Setup') => github.createComment(pull, `${renderAttemptMarker({ kind: 'verify', attempt: 1, outcome: 'success', phase })}\n**The phase verification passed** for ${phase}.`);

test('chain: decide picks the next group, sequential or parallel, with guidance, verify modes, job names, and the first tasks\' check runs', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', '## Phase 1: Setup\n\n- [ ] T001 [P] Create project\n- [ ] T002 [P] Add docs\n\n### Implementation\n\n- [ ] T003 [US1] Build it\n- [ ] T004 [US1] Wire it\n\n## Phase 2\n\n- [ ] T005 Later\n');
    const progress = github.repo.checkRuns.at(-1);
    const parallel = await decide(github, root, twin);
    assert.deepEqual(
      [parallel.outputs.action, parallel.outputs.continue, parallel.outputs.kind, parallel.outputs.mode, parallel.outputs.phase, parallel.outputs.job_name, parallel.outputs.base_branch],
      ['group', 'true', 'implement', 'parallel', 'Phase 1: Setup', 'Phase 1: Setup · T001, T002 in parallel', 'main'],
    );
    assert.match(parallel.text, new RegExp(`^## Spec Kit implement spec #${twin.number} · a title$`, 'm'));
    assert.match(parallel.text, /^Run: Phase 1: Setup · T001, T002 in parallel\.$/m);
    assert.match(parallel.text, /- T001 attempt 1, T002 attempt 1 in parallel$/m);
    assert.deepEqual(JSON.parse(parallel.outputs.entries).map((entry) => [entry.task, entry.attempt, entry.index, entry.name, entry.defer_progress, entry.guidance]),
      [['T001', 1, 0, 'T001', true, ''], ['T002', 1, 0, 'T002', true, '']], 'a parallel group: one matrix job per task of its single entry');
    const known = ['index', 'name', 'task', 'attempt', 'guidance', 'summary', 'defer_progress', 'repair', 'required_sync', 'sync_repair'];
    for (const entry of JSON.parse(parallel.outputs.entries)) assert.deepEqual(Object.keys(entry).filter((key) => !known.includes(key)), [], 'work items only carry the fields Spec Kit implement task documents');
    assert.deepEqual(JSON.parse(parallel.outputs.order).map((entry) => entry.include.map((item) => item.task)), [['T001', 'T002']]);
    assert.ok(!('prompt' in JSON.parse(parallel.outputs.entries)[0]), 'the agents\' prompts live in the Spec Kit workflows');
    assert.equal(progress.external_id, 'speckit:attempt:task:T001:1', 'the first attempt takes over the queued progress check run');
    assert.deepEqual(github.repo.checkRuns.filter((check) => String(check.external_id).startsWith('speckit:attempt:')).map((check) => [check.external_id, check.status]),
      [['speckit:attempt:task:T001:1', 'in_progress'], ['speckit:attempt:task:T002:1', 'in_progress']], 'every task of a parallel group starts at once');
    assert.equal(parallel.outputs.head, github.repo.branches['speckit/a']);

    // The run ended without reporting its attempts: they count as failed, and the next run retries them.
    const retried = await decide(github, root, twin);
    assert.match(retried.text, /Recorded 2 attempt\(s\) of an earlier run that ended without reporting as failed/);
    assert.match(github.comments.find((comment) => comment.body.includes('"task":"T001","attempt":1,"outcome":"failure"')).body, /T001 attempt 1 did not finish/);
    assert.deepEqual(JSON.parse(retried.outputs.entries).map((entry) => entry.attempt), [2, 2]);

    github.setFile('speckit/a', 'specs/a/tasks.md', '## Phase 1: Setup\n\n- [x] T001 [P] Create project\n- [x] T002 [P] Add docs\n\n### Implementation\n\n- [ ] T003 [US1] Build it\n- [ ] T004 [US1] Wire it\n\n## Phase 2\n\n- [ ] T005 Later\n');
    for (const check of github.repo.checkRuns) check.status = 'completed';
    const sequential = await decide(github, root, twin);
    assert.deepEqual([sequential.outputs.mode, sequential.outputs.job_name], ['sequential', 'Phase 1: Setup · T003, T004 one after another']);
    assert.deepEqual(JSON.parse(sequential.outputs.entries).map((entry) => [entry.task, entry.index, entry.name, entry.summary]),
      [['T003', 0, 'T003', '[US1] Build it'], ['T004', 1, 'T004', '[US1] Wire it']], 'a sequential group: one entry of the order per task, one at a time');
    assert.match(sequential.text, /- T003 attempt 1\n- T004 attempt 1\n- Then Phase 1: Setup is verified in the next run\./);
    assert.deepEqual(github.repo.checkRuns.filter((check) => check.status === 'in_progress').map((check) => check.external_id), ['speckit:attempt:task:T003:1'], 'only the first task of a sequential group starts; landing it starts the next');
    assert.equal(pull.number, github.repo.pulls[0].number);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: a finished phase is verified in a run of its own, recorded by verify-report, and a failure stops with a repair', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x') + '\n## Phase 2\n\n- [ ] T003 Later\n');
    await attemptComment(github, pull.number, 'T002', 'success');
    const verify = await decide(github, root, twin);
    assert.deepEqual([verify.outputs.action, verify.outputs.continue, verify.outputs.phase, verify.outputs.job_name, verify.outputs.attempt, verify.outputs.tasks],
      ['verify', 'true', 'Phase 1: Setup', 'Verify Phase 1: Setup', '1', '["T001","T002"]']);
    const check = github.repo.checkRuns.find((item) => String(item.id) === verify.outputs.check_run);
    assert.deepEqual([check.external_id, check.status], ['speckit:attempt:verify::1', 'in_progress']);
    const env = { ...envFor(root), SPECKIT_TWIN: String(twin.number), SPECKIT_PULL: String(pull.number), SPECKIT_FOLDER: 'a', SPECKIT_PHASE: 'Phase 1: Setup', SPECKIT_TASKS: verify.outputs.tasks, SPECKIT_ATTEMPT: '1', SPECKIT_CHECK_RUN: verify.outputs.check_run, GITHUB_RUN_ID: '42' };
    await runVerifyReport({ client: github, env: { ...env, SPECKIT_VERIFY_RESULT: 'success', SPECKIT_VERIFY_FLAKY: 'Ns.A.Works' }, log: silent });
    assert.deepEqual([check.status, check.conclusion], ['completed', 'success']);
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:attempt \{"kind":"verify","task":null,"attempt":1,"outcome":"success","phase":"Phase 1: Setup"\} -->\n\*\*The phase verification passed\*\* for Phase 1: Setup on `[^`]+`\.\n\n- Flaky \(passed on rerun\): `Ns\.A\.Works`/);
    const next = await decide(github, root, twin);
    assert.deepEqual([next.outputs.action, next.outputs.phase], ['group', 'Phase 2'], 'a verified phase is not verified again');

    // A failed verification stops with a pending repair of the phase.
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x') + '\n## Phase 2\n\n- [x] T003 Later\n');
    for (const item of github.repo.checkRuns) item.status = 'completed';
    await attemptComment(github, pull.number, 'T003', 'success');
    const again = await decide(github, root, twin);
    assert.deepEqual([again.outputs.action, again.outputs.phase, again.outputs.tasks], ['verify', 'Phase 2', '["T003"]']);
    await runVerifyReport({ client: github, env: { ...env, SPECKIT_PHASE: 'Phase 2', SPECKIT_TASKS: again.outputs.tasks, SPECKIT_CHECK_RUN: again.outputs.check_run, SPECKIT_VERIFY_RESULT: 'failure' }, log: silent });
    const stop = github.repo.checkRuns.find((item) => String(item.id) === again.outputs.check_run);
    assert.deepEqual([stop.external_id, stop.conclusion, stop.output.title], [CHECK_LIMIT, 'failure', 'The phase verification failed']);
    assert.match(github.comments.at(-1).body, /"kind":"verify"[^\n]*"outcome":"failure","phase":"Phase 2"[\s\S]*Implementation needs attention\*\* @dev: the full verification of Phase 2 failed \(see the \[run\]\([^)]*actions\/runs\/42\)\)/);
    assert.deepEqual([unresolvedRepair(github.comments).scope, unresolvedRepair(github.comments).phase, unresolvedRepair(github.comments).tasks], ['task', 'Phase 2', ['T003']]);
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
    assert.equal(retried.outputs.action, 'group');
    const [first] = JSON.parse(retried.outputs.order);
    assert.deepEqual([first.include[0].task, first.include[0].attempt], ['T001', 1], 'a retry starts a new attempt window');
    assert.match(first.include[0].guidance, /Keep the helper\.$/);
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
    const repairEntry = JSON.parse(authorized.outputs.entry);
    assert.match(repairEntry.guidance, /^Fix the combined registration/);
    assert.deepEqual([repairEntry.index, repairEntry.task, repairEntry.attempt, repairEntry.repair.id], [0, '', 1, repair.id], 'the repair\'s work item');
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
    assert.equal(retried.outputs.action, 'group');
    assert.equal(pull.draft, true, 'retried implementation work restores draft status');
    assert.equal(attention(), false, 'a retried implementation is processing');
    const comment = github.comments.find((item) => item.body.startsWith(RETRY_COMMENT_MARKER));
    assert.equal(parseGuidance(comment.body), 'Use the existing helper.');
    const [first] = JSON.parse(retried.outputs.order);
    assert.deepEqual([first.include[0].task, first.include[0].attempt], ['T001', 1], 'a retry starts a new attempt window');
    assert.equal(first.include[0].guidance, 'Use the existing helper.', 'guidance applies to the group of the stopped task');

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
    assert.equal((await decide(github, root, twin)).outputs.kind, 'resolve');
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
      assert.equal(chosen.outputs.action, 'group');
      const guidance = JSON.parse(chosen.outputs.order)[0].include[0].guidance;
      assert.equal(guidance.includes('Preserve both registrations'), !consumed);
      for (const check of github.repo.checkRuns) check.status = 'completed';
      await attemptComment(github, pull.number, 'T001', 'success');
      github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', ' '));
      const next = await decide(github, root, twin);
      assert.doesNotMatch(JSON.parse(next.outputs.order)[0].include[0].guidance, /Preserve both registrations/, 'guidance ends after the first continued task succeeds');
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
      assert.equal(JSON.parse(next.outputs.entry).guidance.includes('Keep the combined registrations'), !consumed);
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
      await github.dispatchWorkflow('speckit-diagnose.lock.yml', 'main', { issue: String(twin.number), mode: 'diagnose' });
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
        assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.kind, 'resolve');
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
      assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.kind, 'resolve', 'retry preserves and rearms required sync');
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
    assert.equal((await decide(github, root, twin)).outputs.action, 'group');

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
    assert.deepEqual([resolve.outputs.action, resolve.outputs.kind, resolve.outputs.mode, resolve.outputs.job_name], ['group', 'resolve', 'sequential', 'Conflict resolution'], 'decide resolves in the same run');

    settle();
    await attemptComment(github, pull.number, null, 'failure', 'resolve');
    assert.equal((await decide(github, root, twin)).outputs.kind, 'resolve', 'one failed attempt is retried');
    settle();
    await attemptComment(github, pull.number, null, 'failure', 'resolve');
    assert.equal((await sync()).outputs.result, 'none', 'a given-up head is not merged again');
    assert.equal((await decide(github, root, twin)).outputs.kind, 'implement', 'the phase continues on the current base');
    assert.match(lastComment(), new RegExp(`^<!-- speckit-implement:sync-skipped ${conflicted} -->\\n\\*\\*\`main\` was not merged\\*\\*: resolving its conflicts`));
    // The group decided above started its first attempt; the run reported it before the next decision.
    settle();
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
    await phaseVerified(github, pull.number);
    settle();
    const converging = await sync();
    assert.deepEqual([converging.outputs.result, /before the convergence/.test(converging.text)], ['merged', true]);
    assert.equal((await decide(github, root, twin)).outputs.kind, 'converge');

    settle();
    await attemptComment(github, pull.number, null, 'success', 'converge');
    advance('main');
    github.mergeBranchOutcome = 'conflict';
    assert.equal((await sync()).outputs.result, 'conflict');
    assert.equal(github.repo.checkRuns.at(-1).external_id, CHECK_CONFLICT, 'before the merge the resolution is required');
    assert.match(lastComment(), /Copilot resolves the conflicts now, before the merge\.$/);
    const required = await decide(github, root, twin);
    assert.deepEqual([required.outputs.action, required.outputs.kind, required.outputs.job_name], ['group', 'resolve', 'Conflict resolution']);
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
    await phaseVerified(github, pull.number);
    const converge = await decide(github, root, twin);
    assert.deepEqual([converge.outputs.action, converge.outputs.kind, converge.outputs.mode, converge.outputs.job_name], ['group', 'converge', 'sequential', 'Convergence']);
    assert.match(converge.text, /^Run: Convergence\.$/m);
    const [entry] = JSON.parse(converge.outputs.order);
    assert.deepEqual([entry.include[0].task, entry.include[0].attempt, entry.include[0].guidance], ['', 1, '']);
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
    const resolveEntry = JSON.parse(resolve.outputs.entries)[0];
    assert.deepEqual([resolve.outputs.action, resolve.outputs.kind, resolve.outputs.job_name, resolveEntry.task], ['group', 'resolve', 'Conflict resolution', '']);
    assert.equal(resolveEntry.guidance, 'Keep both migration sequences and use the next free number.');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: a run of another kind hands the work over to its workflow before it starts any attempt', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', '## Phase 1: Setup\n\n- [ ] T001 Create project\n');
    const attempts = () => github.repo.checkRuns.filter((check) => String(check.external_id).startsWith('speckit:attempt:')).length;
    const comments = github.comments.length;
    // Started as a phase verification, but the next work is a group (a person pushed after the run was started).
    const handover = await decide(github, root, twin, { SPECKIT_WORKFLOW: 'speckit-implement-verify.yml' });
    assert.deepEqual([handover.outputs.action, handover.outputs.continue], ['handover', 'true']);
    assert.match(handover.text, /^Handover: the next work, Phase 1: Setup · T001 Create project, runs in `speckit-implement-group\.yml`\.$/m);
    assert.equal(attempts(), 0, 'no attempt is started, so none is charged to the handover');
    assert.equal(github.comments.length, comments, 'and nothing is reported on the pull request');
    await runContinue({ client: github, env: { ...envFor(root), SPECKIT_TWIN: String(twin.number), SPECKIT_PULL: String(pull.number) }, log: silent });
    assert.equal(github.repo.runs.at(-1).workflow, 'speckit-implement-group.yml', 'the next run is the group workflow');
    // The workflow of the work's kind decides as usual.
    const group = await decide(github, root, twin, { SPECKIT_WORKFLOW: 'speckit-implement-group.yml' });
    assert.deepEqual([group.outputs.action, group.outputs.kind], ['group', 'implement']);
    assert.equal(attempts(), 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: continue starts the next run, named after the spec and what it will work on', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    const env = { ...envFor(root), SPECKIT_TWIN: String(twin.number), SPECKIT_PULL: String(pull.number) };
    const comments = github.comments.length;
    await runContinue({ client: github, env, log: silent });
    const run = github.repo.runs.at(-1);
    assert.deepEqual([run.workflow, run.inputs], ['speckit-implement-group.yml', { issue: String(twin.number), pull: String(pull.number), title: 'a title', subject: 'Phase 1: Setup · T001 [P] Create project' }]);
    assert.equal(run.display_title, `Spec Kit implement spec #${twin.number} · a title · Phase 1: Setup · T001 [P] Create project`);
    assert.equal(github.comments.length, comments, 'naming the next run changes nothing on the pull request');
    assert.equal(github.repo.checkRuns.some((check) => String(check.external_id).startsWith('speckit:attempt:')), false, 'and starts no attempt');
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
  const specify = github.repo.runs.filter((run) => run.workflow === 'speckit-author.yml');
  assert.equal(specify.length, 1);
  assert.deepEqual([specify[0].inputs.issue, specify[0].inputs.pull, specify[0].inputs.mode], ['6', '21', 'author']);
  assert.match(specify[0].inputs.guidance, /The test `Ns\.Store\.Saves` failed and then passed on its one rerun in the verifications of 3 implementation pull requests \(#20, #21, #22\)[\s\S]*Do not weaken, skip, or delete the test/);
  const onPull = github.comments.filter((comment) => comment.number === 21);
  assert.match(onPull.at(-2).body, /^<!-- speckit-fix:requested \{"by":"dev","stamp":"\d{8}-\d{6}","link":false\} -->/);
  assert.match(onPull.at(-1).body, /^<!-- speckit-implement:flaky-spec \{"test":"Ns\.Store\.Saves"\} -->\n\*\*Flaky test\*\* `Ns\.Store\.Saves` passed only on its rerun in 3 implementations/);
  assert.ok(!(github.repo.pulls.find((item) => item.number === 21).labels ?? []).some((label) => [BLOCKED_LABEL, ATTENTION_LABEL].includes(label.name)));

  github.repo.checkRuns.at(-1).status = 'completed';
  const again = await maintainFlakyTests(github, env, { now: github.clock });
  assert.deepEqual(again.started, [], 'a test is started once');
  assert.equal(github.repo.runs.filter((run) => run.workflow === 'speckit-author.yml').length, 1);
  assert.equal(parseFlakyThreshold('2'), 2);
  assert.equal(parseFlakyThreshold(''), 3);
  assert.equal(parseFlakyThreshold('x'), 3);
  const lower = await maintainFlakyTests(github, { ...env, SPECKIT_FLAKY_THRESHOLD: '1' }, { now: github.clock });
  assert.match(lower.lines.join('\n'), /`Ns\.Other\.Test` was flaky in #20; started a spec/);
});

test('waitingBehind counts the implementations each twin unblocks, also through chains of blocked implementations', async () => {
  const { waitingBehind } = await import('./speckit-orchestrate.mjs');
  // Today's graph: #342 blocks #326, which blocks five implementations, one of which (#246) blocks #231.
  const inProgress = [
    { number: 326, blockers: [342] },
    { number: 246, blockers: [326] }, { number: 297, blockers: [326] }, { number: 279, blockers: [326] },
    { number: 280, blockers: [326] }, { number: 254, blockers: [326] },
    { number: 231, blockers: [246] },
    { number: 303, blockers: [330] }, { number: 173, blockers: [303] },
    { number: 253, blockers: [335, 337] },
  ];
  const counts = waitingBehind(inProgress);
  assert.equal(counts.get(342), 7, 'the environment guard unblocks #326, its five dependents, and #231 behind #246');
  assert.equal(counts.get(330), 2);
  assert.equal(counts.get(337), 1);
  assert.equal(counts.get(999), undefined);
  assert.equal(waitingBehind([{ number: 1, blockers: [2] }, { number: 2, blockers: [1] }]).get(1), 1, 'a cycle ends');
});

test('the roadblock label marks the unblocked implementations most others wait for, and follows the queue', async () => {
  const { maintainRoadblocks, parseRoadblockPulls } = await import('./speckit-orchestrate.mjs');
  // Today's graph: twin -> pull request, blocked or not, and its open blockers.
  const twin = (number, pull, blockers = []) => ({ number, pull, blocked: blockers.length > 0, blockers });
  const inProgress = [
    twin(342, 344), twin(326, 327, [342]),
    twin(246, 248, [326]), twin(297, 298, [326]), twin(279, 309, [326]), twin(280, 310, [326]), twin(254, 315, [326]),
    twin(231, 239, [246]), twin(241, 273, [279]), twin(242, 275, [280]),
    twin(330, 331), twin(303, 304, [330]), twin(256, 318, [330]), twin(308, 319, [330]), twin(173, 294, [303]),
    twin(335, 345), twin(253, 314, [335, 337]),
    twin(255, 316),
  ];
  const labels = new Map(inProgress.map((item) => [item.pull, []]));
  labels.set(316, ['roadblock']);
  const comments = [];
  const client = {
    listOpenPullRequests: async () => [...labels.keys()].map((number) => ({ number, head: { ref: `speckit/f${number}` }, labels: labels.get(number).map((name) => ({ name })) })),
    ensureLabel: async () => {},
    addLabels: async (number, names) => labels.set(number, [...labels.get(number), ...names]),
    removeLabel: async (number, name) => labels.set(number, labels.get(number).filter((item) => item !== name)),
    createComment: async (number, body) => comments.push({ number, body }),
  };
  const lines = await maintainRoadblocks(client, {}, inProgress);
  const labelled = () => [...labels].filter(([, names]) => names.includes('roadblock')).map(([number]) => number).sort((a, b) => a - b);
  assert.deepEqual(labelled(), [331, 344, 345], 'the three unblocked roots, not #327, which unblocks many but is blocked itself');
  const first = comments.find((comment) => comment.number === 344).body;
  assert.match(first, /^<!-- speckit-implement:roadblock -->\n\*\*Roadblock\*\*: 9 implementations wait for this one, directly or through a chain: #239 \(spec #231\), #248 \(spec #246\)/);
  assert.match(comments.find((comment) => comment.number === 345).body, /1 implementation waits for this one[^\n]*#314 \(spec #253\)/);
  assert.match(lines.join('\n'), /Removed `roadblock` from #316/);

  // Nothing changed: no new comment. #344 merged: its waiting implementations are unblocked, and the label moves.
  const count = comments.length;
  await maintainRoadblocks(client, {}, inProgress);
  assert.equal(comments.length, count, 'a label that stays is not explained again');
  labels.delete(344);
  const next = inProgress.filter((item) => item.number !== 342).map((item) => (item.number === 326 ? twin(326, 327) : item));
  await maintainRoadblocks(client, {}, next);
  assert.deepEqual(labelled(), [327, 331, 345], '#327 is unblocked now and leads the queue');
  await maintainRoadblocks(client, { SPECKIT_ROADBLOCK_PULLS: '1' }, next);
  assert.deepEqual(labelled(), [327]);
  await maintainRoadblocks(client, { SPECKIT_ROADBLOCK_PULLS: '0' }, next);
  assert.deepEqual(labelled(), [], '0 turns the label off');
  assert.deepEqual([parseRoadblockPulls(''), parseRoadblockPulls('5'), parseRoadblockPulls('-1'), parseRoadblockPulls('x')], [3, 5, 3, 3]);
});

import { blockedTarget, maintainActionRequired, parseMaxBlocked, parseMaxBlockedDays } from './speckit-orchestrate.mjs';

const DAY = 24 * 60 * 60 * 1000;

test('the blocked target is missed at SPECKIT_MAX_BLOCKED blocked implementations or one open longer than SPECKIT_MAX_BLOCKED_DAYS', () => {
  assert.deepEqual([parseMaxBlocked(''), parseMaxBlocked('3'), parseMaxBlocked('0'), parseMaxBlocked('-1'), parseMaxBlocked('2.5')], [5, 3, 0, 5, 5]);
  assert.deepEqual([parseMaxBlockedDays(undefined), parseMaxBlockedDays('1.5'), parseMaxBlockedDays('0'), parseMaxBlockedDays('x')], [2, 1.5, 0, 2]);
  const now = Date.parse('2026-10-28T12:00:00Z');
  const twin = (number, days, blocked = true) => ({ number, pull: number + 100, blocked, openedAt: new Date(now - days * DAY).toISOString() });
  const fresh = [twin(1, 1), twin(2, 1), twin(3, 1), twin(4, 1), twin(5, 3, false)];
  assert.equal(blockedTarget(fresh, {}, now).missed, false, 'four fresh blocks, and an old one that is not blocked');
  const many = blockedTarget([...fresh, twin(6, 0.5)], {}, now);
  assert.equal(many.missed, true);
  assert.equal(many.reason, '5 implementations are blocked (target: fewer than 5)');
  const old = blockedTarget([twin(1, 1), twin(7, 2.5), twin(8, 4)], {}, now);
  assert.equal(old.reason, '2 blocked implementations are open longer than 2 days (the oldest, #108, for 4 days)');
  assert.deepEqual(old.aged.map((item) => item.number), [8, 7]);
  assert.equal(blockedTarget([twin(8, 4)], { SPECKIT_MAX_BLOCKED_DAYS: '0' }, now).missed, false, '0 turns the age off');
  assert.equal(blockedTarget([...fresh, twin(6, 0.5)], { SPECKIT_MAX_BLOCKED: '0' }, now).missed, false, '0 turns the count off');
});

test('while the blocked target is missed only prerequisites start, and no specs are authored', async () => {
  const tasks = '## Phase 1\n\n- [ ] T001 one\n';
  const root = makeRepo([{ folder: 'a', plan: true, tasks }, { folder: 'b', plan: true, tasks }, { folder: 'c', plan: true, tasks }]);
  try {
    const github = new FakeGitHub();
    await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
    const [a, b, c] = github.issues;
    for (const issue of github.issues) issue.labels = issue.labels.filter((label) => label.name !== 'speckit:deps-pending');
    github.permissions.dev = 'write';
    flag(github, a);
    await orchestrate(github, root);
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    // a waits for c; b is an unrelated spec.
    github.edges.push([a.number, c.number]);
    flag(github, b);
    const env = { SPECKIT_MAX_ACTIVE_SPECS: '5', SPECKIT_MAX_BLOCKED: '1', SPECKIT_BACKLOG_SPECS: '5' };
    const waiting = await orchestrate(github, root, env);
    assert.match(waiting.text, /Blocked target missed: 1 implementation is blocked \(target: fewer than 1\)\. Only prerequisites of blocked implementations start/);
    assert.match(waiting.text, new RegExp(`Waiting: #${b.number} \`b\` starts once the blocked target is met`));
    assert.equal(github.repo.pulls.some((pull) => pull.head.ref === 'speckit/b'), false, 'the unrelated spec did not start below SPECKIT_MAX_ACTIVE_SPECS');
    assert.match(waiting.text, /### Flaky tests\n\n- Paused while the blocked target is missed[\s\S]*### Spec backlog\n\n- Paused while the blocked target is missed/);
    assert.equal(github.repo.runs.some((run) => run.workflow === 'speckit-backlog.yml'), false);
    // The prerequisite c starts once requested; b still waits.
    flag(github, c);
    const started = await orchestrate(github, root, env);
    assert.match(started.text, new RegExp(`Started the implementation of #${c.number}`));
    assert.equal(github.repo.pulls.some((pull) => pull.head.ref === 'speckit/b'), false);
    // With the target met again, b starts.
    const met = await orchestrate(github, root, { ...env, SPECKIT_MAX_BLOCKED: '5' });
    assert.doesNotMatch(met.text, /Blocked target missed/);
    assert.ok(github.repo.pulls.some((pull) => pull.head.ref === 'speckit/b'));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a chain head awaiting human action gets one reminder per stop and its label is reconciled', async () => {
  const github = new FakeGitHub();
  const now = Date.parse('2026-10-28T12:00:00Z');
  for (const number of [10, 11, 12]) github.repo.pulls.push({ number, state: 'open', labels: [], head: { ref: `speckit/f${number}` }, base: { ref: 'main' } });
  const diagnosis = await github.createComment(10, '<!-- speckit-implement:diagnosis -->\n**Diagnosis**: decide');
  const stopped = (comments) => ({ action: 'failed', diagnosis: { state: 'reported' }, comments });
  const inProgress = (comments, overrides = {}) => [
    { number: 1, pull: 10, requester: 'dev', openedAt: new Date(now - 1 * DAY).toISOString(), blocked: false, decision: stopped(comments), blockers: [], ...overrides },
    { number: 2, pull: 11, openedAt: new Date(now - 5 * DAY).toISOString(), blocked: true, blockers: [1] },
    { number: 3, pull: 12, openedAt: new Date(now - 3 * DAY).toISOString(), blocked: true, blockers: [2] },
  ];
  assert.deepEqual(await maintainActionRequired(github, {}, inProgress(await github.listIssueComments(10), { decision: { action: 'failed', diagnosis: { state: 'none' } } }), { now }), [], 'not while a diagnosis is still to come');
  assert.deepEqual(await maintainActionRequired(github, {}, inProgress(await github.listIssueComments(10), { active: true }), { now }), []);
  assert.deepEqual(await maintainActionRequired(github, { SPECKIT_MAX_BLOCKED_DAYS: '6' }, inProgress(await github.listIssueComments(10)), { now }), [], 'nothing waits long enough');
  const lines = await maintainActionRequired(github, {}, inProgress(await github.listIssueComments(10)), { now });
  assert.deepEqual(lines, ['- Awaiting human action on #10 (spec #1): 2 implementations open longer than 2 days wait for its decision.']);
  const reminder = github.comments.at(-1);
  assert.equal(reminder.number, 10);
  assert.match(reminder.body, new RegExp(`^<!-- speckit-implement:escalation \\{"key":"${diagnosis.id}"\\} -->\\n\\*\\*Action required\\*\\* @dev: this implementation awaits human action, and 2 implementations open longer than 2 days wait for it, directly or through a chain: #11 \\(spec #2, open 5 days\\), #12 \\(spec #3, open 3 days\\)\\.`));
  assert.ok(reminder.body.includes(`**Recommended next step**: Follow the [latest diagnosis](${diagnosis.html_url})`));
  assert.equal(reminder.body.match(/Recommended next step/g).length, 1);
  assert.ok(github.repo.pulls[0].labels.some((label) => label.name === ATTENTION_LABEL));
  await github.removeLabel(10, ATTENTION_LABEL);
  await github.updateComment(reminder.id, reminder.body.replace('**Action required**', '**Escalation**'));
  const count = github.comments.length;
  assert.deepEqual(await maintainActionRequired(github, {}, inProgress(await github.listIssueComments(10)), { now }), [], 'once per stop, including historical markers');
  assert.ok(github.repo.pulls[0].labels.some((label) => label.name === ATTENTION_LABEL), 'restore the missing label even when the reminder already exists');
  assert.equal(github.comments.length, count);
  await github.createComment(10, '<!-- speckit-implement:diagnosis -->\n**Diagnosis (round 2)**: decide');
  assert.equal((await maintainActionRequired(github, {}, inProgress(await github.listIssueComments(10)), { now })).length, 1, 'a new stop gets a new reminder');
});