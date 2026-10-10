import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { TWIN_LABEL } from './speckit-prepare-core.mjs';
import { runSync } from './speckit-prepare.mjs';
import { UsageError, main, resolveFolder, runOrchestrate, runRequest, runSelect, runStart } from './speckit-orchestrate.mjs';
import { runContinue, runDecide } from './speckit-spec.mjs';
import { CHECK_CONFLICT, CHECK_LIMIT, CHECK_MERGE, CHECK_RUN_NAME, DONE_COMMENT_MARKER, RESUME_COMMENT_MARKER, START_COMMENT_MARKER, parseGuidance, renderAttemptMarker } from './speckit-implement-core.mjs';
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

const BRANCH_TASKS = (t1, t2) => `## Phase 1: Setup\n\n- [${t1}] T001 [P] Create project\n\n### Implementation\n\n- [${t2}] T002 [US1] Build it\n`;

async function orchestrate(github, root, env = {}) {
  const lines = [];
  const result = await runOrchestrate({ client: github, rootDir: root, env: { ...envFor(root), ...env }, log: (line) => lines.push(line), sleep: async () => {}, now: () => github.clock });
  return { ...result, text: lines.join('\n') };
}

const chainRuns = (github) => github.repo.runs.filter((run) => run.workflow === 'speckit-implement.yml');
const limitCheck = (github, head) => github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'completed', conclusion: 'failure', external_id: CHECK_LIMIT });

test('orchestrate starts the implementation chain and continues it only when no run is active and there is work', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    const result = await orchestrate(github, root);
    const pull = github.repo.pulls[0];
    assert.equal(result.exitCode, 0);
    assert.deepEqual(chainRuns(github).map((run) => [run.display_title, run.ref, run.inputs]), [[`Spec Kit implement #${twin.number}`, 'main', { twin: String(twin.number), pull: String(pull.number) }]]);
    assert.match(result.text, new RegExp(`Started the implementation of #${twin.number} \\(pull request #${pull.number}\\)`));

    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    const active = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 1, 'an active chain is left alone');
    assert.match(active.text, /its implementation run is active/);

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    const broken = await orchestrate(github, root);
    assert.equal(chainRuns(github).length, 2, 'a chain that stopped with work left is continued');
    assert.match(broken.text, new RegExp(`Continued #${twin.number} \\(pull request #${pull.number}\\): next is T001 attempt 1`));
    assert.equal(chainRuns(github).at(-1).display_title, `Spec Kit implement #${twin.number} · Phase 1: Setup · T001–T002`, 'the run is named after what it tackles');

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
    assert.deepEqual(chainRuns(github).map((run) => run.display_title), [`Spec Kit implement #${a.number}`]);
    assert.match(first.text, new RegExp(`Waiting: #${b.number} \`b\` starts when fewer than 1 implementations are active`));

    github.completeRun(github.repo.runs.at(-1).id, 'success');
    await github.createComment(github.repo.pulls[0].number, `${DONE_COMMENT_MARKER}\n**Done**`);
    await orchestrate(github, root, { SPECKIT_MAX_ACTIVE_SPECS: '1' });
    assert.deepEqual(chainRuns(github).map((run) => run.display_title).at(-1), `Spec Kit implement #${b.number}`, 'an implementation waiting for review frees its slot');
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

