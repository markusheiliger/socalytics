import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import test from 'node:test';

import { TWIN_LABEL, renderTwinBody } from './speckit-prepare-core.mjs';
import { CHECK_ATTEMPT, CHECK_CONFLICT, CHECK_DONE, CHECK_LIMIT, CHECK_MERGE, CHECK_PROGRESS, CHECK_RUN_NAME, RESUME_COMMENT_MARKER, renderGuidanceMarker } from './speckit-implement-core.mjs';
import {
  TaskInputError,
  defaultGit,
  lastAgentMessage,
  main,
  parseStepInputs,
  runLandStage,
  startAttemptChecks,
  runIntegrate,
  runLand,
  runMergeLand,
  runPackage,
  runVerdict,
} from './speckit-step.mjs';
import { FakeGitHub, silent } from './speckit-test-helpers.mjs';

const IMPLEMENT = 'speckit:stage:implement';
const TASKS = '# Tasks\n\n## Phase 1\n\n- [ ] T001 Create docs/x.md\n- [ ] T002 Create docs/y.md\n';
const DONE_TASKS = TASKS.replaceAll('- [ ]', '- [x]');
const context = { serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' };
const TASK_INPUTS = { step: 'task', twin: 5, pull: 9, task: 'T001', attempt: 2 };
const CONVERGE_INPUTS = { step: 'converge', twin: 5, pull: 9, task: null, attempt: 1 };
const RESOLVE_INPUTS = { step: 'resolve', twin: 5, pull: 9, task: null, attempt: 1 };
const MERGE_INPUTS = { step: 'merge', twin: 5, pull: 9, task: null, attempt: 1 };

function git(cwd, ...args) {
  const result = spawnSync('git', ['-c', 'user.name=test', '-c', 'user.email=test@example.invalid', ...args], { cwd, encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`git ${args.join(' ')}: ${result.stderr}`);
  return result.stdout;
}

function write(dir, file, content) {
  mkdirSync(path.dirname(path.join(dir, file)), { recursive: true });
  writeFileSync(path.join(dir, file), content);
}

// A bare "remote" with main and speckit/f plus two clones of speckit/f: one for the work job and one for the land
// job. `branch` and `main` add a commit to that branch on top of the shared seed commit.
function makeGitRepos(tasks = TASKS, { branch, main: mainChange } = {}) {
  const base = mkdtempSync(path.join(tmpdir(), 'speckit-step-'));
  const remoteRoot = path.join(base, 'remote');
  const bare = path.join(remoteRoot, 'octo', 'repo.git');
  mkdirSync(bare, { recursive: true });
  const seed = path.join(base, 'seed');
  git(base, 'init', '-q', seed);
  git(bare, 'init', '-q', '--bare');
  write(seed, 'specs/f/tasks.md', tasks);
  write(seed, 'specs/f/spec.md', '# Spec\n');
  write(seed, 'README.md', '# Repo\n');
  write(seed, 'docs/shared.md', '# Shared\n\nline\n');
  git(seed, 'add', '-A');
  git(seed, 'commit', '-q', '-m', 'seed');
  git(seed, 'push', '-q', bare, 'HEAD:refs/heads/main', 'HEAD:refs/heads/speckit/f');
  const seedSha = git(seed, 'rev-parse', 'HEAD').trim();
  for (const [ref, change] of [['speckit/f', branch], ['main', mainChange]]) {
    if (!change) continue;
    git(seed, 'reset', '-q', '--hard', seedSha);
    change(seed);
    git(seed, 'add', '-A');
    git(seed, 'commit', '-q', '-m', `change on ${ref}`);
    git(seed, 'push', '-q', bare, `HEAD:refs/heads/${ref}`);
  }
  const work = path.join(base, 'work');
  const land = path.join(base, 'land');
  git(base, 'clone', '-q', '--branch', 'speckit/f', bare, work);
  git(base, 'clone', '-q', '--branch', 'speckit/f', bare, land);
  return { base, bare, remoteRoot, work, land, resultDir: path.join(base, 'result') };
}

function agentDoesT001(work, { tick = true, extra = {} } = {}) {
  write(work, 'docs/x.md', '# X\n');
  if (tick) write(work, 'specs/f/tasks.md', readFileSync(path.join(work, 'specs/f/tasks.md'), 'utf8').replace('- [ ] T001', '- [X] T001'));
  for (const [file, content] of Object.entries(extra)) write(work, file, content);
}

const appendConvergence = (work, tasks = '- [ ] T003 Add the missing guard (FR-001)\n') => write(
  work,
  'specs/f/tasks.md',
  `${readFileSync(path.join(work, 'specs/f/tasks.md'), 'utf8')}\n## Phase 2: Convergence\n\n${tasks}`,
);

// Runs the work job of a step: integrate (resolve, merge), the simulated agent, package (task, converge, resolve),
// and verdict with a simulated environment-verify outcome (`verify: null` means no verify action).
function work(repos, { inputs = TASK_INPUTS, agentExit = '0', agent, verify = { outcome: 'success', checks: 'Markdown check' }, env: extraEnv = {} } = {}) {
  mkdirSync(repos.resultDir, { recursive: true });
  const outputFile = path.join(repos.resultDir, 'github-output.txt');
  writeFileSync(outputFile, '');
  const env = { GITHUB_OUTPUT: outputFile, SPECKIT_BRANCH: 'main', ...extraEnv };
  const run = { git: defaultGit(repos.work), env, inputs, folder: 'f', workspace: repos.work, resultDir: repos.resultDir, log: silent };
  const integrated = ['resolve', 'merge'].includes(inputs.step) ? runIntegrate(run) : null;
  agent?.(repos);
  if (agentExit !== null && inputs.step !== 'merge') writeFileSync(path.join(repos.resultDir, 'agent-exit.txt'), `${agentExit}\n`);
  const packaged = inputs.step === 'merge' ? null : runPackage(run);
  let result = packaged?.result ?? integrated.result;
  if (inputs.step !== 'converge') {
    const ready = inputs.step === 'merge' ? integrated.result.reasons.length === 0 && integrated.result.conflicts.length === 0 : packaged.verify;
    const verifyEnv = verify === null
      ? { SPECKIT_VERIFY_CONFIGURED: 'false' }
      : { SPECKIT_VERIFY_CONFIGURED: 'true', SPECKIT_VERIFY_OUTCOME: ready ? verify.outcome : 'skipped', SPECKIT_VERIFY_CHECKS: ready ? verify.checks : '', SPECKIT_VERIFY_UNCOVERED: ready ? verify.uncovered ?? '' : '', SPECKIT_SELFTEST_OUTCOME: verify.selfTest ?? '' };
    result = runVerdict({ env: verifyEnv, inputs, resultDir: repos.resultDir, log: silent }).result;
  }
  return { integrated, packaged, result, output: readFileSync(outputFile, 'utf8') };
}

// A fake GitHub with twin #5 (flagged by "dev") and its implementation pull request #9.
function fakeGitHub({ body = '## Tasks (2)\n\n- [ ] T001 Create docs/x.md\n- [ ] T002 Create docs/y.md\n' } = {}) {
  const github = new FakeGitHub([{
    number: 5,
    id: 5000,
    node_id: 'I_5',
    state: 'open',
    title: 'F',
    body: renderTwinBody({ folder: 'f', title: 'F', summary: 's', dependencyNotes: [] }, ['spec.md'], context),
    labels: [{ name: TWIN_LABEL }],
  }]);
  github.humanLabel(5, IMPLEMENT, 'dev');
  github.permissions.dev = 'write';
  github.repo.branches['speckit/f'] = 'sha-head';
  github.repo.pulls.push({ number: 9, node_id: 'PR_9', title: 'Implement: F', body, draft: true, state: 'open', head: { ref: 'speckit/f', sha: 'sha-head', repo: { full_name: 'octo/repo' } }, created_at: github.tick() });
  return github;
}

function landEnv(repos) {
  return { GITHUB_SERVER_URL: pathToFileURL(repos.remoteRoot).href, GITHUB_REPOSITORY: 'octo/repo', GITHUB_TOKEN: 'token', SPECKIT_BRANCH: 'main' };
}

async function land(repos, github, { inputs = TASK_INPUTS, workResult = 'success' } = {}) {
  const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-head', status: 'in_progress', external_id: CHECK_ATTEMPT });
  const result = await runLand({
    client: github,
    git: defaultGit(repos.land),
    env: landEnv(repos),
    inputs: { ...inputs, checkRun: check.id },
    folder: 'f',
    workspace: repos.land,
    resultDir: repos.resultDir,
    workResult,
    log: silent,
  });
  return { ...result, check: github.repo.checkRuns.find((run) => run.id === check.id) };
}

const remoteHead = (repos) => git(repos.base, `--git-dir=${repos.bare}`, 'log', '-1', '--format=%H %P|%s', 'speckit/f').trim();
const remoteShow = (repos, spec) => git(repos.base, `--git-dir=${repos.bare}`, 'show', spec);
const remoteSha = (repos, ref) => git(repos.base, `--git-dir=${repos.bare}`, 'rev-parse', ref).trim();

function withRepos(options, body) {
  const repos = makeGitRepos(options.tasks, options);
  return Promise.resolve(body(repos)).finally(() => rmSync(repos.base, { recursive: true, force: true }));
}

test('parses and validates step inputs', () => {
  const base = { SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_ATTEMPT: '2' };
  assert.deepEqual(parseStepInputs({ ...base, SPECKIT_TASK: 'T001' }), TASK_INPUTS);
  assert.deepEqual(parseStepInputs({ ...base, SPECKIT_STEP: 'converge', SPECKIT_TASK: 'ignored' }), { ...CONVERGE_INPUTS, attempt: 2 });
  assert.deepEqual(parseStepInputs({ ...base, SPECKIT_STEP: 'merge' }), { ...MERGE_INPUTS, attempt: 2 });
  for (const env of [{}, { ...base, SPECKIT_TASK: 'finalize' }, { ...base, SPECKIT_TASK: 'rm -rf' }, { ...base, SPECKIT_TWIN: '0', SPECKIT_TASK: 'T001' }, { ...base, SPECKIT_STEP: 'deploy' }]) {
    assert.throws(() => parseStepInputs(env), TaskInputError);
  }
});

test('task: package writes the change and the changed paths, and verdict records the verification', () => withRepos({}, (repos) => {
  const { packaged, result, output } = work(repos, { agent: (r) => agentDoesT001(r.work) });
  assert.equal(packaged.verify, true);
  assert.match(output, /^verify=true$/m);
  assert.deepEqual(readFileSync(path.join(repos.resultDir, 'changed-files.txt'), 'utf8').split('\n').filter(Boolean).sort(), ['docs/x.md', 'specs/f/tasks.md']);
  assert.ok(readFileSync(path.join(repos.resultDir, 'changes.patch'), 'utf8').includes('docs/x.md'));
  assert.deepEqual([result.verified, result.verification, result.checks], [true, 'configured', [{ name: 'Markdown check', passed: true }]]);
  assert.deepEqual(result.changedFiles.sort(), ['docs/x.md', 'specs/f/tasks.md']);
}));

test('task: package rejects missing ticks, protected paths, and agent failures; verdict records failed verification', async () => {
  const cases = [
    { name: 'no tick', agent: (r) => agentDoesT001(r.work, { tick: false }), reason: /T001 was not checked/, verify: false },
    { name: 'protected path', agent: (r) => agentDoesT001(r.work, { extra: { '.github/workflows/evil.yml': 'x' } }), reason: /changed protected paths: \.github\/workflows\/evil\.yml/, verify: false },
    { name: 'timeout', agent: (r) => agentDoesT001(r.work), agentExit: '124', reason: /did not finish within 60 minutes/, verify: false },
    { name: 'no agent', agentExit: null, reason: /the agent did not run/, verify: false },
    { name: 'failed check', agent: (r) => agentDoesT001(r.work), check: { outcome: 'failure', checks: 'platform build and tests,Markdown check' }, reason: /^Markdown check failed$/, verify: true },
    { name: 'crashed verification', agent: (r) => agentDoesT001(r.work), check: { outcome: 'failure', checks: '' }, reason: /the verification did not succeed \(failure\)/, verify: true },
  ];
  for (const testCase of cases) {
    await withRepos({}, (repos) => {
      const { packaged, result } = work(repos, { agent: testCase.agent, agentExit: testCase.agentExit === undefined ? '0' : testCase.agentExit, verify: testCase.check });
      assert.equal(packaged.verify, testCase.verify, testCase.name);
      assert.equal(result.verified, false, testCase.name);
      assert.match(result.reasons.join(), testCase.reason, testCase.name);
    });
  }
});

test('task: land re-validates, commits, pushes, and reports a verified task', () => withRepos({}, async (repos) => {
  work(repos, { agent: (r) => agentDoesT001(r.work) });
  const github = fakeGitHub();
  const result = await land(repos, github);
  assert.equal(result.exitCode, 0);
  assert.match(remoteHead(repos), /^[0-9a-f]{40} [0-9a-f]{40}\|feat\(f\): T001 Create docs\/x\.md$/);
  assert.equal(remoteHead(repos).split(' ')[0], result.head);
  assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# X\n');
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[X\] T001/);
  assert.match(github.repo.pulls[0].body, /- \[x\] T001 Create docs\/x\.md\n- \[ \] T002/);
  assert.deepEqual([result.check.status, result.check.conclusion, result.check.output.title], ['completed', 'success', 'T001 implemented']);
  const progress = github.repo.checkRuns.at(-1);
  assert.deepEqual([progress.status, progress.external_id, progress.head_sha, progress.output.title], ['queued', CHECK_PROGRESS, result.head, '1 of 2 tasks implemented']);
  assert.match(github.comments.at(-1).body, /\*\*T001 implemented\*\* \(attempt 2\)[\s\S]*`docs\/x\.md`[\s\S]*Markdown check passed[\s\S]*1 of 2 tasks/);
}));

const PARALLEL_TASKS = '# Tasks\n\n## Phase 1\n\n- [ ] T001 [P] Create docs/x.md\n- [ ] T002 [P] Create docs/y.md\n';

// Simulates a parallel sibling (or a person) pushing to the implementation branch after the task started.
function siblingLands(repos, change) {
  const dir = path.join(repos.base, `sibling-${Date.now()}`);
  git(repos.base, 'clone', '-q', '--branch', 'speckit/f', repos.bare, dir);
  change(dir);
  git(dir, 'add', '-A');
  git(dir, 'commit', '-q', '-m', 'sibling');
  git(dir, 'push', '-q', 'origin', 'HEAD:refs/heads/speckit/f');
  return git(dir, 'rev-parse', 'HEAD').trim();
}

const siblingDoesT002 = (extra = {}) => (dir) => {
  write(dir, 'docs/y.md', '# Y\n');
  write(dir, 'specs/f/tasks.md', readFileSync(path.join(dir, 'specs/f/tasks.md'), 'utf8').replace('- [ ] T002', '- [x] T002'));
  for (const [file, content] of Object.entries(extra)) write(dir, file, content);
};

test('task: land rebuilds a parallel task on a branch that a sibling moved', () => withRepos({ tasks: PARALLEL_TASKS }, async (repos) => {
  work(repos, { agent: (r) => agentDoesT001(r.work) });
  const sibling = siblingLands(repos, siblingDoesT002());
  const github = fakeGitHub({ body: '## Tasks (2)\n\n- [ ] T001 [P] Create docs/x.md\n- [ ] T002 [P] Create docs/y.md\n' });
  const superseded = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: sibling, status: 'queued', external_id: CHECK_PROGRESS });
  const result = await land(repos, github);
  assert.equal(result.exitCode, 0);
  assert.match(remoteHead(repos), new RegExp(`^${result.head} ${sibling}\\|feat\\(f\\): T001 \\[P\\] Create docs/x\\.md$`));
  assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# X\n');
  assert.equal(remoteShow(repos, 'speckit/f:docs/y.md'), '# Y\n');
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[x\] T001 \[P\] Create docs\/x\.md\n- \[x\] T002/);
  assert.match(github.repo.pulls[0].body, /- \[x\] T001 [^\n]*\n- \[x\] T002/);
  assert.equal(result.check.output.title, 'T001 implemented');
  assert.equal(github.repo.checkRuns.at(-1).output.title, '2 of 2 tasks implemented');
  assert.match(github.comments.at(-1).body, /2 of 2 tasks implemented/);
  assert.deepEqual([github.repo.checkRuns.find((run) => run.id === superseded.id).status, github.repo.checkRuns.find((run) => run.id === superseded.id).output.title], ['completed', 'Superseded']);
}));

