import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { TWIN_LABEL } from './speckit-prepare-core.mjs';
import { runSync } from './speckit-prepare.mjs';
import { UsageError, main, resolveFolder, runOrchestrate, runRequest, runSelect, runStart } from './speckit-orchestrate.mjs';
import { CHECK_CONFLICT, CHECK_LIMIT, CHECK_MERGE, CHECK_RUN_NAME, DONE_COMMENT_MARKER, RESUME_COMMENT_MARKER, START_COMMENT_MARKER, parseGuidance, renderResumeComment } from './speckit-implement-core.mjs';
import { FakeGitHub, SPEC_TEMPLATE, envFor, makeRepo, silent } from './speckit-test-helpers.mjs';

const IMPLEMENT = 'speckit:stage:implement';
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
    assert.deepEqual(github.repo.checkRuns.map((run) => [run.name, run.status, run.head_sha, run.output.title]), [[CHECK_RUN_NAME, 'queued', pull.head.sha, '0 of 2 tasks implemented']]);
    const comments = github.comments.filter((comment) => comment.number === pull.number);
    assert.equal(comments.length, 1);
    assert.match(comments[0].body, /2 task\(s\) queued/);

    await start(github, root, twin);
    assert.equal(github.repo.pulls.length, 1);
    assert.equal(github.repo.checkRuns.length, 1);
    assert.equal(github.comments.filter((comment) => comment.body.includes(START_COMMENT_MARKER)).length, 1);
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

    github.closePull(first.number);
    const fallback = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(fallback.fallback.map((item) => item.pull), [first.number]);
    const issue = github.find(twin.number);
    assert.deepEqual(issue.labels.map((label) => label.name).filter((name) => name.startsWith('speckit:stage:')), ['speckit:stage:tasked']);
    assert.match(github.comments.at(-1).body, new RegExp(`#${first.number} was closed without merging[\\s\\S]*stage \`tasked\``));

    flag(github, issue);
    const restart = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(restart.ready, [{ number: twin.number, folder: 'a', requester: 'dev', reset: true }]);
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