test('orchestrate waits for a run from before the chain that handed back', async () => {
  const { root, github } = await flaggedRepo();
  try {
    await orchestrate(github, root);
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    const handingBack = github.repo.runs.at(-1);
    let polls = 0;
    await runOrchestrate({
      client: github,
      rootDir: root,
      env: { ...envFor(root), SPECKIT_AFTER_RUN: String(handingBack.id) },
      log: silent,
      sleep: async () => {
        polls += 1;
        if (polls === 2) github.completeRun(handingBack.id);
      },
      now: () => github.clock,
    });
    assert.equal(polls, 2);
    assert.equal(chainRuns(github).length, 2, 'the finished run no longer counts as active');

    polls = 0;
    const stuck = github.repo.runs.at(-1);
    const lines = [];
    await runOrchestrate({ client: github, rootDir: root, env: { ...envFor(root), SPECKIT_AFTER_RUN: String(stuck.id) }, log: (line) => lines.push(line), sleep: async () => { polls += 1; }, now: () => github.clock });
    assert.equal(polls, 60);
    assert.match(lines.join('\n'), new RegExp(`Warning: run ${stuck.id} was still active after 5 minutes`));
    assert.equal(chainRuns(github).length, 2, 'a still active chain gets no second run');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// --- the implementation chain (Spec Kit implement): decide and continue ---

// Runs the decide job of the chain for the twin's pull request and returns its outputs.
async function decide(github, root, twin, env = {}) {
  const output = path.join(root, `decide-${github.clock}.txt`);
  writeFileSync(output, '');
  const lines = [];
  const result = await runDecide({
    client: github,
    env: { ...envFor(root), GITHUB_OUTPUT: output, SPECKIT_TWIN: String(twin.number), SPECKIT_PULL: String(github.repo.pulls[0].number), ...env },
    log: (line) => lines.push(line),
    now: () => github.clock,
  });
  const outputs = Object.fromEntries(readFileSync(output, 'utf8').trim().split('\n').filter(Boolean).map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]));
  return { ...result, outputs, text: lines.join('\n') };
}

const attemptComment = (github, pull, task, outcome, step = 'task') => github.createComment(pull, `${renderAttemptMarker({ step, task, attempt: 1, outcome })}\n**${task ?? step}** ${outcome}`);

test('chain: decide plans the stages of a phase with prompts, verify modes, and the first stage\'s check runs', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', '## Phase 1: Setup\n\n- [ ] T001 [P] Create project\n- [ ] T002 [P] Add docs\n\n### Implementation\n\n- [ ] T003 [US1] Build it\n\n## Phase 2\n\n- [ ] T004 Later\n');
    const progress = github.repo.checkRuns.at(-1);
    const decided = await decide(github, root, twin);
    assert.deepEqual([decided.outputs.action, decided.outputs.continue, decided.outputs.step, decided.outputs.phase_check], ['stages', 'true', 'task', 'false']);
    assert.deepEqual(JSON.parse(decided.outputs.stage_matrix), { include: [{ number: 1, index: 0 }, { number: 2, index: 1 }] });
    const stages = JSON.parse(decided.outputs.stages);
    assert.deepEqual(stages.map((stage) => stage.include.map((entry) => [entry.task, entry.attempt, entry.verify_mode])), [[['T001', 1, 'task'], ['T002', 1, 'task']], [['T003', 1, 'phase']]]);
    assert.match(stages[0].include[0].prompt, /^\/speckit-implement Implement only task T001 of the spec in `specs\/a\/` \(its `tasks\.md`\); ignore the other specs\./);
    assert.equal(stages[1].include[0].summary, '[US1] Build it');
    assert.equal(progress.external_id, 'speckit:attempt:task:T001:1', 'the first attempt takes over the queued progress check run');
    assert.deepEqual(github.repo.checkRuns.filter((check) => String(check.external_id).startsWith('speckit:attempt:')).map((check) => [check.external_id, check.status]), [['speckit:attempt:task:T001:1', 'in_progress'], ['speckit:attempt:task:T002:1', 'in_progress']]);
    assert.equal(decided.outputs.head, github.repo.branches['speckit/a']);
    assert.match(decided.text, /Stage 1: T001 attempt 1, T002 attempt 1\n- Stage 2: T003 attempt 1/);

    // The run ended without reporting its attempts: they count as failed, and the next run retries them.
    const retried = await decide(github, root, twin);
    assert.match(retried.text, /Recorded 2 attempt\(s\) of an earlier run that ended without reporting as failed/);
    assert.match(github.comments.find((comment) => comment.body.includes('"task":"T001","attempt":1,"outcome":"failure"')).body, /T001 attempt 1 did not finish/);
    assert.deepEqual(JSON.parse(retried.outputs.stages)[0].include.map((entry) => entry.attempt), [2, 2]);

    github.setFile('speckit/a', 'specs/a/tasks.md', '## Phase 1: Setup\n\n- [x] T001 a\n- [ ] T002 [P] b\n- [ ] T003 [P] c\n');
    for (const check of github.repo.checkRuns) check.status = 'completed';
    const group = await decide(github, root, twin);
    assert.deepEqual([JSON.parse(group.outputs.stage_matrix).include.length, group.outputs.phase_check], [1, 'true'], 'a phase that ends with a [P] group gets the phase verification');
    assert.equal(pull.number, github.repo.pulls[0].number);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: decide stops at the attempt limit, resumes on request with guidance, and does nothing for unflagged twins', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS(' ', ' '));
    for (let attempt = 0; attempt < 3; attempt += 1) await attemptComment(github, pull.number, 'T001', 'failure');
    const limited = await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' });
    assert.deepEqual([limited.outputs.action, limited.outputs.continue], ['stop', 'false']);
    assert.equal(github.repo.checkRuns.at(-1).external_id, CHECK_LIMIT);
    assert.match(github.comments.at(-1).body, /Implementation needs attention\*\* @dev: T001 did not succeed in 3 attempts[\s\S]*\/speckit diagnose \[notes\]/);
    assert.equal((await decide(github, root, twin, { SPECKIT_AUTO_DIAGNOSE: 'false' })).outputs.action, 'stop', 'a stop waits for a person');

    const diagnosed = await decide(github, root, twin);
    assert.match(diagnosed.text, /Started a diagnosis/);
    assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.checkRuns.at(-1).external_id], ['speckit-diagnose.lock.yml', 'speckit:diagnosing']);

    const resumed = await decide(github, root, twin, { SPECKIT_RESUME: 'true', SPECKIT_GUIDANCE: 'Use the existing helper.' });
    assert.equal(resumed.outputs.action, 'stages');
    const comment = github.comments.find((item) => item.body.startsWith(RESUME_COMMENT_MARKER));
    assert.equal(parseGuidance(comment.body), 'Use the existing helper.');
    const [first, second] = JSON.parse(resumed.outputs.stages);
    assert.deepEqual([first.include[0].task, first.include[0].attempt], ['T001', 1], 'a resume starts a new attempt window');
    assert.match(first.include[0].prompt, /Guidance from the person who resumed this implementation: Use the existing helper\.$/);
    assert.doesNotMatch(second.include[0].prompt, /Guidance/, 'guidance applies to the stopped step only');

    github.mergeBranchOutcome = null;
    twin.labels = twin.labels.filter((label) => label.name !== IMPLEMENT);
    const unflagged = await decide(github, root, twin);
    assert.deepEqual([unflagged.outputs.action, unflagged.outputs.continue], ['none', 'false']);
    assert.match(unflagged.text, /is not open and flagged for implementation/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('chain: decide converges, merges, resolves conflicts, and waits for runs from before the chain', async () => {
  const { root, github, twin } = await flaggedRepo();
  try {
    await start(github, root, twin);
    const pull = github.repo.pulls[0];
    github.setFile('speckit/a', 'specs/a/tasks.md', BRANCH_TASKS('x', 'x'));
    await attemptComment(github, pull.number, 'T002', 'success');
    const converge = await decide(github, root, twin);
    assert.deepEqual([converge.outputs.action, converge.outputs.step], ['stages', 'converge']);
    const [stage] = JSON.parse(converge.outputs.stages);
    assert.deepEqual([stage.include[0].task, stage.include[0].attempt, stage.include[0].prompt.startsWith('/speckit-converge')], ['', 1, true]);
    assert.ok(github.repo.checkRuns.some((check) => check.external_id === 'speckit:attempt:converge::1' && check.status === 'in_progress'));

    for (const check of github.repo.checkRuns) check.status = 'completed';
    await attemptComment(github, pull.number, null, 'success', 'converge');
    const merge = await decide(github, root, twin);
    assert.deepEqual([merge.outputs.action, merge.outputs.continue, merge.outputs.attempt], ['merge', 'true', '1']);
    const mergeCheck = github.repo.checkRuns.find((check) => String(check.id) === merge.outputs.check_run);
    assert.deepEqual([mergeCheck.external_id, mergeCheck.status], [CHECK_MERGE, 'in_progress']);

    Object.assign(mergeCheck, { status: 'completed', conclusion: 'neutral', external_id: CHECK_CONFLICT });
    const resolve = await decide(github, root, twin);
    assert.deepEqual([resolve.outputs.step, JSON.parse(resolve.outputs.stages)[0].include[0].verify_mode], ['resolve', 'finalize']);

    github.repo.runs.push({ id: 900, workflow: 'speckit-implement.yml', display_title: `Spec Kit implement #${twin.number} T002 attempt 1`, status: 'in_progress', created_at: github.tick() });
    const waiting = await decide(github, root, twin);
    assert.deepEqual([waiting.outputs.action, waiting.outputs.continue], ['wait', 'false']);
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
    assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-implement.yml', { twin: String(twin.number), pull: String(pull.number) }]);
    assert.equal(github.repo.checkRuns.some((check) => check.external_id === CHECK_LIMIT), false);

    await runContinue({ client: github, env: { ...env, SPECKIT_PHASE_CHECK: 'failure' }, log: silent });
    const stop = github.repo.checkRuns.at(-1);
    assert.deepEqual([stop.external_id, stop.conclusion, stop.output.title], [CHECK_LIMIT, 'failure', 'The phase verification failed']);
    assert.match(github.comments.at(-1).body, /Implementation needs attention\*\* @dev: the full verification at the end of the phase failed[\s\S]*actions\/runs\/42/);
    assert.equal(github.repo.runs.filter((run) => run.workflow === 'speckit-implement.yml').length, 2, 'the next run reports the stop and starts a diagnosis');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