test('task: land redoes a parallel task whose files a sibling changed, without counting a failure', () => withRepos({ tasks: PARALLEL_TASKS }, async (repos) => {
  work(repos, { agent: (r) => agentDoesT001(r.work) });
  const sibling = siblingLands(repos, siblingDoesT002({ 'docs/x.md': '# Sibling\n' }));
  const github = fakeGitHub();
  const result = await land(repos, github);
  assert.equal(result.exitCode, 0);
  assert.match(result.requeue.join(), /`docs\/x\.md` changed on the branch since the task started/);
  assert.equal(remoteSha(repos, 'speckit/f'), sibling);
  assert.deepEqual([result.check.status, result.check.conclusion, result.check.output.title], ['completed', 'neutral', 'T001 attempt 2 is redone from the new head']);
  assert.match(github.comments.at(-1).body, /\*\*T001 attempt 2 runs again\*\*[\s\S]*does not count as a failed attempt/);
}));

// Runs the work job of one task of a stage in its own clone, as the matrix does, into results/speckit-result-<task>.
function workStageTask(repos, task, agent, { verify } = {}) {
  const dir = path.join(repos.base, `work-${task}`);
  git(repos.base, 'clone', '-q', '--branch', 'speckit/f', repos.bare, dir);
  const env = { SPECKIT_HEAD: git(dir, 'rev-parse', 'HEAD').trim() };
  return work({ ...repos, work: dir, resultDir: path.join(repos.base, 'results', `speckit-result-${task}`) }, { inputs: { ...TASK_INPUTS, task, attempt: 1 }, agent: () => agent(dir), verify, env });
}