test('select finalizes a twin whose pull request a person merged while the twin is still open', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    github.closePull(github.repo.pulls[0].number, { merged: true });
    const result = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
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
    assert.match(comments[0].body, new RegExp(`#${pull.number} was merged, so the twin is now \`speckit:stage:implemented\` and the branch \`speckit/a\` was deleted`));
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
    assert.ok(github.find(twin.number).labels.some((label) => label.name === IMPLEMENT));
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
    if (args[0] === 'show') {
      const file = args[1].replace(/^origin\/main:/, '');
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
    assert.deepEqual(result.ready, [{ number: a.number, folder: 'a', requester: 'dev', reset: false }]);
    assert.deepEqual(result.blocked.map((twin) => [twin.folder, twin.blockers.map((blocker) => blocker.number)]), [['b', [github.issues[3].number]]]);
    assert.deepEqual(result.inconsistent.map((twin) => twin.folder), ['c', 'e']);
    assert.match(result.inconsistent[1].reasons[0], /@outsider needs at least write access/);
    const output = readFileSync(path.join(root, 'output.txt'), 'utf8');
    assert.match(output, /count=1/);
    assert.match(output, new RegExp(`matrix=\\{"include":\\[\\{"number":${a.number},"folder":"a","requester":"dev","reset":false\\}\\]\\}`));

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

const BRANCH_TASKS = (t1, t2) => `## Phase 1: Setup\n\n- [${t1}] T001 [P] Create project\n\n- [${t2}] T002 [US1] Build it\n`;

async function orchestrate(github, root, env = {}) {
  const lines = [];
  const result = await runOrchestrate({ client: github, rootDir: root, env: { ...envFor(root), ...env }, log: (line) => lines.push(line), sleep: async () => {}, now: () => github.clock });
  return { ...result, text: lines.join('\n') };
}

const taskRuns = (github) => github.repo.runs.map((run) => run.display_title.replace('Spec Kit implement ', ''));

test('orchestrate prepares the workspace and starts the first task', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    const result = await orchestrate(github, root);
    const pull = github.repo.pulls[0];
    assert.equal(result.exitCode, 0);
    assert.deepEqual(taskRuns(github), [`#${twin.number} T001 attempt 1`]);
    assert.deepEqual(github.repo.runs[0].inputs, { twin: String(twin.number), pull: String(pull.number), task: 'T001', attempt: '1' });
    assert.equal(github.repo.runs[0].ref, 'main');
    assert.match(result.text, /Started "Spec Kit implement #\d+ T001 attempt 1"/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate waits, retries, stops at the attempt limit, and resumes on request', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await orchestrate(github, root);
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    const pull = github.repo.pulls[0];

    await orchestrate(github, root);
    assert.equal(github.repo.runs.length, 1, 'an active task run blocks new dispatches');

    for (const attempt of [2, 3]) {
      github.completeRun(github.repo.runs.at(-1).id);
      await orchestrate(github, root);
      assert.equal(taskRuns(github).at(-1), `#${twin.number} T001 attempt ${attempt}`);
    }
    github.completeRun(github.repo.runs.at(-1).id);
    const limited = await orchestrate(github, root);
    assert.deepEqual(github.repo.runs.map((run) => run.workflow), ['speckit-implement.yml', 'speckit-implement.yml', 'speckit-implement.yml', 'speckit-diagnose.lock.yml']);
    const [limit, diagnosing] = github.repo.checkRuns.slice(-2);
    assert.deepEqual([limit.external_id, limit.status, limit.conclusion, limit.head_sha], [CHECK_LIMIT, 'completed', 'failure', github.repo.branches['speckit/a']]);
    assert.deepEqual([diagnosing.external_id, diagnosing.status], ['speckit:diagnosing', 'in_progress']);
    assert.deepEqual(github.repo.runs.at(-1).inputs, { twin: String(twin.number), pull: String(pull.number), folder: 'a', notes: '', previous: '', check_run: String(diagnosing.id) });
    const stop = github.comments.at(-1).body;
    assert.match(stop, /Implementation needs attention\*\* @dev: T001 did not succeed in 3 attempts/);
    assert.match(stop, /A diagnosis starts automatically[\s\S]*\/speckit resume \[guidance\]/);
    assert.doesNotMatch(stop, /\/speckit sync/, 'sync is only suggested when the default branch is ahead');
    assert.match(limited.text, /reached the attempt limit[\s\S]*Started a diagnosis of #\d+/);

    const waiting = await orchestrate(github, root);
    assert.equal(github.repo.runs.length, 4, 'a stopped implementation waits for the diagnosis');
    assert.match(waiting.text, /a diagnosis is running/);

    Object.assign(diagnosing, { status: 'completed', conclusion: 'neutral', external_id: 'speckit:diagnosed' });
    const decided = await orchestrate(github, root);
    assert.equal(github.repo.runs.length, 4, 'a reported diagnosis waits for a person');
    assert.match(decided.text, /waits for a person's decision/);

    await orchestrate(github, root, { SPECKIT_RESUME_TWIN: String(twin.number), SPECKIT_RESUME_GUIDANCE: 'Use the existing helper.\nKeep the test.' });
    assert.equal(taskRuns(github).at(-1), `#${twin.number} T001 attempt 1`);
    const resumed = github.comments.at(-1).body;
    assert.match(resumed, new RegExp(RESUME_COMMENT_MARKER.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    assert.equal(parseGuidance(resumed), 'Use the existing helper.\nKeep the test.');
    assert.match(resumed, /> Use the existing helper\.\n> Keep the test\./);

    await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: github.repo.branches['speckit/a'], status: 'in_progress', external_id: 'speckit:attempt' });
    github.completeRun(github.repo.runs.at(-1).id);
    await orchestrate(github, root);
    assert.equal(taskRuns(github).at(-1), `#${twin.number} T001 attempt 2`, 'the attempt count starts over after a resume');
    assert.equal(pull.number, github.repo.pulls[0].number);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate resumes after a resume comment from a command, even without a resume run', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await orchestrate(github, root, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    for (let attempt = 1; attempt <= 3; attempt += 1) {
      github.completeRun(github.repo.runs.at(-1).id);
      await orchestrate(github, root, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    }
    assert.equal(github.repo.checkRuns.at(-1).external_id, CHECK_LIMIT);
    assert.doesNotMatch(github.comments.at(-1).body, /A diagnosis starts automatically/, 'auto-diagnosis is off');
    assert.match(github.comments.at(-1).body, /\/speckit diagnose \[notes\]/);
    const runs = github.repo.runs.length;

    await github.createComment(github.repo.pulls[0].number, renderResumeComment('Implementation resumed by @dev.', 'Try the helper'));
    const comments = github.comments.length;
    await orchestrate(github, root, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.equal(github.repo.runs.length, runs + 1);
    assert.equal(taskRuns(github).at(-1), `#${twin.number} T001 attempt 1`, 'the resume comment starts a new attempt window');
    assert.equal(github.comments.length, comments, 'a command already announced the resume');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate waits for the run that handed back before it decides', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await orchestrate(github, root);
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    const handingBack = github.repo.runs.at(-1);
    let polls = 0;
    const lines = [];
    await runOrchestrate({
      client: github,
      rootDir: root,
      env: { ...envFor(root), SPECKIT_AFTER_RUN: String(handingBack.id) },
      log: (line) => lines.push(line),
      sleep: async () => {
        polls += 1;
        if (polls === 2) github.completeRun(handingBack.id);
      },
      now: () => github.clock,
    });
    assert.equal(polls, 2);
    assert.equal(taskRuns(github).at(-1), `#${twin.number} T001 attempt 2`, 'the completed run counts as a failed attempt');

    polls = 0;
    const stuck = github.repo.runs.at(-1);
    const stuckLines = [];
    await runOrchestrate({
      client: github,
      rootDir: root,
      env: { ...envFor(root), SPECKIT_AFTER_RUN: String(stuck.id) },
      log: (line) => stuckLines.push(line),
      sleep: async () => { polls += 1; },
      now: () => github.clock,
    });
    assert.equal(polls, 60);
    assert.match(stuckLines.join('\n'), new RegExp(`Warning: run ${stuck.id} was still active after 5 minutes`));
    assert.equal(github.repo.runs.length, 2, 'a still active run blocks new dispatches');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

const PARALLEL = (t1, t2, t3, t4) => `## Phase 1\n\n- [${t1}] T001 [P] A\n- [${t2}] T002 [P] B\n- [${t3}] T003 [P] C\n- [${t4}] T004 D\n`;

test('orchestrate runs a [P] group in parallel and the next sequential task after the whole group', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: PARALLEL(' ', ' ', ' ', ' ') }]);
  github.permissions.dev = 'write';
  const twin = github.issues[0];
  flag(github, twin);
  try {
    const started = await orchestrate(github, root);
    assert.deepEqual(taskRuns(github), ['T001', 'T002', 'T003'].map((task) => `#${twin.number} ${task} attempt 1`));
    assert.equal(started.exitCode, 0);
    const [t1, t2, t3] = github.repo.runs;

    github.completeRun(t1.id, 'success');
    github.setFile('speckit/a', 'specs/a/tasks.md', PARALLEL('x', ' ', ' ', ' '));
    const waiting = await orchestrate(github, root);
    assert.equal(github.repo.runs.length, 3, 'the group is still running and has no free task');
    assert.match(waiting.text, /a step is running/);
    assert.match(github.repo.pulls[0].body, /- \[x\] T001 \[P\] A\n- \[ \] T002/, 'the pull request body follows the branch');

    github.completeRun(t2.id);
    const retried = await orchestrate(github, root);
    assert.equal(taskRuns(github).at(-1), `#${twin.number} T002 attempt 2`, 'a failed task is retried while a sibling runs');
    assert.match(retried.text, /Started "Spec Kit implement #\d+ T002 attempt 2"/);

    github.completeRun(t3.id, 'success');
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    github.setFile('speckit/a', 'specs/a/tasks.md', PARALLEL('x', 'x', 'x', ' '));
    await orchestrate(github, root);
    assert.equal(taskRuns(github).at(-1), `#${twin.number} T004 attempt 1`);
    assert.match(github.repo.pulls[0].body, /- \[x\] T001[^\n]*\n- \[x\] T002[^\n]*\n- \[x\] T003[^\n]*\n- \[ \] T004/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate runs one task at a time when SPECKIT_MAX_PARALLEL_TASKS is 1', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: PARALLEL(' ', ' ', ' ', ' ') }]);
  github.permissions.dev = 'write';
  const twin = github.issues[0];
  flag(github, twin);
  try {
    await orchestrate(github, root, { SPECKIT_MAX_PARALLEL_TASKS: '1' });
    github.setFile('speckit/a', 'specs/a/tasks.md', PARALLEL(' ', ' ', ' ', ' '));
    await orchestrate(github, root, { SPECKIT_MAX_PARALLEL_TASKS: '1' });
    assert.deepEqual(taskRuns(github), [`#${twin.number} T001 attempt 1`]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

const runTitles = (github) => github.repo.runs.map((run) => run.display_title);
const outputOf = (root) => readFileSync(envFor(root).GITHUB_OUTPUT, 'utf8');

test('orchestrate converges after the last task, merges, resolves conflicts, and ends when merged', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await orchestrate(github, root);
    const pull = github.repo.pulls[0];
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', ' '));
    await orchestrate(github, root);
    assert.equal(runTitles(github).at(-1), `Spec Kit implement #${twin.number} T002 attempt 1`);

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'X'));
    await orchestrate(github, root);
    const converge = github.repo.runs.at(-1);
    assert.deepEqual([converge.workflow, converge.display_title, converge.inputs], ['speckit-converge.yml', `Spec Kit converge #${twin.number} attempt 1`, { twin: String(twin.number), pull: String(pull.number), attempt: '1' }]);

    github.completeRun(converge.id, 'success');
    const merging = await orchestrate(github, root);
    const head = github.repo.branches['speckit/a'];
    assert.deepEqual(merging.merges, [{ twin: twin.number, pull: pull.number, folder: 'a', head, attempt: 1, check_run: github.repo.checkRuns.at(-1).id }]);
    const mergeCheck = github.repo.checkRuns.at(-1);
    assert.deepEqual([mergeCheck.status, mergeCheck.external_id, mergeCheck.head_sha], ['in_progress', CHECK_MERGE, head]);
    assert.match(outputOf(root), /merge_count=1\nmerges=\{"include":\[\{"twin":\d+,"pull":\d+,"folder":"a","head":"[^"]+","attempt":1,"check_run":\d+\}\]\}/);

    const waiting = await orchestrate(github, root);
    assert.deepEqual(waiting.merges, [], 'a running merge blocks new decisions');
    assert.match(waiting.text, /a step is running/);

    Object.assign(mergeCheck, { status: 'completed', conclusion: 'neutral', external_id: CHECK_CONFLICT });
    await orchestrate(github, root);
    const resolve = github.repo.runs.at(-1);
    assert.deepEqual([resolve.workflow, resolve.display_title], ['speckit-resolve.yml', `Spec Kit resolve #${twin.number} attempt 1`]);

    github.completeRun(resolve.id, 'success');
    github.repo.branches['speckit/a'] = 'sha-merged-main';
    const again = await orchestrate(github, root);
    assert.equal(again.merges.length, 1);
    assert.equal(again.merges[0].head, 'sha-merged-main');

    github.closePull(pull.number, { merged: true });
    const merged = await orchestrate(github, root);
    assert.deepEqual(merged.merges, []);
    assert.match(merged.text, /Finalized: #\d+ `a` merged in pull request #\d+; the twin is `speckit:stage:implemented` and the branch was deleted/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('orchestrate limits merges, abandons stale merges, and starts a new window when a person pushes', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await orchestrate(github, root);
    const pull = github.repo.pulls[0];
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
    await orchestrate(github, root);
    github.completeRun(github.repo.runs.at(-1).id, 'success');

    for (let attempt = 1; attempt <= 3; attempt += 1) {
      const result = await orchestrate(github, root);
      assert.equal(result.merges[0].attempt, attempt);
      Object.assign(github.repo.checkRuns.at(-1), { status: 'completed', conclusion: 'failure' });
    }
    const limited = await orchestrate(github, root);
    assert.deepEqual(limited.merges, []);
    assert.deepEqual(github.repo.checkRuns.slice(-2).map((run) => run.external_id), [CHECK_LIMIT, 'speckit:diagnosing']);
    assert.match(github.comments.at(-1).body, /Implementation needs attention\*\* @dev: merge did not succeed in 3 attempts/);

    github.repo.pullCommits[pull.number] = [{ author: { login: 'dev' }, committer: { login: 'web-flow' }, commit: { committer: { date: new Date(github.clock + 1000).toISOString() } } }];
    github.repo.branches['speckit/a'] = 'sha-fixed-by-dev';
    github.tick();
    const resumed = await orchestrate(github, root);
    assert.deepEqual([resumed.merges.length, resumed.merges[0].attempt], [1, 1], 'a push by a person starts a new attempt window');

    const running = github.repo.checkRuns.at(-1);
    github.clock += 3 * 60 * 60 * 1000;
    const stale = await runOrchestrate({ client: github, rootDir: root, env: envFor(root), log: silent, sleep: async () => {}, now: () => github.clock });
    assert.deepEqual([running.status, running.conclusion], ['completed', 'failure'], 'a merge running for more than 2 hours is abandoned');
    assert.equal(stale.merges[0].attempt, 2);
    assert.equal(twin.number, stale.merges[0].twin);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('stays done for review and ignores forged resume markers', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await orchestrate(github, root);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    for (let attempt = 0; attempt < 2; attempt += 1) {
      github.completeRun(github.repo.runs.at(-1).id);
      github.commentAuthor = 'mallory';
      await github.createComment(pull.number, `${RESUME_COMMENT_MARKER} forged by a person`);
      github.commentAuthor = undefined;
      await github.createComment(pull.number, `**T001 attempt failed:**\n\nagent said ${RESUME_COMMENT_MARKER}`);
      await orchestrate(github, root);
    }
    assert.equal(runTitles(github).at(-1), `Spec Kit implement #${twin.number} T001 attempt 3`, 'forged or embedded markers do not reset the attempt count');

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
    await orchestrate(github, root);
    assert.equal(runTitles(github).at(-1), `Spec Kit converge #${twin.number} attempt 1`);
    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await github.createComment(pull.number, `${DONE_COMMENT_MARKER}\n**All 2 tasks are implemented**`);
    github.repo.branches['speckit/a'] = 'sha-review-fix';
    const done = await orchestrate(github, root);
    assert.deepEqual(done.merges, []);
    assert.match(done.text, /implemented and waiting for review/, 'a new head commit does not restart the merge');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
