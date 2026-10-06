import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import test from 'node:test';

import { TWIN_LABEL, renderTwinBody } from './speckit-prepare-core.mjs';
import { CHECK_ATTEMPT, CHECK_DONE, CHECK_PROGRESS, CHECK_RUN_NAME } from './speckit-implement-core.mjs';
import {
  TaskInputError,
  defaultGit,
  lastAgentMessage,
  main,
  parseTaskInputs,
  runBegin,
  runLand,
  runWork,
} from './speckit-implement.mjs';
import { FakeGitHub, silent } from './speckit-test-helpers.mjs';

const IMPLEMENT = 'speckit:stage:implement';
const TASKS = '# Tasks\n\n## Phase 1\n\n- [ ] T001 Create docs/x.md\n- [ ] T002 Create docs/y.md\n';
const context = { serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' };

function git(cwd, ...args) {
  const result = spawnSync('git', ['-c', 'user.name=test', '-c', 'user.email=test@example.invalid', ...args], { cwd, encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`git ${args.join(' ')}: ${result.stderr}`);
  return result.stdout;
}

// A bare "remote" with branch speckit/f plus two clones: one for the work job and one for the land job.
function makeGitRepos(tasks = TASKS) {
  const base = mkdtempSync(path.join(tmpdir(), 'speckit-task-'));
  const remoteRoot = path.join(base, 'remote');
  const bare = path.join(remoteRoot, 'octo', 'repo.git');
  mkdirSync(bare, { recursive: true });
  const seed = path.join(base, 'seed');
  git(base, 'init', '-q', seed);
  git(bare, 'init', '-q', '--bare');
  mkdirSync(path.join(seed, 'specs', 'f'), { recursive: true });
  writeFileSync(path.join(seed, 'specs', 'f', 'tasks.md'), tasks);
  writeFileSync(path.join(seed, 'README.md'), '# Repo\n');
  git(seed, 'add', '-A');
  git(seed, 'commit', '-q', '-m', 'seed');
  git(seed, 'push', '-q', bare, 'HEAD:refs/heads/speckit/f');
  const work = path.join(base, 'work');
  const land = path.join(base, 'land');
  git(base, 'clone', '-q', '--branch', 'speckit/f', bare, work);
  git(base, 'clone', '-q', '--branch', 'speckit/f', bare, land);
  return { base, bare, remoteRoot, work, land, resultDir: path.join(base, 'result') };
}

function agentDoesT001(work, { tick = true, extra = {} } = {}) {
  mkdirSync(path.join(work, 'docs'), { recursive: true });
  writeFileSync(path.join(work, 'docs', 'x.md'), '# X\n');
  if (tick) {
    const file = path.join(work, 'specs', 'f', 'tasks.md');
    writeFileSync(file, readFileSync(file, 'utf8').replace('- [ ] T001', '- [X] T001'));
  }
  for (const [file, content] of Object.entries(extra)) {
    mkdirSync(path.dirname(path.join(work, file)), { recursive: true });
    writeFileSync(path.join(work, file), content);
  }
}

function fakeRunner(status = 0) {
  const calls = [];
  const run = (command, args, options) => {
    calls.push({ command, args, options });
    return { status, output: status === 0 ? 'ok' : 'boom: something failed' };
  };
  run.calls = calls;
  return run;
}

const TASK_INPUTS = { twin: 5, pull: 9, task: 'T001', attempt: 2, mode: 'task' };

function work(repos, { inputs = TASK_INPUTS, agentExit = '0', run = fakeRunner(), env = {} } = {}) {
  mkdirSync(repos.resultDir, { recursive: true });
  if (agentExit !== null) writeFileSync(path.join(repos.resultDir, 'agent-exit.txt'), `${agentExit}\n`);
  return runWork({ git: defaultGit(repos.work), run, env, inputs, folder: 'f', workspace: repos.work, resultDir: repos.resultDir, toolingDir: path.join(repos.base, 'tooling'), log: silent });
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
  return { GITHUB_SERVER_URL: pathToFileURL(repos.remoteRoot).href, GITHUB_REPOSITORY: 'octo/repo', GITHUB_TOKEN: 'token' };
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

const remoteHead = (repos) => git(repos.base, `--git-dir=${repos.bare}`, 'log', '-1', '--format=%H %s', 'speckit/f').trim();
const remoteShow = (repos, spec) => git(repos.base, `--git-dir=${repos.bare}`, 'show', spec);

test('parses and validates task inputs', () => {
  assert.deepEqual(parseTaskInputs({ SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_TASK: 'T001', SPECKIT_ATTEMPT: '2' }), TASK_INPUTS);
  assert.equal(parseTaskInputs({ SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_TASK: 'finalize', SPECKIT_ATTEMPT: '1' }).mode, 'finalize');
  for (const env of [{}, { SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_TASK: 'rm -rf', SPECKIT_ATTEMPT: '1' }, { SPECKIT_TWIN: '0', SPECKIT_PULL: '9', SPECKIT_TASK: 'T001', SPECKIT_ATTEMPT: '1' }]) {
    assert.throws(() => parseTaskInputs(env), TaskInputError);
  }
});

test('begin marks the attempt in progress when the task is the next step', async () => {
  const github = fakeGitHub();
  github.setFile('sha-head', 'specs/f/tasks.md', TASKS);
  const outputFile = path.join(mkdtempSync(path.join(tmpdir(), 'speckit-out-')), 'out.txt');
  const env = { GITHUB_REPOSITORY: 'octo/repo', GITHUB_OUTPUT: outputFile };
  const result = await runBegin({ client: github, env, inputs: TASK_INPUTS, log: silent });
  assert.equal(result.proceed, true);
  const check = github.repo.checkRuns.at(-1);
  assert.deepEqual([check.status, check.external_id, check.head_sha, check.output.title], ['in_progress', CHECK_ATTEMPT, 'sha-head', 'T001 attempt 2 in progress']);
  const output = readFileSync(outputFile, 'utf8');
  assert.match(output, /proceed=true\nfolder=f\nhead=sha-head\ncheck_run=\d+\nmode=task\nprompt=\/speckit-implement Implement only task T001\./);
  rmSync(path.dirname(outputFile), { recursive: true, force: true });
});

test('begin takes over the queued progress check run of the head', async () => {
  const github = fakeGitHub();
  github.setFile('sha-head', 'specs/f/tasks.md', TASKS);
  const progress = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-head', status: 'queued', external_id: CHECK_PROGRESS });
  const result = await runBegin({ client: github, env: { GITHUB_REPOSITORY: 'octo/repo' }, inputs: TASK_INPUTS, log: silent });
  assert.equal(result.check.id, progress.id);
  assert.equal(github.repo.checkRuns.length, 1);
  assert.deepEqual([github.repo.checkRuns[0].status, github.repo.checkRuns[0].external_id], ['in_progress', CHECK_ATTEMPT]);
});

test('begin does nothing when the inputs no longer match the state', async () => {
  const cases = [
    { name: 'wrong task', setup: (github) => github.setFile('sha-head', 'specs/f/tasks.md', TASKS.replace('- [ ] T001', '- [x] T001')), reason: /next step is T002/ },
    { name: 'not flagged', setup: (github) => { github.issues[0].labels = [{ name: TWIN_LABEL }]; github.setFile('sha-head', 'specs/f/tasks.md', TASKS); }, reason: /not open and flagged/ },
    { name: 'closed pull', setup: (github) => { github.repo.pulls[0].state = 'closed'; github.setFile('sha-head', 'specs/f/tasks.md', TASKS); }, reason: /not the open implementation pull request/ },
    { name: 'finalize too early', inputs: { ...TASK_INPUTS, task: 'finalize', mode: 'finalize' }, setup: (github) => github.setFile('sha-head', 'specs/f/tasks.md', TASKS), reason: /next step is T001, not finalize/ },
  ];
  for (const testCase of cases) {
    const github = fakeGitHub();
    testCase.setup(github);
    const result = await runBegin({ client: github, env: { GITHUB_REPOSITORY: 'octo/repo' }, inputs: testCase.inputs ?? TASK_INPUTS, log: silent });
    assert.equal(result.proceed, false, testCase.name);
    assert.match(result.reasons.join(), testCase.reason, testCase.name);
    assert.equal(github.repo.checkRuns.length, 0, testCase.name);
  }
});

test('work packages, validates, and verifies the agent change without tokens in the verification environment', () => {
  const repos = makeGitRepos();
  try {
    agentDoesT001(repos.work);
    const run = fakeRunner();
    const { result } = work(repos, { run, env: { PATH: process.env.PATH, GITHUB_TOKEN: 'secret', COPILOT_GITHUB_TOKEN: 'secret', ACTIONS_RUNTIME_TOKEN: 'secret' } });
    assert.equal(result.verified, true);
    assert.deepEqual(result.checks, [{ name: 'Markdown check', passed: true }]);
    assert.deepEqual(result.changedFiles.sort(), ['docs/x.md', 'specs/f/tasks.md']);
    assert.ok(readFileSync(path.join(repos.resultDir, 'changes.patch'), 'utf8').includes('docs/x.md'));
    assert.equal(run.calls.length, 1);
    assert.equal(run.calls[0].command, 'node');
    assert.match(run.calls[0].args[0], /tooling[\\/]\.github[\\/]scripts[\\/]check-markdown\.mjs$/);
    assert.equal(run.calls[0].options.cwd, repos.work);
    assert.deepEqual(Object.keys(run.calls[0].options.env), ['PATH']);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('work rejects missing ticks, protected paths, agent failures, and failed verification', () => {
  const cases = [
    { name: 'no tick', agent: (r) => agentDoesT001(r.work, { tick: false }), reason: /T001 was not checked/, runs: 0 },
    { name: 'protected path', agent: (r) => agentDoesT001(r.work, { extra: { '.github/workflows/evil.yml': 'x' } }), reason: /changed protected paths: \.github\/workflows\/evil\.yml/, runs: 0 },
    { name: 'timeout', agent: (r) => agentDoesT001(r.work), agentExit: '124', reason: /did not finish within 60 minutes/, runs: 0 },
    { name: 'no agent', agent: () => {}, agentExit: null, reason: /the agent did not run/, runs: 0 },
    { name: 'verification', agent: (r) => agentDoesT001(r.work), status: 1, reason: /Markdown check failed/, runs: 1 },
  ];
  for (const testCase of cases) {
    const repos = makeGitRepos();
    try {
      testCase.agent(repos);
      const run = fakeRunner(testCase.status ?? 0);
      const { result } = work(repos, { run, agentExit: testCase.agentExit === undefined ? '0' : testCase.agentExit });
      assert.equal(result.verified, false, testCase.name);
      assert.match(result.reasons.join(), testCase.reason, testCase.name);
      assert.equal(run.calls.length, testCase.runs, testCase.name);
      assert.ok(existsSync(path.join(repos.resultDir, 'result.json')), testCase.name);
    } finally {
      rmSync(repos.base, { recursive: true, force: true });
    }
  }
});

test('work runs the full verification in finalize mode', () => {
  const repos = makeGitRepos(TASKS.replaceAll('- [ ]', '- [x]'));
  try {
    const run = fakeRunner();
    const { result } = work(repos, { inputs: { ...TASK_INPUTS, task: 'finalize', mode: 'finalize' }, agentExit: null, run });
    assert.equal(result.verified, true);
    assert.deepEqual(result.checks.map((check) => check.name), ['platform build and tests', 'Markdown check']);
    assert.deepEqual(run.calls.map((call) => `${call.command} ${call.args[0]}`).slice(0, 3), ['dotnet restore', 'dotnet build', 'dotnet test']);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('land re-validates, commits, pushes, and reports a verified task', async () => {
  const repos = makeGitRepos();
  try {
    agentDoesT001(repos.work);
    work(repos);
    const github = fakeGitHub();
    const result = await land(repos, github);
    assert.equal(result.exitCode, 0);
    assert.match(remoteHead(repos), /^[0-9a-f]{40} feat\(f\): T001 Create docs\/x\.md$/);
    assert.equal(remoteHead(repos).split(' ')[0], result.head);
    assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# X\n');
    assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[X\] T001/);
    assert.match(github.repo.pulls[0].body, /- \[x\] T001 Create docs\/x\.md\n- \[ \] T002/);
    assert.deepEqual([result.check.status, result.check.conclusion, result.check.output.title], ['completed', 'success', 'T001 implemented']);
    const progress = github.repo.checkRuns.at(-1);
    assert.deepEqual([progress.status, progress.external_id, progress.head_sha, progress.output.title], ['queued', CHECK_PROGRESS, result.head, '1 of 2 tasks implemented']);
    assert.match(github.comments.at(-1).body, /\*\*T001 implemented\*\* \(attempt 2\)[\s\S]*`docs\/x\.md`[\s\S]*Markdown check passed[\s\S]*1 of 2 tasks/);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('land reports failures without pushing', async () => {
  const repos = makeGitRepos();
  try {
    agentDoesT001(repos.work, { tick: false });
    mkdirSync(repos.resultDir, { recursive: true });
    writeFileSync(path.join(repos.resultDir, 'agent.jsonl'), `${JSON.stringify({ type: 'assistant.message', data: { content: 'I could not finish T001. <!-- speckit-implement:resume -->' } })}\n`);
    const before = remoteHead(repos);
    const github = fakeGitHub();
    const result = await land(repos, github, { workResult: 'failure' });
    assert.equal(result.exitCode, 1);
    assert.match(result.reasons.join(), /work job did not finish \(result: failure\)/);
    assert.equal(remoteHead(repos), before);
    assert.deepEqual([result.check.status, result.check.conclusion, result.check.output.title], ['completed', 'failure', 'T001 attempt 2 failed']);
    assert.match(github.comments.at(-1).body, /\*\*T001 attempt 2 failed:\*\*[\s\S]*Agent summary[\s\S]*I could not finish T001\. &lt;!-- speckit-implement:resume -->/);

    work(repos);
    const rejected = await land(repos, github);
    assert.equal(rejected.exitCode, 1);
    assert.match(rejected.reasons.join(), /T001 was not checked/);
    assert.equal(remoteHead(repos), before);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('land does not trust a forged verification result', async () => {
  const repos = makeGitRepos();
  try {
    agentDoesT001(repos.work, { extra: { '.github/workflows/evil.yml': 'x' } });
    work(repos);
    const resultFile = path.join(repos.resultDir, 'result.json');
    writeFileSync(resultFile, JSON.stringify({ ...JSON.parse(readFileSync(resultFile, 'utf8')), reasons: [], verified: true }));
    const before = remoteHead(repos);
    const result = await land(repos, fakeGitHub());
    assert.equal(result.exitCode, 1);
    assert.match(result.reasons.join(), /changed protected paths: \.github\/workflows\/evil\.yml/);
    assert.equal(remoteHead(repos), before);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('land finalizes: success check run, ready for review, and a review request', async () => {
  const repos = makeGitRepos(TASKS.replaceAll('- [ ]', '- [x]'));
  try {
    work(repos, { inputs: { ...TASK_INPUTS, task: 'finalize', mode: 'finalize' }, agentExit: null });
    const github = fakeGitHub();
    const result = await land(repos, github, { inputs: { ...TASK_INPUTS, task: 'finalize', mode: 'finalize' } });
    assert.equal(result.exitCode, 0);
    assert.deepEqual([result.check.status, result.check.conclusion, result.check.external_id, result.check.output.title], ['completed', 'success', CHECK_DONE, '2 of 2 tasks implemented']);
    assert.equal(github.repo.pulls[0].draft, false);
    assert.deepEqual(github.repo.reviewRequests, [{ number: 9, reviewers: ['dev'] }]);
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:done -->\n\*\*All 2 tasks are implemented\*\*[\s\S]*Review requested from @dev/);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('extracts the last agent message and validates commands', async () => {
  const jsonl = [
    JSON.stringify({ type: 'assistant.message', data: { content: 'first' } }),
    'not json',
    JSON.stringify({ type: 'assistant.message', data: { content: ' last ' } }),
    JSON.stringify({ type: 'assistant.message', data: { content: '' } }),
  ].join('\n');
  assert.equal(lastAgentMessage(jsonl), 'last');
  await assert.rejects(() => main(['nope'], { env: { SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_TASK: 'T001', SPECKIT_ATTEMPT: '1' } }), TaskInputError);
});