// Lands the first of the given stages, as the land job of the stage matrix does; the decide job already showed its
// attempts as in progress on the stage's start commit.
async function landStage(repos, github, entries, { next = null } = {}) {
  const checks = await startAttemptChecks(github, { head: remoteSha(repos, 'speckit/f'), step: 'task', entries });
  const outputFile = path.join(repos.base, 'stage-output.txt');
  writeFileSync(outputFile, '');
  const result = await runLandStage({
    client: github,
    git: defaultGit(repos.land),
    env: {
      ...landEnv(repos),
      GITHUB_OUTPUT: outputFile,
      SPECKIT_STEP: 'task',
      SPECKIT_TWIN: '5',
      SPECKIT_PULL: '9',
      SPECKIT_STAGES: JSON.stringify([{ include: entries }, ...(next ? [next] : [])]),
      SPECKIT_STAGE_INDEX: '0',
      SPECKIT_WORK_RESULT: 'success',
    },
    folder: 'f',
    workspace: repos.land,
    resultRoot: path.join(repos.base, 'results'),
    log: silent,
  });
  return { ...result, checks, output: readFileSync(outputFile, 'utf8') };
}

test('stage: lands the tasks of a [P] group in order and shows the next stage as in progress', () => withRepos({ tasks: `${PARALLEL_TASKS}- [ ] T003 Create docs/z.md\n` }, async (repos) => {
  workStageTask(repos, 'T001', (dir) => agentDoesT001(dir));
  workStageTask(repos, 'T002', siblingDoesT002());
  const github = fakeGitHub({ body: '## Tasks (3)\n\n- [ ] T001 [P] Create docs/x.md\n- [ ] T002 [P] Create docs/y.md\n- [ ] T003 Create docs/z.md\n' });
  await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: remoteSha(repos, 'speckit/f'), status: 'queued', external_id: CHECK_PROGRESS });
  const result = await landStage(repos, github, [{ task: 'T001', attempt: 1 }, { task: 'T002', attempt: 1 }], { next: { include: [{ task: 'T003', attempt: 1, summary: 'Create docs/z.md' }] } });
  assert.equal(result.ok, true);
  assert.deepEqual(result.outcomes.map((outcome) => [outcome.key, outcome.landed]), [['T001', true], ['T002', true]]);
  assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# X\n');
  assert.equal(remoteShow(repos, 'speckit/f:docs/y.md'), '# Y\n');
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[X\] T001 [^\n]*\n- \[x\] T002[^\n]*\n- \[ \] T003/);
  assert.match(remoteHead(repos), /\|feat\(f\): T002 \[P\] Create docs\/y\.md$/, 'the later task is rebuilt on the earlier one');
  const comments = github.comments.filter((comment) => /implemented\*\*/.test(comment.body));
  assert.deepEqual(comments.map((comment) => comment.body.split('\n')[0]), [
    '<!-- speckit-implement:attempt {"step":"task","task":"T001","attempt":1,"outcome":"success"} -->',
    '<!-- speckit-implement:attempt {"step":"task","task":"T002","attempt":1,"outcome":"success"} -->',
  ]);
  assert.deepEqual(result.checks, { T001: 1, T002: 2 }, 'the first attempt took over the queued progress check run');
  const reported = github.repo.checkRuns.filter((check) => [result.checks.T001, result.checks.T002].includes(check.id));
  assert.deepEqual(reported.map((check) => [check.status, check.conclusion]), [['completed', 'success'], ['completed', 'success']], 'the stage reports through the check runs decide created');
  const next = github.repo.checkRuns.find((check) => check.external_id === 'speckit:attempt:task:T003:1');
  assert.deepEqual([next.status, next.output.title, next.output.summary], ['in_progress', 'T003 attempt 1 in progress', 'Create docs/z.md']);
  assert.match(result.output, /^ok=true\nhead=sha-head\n$/);
  assert.equal(result.exitCode, 0);
}));

