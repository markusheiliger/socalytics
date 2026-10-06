import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
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
  runPackage,
  runVerdict,
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

const TASK_INPUTS = { twin: 5, pull: 9, task: 'T001', attempt: 2, mode: 'task' };

// Runs "package", then simulates the environment-verify step, then "verdict", like the work job does.
function work(repos, { inputs = TASK_INPUTS, agentExit = '0', verify = { outcome: 'success', checks: 'Markdown check' } } = {}) {
  mkdirSync(repos.resultDir, { recursive: true });
  if (agentExit !== null) writeFileSync(path.join(repos.resultDir, 'agent-exit.txt'), `${agentExit}\n`);
  const outputFile = path.join(repos.resultDir, 'github-output.txt');
  const packaged = runPackage({ git: defaultGit(repos.work), env: { GITHUB_OUTPUT: outputFile }, inputs, folder: 'f', workspace: repos.work, resultDir: repos.resultDir, log: silent });
  const env = verify === null
    ? { SPECKIT_VERIFY_CONFIGURED: 'false', SPECKIT_VERIFY_OUTCOME: '' }
    : { SPECKIT_VERIFY_CONFIGURED: 'true', SPECKIT_VERIFY_OUTCOME: packaged.verify ? verify.outcome : 'skipped', SPECKIT_VERIFY_CHECKS: packaged.verify ? verify.checks : '' };
  const { result } = runVerdict({ env, inputs, resultDir: repos.resultDir, log: silent });
  return { packaged, result, output: readFileSync(outputFile, 'utf8') };
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

test('package writes the change and the changed paths, and verdict records the verification', () => {
  const repos = makeGitRepos();
  try {
    agentDoesT001(repos.work);
    const { packaged, result, output } = work(repos);
    assert.equal(packaged.verify, true);
    assert.match(output, /^verify=true$/m);
    assert.deepEqual(readFileSync(path.join(repos.resultDir, 'changed-files.txt'), 'utf8').split('\n').filter(Boolean).sort(), ['docs/x.md', 'specs/f/tasks.md']);
    assert.ok(readFileSync(path.join(repos.resultDir, 'changes.patch'), 'utf8').includes('docs/x.md'));
    assert.equal(result.verified, true);
    assert.equal(result.verification, 'configured');
    assert.deepEqual(result.checks, [{ name: 'Markdown check', passed: true }]);
    assert.deepEqual(result.changedFiles.sort(), ['docs/x.md', 'specs/f/tasks.md']);
    assert.deepEqual(JSON.parse(readFileSync(path.join(repos.resultDir, 'result.json'), 'utf8')).checks, result.checks);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('package rejects missing ticks, protected paths, and agent failures; verdict records failed verification', () => {
  const cases = [
    { name: 'no tick', agent: (r) => agentDoesT001(r.work, { tick: false }), reason: /T001 was not checked/, verify: false },
    { name: 'protected path', agent: (r) => agentDoesT001(r.work, { extra: { '.github/workflows/evil.yml': 'x' } }), reason: /changed protected paths: \.github\/workflows\/evil\.yml/, verify: false },
    { name: 'timeout', agent: (r) => agentDoesT001(r.work), agentExit: '124', reason: /did not finish within 60 minutes/, verify: false },
    { name: 'no agent', agent: () => {}, agentExit: null, reason: /the agent did not run/, verify: false },
    { name: 'failed check', agent: (r) => agentDoesT001(r.work), check: { outcome: 'failure', checks: 'platform build and tests,Markdown check' }, reason: /^Markdown check failed$/, verify: true },
    { name: 'crashed verification', agent: (r) => agentDoesT001(r.work), check: { outcome: 'failure', checks: '' }, reason: /the verification did not succeed \(failure\)/, verify: true },
  ];
  for (const testCase of cases) {
    const repos = makeGitRepos();
    try {
      testCase.agent(repos);
      const { packaged, result } = work(repos, { agentExit: testCase.agentExit === undefined ? '0' : testCase.agentExit, verify: testCase.check });
      assert.equal(packaged.verify, testCase.verify, testCase.name);
      assert.equal(result.verified, false, testCase.name);
      assert.match(result.reasons.join(), testCase.reason, testCase.name);
      if (testCase.name === 'failed check') {
        assert.deepEqual(result.checks, [{ name: 'platform build and tests', passed: true }, { name: 'Markdown check', passed: false }]);
      }
    } finally {
      rmSync(repos.base, { recursive: true, force: true });
    }
  }
});

test('verdict counts a change as unverified but accepted when no environment-verify action exists', () => {
  const repos = makeGitRepos();
  try {
    agentDoesT001(repos.work);
    const { result } = work(repos, { verify: null });
    assert.deepEqual([result.verified, result.verification, result.checks], [true, 'not-configured', []]);

    const missing = makeGitRepos();
    try {
      const { result: unpackaged } = runVerdict({ env: { SPECKIT_VERIFY_CONFIGURED: 'true', SPECKIT_VERIFY_OUTCOME: 'success' }, inputs: TASK_INPUTS, resultDir: missing.resultDir, log: silent });
      assert.deepEqual([unpackaged.verified, unpackaged.reasons], [false, ['the change was not packaged']]);
    } finally {
      rmSync(missing.base, { recursive: true, force: true });
    }
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('package hands finalize runs straight to the full verification', () => {
  const repos = makeGitRepos(TASKS.replaceAll('- [ ]', '- [x]'));
  try {
    const { packaged, result } = work(repos, { inputs: { ...TASK_INPUTS, task: 'finalize', mode: 'finalize' }, agentExit: null, verify: { outcome: 'success', checks: 'platform build and tests,Markdown check' } });
    assert.equal(packaged.verify, true);
    assert.equal(readFileSync(path.join(repos.resultDir, 'changed-files.txt'), 'utf8'), '');
    assert.equal(result.verified, true);
    assert.deepEqual(result.checks.map((check) => check.name), ['platform build and tests', 'Markdown check']);
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
    work(repos, { inputs: { ...TASK_INPUTS, task: 'finalize', mode: 'finalize' }, agentExit: null, verify: { outcome: 'success', checks: 'platform build and tests,Markdown check' } });
    const github = fakeGitHub();
    const result = await land(repos, github, { inputs: { ...TASK_INPUTS, task: 'finalize', mode: 'finalize' } });
    assert.equal(result.exitCode, 0);
    assert.deepEqual([result.check.status, result.check.conclusion, result.check.external_id, result.check.output.title], ['completed', 'success', CHECK_DONE, '2 of 2 tasks implemented']);
    assert.equal(github.repo.pulls[0].draft, false);
    assert.deepEqual(github.repo.reviewRequests, [{ number: 9, reviewers: ['dev'] }]);
    assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:done -->\n\*\*All 2 tasks are implemented\*\* and the full verification passed \(platform build and tests, Markdown check\)[\s\S]*Review requested from @dev/);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
});

test('land says so when no verification is configured', async () => {
  const repos = makeGitRepos();
  try {
    agentDoesT001(repos.work);
    work(repos, { verify: null });
    const github = fakeGitHub();
    const result = await land(repos, github);
    assert.equal(result.exitCode, 0);
    assert.match(github.comments.at(-1).body, /- Verification: no verification configured \(no environment-verify action\)/);
  } finally {
    rmSync(repos.base, { recursive: true, force: true });
  }
  const done = makeGitRepos(TASKS.replaceAll('- [ ]', '- [x]'));
  try {
    const finalize = { ...TASK_INPUTS, task: 'finalize', mode: 'finalize' };
    work(done, { inputs: finalize, agentExit: null, verify: null });
    const github = fakeGitHub();
    const result = await land(done, github, { inputs: finalize });
    assert.equal(result.exitCode, 0);
    assert.match(result.check.output.summary, /no verification is configured/);
    assert.match(github.comments.at(-1).body, /\*\*All 2 tasks are implemented\*\*; no verification is configured \(no environment-verify action\)\./);
  } finally {
    rmSync(done.base, { recursive: true, force: true });
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
  await assert.rejects(() => main(['work'], { env: { SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_TASK: 'T001', SPECKIT_ATTEMPT: '1' } }), /begin \| package \| verdict \| land/);
});