test('stage: a sibling that changed the same files is redone, and the chain stops after the stage', () => withRepos({ tasks: PARALLEL_TASKS }, async (repos) => {
  workStageTask(repos, 'T001', (dir) => agentDoesT001(dir));
  workStageTask(repos, 'T002', siblingDoesT002({ 'docs/x.md': '# Sibling\n' }));
  const github = fakeGitHub();
  const result = await landStage(repos, github, [{ task: 'T001', attempt: 1 }, { task: 'T002', attempt: 1 }], { next: { include: [{ task: 'T003', attempt: 1 }] } });
  assert.equal(result.ok, false);
  assert.deepEqual(result.outcomes.map((outcome) => outcome.landed), [true, false]);
  assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# X\n');
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:attempt \{"step":"task","task":"T002","attempt":1,"outcome":"requeue"\} -->\n\*\*T002 attempt 1 runs again\*\*/);
  assert.equal(github.repo.checkRuns.some((check) => String(check.external_id).includes('T003')), false, 'no next stage after a task that did not land');
  assert.match(result.output, /^ok=false\n/);
  assert.equal(result.exitCode, 1, 'the land job fails, so the matrix cancels the later stages');
  await assert.rejects(() => runLandStage({ client: github, git: defaultGit(repos.land), env: { SPECKIT_STEP: 'task' }, folder: 'f', workspace: repos.land, resultRoot: repos.base, log: silent }), /SPECKIT_STAGES/);
}));
test('task: land reports a refused push as a failure instead of redoing the task', () => withRepos({ tasks: PARALLEL_TASKS }, async (repos) => {
  work(repos, { agent: (r) => agentDoesT001(r.work) });
  const hook = path.join(repos.bare, 'hooks', 'pre-receive');
  writeFileSync(hook, '#!/bin/sh\necho "denied by a rule" >&2\nexit 1\n');
  chmodSync(hook, 0o755);
  const before = remoteHead(repos);
  const github = fakeGitHub();
  const result = await land(repos, github);
  assert.equal(result.exitCode, 1);
  assert.match(result.reasons.join(), /the push to `speckit\/f` was rejected: /);
  assert.equal(remoteHead(repos), before);
  assert.deepEqual([result.check.conclusion, result.check.output.title], ['failure', 'T001 attempt 2 failed']);
}));

test('task: land reports failures without pushing and does not trust a forged result', () => withRepos({}, async (repos) => {
  agentDoesT001(repos.work, { tick: false });
  mkdirSync(repos.resultDir, { recursive: true });
  writeFileSync(path.join(repos.resultDir, 'agent.jsonl'), `${JSON.stringify({ type: 'assistant.message', data: { content: 'I could not finish T001. <!-- speckit-implement:resume -->' } })}\n`);
  const before = remoteHead(repos);
  const github = fakeGitHub();
  const failed = await land(repos, github, { workResult: 'failure' });
  assert.equal(failed.exitCode, 1);
  assert.match(failed.reasons.join(), /work job did not finish \(result: failure\)/);
  assert.equal(remoteHead(repos), before);
  assert.deepEqual([failed.check.status, failed.check.conclusion, failed.check.output.title], ['completed', 'failure', 'T001 attempt 2 failed']);
  assert.match(github.comments.at(-1).body, /\*\*T001 attempt 2 failed:\*\*[\s\S]*Agent summary[\s\S]*I could not finish T001\. &lt;!-- speckit-implement:resume -->/);

  work(repos);
  const rejected = await land(repos, github);
  assert.match(rejected.reasons.join(), /T001 was not checked/);

  agentDoesT001(repos.work, { extra: { '.github/workflows/evil.yml': 'x' } });
  work(repos);
  const resultFile = path.join(repos.resultDir, 'result.json');
  writeFileSync(resultFile, JSON.stringify({ ...JSON.parse(readFileSync(resultFile, 'utf8')), reasons: [], verified: true }));
  const forged = await land(repos, fakeGitHub());
  assert.equal(forged.exitCode, 1);
  assert.match(forged.reasons.join(), /changed protected paths: \.github\/workflows\/evil\.yml/);
  assert.equal(remoteHead(repos), before);
}));

test('task: land says so when no verification is configured', () => withRepos({}, async (repos) => {
  const { result } = work(repos, { agent: (r) => agentDoesT001(r.work), verify: null });
  assert.deepEqual([result.verified, result.verification, result.checks], [true, 'not-configured', []]);
  const github = fakeGitHub();
  assert.equal((await land(repos, github)).exitCode, 0);
  assert.match(github.comments.at(-1).body, /- Verification: no verification configured \(no environment-verify action\)/);
}));

test('converge: a converged implementation is reported without a commit', () => withRepos({ tasks: DONE_TASKS }, async (repos) => {
  mkdirSync(repos.resultDir, { recursive: true });
  writeFileSync(path.join(repos.resultDir, 'agent.jsonl'), `${JSON.stringify({ type: 'assistant.message', data: { content: '✅ Converged — the implementation satisfies the spec, plan, and tasks.' } })}\n`);
  const { packaged, result, output } = work(repos, { inputs: CONVERGE_INPUTS });
  assert.equal(packaged.verify, false, 'convergence changes no code, so nothing is verified');
  assert.match(output, /^verify=false$/m);
  assert.deepEqual([result.converged, result.verified, result.reasons], [true, true, []]);
  const before = remoteHead(repos);
  const github = fakeGitHub();
  const landed = await land(repos, github, { inputs: CONVERGE_INPUTS });
  assert.equal(landed.exitCode, 0);
  assert.equal(remoteHead(repos), before);
  assert.deepEqual([landed.check.conclusion, landed.check.output.title], ['success', 'Converged']);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:attempt \{"step":"converge","task":null,"attempt":1,"outcome":"success"\} -->\n\*\*Converged\*\* \(attempt 1\)[\s\S]*Convergence report[\s\S]*Converged — the implementation satisfies/);
}));

test('converge: appended tasks are committed and added to the pull request', () => withRepos({ tasks: DONE_TASKS }, async (repos) => {
  const { result } = work(repos, { inputs: CONVERGE_INPUTS, agent: (r) => appendConvergence(r.work, '- [ ] T003 Add the missing guard (FR-001)\n- [ ] T004 Cover SC-002\n') });
  assert.deepEqual([result.converged, result.verified, result.appended.map((task) => task.id)], [false, true, ['T003', 'T004']]);
  const github = fakeGitHub({ body: '## Tasks (2)\n\n- [x] T001 Create docs/x.md\n- [x] T002 Create docs/y.md\n' });
  const landed = await land(repos, github, { inputs: CONVERGE_INPUTS });
  assert.equal(landed.exitCode, 0);
  assert.match(remoteHead(repos), /\|chore\(f\): convergence round 1$/);
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /## Phase 2: Convergence\n\n- \[ \] T003 Add the missing guard \(FR-001\)\n- \[ \] T004 Cover SC-002/);
  assert.match(github.repo.pulls[0].body, /## Tasks \(4\)[\s\S]*### Phase 2: Convergence\n\n- \[ \] T003 Add the missing guard \(FR-001\)\n- \[ \] T004 Cover SC-002\n\n<!-- speckit-implement:tasks-end -->\n$/);
  assert.equal(landed.check.output.title, 'Convergence round 1: 2 task(s) appended');
  assert.equal(github.repo.checkRuns.at(-1).output.title, '2 of 4 tasks implemented');
  assert.match(github.comments.at(-1).body, /Convergence round 1 of at most 3\*\* found gaps; 2 task\(s\)[\s\S]*- T003 Add the missing guard/);
}));

test('converge: code changes are rejected, and the round limit asks the requester', async () => {
  await withRepos({ tasks: DONE_TASKS }, async (repos) => {
    const { result } = work(repos, { inputs: CONVERGE_INPUTS, agent: (r) => { appendConvergence(r.work); write(r.work, 'src/a.cs', 'x'); } });
    assert.match(result.reasons.join(), /may only change `specs\/f\/tasks\.md`, but it changed src\/a\.cs/);
    const landed = await land(repos, fakeGitHub(), { inputs: CONVERGE_INPUTS });
    assert.deepEqual([landed.exitCode, landed.check.conclusion, landed.check.external_id], [1, 'failure', CHECK_ATTEMPT]);
  });
  const three = `${DONE_TASKS}\n## Phase 2: Convergence\n\n- [x] T003 a\n\n## Phase 3: Convergence\n\n- [x] T004 b\n\n## Phase 4: Convergence\n\n- [x] T005 c\n`;
  await withRepos({ tasks: three }, async (repos) => {
    const before = remoteHead(repos);
    const { result } = work(repos, { inputs: CONVERGE_INPUTS, agent: (r) => write(r.work, 'specs/f/tasks.md', `${three}\n## Phase 5: Convergence\n\n- [ ] T006 d\n`) });
    assert.equal(result.limit, true);
    const github = fakeGitHub();
    const landed = await land(repos, github, { inputs: CONVERGE_INPUTS });
    assert.deepEqual([landed.exitCode, landed.check.conclusion, landed.check.external_id], [1, 'failure', CHECK_LIMIT]);
    assert.equal(remoteHead(repos), before);
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:attempt [^\n]*"outcome":"attention"\} -->\n\*\*Implementation needs attention\*\* @dev: convergence still found gaps after 3 rounds/);
    assert.match(github.comments.at(-1).body, /\*\*Next steps\*\*[\s\S]*A diagnosis starts automatically[\s\S]*\/speckit resume \[guidance\]/);
  });
});

const conflicting = {
  tasks: DONE_TASKS,
  branch: (dir) => write(dir, 'docs/shared.md', '# Shared\n\nbranch line\n'),
  main: (dir) => write(dir, 'docs/shared.md', '# Shared\n\nmain line\n'),
};
const resolveShared = (r) => write(r.work, 'docs/shared.md', '# Shared\n\nbranch line\nmain line\n');

test('merge: integrate merges the default branch and reports conflicts', async () => {
  await withRepos({ tasks: DONE_TASKS, main: (dir) => write(dir, 'docs/other.md', '# Other\n') }, (repos) => {
    const { integrated, result, output } = work(repos, { inputs: MERGE_INPUTS, verify: { outcome: 'success', checks: 'platform build and tests,Markdown check' } });
    assert.deepEqual(integrated.result.conflicts, []);
    assert.equal(integrated.result.mainSha, remoteSha(repos, 'main'));
    assert.equal(integrated.result.head, remoteSha(repos, 'speckit/f'));
    assert.match(output, /conflicts=false\nagent=false\nverify=true\nenvironment=false\nprompt=\n/);
    assert.equal(readFileSync(path.join(repos.work, 'docs/other.md'), 'utf8').replaceAll('\r\n', '\n'), '# Other\n', 'the verification sees the merged tree');
    assert.deepEqual([result.verified, result.checks.map((check) => check.name)], [true, ['platform build and tests', 'Markdown check']]);
  });
  await withRepos(conflicting, (repos) => {
    const { result, output } = work(repos, { inputs: MERGE_INPUTS });
    assert.deepEqual([result.conflicts, result.verified, result.reasons], [['docs/shared.md'], false, []]);
    assert.match(output, /conflicts=true\nagent=false\nverify=false\n/);
  });
});

test('resolve: the agent resolution is verified, replayed, and pushed as a merge commit', () => withRepos(conflicting, async (repos) => {
  const { integrated, result, output } = work(repos, { inputs: RESOLVE_INPUTS, agent: resolveShared, verify: { outcome: 'success', checks: 'platform build and tests' } });
  assert.match(output, /conflicts=true\nagent=true\nverify=false\nenvironment=false\nprompt=The working tree is in the middle of merging[^\n]*`docs\/shared\.md`/);
  assert.deepEqual(integrated.result.conflicts, ['docs/shared.md']);
  assert.deepEqual([result.verified, result.resolutions], [true, [{ path: 'docs/shared.md', deleted: false, blob: '0' }]]);

  const mainSha = remoteSha(repos, 'main');
  const branchSha = remoteSha(repos, 'speckit/f');
  const github = fakeGitHub();
  const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
  assert.equal(landed.exitCode, 0);
  const [head, parents] = remoteHead(repos).split('|')[0].split(/ (.*)/);
  assert.equal(head, landed.head);
  assert.equal(parents, `${branchSha} ${mainSha}`);
  assert.match(remoteHead(repos), /\|Merge main into speckit\/f$/);
  assert.equal(remoteShow(repos, 'speckit/f:docs/shared.md'), '# Shared\n\nbranch line\nmain line\n');
  assert.deepEqual([landed.check.conclusion, landed.check.output.title], ['success', 'Merge conflicts resolved']);
  assert.match(github.comments.at(-1).body, /\*\*Conflicts resolved\*\* \(attempt 1\): `docs\/shared\.md`[\s\S]*platform build and tests passed[\s\S]*Next: merge/);
}));

test('resolve: markers, changes outside the conflicts, and forged resolutions are rejected', async () => {
  await withRepos(conflicting, (repos) => {
    const { result } = work(repos, { inputs: RESOLVE_INPUTS, agent: () => {} });
    assert.match(result.reasons.join(), /`docs\/shared\.md` still contains conflict markers/);
  });
  await withRepos(conflicting, (repos) => {
    const { result } = work(repos, { inputs: RESOLVE_INPUTS, agent: (r) => { resolveShared(r); write(r.work, 'README.md', '# Changed\n'); write(r.work, 'new.txt', 'x'); } });
    assert.match(result.reasons.join(), /changed files outside the conflicts: README\.md, new\.txt/);
  });
  await withRepos(conflicting, async (repos) => {
    work(repos, { inputs: RESOLVE_INPUTS, agent: resolveShared });
    writeFileSync(path.join(repos.resultDir, 'resolved', '0'), '<<<<<<< HEAD\nforged\n=======\nx\n>>>>>>> main\n');
    const before = remoteHead(repos);
    const landed = await land(repos, fakeGitHub(), { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 1);
    assert.match(landed.reasons.join(), /still contains conflict markers/);
    assert.equal(remoteHead(repos), before);
  });
  await withRepos(conflicting, async (repos) => {
    work(repos, { inputs: RESOLVE_INPUTS, agent: resolveShared });
    const seed = path.join(repos.base, 'seed');
    write(seed, '.github/scripts/evil.mjs', 'x');
    git(seed, 'add', '-A');
    git(seed, 'commit', '-q', '-m', 'unreviewed');
    git(seed, 'push', '-q', repos.bare, 'HEAD:refs/heads/other');
    git(repos.land, 'fetch', '-q', 'origin');
    const resultFile = path.join(repos.resultDir, 'result.json');
    writeFileSync(resultFile, JSON.stringify({ ...JSON.parse(readFileSync(resultFile, 'utf8')), mainSha: remoteSha(repos, 'other') }));
    const before = remoteHead(repos);
    const landed = await land(repos, fakeGitHub(), { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 1);
    assert.match(landed.reasons.join(), /is not on the default branch main/);
    assert.equal(remoteHead(repos), before);
  });
});

const ENVIRONMENT_ACTION = '.github/actions/environment-setup/action.yml';

test('environment specs: a task may change the environment actions, but not together with other changes', async () => {
  await withRepos({}, async (repos) => {
    const environmentTask = (r) => {
      write(r.work, ENVIRONMENT_ACTION, 'name: x\n');
      write(r.work, 'specs/f/tasks.md', TASKS.replace('- [ ] T001', '- [X] T001'));
    };
    const { result } = work(repos, { agent: environmentTask, verify: { outcome: 'success', checks: '', uncovered: '' } });
    assert.deepEqual(result.reasons, []);
    const github = fakeGitHub();
    assert.equal((await land(repos, github)).exitCode, 0);
    assert.equal(remoteShow(repos, `speckit/f:${ENVIRONMENT_ACTION}`), 'name: x\n');
  });
  await withRepos({ branch: (dir) => write(dir, 'src/a.cs', 'class A {}\n') }, async (repos) => {
    const { result } = work(repos, { agent: (r) => agentDoesT001(r.work, { extra: { [ENVIRONMENT_ACTION]: 'name: x\n' } }) });
    assert.match(result.reasons.join(), /standalone environment spec, but the branch also changes docs\/x\.md, src\/a\.cs/);
    const landed = await land(repos, fakeGitHub());
    assert.equal(landed.exitCode, 1, 'the land job re-checks the whole branch');
    assert.match(landed.reasons.join(), /standalone environment spec/);
  });
});

test('task comments list changed files that no check covers', () => withRepos({}, async (repos) => {
  const { result } = work(repos, { agent: (r) => agentDoesT001(r.work, { extra: { 'src/clients/app.ts': 'x' } }), verify: { outcome: 'success', checks: 'Markdown check', uncovered: 'src/clients/app.ts' } });
  assert.deepEqual(result.uncovered, ['src/clients/app.ts']);
  const github = fakeGitHub();
  assert.equal((await land(repos, github)).exitCode, 0);
  assert.match(github.comments.at(-1).body, /- Not covered by any check: `src\/clients\/app\.ts`/);
}));

test('merge: integrate reports the changed paths and environment changes; the verdict records the self-test', async () => {
  await withRepos({ tasks: DONE_TASKS, branch: (dir) => write(dir, ENVIRONMENT_ACTION, 'name: x\n') }, (repos) => {
    const { integrated, result, output } = work(repos, { inputs: MERGE_INPUTS, verify: { outcome: 'success', checks: 'Markdown check', selfTest: 'success' } });
    assert.deepEqual(integrated.result.changedFiles, [ENVIRONMENT_ACTION]);
    assert.equal(readFileSync(path.join(repos.resultDir, 'changed-files.txt'), 'utf8'), `${ENVIRONMENT_ACTION}\n`);
    assert.match(output, /environment=true/);
    assert.deepEqual([result.verified, result.selfTest], [true, 'success']);
  });
  await withRepos({ tasks: DONE_TASKS, branch: (dir) => write(dir, ENVIRONMENT_ACTION, 'name: x\n') }, (repos) => {
    const { result } = work(repos, { inputs: MERGE_INPUTS, verify: { outcome: 'success', checks: 'Markdown check', selfTest: 'failure' } });
    assert.equal(result.verified, false);
    assert.match(result.reasons.join(), /changed environment actions failed their self-test \(failure\)/);
  });
});

test('package rejects agents that commit their changes', () => withRepos({ tasks: DONE_TASKS }, (repos) => {
  const head = remoteSha(repos, 'speckit/f');
  mkdirSync(repos.resultDir, { recursive: true });
  writeFileSync(path.join(repos.resultDir, 'agent-exit.txt'), '0\n');
  appendConvergence(repos.work);
  git(repos.work, 'commit', '-q', '-am', 'agent commit');
  const { result } = runPackage({ git: defaultGit(repos.work), env: { SPECKIT_HEAD: head }, inputs: CONVERGE_INPUTS, folder: 'f', workspace: repos.work, resultDir: repos.resultDir, log: silent });
  assert.deepEqual([result.converged, result.verified], [false, false]);
  assert.match(result.reasons.join(), /the agent created commits/);
}));

test('resolve: conflicts in protected paths ask the requester', () => withRepos({
  tasks: DONE_TASKS,
  branch: (dir) => write(dir, 'specs/f/spec.md', '# Spec\n\nbranch\n'),
  main: (dir) => write(dir, 'specs/f/spec.md', '# Spec\n\nmain\n'),
}, async (repos) => {
  const { integrated, output } = work(repos, { inputs: RESOLVE_INPUTS });
  assert.match(output, /agent=false/);
  assert.equal(integrated.result.attention, true);
  const github = fakeGitHub();
  const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
  assert.deepEqual([landed.exitCode, landed.check.external_id], [1, CHECK_LIMIT]);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:attempt [^\n]*"outcome":"attention"\} -->\n\*\*Implementation needs attention\*\* @dev: conflicts in protected paths need a person: specs\/f\/spec\.md/);
  assert.doesNotMatch(github.comments.at(-1).body, /Spec Kit orchestrate` workflow manually/);
  assert.match(github.comments.at(-1).body, /\/speckit diagnose|A diagnosis starts automatically/);
}));

// merge-land only reads the result of merge-verify and talks to the GitHub API.
async function mergeLand(github, result, { env = {}, workResult = 'success' } = {}) {
  const resultDir = mkdtempSync(path.join(tmpdir(), 'speckit-merge-'));
  try {
    if (result) writeFileSync(path.join(resultDir, 'result.json'), JSON.stringify(result));
    const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-head', status: 'in_progress', external_id: CHECK_MERGE });
    const landed = await runMergeLand({ client: github, env: { SPECKIT_BRANCH: 'main', ...env }, inputs: { ...MERGE_INPUTS, checkRun: check.id, folder: 'f' }, resultDir, workResult, log: silent });
    return { ...landed, check: github.repo.checkRuns.find((run) => run.id === check.id) };
  } finally {
    rmSync(resultDir, { recursive: true, force: true });
  }
}

const verifiedMerge = { step: 'merge', mainSha: 'sha-main', head: 'sha-head', conflicts: [], reasons: [], checks: [{ name: 'platform build and tests', passed: true }, { name: 'Markdown check', passed: true }], verification: 'configured', verified: true };

function mergeableGitHub() {
  const github = fakeGitHub({ body: '## Tasks (2)\n\n- [x] T001 Create docs/x.md\n- [x] T002 Create docs/y.md\n\nCloses #5\n' });
  github.setFile('sha-head', 'specs/f/tasks.md', DONE_TASKS);
  return github;
}

test('merge-land squash-merges a verified implementation and completes the twin', async () => {
  const github = mergeableGitHub();
  github.repo.pulls[0].stack = { number: 3 };
  const landed = await mergeLand(github, verifiedMerge);
  assert.equal(landed.exitCode, 0);
  assert.deepEqual(github.unstacked, [3], 'a stacked implementation is unstacked before the merge');
  const [merge] = github.repo.merges;
  assert.deepEqual([merge.number, merge.sha, merge.merge_method, merge.commit_title], [9, 'sha-head', 'squash', 'F (#9)']);
  assert.match(merge.commit_message, /^- T001 Create docs\/x\.md\n- T002 Create docs\/y\.md\n\nImplemented with Spec Kit in #9\.\nCloses #5$/);
  assert.equal(github.repo.pulls[0].draft, false, 'the pull request is marked ready before the merge');
  assert.deepEqual([landed.check.status, landed.check.conclusion, landed.check.external_id], ['completed', 'success', CHECK_DONE]);
  assert.deepEqual([github.issues[0].state, github.issues[0].state_reason, github.issues[0].labels.map((label) => label.name)], ['closed', 'completed', [TWIN_LABEL, 'speckit:stage:implemented']]);
  assert.equal(github.repo.branches['speckit/f'], undefined);
  assert.deepEqual(github.repo.runs.map((run) => [run.workflow, run.inputs]), [['speckit-prepare.yml', { dry_run: 'false' }]]);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:done -->\n\*\*Merged into `main`\*\* as sha-\d+ @dev: all 2 tasks are implemented, the implementation converged, and the full verification passed \(platform build and tests, Markdown check\)\.$/);
});

test('merge-land waits when main moved, reports conflicts, and fails unverified or moved heads', async () => {
  const moved = mergeableGitHub();
  moved.repo.branches.main = 'sha-newer';
  const movedResult = await mergeLand(moved, verifiedMerge);
  assert.deepEqual([movedResult.exitCode, movedResult.moved, movedResult.check.conclusion, movedResult.check.external_id], [0, true, 'neutral', CHECK_MERGE]);
  assert.equal(moved.repo.merges.length, 0);

  const conflicts = mergeableGitHub();
  const conflicted = await mergeLand(conflicts, { ...verifiedMerge, conflicts: ['docs/shared.md'], verified: false, checks: [] });
  assert.deepEqual([conflicted.exitCode, conflicted.check.conclusion, conflicted.check.external_id], [0, 'neutral', CHECK_CONFLICT]);
  assert.match(conflicts.comments.at(-1).body, /Merge conflicts with `main`\*\* in `docs\/shared\.md`\. They are resolved next\./);

  for (const [name, result, reason] of [
    ['failed verification', { ...verifiedMerge, verified: false, reasons: ['Markdown check failed'] }, /Markdown check failed/],
    ['missing result', null, /merge verification did not finish \(result: failure\)/],
    ['moved head', { ...verifiedMerge, head: 'sha-older' }, /implementation branch moved during verification/],
  ]) {
    const github = mergeableGitHub();
    const failed = await mergeLand(github, result, { workResult: 'failure' });
    assert.equal(failed.exitCode, 1, name);
    assert.equal(failed.check.conclusion, 'failure', name);
    assert.match(github.comments.at(-1).body, reason, name);
    assert.equal(github.repo.merges.length, 0, name);
  }

  const refused = mergeableGitHub();
  refused.mergeRefusal = 'Pull Request is not mergeable';
  const refusal = await mergeLand(refused, verifiedMerge);
  assert.deepEqual([refusal.exitCode, refusal.check.conclusion], [1, 'failure']);
  assert.match(refused.comments.at(-1).body, /GitHub did not merge the pull request: Pull Request is not mergeable/);
});

test('merge-land asks for review when automatic merging is off', async () => {
  const github = mergeableGitHub();
  const landed = await mergeLand(github, { ...verifiedMerge, verification: 'not-configured', checks: [] }, { env: { SPECKIT_AUTO_MERGE: 'false' } });
  assert.equal(landed.exitCode, 0);
  assert.equal(github.repo.merges.length, 0);
  assert.deepEqual([landed.check.conclusion, landed.check.external_id], ['success', CHECK_DONE]);
  assert.equal(github.repo.pulls[0].draft, false);
  assert.deepEqual(github.repo.reviewRequests, [{ number: 9, reviewers: ['dev'] }]);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:done -->\n\*\*All 2 tasks are implemented\*\*, the implementation converged, and no verification is configured[\s\S]*- automatic merging is off \(`SPECKIT_AUTO_MERGE=false`\)[\s\S]*Review requested from @dev/);
});

test('merge-land holds environment changes and uncovered files for review, whatever the work result claims', async () => {
  const environment = mergeableGitHub();
  environment.repo.pullFiles[9] = ['specs/f/tasks.md', '.github/actions/environment-verify/action.yml'];
  const held = await mergeLand(environment, { ...verifiedMerge, environment: false });
  assert.deepEqual([held.exitCode, environment.repo.merges.length, held.check.external_id], [0, 0, CHECK_DONE]);
  assert.match(environment.comments.at(-1).body, /- it changes the environment actions \(`\.github\/actions\/environment-verify\/action\.yml`\), which every later implementation is verified with/);
  assert.deepEqual(environment.repo.reviewRequests, [{ number: 9, reviewers: ['dev'] }]);

  const uncovered = mergeableGitHub();
  const review = await mergeLand(uncovered, { ...verifiedMerge, uncovered: ['src/clients/app.ts'] });
  assert.deepEqual([review.exitCode, uncovered.repo.merges.length], [0, 0]);
  assert.match(uncovered.comments.at(-1).body, /- no check covers `src\/clients\/app\.ts`; extend the environment actions with a standalone environment spec/);

  const renamed = mergeableGitHub();
  renamed.repo.pullFiles[9] = [{ filename: 'docs/verify.yml', previous_filename: '.github/actions/environment-verify/action.yml' }];
  await mergeLand(renamed, verifiedMerge);
  assert.equal(renamed.repo.merges.length, 0, 'moving files out of the environment actions is held for review');

  const huge = mergeableGitHub();
  huge.repo.pullFiles[9] = Array.from({ length: 3000 }, (_, index) => `src/f${index}.cs`);
  await mergeLand(huge, verifiedMerge);
  assert.equal(huge.repo.merges.length, 0);
  assert.match(huge.comments.at(-1).body, /changes 3000 or more files/);
});

test('task checks fail closed when the default branch is not in the checkout', () => withRepos({}, (repos) => {
  git(repos.work, 'update-ref', '-d', 'refs/remotes/origin/main');
  const { result } = work(repos, { agent: (r) => agentDoesT001(r.work) });
  assert.match(result.reasons.join(), /cannot be compared with main, because main is not in the checkout/);
}));

test('extracts the last agent message and validates commands', async () => {
  const jsonl = [
    JSON.stringify({ type: 'assistant.message', data: { content: 'first' } }),
    'not json',
    JSON.stringify({ type: 'assistant.message', data: { content: ' last ' } }),
    JSON.stringify({ type: 'assistant.message', data: { content: '' } }),
  ].join('\n');
  assert.equal(lastAgentMessage(jsonl), 'last');
  const env = { SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_TASK: 'T001', SPECKIT_ATTEMPT: '1' };
  await assert.rejects(() => main(['nope'], { env }), /integrate \| package \| verdict \| land-stage \| merge-land/);
  await assert.rejects(() => main(['package'], { env: { ...env, SPECKIT_STEP: 'merge' } }), /merge step has no package command/);
});
