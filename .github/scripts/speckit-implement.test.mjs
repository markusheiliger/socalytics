import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import test from 'node:test';

import { TWIN_LABEL, renderTwinBody } from './speckit-prepare-core.mjs';
import { archiveSpec, defaultGit as archiveGit } from './speckit-archive.mjs';
import { CHECK_ATTEMPT, CHECK_CONFLICT, CHECK_DONE, CHECK_LIMIT, CHECK_MERGE, CHECK_PROGRESS, CHECK_RUN_NAME, CHECK_SYNC_CONFLICT, baseOwnedChanges, pointSpecLinks, renderBaseOwnedMarker, renderGuidanceMarker, renderRepairMarker, renderSyncComment, syncBarrier, taskNamedFiles, unresolvedRepair } from './speckit-implement-core.mjs';
import {
  TaskInputError,
  combinedGroupEdits,
  defaultGit,
  lastAgentMessage,
  main,
  parseTaskInputs,
  runArchive,
  runLandTasks,
  runCombinedVerified,
  startAttemptChecks,
  runIntegrate,
  runLand,
  doneByPush,
  runMergeLand,
  runPackage,
  runRepairReport,
  runVerdict,
} from './speckit-implement.mjs';
import { main as convergeMain } from './speckit-converge.mjs';
import { main as completionMain, taskDigest } from './speckit-completion.mjs';
import { main as groupMain } from './speckit-group.mjs';
import { main as mergeMain } from './speckit-merge.mjs';
import { main as repairMain, repairVerificationScope } from './speckit-repair.mjs';
import { main as resolveMain } from './speckit-resolve.mjs';
import { FakeGitHub, silent } from './speckit-test-helpers.mjs';

const IMPLEMENT = 'speckit:stage:implement';
const TASKS = '# Tasks\n\n## Phase 1\n\n- [ ] T001 Create docs/x.md\n- [ ] T002 Create docs/y.md\n';
const DONE_TASKS = TASKS.replaceAll('- [ ]', '- [x]');
const context = { serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' };
const TASK_INPUTS = { kind: 'task', twin: 5, pull: 9, task: 'T001', attempt: 2 };
const CONVERGE_INPUTS = { kind: 'converge', twin: 5, pull: 9, task: null, attempt: 1 };
const RESOLVE_INPUTS = { kind: 'resolve', twin: 5, pull: 9, task: null, attempt: 1 };
const MERGE_INPUTS = { kind: 'merge', twin: 5, pull: 9, task: null, attempt: 1 };
const REPAIR_INPUTS = { kind: 'repair', twin: 5, pull: 9, task: null, attempt: 1 };

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
function makeGitRepos(tasks = TASKS, { branch, main: mainChange, seed: seedChange } = {}) {
  const base = mkdtempSync(path.join(tmpdir(), 'speckit-task-'));
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
  seedChange?.(seed);
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

// Runs the work job of a kind: integrate (resolve, merge), the simulated agent, package (task, converge, resolve),
// `between` (the steps after package, for example the trusted runner of a completion without a change), and verdict
// with a simulated environment-verify outcome (`verify: null` means no verify action).
function work(repos, { inputs = TASK_INPUTS, agentExit = '0', agent, verify = { outcome: 'success', checks: 'Markdown check' }, env: extraEnv = {}, between } = {}) {
  mkdirSync(repos.resultDir, { recursive: true });
  const outputFile = path.join(repos.resultDir, 'github-output.txt');
  writeFileSync(outputFile, '');
  const env = { GITHUB_OUTPUT: outputFile, SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_BASE_BRANCH: 'main', ...extraEnv };
  const run = { git: defaultGit(repos.work), env, inputs, folder: 'f', workspace: repos.work, resultDir: repos.resultDir, log: silent };
  const integrated = ['resolve', 'merge'].includes(inputs.kind) ? runIntegrate(run) : null;
  agent?.(repos);
  if (agentExit !== null && inputs.kind !== 'merge') writeFileSync(path.join(repos.resultDir, 'agent-exit.txt'), `${agentExit}\n`);
  const packaged = inputs.kind === 'merge' ? null : runPackage(run);
  between?.(packaged);
  let result = packaged?.result ?? integrated.result;
  if (inputs.kind !== 'converge') {
    const ready = inputs.kind === 'merge' ? integrated.result.reasons.length === 0 && integrated.result.conflicts.length === 0 : packaged.verify;
    const verifyEnv = verify === null
      ? { SPECKIT_VERIFY_CONFIGURED: 'false' }
      : { SPECKIT_VERIFY_CONFIGURED: 'true', SPECKIT_VERIFY_OUTCOME: ready ? verify.outcome : 'skipped', SPECKIT_VERIFY_CHECKS: ready ? verify.checks : '', SPECKIT_VERIFY_UNCOVERED: ready ? verify.uncovered ?? '' : '', SPECKIT_SELFTEST_OUTCOME: verify.selfTest ?? '' };
    result = runVerdict({ env: { ...extraEnv, ...verifyEnv, ...(inputs.kind === 'resolve' ? { SPECKIT_VERIFY_MODE: 'finalize' } : {}) }, inputs, resultDir: repos.resultDir, log: silent }).result;
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
  github.repo.pulls.push({ number: 9, node_id: 'PR_9', title: 'Implement: F', body, draft: true, state: 'open', base: { ref: 'main' }, head: { ref: 'speckit/f', sha: 'sha-head', repo: { full_name: 'octo/repo' } }, created_at: github.tick() });
  return github;
}

function landEnv(repos) {
  return { GITHUB_SERVER_URL: pathToFileURL(repos.remoteRoot).href, GITHUB_REPOSITORY: 'octo/repo', GITHUB_TOKEN: 'token', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_BASE_BRANCH: 'main' };
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

test('taskNamedFiles keeps distinct existing safe paths in first-mention order and respects its cap', () => {
  const existing = new Set(['docs/a.md', 'src/b.ts', 'unsafe.txt', 'src/c.ts', '../secret.md']);
  const text = 'Use `docs/a.md`, then src/b.ts and `docs/a.md`; unsafe.txt, Missing.md, `../secret.md`, and `src/c.ts`.';
  const options = {
    exists: (file) => existing.has(file),
    isSafe: (file) => !file.startsWith('../') && file !== 'unsafe.txt',
  };
  assert.deepEqual(taskNamedFiles(text, { ...options, max: 2 }), ['docs/a.md', 'src/b.ts']);
  assert.deepEqual(taskNamedFiles(text, { ...options, max: 10 }), ['docs/a.md', 'src/b.ts', 'src/c.ts']);
  assert.deepEqual(taskNamedFiles(text, { ...options, max: 0 }), []);
});

test('task: package selects named files for unchanged verification without reporting changes', () => withRepos({
  branch: (dir) => write(dir, 'docs/x.md', '# Existing task work\n'),
}, (repos) => {
  const head = git(repos.work, 'rev-parse', 'HEAD').trim();
  const { packaged, result } = work(repos, {
    env: { SPECKIT_HEAD: head },
    agent: () => {},
    verify: { outcome: 'success', checks: 'platform build and tests' },
  });
  assert.equal(packaged.result.patchBytes, 0);
  assert.deepEqual(result.changedFiles, []);
  assert.deepEqual(result.taskFiles, ['docs/x.md']);
  assert.equal(readFileSync(path.join(repos.resultDir, 'changed-files.txt'), 'utf8'), 'docs/x.md\n');
  assert.deepEqual(result.unchanged, {
    status: 'passed',
    head,
    checks: ['platform build and tests'],
    uncovered: [],
    files: ['docs/x.md'],
  });
  assert.deepEqual([result.reasons, result.verified], [[], false]);
}));

test('task: unchanged verification records failed and not-run outcomes without changing verdict state', () => withRepos({
  branch: (dir) => write(dir, 'docs/x.md', '# Existing task work\n'),
}, (repos) => {
  for (const [verify, expected] of [
    [{ outcome: 'failure', checks: 'platform build and tests' }, 'failed'],
    [{ outcome: 'skipped', checks: '' }, 'not-run'],
  ]) {
    rmSync(repos.resultDir, { recursive: true, force: true });
    const head = git(repos.work, 'rev-parse', 'HEAD').trim();
    const { result } = work(repos, { env: { SPECKIT_HEAD: head }, agent: () => {}, verify });
    assert.equal(result.unchanged.status, expected);
    assert.deepEqual([result.reasons, result.verified], [[], false]);
  }
}));

test('task: an unchanged task passes only with current-head task-specific verification', () => withRepos({
  branch: (dir) => write(dir, 'docs/x.md', '# Existing task work\n'),
}, async (repos) => {
  const head = git(repos.work, 'rev-parse', 'HEAD').trim();
  const { result: verified } = work(repos, {
    env: { SPECKIT_HEAD: head },
    agent: () => {},
    verify: { outcome: 'success', checks: 'platform build and tests' },
  });
  const github = fakeGitHub();
  github.repo.branches['speckit/f'] = head;
  const landed = await land(repos, github);
  assert.equal(landed.exitCode, 0);
  assert.equal(verified.unchanged.status, 'passed');
  assert.equal(landed.check.output.title, 'T001 already satisfied');
  assert.match(github.comments.at(-1).body, /T001 already satisfied and ticked[\s\S]*verification passed on the unchanged branch/);
  assert.match(remoteHead(repos), /\|feat\(f\): T001 already satisfied$/);
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[x\] T001 .* — already satisfied by [0-9a-f]+ on the implementation branch \(verified; no change by this task\)/);
  assert.match(github.comments.at(-1).body, /Provenance: already satisfied by [0-9a-f]+ on the implementation branch/);
}));

test('task: an unchanged task whose work landed on the base branch since the implementation started is ticked with its provenance', async () => {
  for (const [created, accepted] of [['2000-01-01T00:00:00Z', true], [new Date(Date.now() + 3_600_000).toISOString(), false]]) {
    await withRepos({ main: (dir) => write(dir, 'docs/x.md', '# Landed on main\n') }, async (repos) => {
      // The implementation branch merged its base branch, which already holds the task's work.
      git(repos.base, `--git-dir=${repos.bare}`, 'update-ref', 'refs/heads/speckit/f', 'refs/heads/main');
      for (const dir of [repos.work, repos.land]) {
        git(dir, 'fetch', '-q', 'origin');
        git(dir, 'reset', '-q', '--hard', 'origin/speckit/f');
      }
      const head = git(repos.work, 'rev-parse', 'HEAD').trim();
      const landedOnMain = git(repos.work, 'log', '-1', '--format=%h', 'origin/main').trim();
      work(repos, { env: { SPECKIT_HEAD: head }, agent: () => {}, verify: { outcome: 'success', checks: 'platform build and tests' } });
      const github = fakeGitHub();
      github.repo.branches['speckit/f'] = head;
      github.repo.pulls.find((pull) => pull.number === 9).created_at = created;
      const landed = await land(repos, github);
      if (accepted) {
        assert.equal(landed.exitCode, 0);
        assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), new RegExp(`- \\[x\\] T001 .* — already satisfied by ${landedOnMain} on main \\(verified; no change by this task\\)`));
        assert.match(github.comments.at(-1).body, new RegExp(`Provenance: already satisfied by ${landedOnMain} on main`));
      } else {
        // Work that was on the base branch before the implementation started is not evidence for this task.
        assert.equal(landed.exitCode, 1);
        assert.match(landed.reasons.join('; '), /changed on main since the implementation started/);
        assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[ \] T001/);
      }
    });
  }
});

test('task: missing, uncovered, mismatched, and shared unchanged work fails without ticking', () => withRepos({
  tasks: `${TASKS}- [ ] T002 Reuse docs/x.md\n`,
  branch: (dir) => write(dir, 'docs/x.md', '# Existing task work\n'),
}, async (repos) => {
  const head = git(repos.work, 'rev-parse', 'HEAD').trim();
  for (const [verify, headNow, status, reason] of [
    [{ outcome: 'failure', checks: 'platform build and tests' }, head, 'failed', /verification of the unchanged branch failed/],
    [null, head, 'not-configured', /verification of the unchanged branch was not configured/],
    [{ outcome: 'success', checks: 'Markdown check' }, head, 'passed', /no test or build check covered/],
    [{ outcome: 'success', checks: 'platform build and tests' }, 'moved-head', 'passed', /ran on a different head/],
  ]) {
    rmSync(repos.resultDir, { recursive: true, force: true });
    const worked = work(repos, { env: { SPECKIT_HEAD: head }, agent: () => {}, verify });
    const github = fakeGitHub();
    github.repo.branches['speckit/f'] = headNow;
    const landed = await land(repos, github);
    assert.equal(landed.exitCode, 1);
    assert.match(landed.reasons.join('; '), reason);
    assert.equal(remoteSha(repos, 'speckit/f'), head);
    assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[ \] T001/);
    assert.equal(worked.result.unchanged.status, status);
  }
  rmSync(repos.resultDir, { recursive: true, force: true });
  work(repos, { env: { SPECKIT_HEAD: head }, agent: () => {}, verify: { outcome: 'success', checks: 'platform build and tests' } });
  const github = fakeGitHub({ body: '## Tasks (2)\n\n- [ ] T001 Create docs/x.md\n- [ ] T002 Reuse docs/x.md\n' });
  github.repo.branches['speckit/f'] = head;
  const landed = await land(repos, github);
  assert.equal(landed.exitCode, 1);
  assert.match(landed.reasons.join('; '), /no named file is specific to this task/);
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[ \] T001/);
}));

test('task: empty-patch land survives a moved branch without reapplying the patch', async () => {
  for (const mode of ['verified', 'doneByPush']) {
    await withRepos({
      branch: (dir) => write(dir, 'docs/x.md', '# Existing task work\n'),
    }, async (repos) => {
      const head = git(repos.work, 'rev-parse', 'HEAD').trim();
      work(repos, {
        env: { SPECKIT_HEAD: head },
        agent: () => {},
        verify: { outcome: 'success', checks: 'platform build and tests' },
      });
      const github = fakeGitHub();
      github.repo.branches['speckit/f'] = head;
      if (mode === 'doneByPush') {
        const at = new Date(Date.now() + 60_000).toISOString();
        github.repo.pullCommits = { 9: [
          { sha: 'human', author: { login: 'Copilot' }, committer: { login: 'web-flow' }, commit: { committer: { date: at } } },
        ] };
      }
      const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'in_progress', external_id: CHECK_ATTEMPT });
      const realGit = defaultGit(repos.land);
      let pushes = 0;
      let applies = 0;
      const racingGit = (args) => {
        if (args[0] === 'apply') applies += 1;
        if (args.includes('push') && ++pushes === 1) {
          const seed = path.join(repos.base, 'seed');
          write(seed, 'docs/sibling.md', '# Parallel task\n');
          git(seed, 'add', '-A');
          git(seed, 'commit', '-q', '-m', 'parallel task');
          git(seed, 'push', '-q', repos.bare, 'HEAD:refs/heads/speckit/f');
          return { status: 1, stdout: '', stderr: 'push rejected: branch moved' };
        }
        return realGit(args);
      };
      const landed = await runLand({
        client: github, git: racingGit, env: landEnv(repos), inputs: { ...TASK_INPUTS, checkRun: check.id },
        folder: 'f', workspace: repos.land, resultDir: repos.resultDir, workResult: 'success', log: silent,
      });
      assert.equal(landed.exitCode, 0, `${mode}: ${(landed.reasons ?? []).join('; ')}`);
      assert.equal(pushes, 2, mode);
      assert.equal(applies, 0, mode);
      assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[x\] T001/, mode);
      assert.equal(remoteShow(repos, 'speckit/f:docs/sibling.md'), '# Parallel task\n', mode);
    });
  }
});

// A conditional task (T001) and a verification task (T002), as upstream Spec Kit writes them; neither needs a change.
const COMPLETION_TASKS = '# Tasks\n\n## Phase 1\n\n- [ ] T001 Only if docs/evidence.md reports a platform defect, fix it in src/T/Fix.cs and add a test\n- [ ] T002 Run `dotnet test src/T/T.csproj --filter Evidence` 3 times in a row\n';
const COMPLETION_BODY = '## Tasks (2)\n\n- [ ] T001 Only if docs/evidence.md reports a platform defect\n- [ ] T002 Run the evidence tests\n';
const EVIDENCE_DOC = '# Evidence\n\n## T004\n\nThe cause is the test scenario; no platform defect.\n';
const seedCompletion = (dir) => {
  write(dir, 'docs/evidence.md', EVIDENCE_DOC);
  write(dir, 'src/T/T.csproj', '<Project />\n');
};
const conditionFalse = {
  format: 1,
  task: 'T001',
  verdict: 'accept',
  outcome: 'not-applicable',
  rationale: 'The task applies only to a platform defect; the evidence records a test-scenario cause.',
  obligations: [{ text: 'Fix a platform defect if docs/evidence.md reports one', disposition: 'condition-false' }],
  condition: { text: 'docs/evidence.md reports a platform defect', evidence: [{ path: 'docs/evidence.md', lines: [5, 5], excerpt: 'no platform defect.' }] },
};
const EVIDENCE_TESTS = { runner: 'dotnet-test', project: 'src/T/T.csproj', filter: 'Evidence', runs: 3 };
const verifyOnly = {
  format: 1,
  task: 'T002',
  verdict: 'accept',
  outcome: 'verified',
  rationale: 'The task only runs the evidence tests.',
  obligations: [{ text: 'Run the evidence tests 3 times in a row', disposition: 'verify' }],
  requirements: [EVIDENCE_TESTS],
  unsupported: [],
};
const taskTextOf = (id) => COMPLETION_TASKS.split('\n').find((line) => line.includes(` ${id} `)).replace(/^- \[ \] T\d+ /, '');

// What the implement-task workflow collects (workflow.json) when its assessment accepted a completion without a change.
function recordCompletion(repos, { task = 'T001', assessment, head, status = 'completed', error = '' }) {
  mkdirSync(repos.resultDir, { recursive: true });
  const completion = {
    format: 1,
    task,
    outcome: assessment.outcome,
    taskDigest: taskDigest(taskTextOf(task)),
    head,
    claim: { format: 1, task, outcome: assessment.outcome, rationale: 'No change is needed.' },
    assessment,
  };
  writeFileSync(path.join(repos.resultDir, 'workflow.json'), JSON.stringify({ workflow: 'implement-task', status, step: status === 'completed' ? 'result' : 'assess', error, result: status === 'completed' ? { completion } : null }));
}

const landedFiles = (repos) => git(repos.base, `--git-dir=${repos.bare}`, 'diff-tree', '--no-commit-id', '--name-only', '-r', 'speckit/f').trim().split('\n');

test('task: a conditional task whose condition does not hold lands as not applicable, with its evidence and no change', () => withRepos({
  tasks: COMPLETION_TASKS,
  seed: seedCompletion,
}, async (repos) => {
  const head = git(repos.work, 'rev-parse', 'HEAD').trim();
  const { packaged, result, output } = work(repos, {
    env: { SPECKIT_HEAD: head },
    agent: () => recordCompletion(repos, { assessment: conditionFalse, head }),
    verify: { outcome: 'success', checks: 'platform build and tests' },
  });
  assert.deepEqual(result.reasons, []);
  assert.equal(packaged.verify, false, 'no unrelated verification is imposed on a not-applicable task');
  assert.match(output, /completion=not-applicable/);
  assert.equal(result.completion.assessment.condition.evidence[0].blob, git(repos.work, 'rev-parse', `${head}:docs/evidence.md`).trim(), 'the citation is bound to the content it was read from');
  const github = fakeGitHub({ body: COMPLETION_BODY });
  github.repo.branches['speckit/f'] = head;
  const landed = await land(repos, github);
  assert.equal(landed.exitCode, 0, (landed.reasons ?? []).join('; '));
  assert.equal(landed.check.output.title, 'T001 not applicable');
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), new RegExp(`- \\[x\\] T001 .* — not applicable: docs/evidence\\.md reports a platform defect \\(assessed at ${head.slice(0, 8)}; no change by this task\\)`));
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[ \] T002/);
  assert.deepEqual(landedFiles(repos), ['specs/f/tasks.md'], 'only the tick lands');
  assert.match(remoteHead(repos), /\|feat\(f\): T001 not applicable$/);
  const comment = github.comments.at(-1).body;
  assert.match(comment, /T001 not applicable and ticked[\s\S]*Condition: docs\/evidence\.md reports a platform defect[\s\S]*Evidence: \[`docs\/evidence\.md` lines 5-5\]\([^)]*\/blob\/[0-9a-f]{40}\/docs\/evidence\.md#L5-L5\)/);
  assert.match(github.repo.pulls[0].body, /- \[x\] T001/);
}));

test('task: a verification task lands verified without changes only after the trusted runner passed every required run', async () => {
  for (const failAt of [null, 2]) {
    await withRepos({ tasks: COMPLETION_TASKS, seed: seedCompletion }, async (repos) => {
      const head = git(repos.work, 'rev-parse', 'HEAD').trim();
      const commands = [];
      let runs = 0;
      const exec = (command, args) => {
        commands.push([command, ...args].join(' '));
        runs += 1;
        return { status: runs === failAt ? 1 : 0, output: '', timedOut: false };
      };
      const { result, output } = work(repos, {
        inputs: { ...TASK_INPUTS, task: 'T002' },
        env: { SPECKIT_HEAD: head },
        agent: () => recordCompletion(repos, { task: 'T002', assessment: verifyOnly, head }),
        between: () => completionMain(['verify', '--workspace', repos.work, '--result-dir', repos.resultDir], { env: {}, log: silent, exec }),
      });
      assert.match(output, /completion=verified/);
      assert.match(output, /verify=false/, 'the environment verification is not run without an environment-verify requirement');
      const github = fakeGitHub({ body: COMPLETION_BODY });
      github.repo.branches['speckit/f'] = head;
      const landed = await land(repos, github, { inputs: { ...TASK_INPUTS, task: 'T002' } });
      if (failAt === null) {
        assert.deepEqual(commands, [
          'dotnet test src/T/T.csproj --filter Evidence',
          'dotnet test src/T/T.csproj --filter Evidence --no-build',
          'dotnet test src/T/T.csproj --filter Evidence --no-build',
        ]);
        assert.equal(result.completion.execution.status, 'passed');
        assert.equal(landed.exitCode, 0, (landed.reasons ?? []).join('; '));
        assert.equal(landed.check.output.title, 'T002 verified without changes');
        assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), new RegExp(`- \\[x\\] T002 .* — verified without changes by the assessed verification at ${head.slice(0, 8)} \\(no change by this task\\)`));
        assert.deepEqual(landedFiles(repos), ['specs/f/tasks.md']);
        assert.match(github.comments.at(-1).body, /T002 verified without changes and ticked[\s\S]*Verification: `dotnet test src\/T\/T\.csproj --filter Evidence`, 3 consecutive passing runs passed 3 of 3 runs/);
      } else {
        assert.equal(commands.length, 2, 'a failed run is never retried');
        assert.equal(landed.exitCode, 1);
        assert.match(landed.reasons.join('; '), /passed 1 of 3 required runs/);
        assert.equal(remoteSha(repos, 'speckit/f'), head);
        assert.match(github.comments.at(-1).body, /T002 attempt 2 failed/);
      }
    });
  }
});

test('task: a rejected, tampered, or stale completion without a change fails without ticking', async () => {
  const scenarios = [
    ['the assessment rejected it', (repos, head) => recordCompletion(repos, { assessment: conditionFalse, head, status: 'failed', error: 'The independent assessment rejected the completion of T001 without a change:\n- The task requires a fix.' }), /rejected the completion of T001 without a change/],
    ['an obligation needs a change', (repos, head) => recordCompletion(repos, { assessment: { ...conditionFalse, obligations: [...conditionFalse.obligations, { text: 'Record the decision in docs/evidence.md', disposition: 'change' }] }, head }), /the task still requires a change: Record the decision/],
    ['the claim is for another commit', (repos) => recordCompletion(repos, { assessment: conditionFalse, head: 'c'.repeat(40) }), /assessed on another commit/],
    ['the evidence does not say so', (repos, head) => recordCompletion(repos, { assessment: { ...conditionFalse, condition: { ...conditionFalse.condition, evidence: [{ path: 'docs/evidence.md', lines: [5, 5], excerpt: 'a platform defect was found' }] } }, head }), /excerpt does not appear/],
    ['the verification was not run', (repos, head) => recordCompletion(repos, { task: 'T001', assessment: { ...verifyOnly, task: 'T001' }, head }), /T001 attempt 2 failed|did not run on the current head/],
  ];
  for (const [name, record, reason] of scenarios) {
    await withRepos({ tasks: COMPLETION_TASKS, seed: seedCompletion }, async (repos) => {
      const head = git(repos.work, 'rev-parse', 'HEAD').trim();
      work(repos, { env: { SPECKIT_HEAD: head }, agent: () => record(repos, head) });
      const github = fakeGitHub({ body: COMPLETION_BODY });
      github.repo.branches['speckit/f'] = head;
      const landed = await land(repos, github);
      assert.equal(landed.exitCode, 1, name);
      assert.match(landed.reasons.join('; '), reason, name);
      assert.equal(remoteSha(repos, 'speckit/f'), head, name);
    });
  }
});

test('task: a completion without a change is assessed again when the branch moved before or during the landing', () => withRepos({
  tasks: COMPLETION_TASKS,
  seed: seedCompletion,
}, async (repos) => {
  const head = git(repos.work, 'rev-parse', 'HEAD').trim();
  work(repos, { env: { SPECKIT_HEAD: head }, agent: () => recordCompletion(repos, { assessment: conditionFalse, head }) });
  const moved = fakeGitHub({ body: COMPLETION_BODY });
  moved.repo.branches['speckit/f'] = 'd'.repeat(40);
  const stale = await land(repos, moved);
  assert.equal(stale.exitCode, 1);
  assert.match(stale.reasons.join('; '), /assessed on a different head/);

  const github = fakeGitHub({ body: COMPLETION_BODY });
  github.repo.branches['speckit/f'] = head;
  const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'in_progress', external_id: CHECK_ATTEMPT });
  const realGit = defaultGit(repos.land);
  let pushes = 0;
  const racingGit = (args) => {
    if (args.includes('push') && ++pushes === 1) {
      const seed = path.join(repos.base, 'seed');
      git(seed, 'fetch', '-q', repos.bare, 'speckit/f');
      git(seed, 'reset', '-q', '--hard', 'FETCH_HEAD');
      write(seed, 'docs/sibling.md', '# Parallel task\n');
      git(seed, 'add', '-A');
      git(seed, 'commit', '-q', '-m', 'parallel task');
      git(seed, 'push', '-q', repos.bare, 'HEAD:refs/heads/speckit/f');
      return { status: 1, stdout: '', stderr: 'push rejected: branch moved' };
    }
    return realGit(args);
  };
  const landed = await runLand({
    client: github, git: racingGit, env: landEnv(repos), inputs: { ...TASK_INPUTS, checkRun: check.id },
    folder: 'f', workspace: repos.land, resultDir: repos.resultDir, workResult: 'success', log: silent,
  });
  assert.equal(landed.exitCode, 0);
  assert.match(landed.requeue.join('; '), /moved after the completion of T001 without a change was assessed/);
  assert.equal(pushes, 1, 'the decision is never carried onto the new head');
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[ \] T001/);
}));

// On Windows, `bash` on the PATH is often WSL's System32 launcher, which neither receives the environment these tests
// pass nor understands Windows paths; Git for Windows' bash does both.
function resolveBash() {
  if (process.platform !== 'win32') return 'bash';
  const execPath = execFileSync('git', ['--exec-path'], { encoding: 'utf8' }).trim();
  const gitBash = path.resolve(execPath, '..', '..', '..', 'bin', 'bash.exe');
  return existsSync(gitBash) ? gitBash : 'bash';
}
const BASH = resolveBash();
const PYTHON = spawnSync(process.platform === 'win32' ? 'python' : 'python3', ['-c', 'import sys;print(sys.executable)'], { encoding: 'utf8' }).stdout?.trim() || 'python3';
// Runs a workflow script from a file: Windows re-quotes a long `-c` argument with nested quotes, which bash then misparses.
const runBash = (script, options) => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-bash-'));
  try {
    writeFileSync(path.join(dir, 'script.sh'), script);
    return spawnSync(BASH, ['-e', path.join(dir, 'script.sh')], options);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
};
const readText = (url) => readFileSync(url, 'utf8').replace(/\r\n/g, '\n');
const prependPath = (env, dir) => {
  const key = Object.keys(env).find((name) => name.toUpperCase() === 'PATH') ?? 'PATH';
  const { [key]: current = '', ...rest } = env;
  return { ...rest, PATH: `${dir}${path.delimiter}${current}` };
};

function withRepos(options, body) {
  const repos = makeGitRepos(options.tasks, options);
  return Promise.resolve(body(repos)).finally(() => rmSync(repos.base, { recursive: true, force: true }));
}

test('parses and validates task inputs', () => {
  const base = { SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_ATTEMPT: '2' };
  assert.deepEqual(parseTaskInputs({ ...base, SPECKIT_TASK: 'T001' }), TASK_INPUTS);
  assert.deepEqual(parseTaskInputs({ ...base, SPECKIT_KIND: 'converge', SPECKIT_TASK: 'ignored' }), { ...CONVERGE_INPUTS, attempt: 2 });
  assert.deepEqual(parseTaskInputs({ ...base, SPECKIT_KIND: 'merge' }), { ...MERGE_INPUTS, attempt: 2 });
  assert.deepEqual(parseTaskInputs({ ...base, SPECKIT_KIND: 'repair' }), { ...REPAIR_INPUTS, attempt: 2 });
  for (const env of [{}, { ...base, SPECKIT_TASK: 'finalize' }, { ...base, SPECKIT_TASK: 'rm -rf' }, { ...base, SPECKIT_TWIN: '0', SPECKIT_TASK: 'T001' }, { ...base, SPECKIT_KIND: 'deploy' }]) {
    assert.throws(() => parseTaskInputs(env), TaskInputError);
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
  assert.equal('unchanged' in result, false);
}));

test('repair: package and land change code without changing completed task checkboxes', () => withRepos({ tasks: DONE_TASKS }, async (repos) => {
  const { result } = work(repos, {
    inputs: REPAIR_INPUTS,
    verify: null,
    agent: (r) => write(r.work, 'docs/x.md', '# repaired\n'),
  });
  assert.deepEqual([result.verified, result.changedFiles], [true, ['docs/x.md']]);
  const github = fakeGitHub({ body: '## Tasks (2)\n\n- [x] T001 one\n- [x] T002 two\n' });
  const landed = await land(repos, github, { inputs: REPAIR_INPUTS });
  assert.equal(landed.exitCode, 0);
  assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# repaired\n');
  assert.equal(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), DONE_TASKS);
  assert.equal(landed.check.status, 'in_progress', 'the repair check stays open until reverification reports it');
}));

test('repair: package rejects task-list and protected changes', async () => {
  for (const [file, reason] of [['specs/f/tasks.md', /protected files: specs\/f\/tasks\.md/], ['.github/workflows/evil.yml', /protected files: \.github\/workflows\/evil\.yml/]]) {
    await withRepos({ tasks: DONE_TASKS }, (repos) => {
      const { result } = work(repos, {
        inputs: REPAIR_INPUTS,
        verify: null,
        agent: (r) => write(r.work, file, file.endsWith('tasks.md') ? `${readFileSync(path.join(r.work, file), 'utf8')}\nchanged\n` : 'changed\n'),
      });
      assert.match(result.reasons.join('; '), reason);
    });
  }
});

test('repair: successful no-op lands unchanged without a commit and still requires reverification', () => withRepos({ tasks: DONE_TASKS }, async (repos) => {
  const before = remoteHead(repos);
  const { result } = work(repos, { inputs: REPAIR_INPUTS, verify: null });
  assert.deepEqual([result.verified, result.changedFiles, result.reasons], [true, [], []]);
  const github = fakeGitHub();
  const repair = { id: 'phase:no-op', state: 'pending', scope: 'phase', head: remoteSha(repos, 'speckit/f'), at: github.tick() };
  await github.createComment(9, renderRepairMarker(repair));
  const landed = await land(repos, github, { inputs: REPAIR_INPUTS });
  assert.equal(landed.exitCode, 0);
  assert.equal(remoteHead(repos), before);
  assert.equal(landed.check.status, 'in_progress');
  assert.equal(unresolvedRepair(github.comments).id, repair.id);
  const env = {
    ...landEnv(repos), SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_ATTEMPT: '1',
    SPECKIT_CHECK_RUN: String(landed.check.id), SPECKIT_REPAIR: JSON.stringify(repair),
    SPECKIT_HEAD: landed.head, SPECKIT_VERIFY_RESULT: 'failure',
  };
  assert.equal((await runRepairReport({ client: github, env, log: silent })).exitCode, 1);
  assert.equal(unresolvedRepair(github.comments).id, repair.id);
  assert.match(github.comments.at(-1).body, /repair attempt 1 reached reverification, but the phase verification failed[\s\S]*\*\*Recommended next step\*\*: \[Wait for the diagnosis\]/);
  assert.doesNotMatch(github.comments.at(-1).body, /```text\n\s*\/speckit retry/);
  assert.doesNotMatch(github.comments.at(-1).body, /attempt 1 landed|fresh attempt count/);
  env.SPECKIT_VERIFY_RESULT = 'success';
  env.SPECKIT_VERIFICATION_OUTCOME = 'success';
  env.SPECKIT_VERIFY_FLAKY = 'Ns.Repair.Flaky';
  github.repo.branches['speckit/f'] = landed.head;
  assert.equal((await runRepairReport({ client: github, env, log: silent })).exitCode, 0);
  assert.equal(unresolvedRepair(github.comments), null);
  assert.match(github.comments.at(-1).body, /\*\*Repair verified\*\*[^\n]*\n\n- Flaky \(passed on rerun\): `Ns\.Repair\.Flaky`\n<!-- speckit-implement:flaky \{"tests":\["Ns\.Repair\.Flaky"\]\} -->/);
}));

test('repair: no-op does not excuse agent failure, missing patch, invalid patch, or failed work job', async () => {
  for (const failure of ['agent', 'missing', 'invalid', 'work']) {
    await withRepos({ tasks: DONE_TASKS }, async (repos) => {
      work(repos, { inputs: REPAIR_INPUTS, verify: null, agentExit: failure === 'agent' ? '1' : '0' });
      if (failure === 'missing') rmSync(path.join(repos.resultDir, 'changes.patch'));
      if (failure === 'invalid') writeFileSync(path.join(repos.resultDir, 'changes.patch'), 'not a patch');
      const landed = await land(repos, fakeGitHub(), { inputs: REPAIR_INPUTS, workResult: failure === 'work' ? 'failure' : 'success' });
      assert.equal(landed.exitCode, 1, failure);
    });
  }
});

test('repair: reverification preserves original product paths and unions repair paths, even for no-ops', () => {
  const originalChangedFiles = 'src/platform/Host.cs\nsrc/clients/web/app.ts\n';
  assert.deepEqual(repairVerificationScope({ scope: 'phase', originalChangedFiles, repairChangedFiles: '' }), {
    mode: 'phase', changedFiles: ['src/clients/web/app.ts', 'src/platform/Host.cs'], environment: false,
  });
  const combined = repairVerificationScope({ scope: 'group', originalChangedFiles, repairChangedFiles: 'src/platform/Host.cs\nsrc/analysts/manager/Worker.cs\n' });
  assert.equal(combined.mode, 'phase');
  assert.deepEqual(combined.changedFiles, ['src/analysts/manager/Worker.cs', 'src/clients/web/app.ts', 'src/platform/Host.cs']);
  for (const original of [null, '']) {
    assert.equal(repairVerificationScope({ scope: 'phase', originalChangedFiles: original, repairChangedFiles: '' }).mode, 'finalize');
  }
  assert.equal(repairVerificationScope({ scope: 'phase', originalChangedFiles }).mode, 'finalize', 'missing repair diff cannot silently narrow verification');
  assert.equal(repairVerificationScope({ scope: 'merge', originalChangedFiles, repairChangedFiles: '' }).mode, 'finalize');
});

test('repair workflow: unchanged HEAD reverifies the original product scope, or all checks without evidence', () => withRepos({ tasks: DONE_TASKS }, (repos) => {
  // The repair's reverification runs in Spec Kit implement repair; its work in Spec Kit implement task.
  const workflow = readText(new URL('../workflows/speckit-implement-repair.yml', import.meta.url));
  const unit = readText(new URL('../workflows/speckit-implement-task.yml', import.meta.url));
  const script = workflow.match(/- name: Prepare the verification tree[\s\S]*?        run: \|\n([\s\S]*?)\n      - name:/)[1].replace(/^ {10}/gm, '');
  symlinkSync(process.cwd(), path.join(repos.base, 'tooling'), 'dir');
  const evidence = path.join(repos.base, 'failed-verification');
  const output = path.join(repos.base, 'output');
  for (const available of [true, false]) {
    if (available) write(evidence, 'changed-files.txt', 'src/platform/Host.cs\n');
    else rmSync(evidence, { recursive: true });
    writeFileSync(output, '');
    const run = runBash(script, {
      cwd: repos.work, encoding: 'utf8',
      env: { ...process.env, SCOPE: 'phase', FAILED_HEAD: git(repos.work, 'rev-parse', 'HEAD').trim(), SPECKIT_RESULT_DIR: repos.resultDir, GITHUB_OUTPUT: output },
    });
    assert.equal(run.status, 0, run.stderr);
    assert.match(readFileSync(output, 'utf8'), available ? /mode=phase/ : /mode=finalize/);
    assert.match(readFileSync(output, 'utf8'), new RegExp(`^tree=${git(repos.work, 'rev-parse', 'HEAD^{tree}').trim()}$`, 'm'), 'the shards rebuild and compare this tree');
    assert.equal(readFileSync(path.join(repos.resultDir, 'changed-files.txt'), 'utf8'), available ? 'src/platform/Host.cs\n' : '');
  }
  assert.match(workflow, /mode: \$\{\{ steps\.tree\.outputs\.mode \}\}/);
  assert.match(unit, /path: \$\{\{ github\.workspace \}\}\/workspace\/\.speckit-repair-evidence/);
  assert.match(unit, /echo '\/\.speckit-repair-evidence\/' >> \.git\/info\/exclude/);
  assert.match(workflow, /ref: \$\{\{ needs\.decide\.outputs\.verify_only == 'true' && needs\.decide\.outputs\.head \|\| needs\.implement\.outputs\.head \}\}/);
  assert.match(workflow, /Require the verification action\n        run: test -f tooling\/\.github\/actions\/environment-verify\/action\.yml/);
  assert.match(workflow, /SPECKIT_VERIFICATION_OUTCOME: \$\{\{ needs\.verdict\.outputs\.verification \}\}/);
  const guard = workflow.match(/Require the verification action\n        run: ([^\n]+)/)[1];
  assert.equal(runBash(guard, { cwd: repos.land }).status, 1, 'missing verifier fails the job instead of skipping verification');
}));

test('repair: no-op on a moved branch pins the actual current checkout, never an unchecked API head', () => withRepos({ tasks: DONE_TASKS }, async (repos) => {
  const original = git(repos.work, 'rev-parse', 'HEAD').trim();
  work(repos, { inputs: REPAIR_INPUTS, verify: null, env: { SPECKIT_HEAD: original } });
  write(repos.land, 'docs/concurrent.md', '# Concurrent change\n');
  git(repos.land, 'add', '-A');
  git(repos.land, 'commit', '-q', '-m', 'concurrent change');
  const tip = git(repos.land, 'rev-parse', 'HEAD').trim();
  const github = fakeGitHub();
  const result = await runLandTasks({
    client: github, git: defaultGit(repos.land), folder: 'f', workspace: repos.land,
    resultRoot: repos.resultDir, log: silent,
    env: { ...landEnv(repos), SPECKIT_KIND: 'repair', SPECKIT_TWIN: '5', SPECKIT_PULL: '9',
      SPECKIT_ORDER: '[{"include":[{"task":null,"attempt":1}]}]', SPECKIT_ORDER_INDEX: '0', SPECKIT_WORK_RESULT: 'success' },
  });
  assert.equal(result.exitCode, 0);
  assert.equal(result.head, tip);
  assert.notEqual(result.head, 'sha-head', 'the API head was not checked out or verified');
  assert.equal(git(repos.land, 'rev-parse', 'HEAD').trim(), tip);
  assert.equal(git(repos.land, 'diff', '--name-only', original, result.head).trim(), 'docs/concurrent.md');
}));

test('repair fallback: finalize dispatches real product verification commands with an empty changed-file list', () => withRepos({}, (repos) => {
  const action = readText(new URL('../actions/environment-verify/action.yml', import.meta.url));
  const script = action.match(/      run: \|\n([\s\S]*)/)[1].replace(/^ {8}/gm, '');
  const bin = path.join(repos.base, 'bin');
  for (const command of ['dotnet', 'npm']) {
    write(bin, command, `#!/bin/sh\nprintf '${command} %s\\n' "$*" >> "$CALLS"\n`);
    chmodSync(path.join(bin, command), 0o755);
  }
  write(repos.work, 'src/platform/SocAlytics.Platform.slnx', '');
  write(repos.work, 'src/platform/Tests/Host.Tests.csproj', '');
  write(repos.work, 'src/platform/Tests/Integration.Tests.csproj', '');
  write(repos.work, 'src/platform/global.json', '{"sdk":{"version":"10.0.100"}}');
  write(repos.work, 'src/analysts/manager/SocAlytics.Analysts.Manager.slnx', '');
  write(repos.work, 'src/analysts/manager/Worker.Tests.csproj', '');
  write(repos.work, 'src/analysts/manager/global.json', '{"sdk":{"version":"10.0.100"}}');
  write(repos.work, 'src/clients/web/package.json', '{"scripts":{"build":"b","test":"t","test:e2e":"e"}}');
  write(repos.work, 'src/clients/web/node_modules/@playwright/test/package.json', '{"version":"1.63.0"}');
  write(repos.base, 'markdown.mjs', '');
  const mode = repairVerificationScope({ scope: 'phase', repairChangedFiles: '' }).mode;
  const run = runBash(script, { cwd: repos.work, encoding: 'utf8', env: {
    ...prependPath(process.env, bin), MODE: mode, WORKSPACE: repos.work,
    CHANGED_FILES: '', LOG_FILE: path.join(repos.base, 'verification.log'),
    GITHUB_OUTPUT: path.join(repos.base, 'output'), GITHUB_STEP_SUMMARY: path.join(repos.base, 'summary'),
    MARKDOWN_CHECK: path.join(repos.base, 'markdown.mjs'), CALLS: path.join(repos.base, 'calls'),
    ANALYST_PROJECTS: fileURLToPath(new URL('../actions/environment-verify/analyst-projects.py', import.meta.url)),
    VERIFICATION_PLAN: fileURLToPath(new URL('../actions/environment-verify/verification-plan.mjs', import.meta.url)),
    SOCALYTICS_PYTHON: PYTHON, ENVIRONMENT_TESTS: '',
  } });
  assert.equal(run.status, 0, run.stderr + readFileSync(path.join(repos.base, 'verification.log'), 'utf8'));
  const calls = readFileSync(path.join(repos.base, 'calls'), 'utf8');
  // Each component builds and tests from its own folder.
  assert.match(calls, /dotnet test Tests\/Host.Tests.csproj --no-build/);
  assert.match(calls, /dotnet test Tests\/Integration.Tests.csproj --no-build/);
  assert.match(calls, /dotnet test Worker.Tests.csproj --no-build/);
  assert.match(calls, /npm test\nnpm run test:e2e/);
}));

test('task: package rejects missing ticks, protected paths, and agent failures; verdict records failed verification', async () => {
  const cases = [
    { name: 'wrong tick', agent: (r) => { agentDoesT001(r.work, { tick: false }); write(r.work, 'specs/f/tasks.md', TASKS.replace('- [ ] T002', '- [x] T002')); }, reason: /may only change by checking T001/, verify: false },
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

test('task: land checks the task itself when the agent left tasks.md untouched, keeping its line endings', () => withRepos({ tasks: TASKS.replaceAll('\n', '\r\n') }, async (repos) => {
  const { result: verdict } = work(repos, { agent: (r) => agentDoesT001(r.work, { tick: false }), env: { SPECKIT_VERIFY_FLAKY: 'Ns.T.A, Ns.T.<b>-->' } });
  assert.equal(verdict.verified, true, verdict.reasons.join());
  assert.deepEqual(verdict.changedFiles, ['docs/x.md']);
  assert.deepEqual(verdict.flaky, ['Ns.T.A', 'Ns.T.<b>-->']);
  const github = fakeGitHub();
  const result = await land(repos, github);
  assert.equal(result.exitCode, 0, JSON.stringify(result.reasons));
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[x\] T001 Create docs\/x\.md\r?\n- \[ \] T002/);
  assert.match(readFileSync(path.join(repos.land, 'specs/f/tasks.md'), 'utf8'), /- \[x\] T001 Create docs\/x\.md\r\n- \[ \] T002 Create docs\/y\.md\r\n/);
  assert.match(github.repo.pulls[0].body, /- \[x\] T001 Create docs\/x\.md\n- \[ \] T002/);
  assert.match(github.comments.at(-1).body, /\*\*T001 implemented\*\*[\s\S]*1 of 2 tasks/);
  assert.match(github.comments.at(-1).body, /\n- Flaky \(passed on rerun\): `Ns\.T\.A`, `Ns\.T\.<b>-->`\n<!-- speckit-implement:flaky \{"tests":\["Ns\.T\.A","Ns\.T\.\\u003cb\\u003e--\\u003e"\]\} -->\n/);
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
  assert.match(result.requeue.join(), /`docs\/x\.md` changed on the branch since the task started, and the changes conflict/);
  assert.equal(remoteSha(repos, 'speckit/f'), sibling);
  assert.deepEqual([result.check.status, result.check.conclusion, result.check.output.title], ['completed', 'neutral', 'T001 attempt 2 is redone from the new head']);
  assert.match(github.comments.at(-1).body, /\*\*T001 attempt 2 runs again\*\*[\s\S]*does not count as a failed attempt/);
}));

// Runs the work job of one task in its own clone, as the matrix does, into results/speckit-result-<task>.
function workTask(repos, task, agent, { verify } = {}) {
  const dir = path.join(repos.base, `work-${task}`);
  git(repos.base, 'clone', '-q', '--branch', 'speckit/f', repos.bare, dir);
  const env = { SPECKIT_HEAD: git(dir, 'rev-parse', 'HEAD').trim() };
  return work({ ...repos, work: dir, resultDir: path.join(repos.base, 'results', `speckit-result-${task}`) }, { inputs: { ...TASK_INPUTS, task, attempt: 1 }, agent: () => agent(dir), verify, env });
}

// Lands the first entry of the given task order, as the land job of the phase's tasks matrix does; the decide job
// already showed its attempts as in progress on the start commit.
async function landTasks(repos, github, entries, { next = null } = {}) {
  const checks = await startAttemptChecks(github, { head: remoteSha(repos, 'speckit/f'), kind: 'task', entries });
  const outputFile = path.join(repos.base, 'land-output.txt');
  writeFileSync(outputFile, '');
  const result = await runLandTasks({
    client: github,
    git: defaultGit(repos.land),
    env: {
      ...landEnv(repos),
      GITHUB_OUTPUT: outputFile,
      SPECKIT_KIND: 'task',
      SPECKIT_TWIN: '5',
      SPECKIT_PULL: '9',
      SPECKIT_ORDER: JSON.stringify([{ include: entries }, ...(next ? [next] : [])]),
      SPECKIT_ORDER_INDEX: '0',
      SPECKIT_WORK_RESULT: 'success',
    },
    folder: 'f',
    workspace: repos.land,
    resultRoot: path.join(repos.base, 'results'),
    log: silent,
  });
  return { ...result, checks, output: readFileSync(outputFile, 'utf8') };
}

test('land: lands parallel [P] tasks in order and shows the next task as in progress', () => withRepos({ tasks: `${PARALLEL_TASKS}- [ ] T003 Create docs/z.md\n` }, async (repos) => {
  workTask(repos, 'T001', (dir) => agentDoesT001(dir));
  workTask(repos, 'T002', siblingDoesT002());
  const github = fakeGitHub({ body: '## Tasks (3)\n\n- [ ] T001 [P] Create docs/x.md\n- [ ] T002 [P] Create docs/y.md\n- [ ] T003 Create docs/z.md\n' });
  await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: remoteSha(repos, 'speckit/f'), status: 'queued', external_id: CHECK_PROGRESS });
  const result = await landTasks(repos, github, [{ task: 'T001', attempt: 1 }, { task: 'T002', attempt: 1 }], { next: { include: [{ task: 'T003', attempt: 1, summary: 'Create docs/z.md' }] } });
  assert.equal(result.ok, true);
  assert.deepEqual(result.outcomes.map((outcome) => [outcome.key, outcome.landed]), [['T001', true], ['T002', true]]);
  assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# X\n');
  assert.equal(remoteShow(repos, 'speckit/f:docs/y.md'), '# Y\n');
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[X\] T001 [^\n]*\n- \[x\] T002[^\n]*\n- \[ \] T003/);
  assert.match(remoteHead(repos), /\|feat\(f\): T002 \[P\] Create docs\/y\.md$/, 'the later task is rebuilt on the earlier one');
  const comments = github.comments.filter((comment) => /implemented\*\*/.test(comment.body));
  assert.deepEqual(comments.map((comment) => comment.body.split('\n')[0]), [
    '<!-- speckit-implement:attempt {"kind":"task","task":"T001","attempt":1,"outcome":"success"} -->',
    '<!-- speckit-implement:attempt {"kind":"task","task":"T002","attempt":1,"outcome":"success"} -->',
  ]);
  assert.deepEqual(result.checks, { T001: 1, T002: 2 }, 'the first attempt took over the queued progress check run');
  const reported = github.repo.checkRuns.filter((check) => [result.checks.T001, result.checks.T002].includes(check.id));
  assert.deepEqual(reported.map((check) => [check.status, check.conclusion]), [['completed', 'success'], ['completed', 'success']], 'the tasks report through the check runs decide created');
  const next = github.repo.checkRuns.find((check) => check.external_id === 'speckit:attempt:task:T003:1');
  assert.deepEqual([next.status, next.output.title, next.output.summary], ['in_progress', 'T003 attempt 1 in progress', 'Create docs/z.md']);
  assert.match(result.output, /^ok=true\nhead=sha-head\nstart=[0-9a-f]{40}\ncombined=false\n$/);
  assert.equal(result.exitCode, 0);
}));

test('land: a sibling that changed the same files is redone, and the chain stops after the parallel tasks', () => withRepos({ tasks: PARALLEL_TASKS }, async (repos) => {
  workTask(repos, 'T001', (dir) => agentDoesT001(dir));
  workTask(repos, 'T002', siblingDoesT002({ 'docs/x.md': '# Sibling\n' }));
  const github = fakeGitHub();
  const result = await landTasks(repos, github, [{ task: 'T001', attempt: 1 }, { task: 'T002', attempt: 1 }], { next: { include: [{ task: 'T003', attempt: 1 }] } });
  assert.equal(result.ok, false);
  assert.deepEqual(result.outcomes.map((outcome) => outcome.landed), [true, false]);
  assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# X\n');
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:attempt \{"kind":"task","task":"T002","attempt":1,"outcome":"requeue"\} -->\n\*\*T002 attempt 1 runs again\*\*/);
  assert.equal(github.repo.checkRuns.some((check) => String(check.external_id).includes('T003')), false, 'no next task after a task that did not land');
  assert.match(result.output, /^ok=false\n/);
  assert.equal(result.exitCode, 1, 'the land job fails, so the matrix cancels the later tasks');
  await assert.rejects(() => runLandTasks({ client: github, git: defaultGit(repos.land), env: { SPECKIT_KIND: 'task' }, folder: 'f', workspace: repos.land, resultRoot: repos.base, log: silent }), /SPECKIT_ORDER/);
}));

// A file both parallel tasks edit: each edit lies in the context of the other's patch, so a plain apply fails and only a
// three-way merge combines them.
const LIST = 'one\ntwo\nthree\nfour\nfive\nsix\n';
const withList = (tasks = PARALLEL_TASKS) => ({ tasks, branch: (dir) => write(dir, 'docs/list.md', LIST) });
const listByT001 = { 'docs/list.md': LIST.replace('two', 'two (x)') };
const listByT002 = { 'docs/list.md': LIST.replace('five', 'five (y)') };

test('task: land merges edits a sibling made to the same file elsewhere, and says so', () => withRepos(withList(), async (repos) => {
  work(repos, { agent: (r) => agentDoesT001(r.work, { extra: listByT001 }) });
  siblingLands(repos, siblingDoesT002(listByT002));
  const github = fakeGitHub();
  const result = await land(repos, github);
  assert.equal(result.exitCode, 0);
  assert.equal(result.requeue, undefined);
  assert.deepEqual(result.combined, ['docs/list.md']);
  assert.equal(remoteShow(repos, 'speckit/f:docs/list.md'), 'one\ntwo (x)\nthree\nfour\nfive (y)\nsix\n');
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[x\] T001 [^\n]*\n- \[x\] T002/);
  assert.match(git(repos.base, `--git-dir=${repos.bare}`, 'log', '-1', '--format=%B', 'speckit/f'), /Combined with parallel changes to docs\/list\.md; verified together after the parallel tasks\./);
  assert.match(github.comments.at(-1).body, /\*\*T001 implemented\*\*[\s\S]*- Combined with the parallel changes to `docs\/list\.md`; the parallel tasks are verified together next/);
}));

test('land: parallel edits to the same file are combined, and the next task waits for their verification', () => withRepos(withList(`${PARALLEL_TASKS}- [ ] T003 Create docs/z.md\n`), async (repos) => {
  workTask(repos, 'T001', (dir) => agentDoesT001(dir, { extra: listByT001 }));
  workTask(repos, 'T002', siblingDoesT002(listByT002));
  const github = fakeGitHub();
  const start = remoteSha(repos, 'speckit/f');
  const result = await landTasks(repos, github, [{ task: 'T001', attempt: 1 }, { task: 'T002', attempt: 1 }], { next: { include: [{ task: 'T003', attempt: 1 }] } });
  assert.deepEqual([result.ok, result.exitCode, result.combined], [true, 0, ['docs/list.md']]);
  assert.deepEqual(result.outcomes.map((outcome) => [outcome.key, outcome.landed, outcome.combined]), [['T001', true, []], ['T002', true, ['docs/list.md']]]);
  assert.equal(remoteShow(repos, 'speckit/f:docs/list.md'), 'one\ntwo (x)\nthree\nfour\nfive (y)\nsix\n');
  assert.match(result.output, /^ok=true\nhead=sha-head\nstart=[0-9a-f]{40}\ncombined=true\n$/);
  assert.equal(github.repo.checkRuns.some((check) => String(check.external_id).includes('T003')), false, 'the next task waits for the combined verification');
  // The group's verify job reads the combination back from the commit messages the landings wrote.
  assert.deepEqual(combinedGroupEdits(bareGit(repos), start, 'speckit/f'), ['docs/list.md']);
  const detected = await combinedEditsOutput(repos, start);
  assert.deepEqual([detected.exitCode, detected.output], [0, 'combined=true\n']);
}));

test('combined edits: parallel tasks that landed without merging a sibling\'s edits need no verification together', () => withRepos({ tasks: PARALLEL_TASKS }, async (repos) => {
  workTask(repos, 'T001', (dir) => agentDoesT001(dir));
  workTask(repos, 'T002', siblingDoesT002());
  const start = remoteSha(repos, 'speckit/f');
  const result = await landTasks(repos, fakeGitHub(), [{ task: 'T001', attempt: 1 }, { task: 'T002', attempt: 1 }]);
  assert.equal(result.ok, true);
  assert.deepEqual(combinedGroupEdits(bareGit(repos), start, 'speckit/f'), []);
  const detected = await combinedEditsOutput(repos, start);
  assert.deepEqual([detected.exitCode, detected.output], [0, 'combined=false\n']);
  await assert.rejects(() => groupMain(['combined-edits'], { env: { SPECKIT_GROUP_START: 'not-a-sha' }, git: bareGit(repos), log: silent }), /SPECKIT_GROUP_START/);
}));

function bareGit(repos) {
  const git = defaultGit(repos.base);
  return (args) => git([`--git-dir=${repos.bare}`, ...args]);
}

// Runs the group's combined-edits command against the implementation branch, as the verify job does on its checkout.
async function combinedEditsOutput(repos, start) {
  const outputFile = path.join(repos.base, 'combined-output.txt');
  writeFileSync(outputFile, '');
  const run = bareGit(repos);
  const git = (args) => run(args[0] === 'log' ? [...args.slice(0, -1), args.at(-1).replace(/HEAD$/, 'speckit/f')] : args);
  const exitCode = await groupMain(['combined-edits'], { env: { SPECKIT_GROUP_START: start, GITHUB_OUTPUT: outputFile }, git, log: silent });
  return { exitCode, output: readFileSync(outputFile, 'utf8') };
}

function combinedEnv(extra = {}) {
  return {
    SPECKIT_TWIN: '5',
    SPECKIT_PULL: '9',
    SPECKIT_FOLDER: 'f',
    SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_BASE_BRANCH: 'main',
    SPECKIT_HEAD: 'sha-new',
    SPECKIT_ORDER: JSON.stringify([
      { include: [{ task: 'T001', attempt: 1 }, { task: 'T002', attempt: 1 }] },
      { include: [{ task: 'T003', attempt: 1, summary: 'Create docs/z.md' }] },
    ]),
    SPECKIT_ORDER_INDEX: '0',
    SPECKIT_LAND_OK: 'true',
    ...extra,
  };
}

test('combined verification: success shows the next task, anything else stops the implementation for a person', async () => {
  const verified = fakeGitHub();
  const verifiedLines = [];
  assert.equal((await runCombinedVerified({ client: verified, env: combinedEnv({ SPECKIT_VERIFY_RESULT: 'success' }), log: (line) => verifiedLines.push(line) })).exitCode, 0);
  const next = verified.repo.checkRuns.find((check) => check.external_id === 'speckit:attempt:task:T003:1');
  assert.deepEqual([next.status, next.head_sha, next.output.summary], ['in_progress', 'sha-new', 'Create docs/z.md']);
  assert.equal(verified.comments.some((comment) => /needs attention/.test(comment.body)), false);
  assert.match(verifiedLines.join('\n'), /T001, T002 verified together on sha-new\./);
  assert.equal(verified.comments.length, 0, 'nothing to report without flaky tests');
  const flakyGroup = fakeGitHub();
  await runCombinedVerified({ client: flakyGroup, env: combinedEnv({ SPECKIT_VERIFY_RESULT: 'success', SPECKIT_VERIFY_FLAKY: 'Ns.A.Works,Ns.A.Works' }), log: silent });
  assert.equal(flakyGroup.comments.at(-1).body, '**T001, T002 verified together** on `sha-new`.\n\n- Flaky (passed on rerun): `Ns.A.Works`\n<!-- speckit-implement:flaky {"tests":["Ns.A.Works"]} -->');

  // Without combined edits nothing ran together; the next task starts and the phase verification covers the group.
  const separate = fakeGitHub();
  const separateLines = [];
  const uncombined = await runCombinedVerified({ client: separate, env: combinedEnv({ SPECKIT_VERIFY_RESULT: 'success', SPECKIT_COMBINED: 'false' }), log: (line) => separateLines.push(line) });
  assert.deepEqual([uncombined.exitCode, uncombined.combined], [0, false]);
  assert.equal(separate.repo.checkRuns.find((check) => check.external_id === 'speckit:attempt:task:T003:1')?.status, 'in_progress');
  assert.equal(separate.comments.some((comment) => /needs attention/.test(comment.body)), false);
  assert.match(separateLines.join('\n'), /T001, T002 landed on sha-new without combining edits; the phase verification covers them\./);

  const partial = fakeGitHub();
  await runCombinedVerified({ client: partial, env: combinedEnv({ SPECKIT_VERIFY_RESULT: 'success', SPECKIT_LAND_OK: 'false' }), log: silent });
  assert.equal(partial.repo.checkRuns.some((check) => String(check.external_id).includes('T003')), false, 'no next task after a task that was not pushed');

  const broken = fakeGitHub();
  const failed = await runCombinedVerified({ client: broken, env: { ...combinedEnv({ SPECKIT_VERIFY_RESULT: 'failure' }), GITHUB_REPOSITORY: 'octo/repo', GITHUB_RUN_ID: '77' }, log: silent });
  assert.equal(failed.exitCode, 1);
  const stop = broken.repo.checkRuns.at(-1);
  assert.deepEqual([stop.head_sha, stop.status, stop.conclusion, stop.external_id, stop.output.title], ['sha-new', 'completed', 'failure', CHECK_LIMIT, 'The combined verification failed']);
  assert.match(broken.comments.at(-1).body, /\*\*Implementation needs attention\*\* @dev: the verification of T001, T002 together failed \(see the \[run\]\(https:\/\/github\.com\/octo\/repo\/actions\/runs\/77\)\)/);
  assert.deepEqual(unresolvedRepair(broken.comments).tasks, ['T001', 'T002']);

  const cancelled = fakeGitHub();
  assert.equal((await runCombinedVerified({ client: cancelled, env: combinedEnv({ SPECKIT_VERIFY_RESULT: 'cancelled' }), log: silent })).exitCode, 1);
  assert.match(cancelled.comments.at(-1).body, /T001, T002 together did not finish \(cancelled\)/);
  await assert.rejects(() => runCombinedVerified({ client: broken, env: { SPECKIT_TWIN: '5', SPECKIT_PULL: '9' }, log: silent }), /SPECKIT_ORDER/);
});

test('repair reporting clears durable repair state only after successful reverification', async () => {
  const github = fakeGitHub();
  const repair = { id: 'phase:77:sha-old', state: 'pending', scope: 'phase', tasks: ['T001'], head: 'sha-old', at: github.tick() };
  await github.createComment(9, `${renderRepairMarker(repair)}\npending`);
  const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-new', status: 'in_progress', external_id: 'speckit:attempt:repair::1', output: {} });
  const env = {
    SPECKIT_TWIN: '5',
    SPECKIT_PULL: '9',
    SPECKIT_ATTEMPT: '1',
    SPECKIT_CHECK_RUN: String(check.id),
    SPECKIT_HEAD: 'sha-new',
    SPECKIT_REPAIR: JSON.stringify(repair),
    SPECKIT_VERIFY_RESULT: 'success',
    SPECKIT_VERIFICATION_OUTCOME: 'success',
  };
  github.repo.branches['speckit/f'] = 'sha-new';
  assert.equal((await runRepairReport({ client: github, env, log: silent })).exitCode, 0);
  assert.equal(unresolvedRepair(github.comments), null);
  assert.equal(github.repo.checkRuns.find((item) => item.id === check.id).conclusion, 'success');

  const failed = fakeGitHub();
  await failed.createComment(9, `${renderRepairMarker(repair)}\npending`);
  const failedCheck = await failed.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-new', status: 'in_progress', external_id: 'speckit:attempt:repair::1', output: {} });
  const result = await runRepairReport({ client: failed, env: { ...env, SPECKIT_CHECK_RUN: String(failedCheck.id), SPECKIT_VERIFY_RESULT: 'failure' }, log: silent });
  assert.equal(result.exitCode, 1);
  assert.equal(unresolvedRepair(failed.comments).id, repair.id);
  assert.equal(failed.repo.checkRuns.find((item) => item.id === failedCheck.id).external_id, CHECK_LIMIT);
  for (const outcome of ['', 'skipped', 'failure', 'success']) {
    const pending = fakeGitHub();
    await pending.createComment(9, renderRepairMarker(repair));
    const check = await pending.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-new', status: 'in_progress', output: {} });
    if (outcome !== 'success') pending.repo.branches['speckit/f'] = 'sha-new';
    const result = await runRepairReport({ client: pending, env: {
      ...env, SPECKIT_CHECK_RUN: String(check.id), SPECKIT_VERIFICATION_OUTCOME: outcome,
    }, log: silent });
    assert.equal(result.exitCode, 1, outcome === 'success' ? 'the branch moved after verification' : `verifier ${outcome || 'missing'}`);
    assert.equal(unresolvedRepair(pending.comments).id, repair.id);
  }
});

test('land: a single task finds its result where download-artifact puts a single artifact', () => withRepos({}, async (repos) => {
  workTask(repos, 'T001', (dir) => agentDoesT001(dir));
  const results = path.join(repos.base, 'results');
  for (const file of ['result.json', 'changes.patch', 'agent-exit.txt', 'changed-files.txt']) {
    writeFileSync(path.join(results, file), readFileSync(path.join(results, 'speckit-result-T001', file)));
  }
  rmSync(path.join(results, 'speckit-result-T001'), { recursive: true, force: true });
  const github = fakeGitHub();
  const result = await landTasks(repos, github, [{ task: 'T001', attempt: 1 }]);
  assert.deepEqual([result.ok, result.exitCode], [true, 0]);
  assert.equal(remoteShow(repos, 'speckit/f:docs/x.md'), '# X\n');
}));
test('task: a task a push already did lands without a change, but an agent cannot skip one', () => withRepos({}, async (repos) => {
  // The agent makes no change: without a push that is not the workflow's, that fails the attempt.
  const taskHead = git(repos.work, 'rev-parse', 'HEAD').trim();
  work(repos, { env: { SPECKIT_HEAD: taskHead }, agent: () => {} });
  const before = remoteHead(repos);
  const github = fakeGitHub();
  github.repo.branches['speckit/f'] = taskHead;
  const refused = await land(repos, github);
  assert.equal(refused.exitCode, 1);
  assert.match(refused.reasons.join(), /the task names no verifiable file/);
  assert.equal(remoteHead(repos), before);

  // A person (or Copilot for them) pushed after that attempt: the task lands with only its tick.
  const at = new Date(Date.now() + 60_000).toISOString();
  github.repo.pullCommits = { 9: [
    { sha: 'a', author: { login: 'github-actions[bot]' }, committer: { login: 'github-actions[bot]' }, commit: { committer: { date: at } } },
    { sha: 'b', author: { login: 'Copilot' }, committer: { login: 'web-flow' }, commit: { committer: { date: at } } },
  ] };
  work(repos, { env: { SPECKIT_HEAD: taskHead }, agent: () => {} });
  const landed = await land(repos, github, { inputs: { ...TASK_INPUTS, attempt: 2 } });
  assert.deepEqual([landed.exitCode, landed.reasons ?? []], [0, []]);
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[x\] T001 .* — already done by a push to the implementation branch \(verified; no change by this task\)/, 'the land job ticked the task and said where its work came from');
}));

test('doneByPush: only a push after the task started, or a push at the head, lets a task land without a change', async () => {
  const commit = (login, date) => ({ author: { login }, committer: { login }, commit: { committer: { date } } });
  const client = (commits, comments = []) => ({ listPullRequestCommits: async () => commits, listIssueComments: async () => comments });
  const attempt = (task, at) => ({ user: { login: 'github-actions[bot]' }, created_at: at, body: `<!-- speckit-implement:attempt {"kind":"task","task":"${task}","attempt":1,"outcome":"failure"} -->\nfailed` });
  const bot = (date) => commit('github-actions[bot]', date);
  assert.equal(await doneByPush(client([bot('2026-10-26T10:00:00Z')]), { pull: 9, task: 'T001' }), false, 'only workflow commits');
  assert.equal(await doneByPush(client([bot('2026-10-26T10:00:00Z'), commit('dev', '2026-10-26T11:00:00Z')]), { pull: 9, task: 'T001' }), true, 'a person pushed last');
  const earlier = [commit('dev', '2026-10-26T09:00:00Z'), bot('2026-10-26T10:00:00Z')];
  assert.equal(await doneByPush(client(earlier, [attempt('T001', '2026-10-26T08:00:00Z')]), { pull: 9, task: 'T001' }), true, 'a push after the first attempt of the task');
  assert.equal(await doneByPush(client(earlier, [attempt('T001', '2026-10-26T09:30:00Z')]), { pull: 9, task: 'T001' }), false, 'a push before the task started does not count');
  assert.equal(await doneByPush(client(earlier, [attempt('T002', '2026-10-26T08:00:00Z')]), { pull: 9, task: 'T001' }), false, 'another task\'s attempt does not count');
  assert.equal(await doneByPush({ listPullRequestCommits: async () => { throw new Error('down'); } }, { pull: 9, task: 'T001' }), false);
});

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

  write(repos.work, 'specs/f/tasks.md', TASKS.replace('- [ ] T001 Create docs/x.md', '- [x] T001 Create docs/x.md (done)'));
  work(repos);
  const rejected = await land(repos, github);
  assert.match(rejected.reasons.join(), /may only change by checking T001/);
  assert.equal(remoteHead(repos), before);
  git(repos.work, 'checkout', '-q', 'HEAD', '--', 'specs/f/tasks.md');

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
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:attempt \{"kind":"converge","task":null,"attempt":1,"outcome":"success"\} -->\n\*\*Converged\*\* \(attempt 1\)[\s\S]*Convergence report[\s\S]*Converged — the implementation satisfies/);
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
    assert.match(github.comments.at(-1).body, /\*\*Recommended next step\*\*: \[Wait for the diagnosis\]\([^)]+speckit-diagnose\.lock\.yml\)[\s\S]*<summary>Other options<\/summary>/);
    assert.doesNotMatch(github.comments.at(-1).body, /```text\n\s*\/speckit retry/);
    assert.doesNotMatch(github.comments.at(-1).body, /\/speckit resume/);
  });
});

const conflicting = {
  tasks: DONE_TASKS,
  branch: (dir) => write(dir, 'docs/shared.md', '# Shared\n\nbranch line\n'),
  main: (dir) => write(dir, 'docs/shared.md', '# Shared\n\nmain line\n'),
};
const resolveShared = (r) => write(r.work, 'docs/shared.md', '# Shared\n\nbranch line\nmain line\n');

test('merge: integrate merges the base branch and reports conflicts', async () => {
  await withRepos({ tasks: DONE_TASKS, main: (dir) => write(dir, 'docs/other.md', '# Other\n') }, (repos) => {
    const { integrated, result, output } = work(repos, { inputs: MERGE_INPUTS, env: { SPECKIT_GUIDANCE: 'Unused without an agent.' }, verify: { outcome: 'success', checks: 'platform build and tests,Markdown check' } });
    assert.deepEqual(integrated.result.conflicts, []);
    assert.equal(integrated.result.baseSha, remoteSha(repos, 'main'));
    assert.equal(integrated.result.head, remoteSha(repos, 'speckit/f'));
    assert.match(output, /conflicts=false\nagent=false\nverify=true\nenvironment=false\n/);
  assert.doesNotMatch(output, /prompt=/, 'the resolution\'s prompt lives in the Spec Kit workflow resolve');
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
  const { integrated, result, output } = work(repos, {
    inputs: RESOLVE_INPUTS,
    env: { SPECKIT_GUIDANCE: '  Preserve\nboth migration sequences. ' },
    agent: resolveShared,
    verify: { outcome: 'success', checks: 'platform build and tests' },
  });
  assert.match(output, /conflicts=true\nagent=true\nverify=false\nenvironment=false\n/);
  assert.deepEqual(integrated.result.conflicts, ['docs/shared.md']);
  assert.deepEqual([result.verified, result.resolutions], [true, [{ path: 'docs/shared.md', deleted: false, blob: '0' }]]);

  const baseSha = remoteSha(repos, 'main');
  const branchSha = remoteSha(repos, 'speckit/f');
  const github = fakeGitHub();
  const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
  assert.equal(landed.exitCode, 0);
  const [head, parents] = remoteHead(repos).split('|')[0].split(/ (.*)/);
  assert.equal(head, landed.head);
  assert.equal(parents, `${branchSha} ${baseSha}`);
  assert.match(remoteHead(repos), /\|Merge main into speckit\/f$/);
  assert.equal(remoteShow(repos, 'speckit/f:docs/shared.md'), '# Shared\n\nbranch line\nmain line\n');
  assert.deepEqual([landed.check.conclusion, landed.check.output.title], ['success', 'Merge conflicts resolved']);
  assert.match(github.comments.at(-1).body, /\*\*Conflicts resolved\*\* \(attempt 1\): `docs\/shared\.md`[\s\S]*platform build and tests passed[\s\S]*Next: merge/);
}));

test('required sync verifies and lands clean, conflicted and already-current merges without empty commits', async () => {
  for (const options of [{}, { branch: (dir) => write(dir, 'docs/own.md', '# Implementation delta\n') }, { main: (dir) => write(dir, 'docs/base.md', '# Base fix\n') }, conflicting]) {
    await withRepos(options, async (repos) => {
      const github = fakeGitHub();
      await github.createComment(9, renderSyncComment('Required sync'));
      const barrier = syncBarrier(github.comments);
      const before = remoteSha(repos, 'speckit/f');
      const { packaged, result } = work(repos, {
        inputs: RESOLVE_INPUTS,
        env: { SPECKIT_REQUIRED_SYNC: barrier.request },
        agent: options === conflicting ? resolveShared : undefined,
      });
      assert.equal(packaged.verify, true, 'clean and no-op merges still run full verification');
      assert.equal(result.verified, true);
      assert.equal(readFileSync(path.join(repos.resultDir, 'changed-files.txt'), 'utf8'), result.changedFiles.map((file) => `${file}\n`).join(''));
      if (options.branch && options !== conflicting) assert.ok(result.changedFiles.includes('docs/own.md'), 'no-op sync includes the implementation delta, not just conflicts');
      const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
      assert.equal(landed.exitCode, 0, landed.reasons?.join('; '));
      const completion = syncBarrier(github.comments).completion;
      assert.equal(completion.state, 'verified', 'landing does not itself release the latest-base barrier');
      for (const comment of github.comments) {
        assert.notEqual(comment.body.replace(/<!--[\s\S]*?-->/g, '').trim(), '', 'no comment consists only of markers');
      }
      assert.equal(completion.guidanceConsumed, options === conflicting, 'only a successful Copilot conflict resolution consumes sync guidance');
      assert.equal(completion.baseSha, remoteSha(repos, 'main'));
      if (!options.main && options !== conflicting) {
        assert.equal(remoteSha(repos, 'speckit/f'), before, 'already-current sync creates no empty commit');
      } else {
        const parents = remoteHead(repos).split('|')[0].split(' ');
        assert.equal(parents.length, 3, 'the landed commit is a true two-parent merge');
      }
    });
  }
});

test('required sync records original-scope verification without resetting exhausted repair attempts', async () => {
  for (const scope of ['task', 'group', 'phase', 'merge']) {
    await withRepos({}, async (repos) => {
      const github = fakeGitHub();
      const repair = { id: `${scope}:original`, state: 'pending', scope, tasks: ['T001'], at: github.tick() };
      await github.createComment(9, renderRepairMarker(repair));
      await github.createComment(9, renderSyncComment('Required sync'));
      const original = path.join(repos.base, 'failed-changed-files.txt');
      writeFileSync(original, 'docs/shared.md\n');
      const { result } = work(repos, {
        inputs: RESOLVE_INPUTS,
        env: {
          SPECKIT_REQUIRED_SYNC: syncBarrier(github.comments).request,
          SPECKIT_SYNC_REPAIR: JSON.stringify(unresolvedRepair(github.comments)),
          SPECKIT_SYNC_ORIGINAL_SCOPE: original,
        },
      });
      assert.equal(result.syncRepair.covered, true);
      assert.ok(result.changedFiles.includes('docs/shared.md'), 'the original failed paths are verified even after a clean/no-op merge');
      const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
      assert.equal(landed.exitCode, 0, landed.reasons?.join('; '));
      assert.equal(syncBarrier(github.comments).completion.repair.id, repair.id);
      assert.equal(syncBarrier(github.comments).completion.repair.covered, true);
      assert.deepEqual(syncBarrier(github.comments).completion.repair.snapshot, unresolvedRepair(github.comments));
      assert.equal(unresolvedRepair(github.comments).id, repair.id, 'the final latest-base check still gates clearance');
    });
  }
});

test('required sync reconciles only base-equivalent original uncovered paths and retains their test selection', async () => {
  for (const variant of ['unchanged', 'content', 'mode']) {
    const changed = variant !== 'unchanged';
    await withRepos({ seed: (dir) => write(dir, '.github/workflows/old.yml', 'original\n'),
      ...(changed ? { branch: (dir) => {
        if (variant === 'content') write(dir, '.github/workflows/old.yml', 'changed\n');
        else {
          git(dir, 'config', 'core.filemode', 'false');
          git(dir, 'update-index', '--chmod=+x', '.github/workflows/old.yml');
        }
      } } : {}) }, async (repos) => {
      const github = fakeGitHub();
      await github.createComment(9, renderRepairMarker({ id: 'merge:original', state: 'pending', scope: 'merge', at: github.tick() }));
      await github.createComment(9, renderSyncComment('Required sync'));
      const original = path.join(repos.base, 'failed-changed-files.txt');
      writeFileSync(original, '.github/workflows/old.yml\n.github/workflows/reverted-addition.yml\n');
      const { result } = work(repos, {
        inputs: RESOLVE_INPUTS,
        env: {
          SPECKIT_REQUIRED_SYNC: syncBarrier(github.comments).request,
          SPECKIT_SYNC_REPAIR: JSON.stringify(unresolvedRepair(github.comments)),
          SPECKIT_SYNC_ORIGINAL_SCOPE: original,
        },
        verify: { outcome: 'success', checks: 'original checks', uncovered: '.github/workflows/old.yml,.github/workflows/reverted-addition.yml' },
      });
      assert.equal(result.verified, !changed, result.reasons.join('; '));
      assert.ok(result.changedFiles.includes('.github/workflows/old.yml'));
      assert.deepEqual(result.reportedUncovered, ['.github/workflows/old.yml', '.github/workflows/reverted-addition.yml']);
      assert.deepEqual(result.uncovered, changed ? ['.github/workflows/old.yml'] : []);
      assert.equal(result.syncRepair.covered, !changed);
      const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
      assert.equal(landed.exitCode, changed ? 1 : 0, landed.reasons?.join('; '));
    });
  }
});

test('required sync trusted landing rejects forged base-equivalence evidence for a changed workflow', async () => {
  await withRepos({ seed: (dir) => write(dir, '.github/workflows/old.yml', 'original\n'),
    branch: (dir) => write(dir, '.github/workflows/old.yml', 'changed\n') }, async (repos) => {
    const github = fakeGitHub();
    await github.createComment(9, renderRepairMarker({ id: 'merge:original', state: 'pending', scope: 'merge', at: github.tick() }));
    await github.createComment(9, renderSyncComment('Required sync'));
    const original = path.join(repos.base, 'failed-changed-files.txt');
    writeFileSync(original, '.github/workflows/old.yml\n');
    const { result } = work(repos, {
      inputs: RESOLVE_INPUTS,
      env: { SPECKIT_REQUIRED_SYNC: syncBarrier(github.comments).request, SPECKIT_SYNC_REPAIR: JSON.stringify(unresolvedRepair(github.comments)), SPECKIT_SYNC_ORIGINAL_SCOPE: original },
      verify: { outcome: 'success', checks: 'original checks', uncovered: '.github/workflows/old.yml' },
      between: ({ result: packaged }) => {
        packaged.syncRepair.baseEquivalent = ['.github/workflows/old.yml'];
        writeFileSync(path.join(repos.resultDir, 'result.json'), JSON.stringify(packaged));
      },
    });
    assert.equal(result.verified, true, 'the worker result claims equivalence');
    const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 1);
    assert.match(landed.reasons.join('; '), /uncovered path without trusted base-equivalence evidence/);
    assert.equal(syncBarrier(github.comments).completion, null);
  });
});

test('required sync finalize is a full-scope superset but cannot clear a replaced pending repair', async () => {
  for (const scope of ['task', 'group', 'phase']) {
    for (const replaced of [false, true]) {
      await withRepos({}, async (repos) => {
        const github = fakeGitHub();
        const repair = { id: `${scope}:original`, state: 'pending', scope, tasks: ['T001'], at: github.tick() };
        await github.createComment(9, renderRepairMarker(repair));
        await github.createComment(9, renderSyncComment('Required sync'));
        const { result } = work(repos, {
          inputs: RESOLVE_INPUTS,
          env: {
            SPECKIT_REQUIRED_SYNC: syncBarrier(github.comments).request,
            SPECKIT_SYNC_REPAIR: JSON.stringify(unresolvedRepair(github.comments)),
          },
        });
        assert.equal(result.verificationMode, 'finalize');
        assert.equal(result.syncRepair.covered, true, 'every configured check with all tests covers the original task/group/phase even if its artifact expired');
        if (replaced) await github.createComment(9, renderRepairMarker({ ...repair, tasks: ['T002'] }));
        const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
        assert.equal(landed.exitCode, 0);
        assert.equal(Boolean(syncBarrier(github.comments).completion.repair), !replaced, 'a same-id replacement is not the scope captured at work start');
      });
    }
  }
});

test('required sync cannot claim merged environment coverage with missing evidence or failed self-tests', async () => {
  for (const scenario of ['missing evidence', 'environment self-test failure', 'environment self-test success']) {
    await withRepos({}, async (repos) => {
      const github = fakeGitHub();
      const repair = { id: 'merge:original', state: 'pending', scope: 'merge', at: github.tick() };
      await github.createComment(9, renderRepairMarker(repair));
      await github.createComment(9, renderSyncComment('Required sync'));
      const original = path.join(repos.base, 'failed-changed-files.txt');
      if (scenario !== 'missing evidence') writeFileSync(original, '.github/actions/environment-verify/action.yml\n');
      const { result } = work(repos, {
        inputs: RESOLVE_INPUTS,
        env: {
          SPECKIT_REQUIRED_SYNC: syncBarrier(github.comments).request,
          SPECKIT_SYNC_REPAIR: JSON.stringify(unresolvedRepair(github.comments)),
          SPECKIT_SYNC_ORIGINAL_SCOPE: original,
          ...(scenario === 'environment self-test success' ? { SPECKIT_SYNC_SETUP_OUTCOME: 'success', SPECKIT_SYNC_VERIFY_OUTCOME: 'success' } : {}),
        },
      });
      assert.equal(result.syncRepair.covered, scenario === 'environment self-test success');
      const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
      assert.equal(landed.exitCode, scenario === 'environment self-test failure' ? 1 : 0);
      assert.equal(Boolean(syncBarrier(github.comments).completion?.repair), scenario === 'environment self-test success');
    });
  }
});

test('required sync rejects failed, skipped, missing and uncovered verification without releasing the barrier', async () => {
  for (const verify of [null, { outcome: 'failure', checks: 'tests' }, { outcome: 'skipped', checks: '' }, { outcome: 'success', checks: 'tests', uncovered: 'docs/shared.md' }]) {
    await withRepos({}, async (repos) => {
      const github = fakeGitHub();
      await github.createComment(9, renderSyncComment('Required sync'));
      const barrier = syncBarrier(github.comments);
      const before = remoteHead(repos);
      const { result } = work(repos, { inputs: RESOLVE_INPUTS, verify, env: { SPECKIT_REQUIRED_SYNC: barrier.request } });
      assert.equal(result.verified, false);
      const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
      assert.equal(landed.exitCode, 1);
      assert.equal(remoteHead(repos), before);
      assert.equal(syncBarrier(github.comments).completion, null);
    });
  }
});

test('required sync rejects an unfinished work job or a replay that does not match the verified tree', async () => {
  for (const failure of ['cancelled', 'tree mismatch']) {
    await withRepos({}, async (repos) => {
      const github = fakeGitHub();
      await github.createComment(9, renderSyncComment('Required sync'));
      work(repos, { inputs: RESOLVE_INPUTS, env: { SPECKIT_REQUIRED_SYNC: syncBarrier(github.comments).request } });
      if (failure === 'tree mismatch') {
        const file = path.join(repos.resultDir, 'result.json');
        const result = JSON.parse(readFileSync(file, 'utf8'));
        result.tree = '0'.repeat(40);
        writeFileSync(file, JSON.stringify(result));
      }
      const landed = await land(repos, github, { inputs: RESOLVE_INPUTS, workResult: failure === 'cancelled' ? 'cancelled' : 'success' });
      assert.equal(landed.exitCode, 1);
      assert.equal(syncBarrier(github.comments).completion, null);
    });
  }
});

test('required sync does not downgrade protected conflicts to best effort or land work from an older request', async () => {
  await withRepos({
    branch: (dir) => write(dir, '.specify/memory/constitution.md', '# Branch constitution\n'),
    main: (dir) => write(dir, '.specify/memory/constitution.md', '# Base constitution\n'),
  }, async (repos) => {
    const github = fakeGitHub();
    await github.createComment(9, renderSyncComment('Required sync'));
    const barrier = syncBarrier(github.comments);
    work(repos, { inputs: RESOLVE_INPUTS, env: { SPECKIT_REQUIRED_SYNC: barrier.request } });
    await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: remoteSha(repos, 'speckit/f'), external_id: CHECK_SYNC_CONFLICT, status: 'completed', conclusion: 'neutral' });
    const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 1);
    assert.equal(syncBarrier(github.comments).completion, null);
    assert.ok(github.repo.pulls[0].labels.some((label) => label.name === 'help wanted'));
  });
  await withRepos({}, async (repos) => {
    const github = fakeGitHub();
    await github.createComment(9, renderSyncComment('First request'));
    work(repos, { inputs: RESOLVE_INPUTS, env: { SPECKIT_REQUIRED_SYNC: syncBarrier(github.comments).request } });
    await github.createComment(9, renderSyncComment('Newer request'));
    const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 1);
    assert.match(landed.reasons.join('; '), /request changed/);
    assert.equal(syncBarrier(github.comments).completion, null);
  });
});

test('a required sync arriving during ordinary work prevents that work landing', () => withRepos({}, async (repos) => {
  const github = fakeGitHub();
  work(repos, { agent: (r) => agentDoesT001(r.work) });
  await github.createComment(9, renderSyncComment('Sync before any more work'));
  const before = remoteHead(repos);
  const landed = await land(repos, github);
  assert.ok(landed.requeue);
  assert.equal(remoteHead(repos), before);
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[ \] T001/);
  const merge = await runMergeLand({
    client: github, env: landEnv(repos), inputs: { ...MERGE_INPUTS, checkRun: landed.check.id },
    resultDir: repos.resultDir, workResult: 'success', log: silent,
  });
  assert.ok(merge.requeue, 'a merge already in flight must also respect a newer sync request');
  assert.equal(github.repo.merges.length, 0);
  const archive = await runArchive({
    client: github, git: defaultGit(repos.land),
    env: { ...landEnv(repos), SPECKIT_PULL: '9', SPECKIT_CHECK_RUN: String(landed.check.id) },
    folder: 'f', log: silent,
  });
  assert.equal(archive.exitCode, 1);
  assert.equal(remoteHead(repos), before, 'archiving cannot change the branch while sync is pending');
}));

test('a sync accepted during landing preparation is rechecked before task publication', () => withRepos({}, async (repos) => {
  const github = fakeGitHub();
  work(repos, { agent: (r) => agentDoesT001(r.work) });
  const before = remoteHead(repos);
  const getPull = github.getPullRequest.bind(github);
  github.getPullRequest = async (number) => {
    const pull = await getPull(number);
    await github.createComment(9, renderSyncComment('Sync accepted during getPullRequest'));
    github.getPullRequest = getPull;
    return pull;
  };
  const landed = await land(repos, github);
  assert.ok(landed.requeue);
  assert.equal(remoteHead(repos), before);
}));

test('a sync accepted after a rejected task push prevents every subsequent push retry', () => withRepos({}, async (repos) => {
  const github = fakeGitHub();
  work(repos, { agent: (r) => agentDoesT001(r.work) });
  const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-head', status: 'in_progress', external_id: CHECK_ATTEMPT });
  const realGit = defaultGit(repos.land);
  let pushes = 0;
  let siblingHead;
  const racingGit = (args) => {
    if (args.includes('push')) {
      pushes++;
      if (pushes === 1) {
        const seed = path.join(repos.base, 'seed');
        write(seed, 'docs/sibling.md', '# Parallel task\n');
        git(seed, 'add', '-A');
        git(seed, 'commit', '-q', '-m', 'parallel task');
        git(seed, 'push', '-q', repos.bare, 'HEAD:refs/heads/speckit/f');
        siblingHead = remoteHead(repos);
        github.createComment(9, renderSyncComment('Sync accepted after the rejected push'));
        return { status: 1, stdout: '', stderr: 'push rejected: branch moved' };
      }
    }
    return realGit(args);
  };
  const landed = await runLand({
    client: github, git: racingGit, env: landEnv(repos), inputs: { ...TASK_INPUTS, checkRun: check.id },
    folder: 'f', workspace: repos.land, resultDir: repos.resultDir, workResult: 'success', log: silent,
  });
  assert.ok(landed.requeue);
  assert.equal(pushes, 1);
  assert.equal(remoteHead(repos), siblingHead, 'only the previously landed sibling remains on the branch');
  assert.match(remoteShow(repos, 'speckit/f:specs/f/tasks.md'), /- \[ \] T001/);
}));

test('a sync accepted during archive preparation prevents its publication', () => withRepos({ tasks: DONE_TASKS }, async (repos) => {
  const github = fakeGitHub();
  const before = remoteHead(repos);
  const realGit = defaultGit(repos.land);
  const racingGit = (args) => {
    const result = realGit(args);
    if (args.includes('commit') && result.status === 0) github.createComment(9, renderSyncComment('Sync accepted during archive preparation'));
    return result;
  };
  const archived = await runArchive({
    client: github, git: racingGit,
    env: { ...landEnv(repos), SPECKIT_PULL: '9' }, folder: 'f', log: silent,
  });
  assert.equal(archived.exitCode, 1);
  assert.ok(syncBarrier(github.comments), 'the request was interleaved after the archive entry check');
  assert.equal(remoteHead(repos), before);
}));

test('resolve: markers, changes outside the conflicts, and forged resolutions are rejected', async () => {
  await withRepos(conflicting, (repos) => {
    const { result } = work(repos, { inputs: RESOLVE_INPUTS, agent: () => {} });
    assert.match(result.reasons.join(), /`docs\/shared\.md` still contains conflict markers/);
  });
  await withRepos(conflicting, (repos) => {
    const { result } = work(repos, { inputs: RESOLVE_INPUTS, agent: (r) => { resolveShared(r); write(r.work, 'README.md', '# Changed\n'); write(r.work, 'new.txt', 'x'); write(r.work, '.github/x.yml', 'x'); } });
    assert.match(result.reasons.join(), /changed protected files: \.github\/x\.yml/);
    assert.deepEqual(result.baseOwned, ['README.md']);
  });
  await withRepos(conflicting, (repos) => {
    const { result } = work(repos, { inputs: RESOLVE_INPUTS, agent: (r) => { resolveShared(r); rmSync(path.join(r.work, 'README.md')); } });
    assert.match(result.reasons.join(), /deleted files that only the base branch owns: README\.md/);
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
    writeFileSync(resultFile, JSON.stringify({ ...JSON.parse(readFileSync(resultFile, 'utf8')), baseSha: remoteSha(repos, 'other') }));
    const before = remoteHead(repos);
    const landed = await land(repos, fakeGitHub(), { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 1);
    assert.match(landed.reasons.join(), /is not on the base branch main/);
    assert.equal(remoteHead(repos), before);
  });
});

const ENVIRONMENT_ACTION = '.github/actions/environment-setup/action.yml';

// Both sides add migration 0017 (no textual conflict) and change docs/shared.md (a conflict).
const clashingMigrations = {
  tasks: DONE_TASKS,
  branch: (dir) => {
    conflicting.branch(dir);
    write(dir, 'db/0017_feature.sql', 'create table feature();\n');
    write(dir, 'docs/feature.md', 'Uses db/0017_feature.sql.\n');
  },
  main: (dir) => {
    conflicting.main(dir);
    write(dir, 'db/0017_other.sql', 'create table other();\n');
    write(dir, 'db/0018_more.sql', 'create table more();\n');
  },
};
const renumberMigration = (r) => {
  resolveShared(r);
  git(r.work, 'mv', 'db/0017_feature.sql', 'db/0019_feature.sql');
  write(r.work, 'docs/feature.md', 'Uses db/0019_feature.sql.\n');
};

test('resolve: clashes without a textual conflict are fixed in the implementation\'s own files and replayed', () => withRepos(clashingMigrations, async (repos) => {
  const { result, output } = work(repos, { inputs: RESOLVE_INPUTS, agent: renumberMigration, verify: { outcome: 'success', checks: 'platform build and tests' } });
  assert.match(output, /agent=true/);
  assert.deepEqual(result.reasons, []);
  assert.deepEqual(result.changes, [
    { path: 'db/0017_feature.sql', deleted: true },
    { path: 'db/0019_feature.sql', deleted: false, blob: 'c1' },
    { path: 'docs/feature.md', deleted: false, blob: 'c2' },
  ]);
  const github = fakeGitHub();
  const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
  assert.equal(landed.exitCode, 0, landed.reasons?.join());
  const tree = git(repos.base, `--git-dir=${repos.bare}`, 'ls-tree', '--name-only', 'speckit/f', 'db/').trim().split('\n');
  assert.deepEqual(tree, ['db/0017_other.sql', 'db/0018_more.sql', 'db/0019_feature.sql']);
  assert.equal(remoteShow(repos, 'speckit/f:docs/feature.md'), 'Uses db/0019_feature.sql.\n');
  assert.match(github.comments.at(-1).body, /Also adapted this implementation's own files: `db\/0017_feature\.sql`, `db\/0019_feature\.sql`, `docs\/feature\.md`/);
}));

test('resolve: the land job records changes to files only the base branch owns, whatever the work result claims', () => withRepos(clashingMigrations, async (repos) => {
  const fixBaseFile = (r) => {
    renumberMigration(r);
    write(r.work, 'db/0018_more.sql', 'create table more(feature_id int);\n');
  };
  const { result } = work(repos, { inputs: RESOLVE_INPUTS, agent: fixBaseFile, verify: { outcome: 'success', checks: 'platform build and tests' } });
  assert.deepEqual([result.reasons, result.baseOwned], [[], ['db/0018_more.sql']]);
  const resultFile = path.join(repos.resultDir, 'result.json');
  writeFileSync(resultFile, JSON.stringify({ ...JSON.parse(readFileSync(resultFile, 'utf8')), baseOwned: [] }));
  const github = fakeGitHub();
  const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
  assert.equal(landed.exitCode, 0, landed.reasons?.join());
  assert.equal(remoteShow(repos, 'speckit/f:db/0018_more.sql'), 'create table more(feature_id int);\n');
  const body = github.comments.at(-1).body;
  assert.deepEqual(baseOwnedChanges(github.comments), ['db/0018_more.sql']);
  assert.match(body, /Also adapted this implementation's own files: `db\/0017_feature\.sql`, `db\/0019_feature\.sql`, `docs\/feature\.md`\n/);
  assert.match(body, /Also adapted files of `main` that the merged result needed: `db\/0018_more\.sql`\. The final merge waits for a person's review/);
  assert.match(body, /<details><summary>Changes to files of `main`<\/summary>[\s\S]*```diff[\s\S]*-create table more\(\);\n\+create table more\(feature_id int\);/);
  assert.match(remoteHead(repos), /\|Merge main into speckit\/f$/);
}));

test('required sync retains base-owned review evidence while verifying the replayed tree', () => withRepos(clashingMigrations, async (repos) => {
  const github = fakeGitHub();
  await github.createComment(9, renderSyncComment('Integrate the upstream fix'));
  const barrier = syncBarrier(github.comments);
  const { result } = work(repos, {
    inputs: RESOLVE_INPUTS,
    env: { SPECKIT_REQUIRED_SYNC: barrier.request },
    agent: (r) => {
      renumberMigration(r);
      write(r.work, 'db/0018_more.sql', 'create table more(feature_id int);\n');
    },
    verify: { outcome: 'success', checks: 'platform build and tests' },
  });
  assert.deepEqual(result.baseOwned, ['db/0018_more.sql']);
  const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
  assert.equal(landed.exitCode, 0, landed.reasons?.join());
  assert.deepEqual(baseOwnedChanges(github.comments), ['db/0018_more.sql']);
  assert.ok(github.comments.some((comment) => /Base synchronized and verified[\s\S]*final merge waits for a person's review/.test(comment.body)));
  assert.equal(syncBarrier(github.comments).completion.state, 'verified', 'only the planner can release the barrier after rechecking the current base');
}));

test('resolve: the land job rejects forged changes outside the implementation\'s own files', async () => {
  const forge = async (repos, changes, content = 'x\n') => {
    work(repos, { inputs: RESOLVE_INPUTS, agent: renumberMigration });
    const resultFile = path.join(repos.resultDir, 'result.json');
    writeFileSync(path.join(repos.resultDir, 'resolved', 'forged'), content);
    writeFileSync(resultFile, JSON.stringify({ ...JSON.parse(readFileSync(resultFile, 'utf8')), changes }));
    const before = remoteHead(repos);
    const landed = await land(repos, fakeGitHub(), { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 1);
    assert.equal(remoteHead(repos), before);
    return landed.reasons.join();
  };
  await withRepos(clashingMigrations, async (repos) => {
    assert.match(await forge(repos, [{ path: 'db/0018_more.sql', deleted: true }]), /deleted files that only the base branch owns: db\/0018_more\.sql/);
  });
  await withRepos(clashingMigrations, async (repos) => {
    assert.match(await forge(repos, [{ path: '.github/workflows/x.yml', deleted: false, blob: 'forged' }]), /changed protected files/);
  });
  await withRepos(clashingMigrations, async (repos) => {
    assert.match(await forge(repos, [{ path: '../outside.txt', deleted: false, blob: 'forged' }]), /invalid paths: \.\.\/outside\.txt/);
  });
  await withRepos(clashingMigrations, async (repos) => {
    assert.match(await forge(repos, [{ path: '.git/hooks/post-commit', deleted: false, blob: 'forged' }]), /invalid paths/);
  });
  await withRepos(clashingMigrations, async (repos) => {
    assert.match(await forge(repos, [{ path: 'docs/shared.md', deleted: false, blob: 'forged' }]), /invalid paths: docs\/shared\.md/, 'conflicts are replayed only from their resolutions');
  });
  await withRepos(clashingMigrations, async (repos) => {
    assert.match(await forge(repos, [{ path: 'docs/feature.md', deleted: false, blob: 'forged' }], '<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> main\n'), /`docs\/feature\.md` contains conflict markers/);
  });
});

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

test('merge: integrate with a pinned base rebuilds the same merged tree after the base branch moved', async () => {
  await withRepos({ tasks: DONE_TASKS, branch: (dir) => write(dir, 'docs/x.md', '# X\n'), main: (dir) => write(dir, 'docs/m.md', '# M\n') }, (repos) => {
    const planned = work(repos, { inputs: MERGE_INPUTS });
    const { baseSha, tree } = planned.integrated.result;
    assert.match(planned.output, new RegExp(`^base_sha=${baseSha}$`, 'm'));
    assert.match(planned.output, new RegExp(`^tree=${tree}$`, 'm'));
    assert.equal(tree, git(repos.work, 'write-tree').trim());
    // The base branch moves on before a shard starts.
    const mover = path.join(repos.base, 'mover');
    git(repos.base, 'clone', '-q', '--branch', 'main', repos.bare, mover);
    write(mover, 'docs/later.md', '# Later\n');
    git(mover, 'add', '-A');
    git(mover, 'commit', '-q', '-m', 'later');
    git(mover, 'push', '-q', 'origin', 'HEAD:main');
    git(repos.land, 'fetch', '-q', 'origin');
    const run = (env) => runIntegrate({ git: defaultGit(repos.land), env: { SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_BASE_BRANCH: 'main', ...env }, inputs: MERGE_INPUTS, folder: 'f', resultDir: path.join(repos.base, `shard-${Object.keys(env).length}`), log: silent });
    const pinned = run({ SPECKIT_BASE_SHA: baseSha });
    assert.deepEqual([pinned.result.baseSha, pinned.result.tree], [baseSha, tree], 'a shard verifies exactly the planned tree');
    git(repos.land, 'merge', '--abort');
    assert.notEqual(run({}).result.tree, tree, 'without the pin the moved base branch changes the tree');
    git(repos.land, 'merge', '--abort');
    assert.throws(() => run({ SPECKIT_BASE_SHA: 'main' }), /SPECKIT_BASE_SHA is not a commit SHA/);
  });
});

test('merge: the verdict of a sharded verification names every failed check and every missing shard', async () => {
  const combined = {
    SPECKIT_VERIFY_COMBINED: 'true',
    SPECKIT_VERIFY_FAILED: 'platform build and tests,Web UI build and tests',
    SPECKIT_VERIFY_PROBLEMS: 'verification shard 3/3 (manager) did not report\n',
  };
  await withRepos({ tasks: DONE_TASKS, branch: (dir) => write(dir, 'docs/x.md', '# X\n') }, (repos) => {
    const { result } = work(repos, { inputs: MERGE_INPUTS, env: combined, verify: { outcome: 'failure', checks: 'Markdown check,platform build and tests,Web UI build and tests' } });
    assert.equal(result.verified, false);
    assert.deepEqual(result.checks, [
      { name: 'Markdown check', passed: true },
      { name: 'platform build and tests', passed: false },
      { name: 'Web UI build and tests', passed: false },
    ]);
    assert.deepEqual(result.reasons, ['platform build and tests, Web UI build and tests failed', 'verification shard 3/3 (manager) did not report']);
  });
  await withRepos({ tasks: DONE_TASKS, branch: (dir) => write(dir, 'docs/x.md', '# X\n') }, (repos) => {
    const { result } = work(repos, { inputs: MERGE_INPUTS, env: { SPECKIT_VERIFY_COMBINED: 'true' }, verify: { outcome: 'success', checks: 'Markdown check,platform build and tests' } });
    assert.equal(result.verified, true);
    assert.ok(result.checks.every((check) => check.passed));
  });
  await withRepos({ tasks: DONE_TASKS, branch: (dir) => write(dir, 'docs/x.md', '# X\n') }, (repos) => {
    const { result } = work(repos, { inputs: MERGE_INPUTS, env: { SPECKIT_VERIFY_COMBINED: 'true' }, verify: { outcome: 'failure', checks: '' } });
    assert.deepEqual(result.reasons, ['the verification did not succeed (failure)']);
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

// Two specs that reference each other: the branch archives its own spec `f`, and main archives `g` meanwhile, so both
// rewrite the same lines of each other's spec.md.
const crossArchived = {
  tasks: DONE_TASKS,
  seed: (dir) => {
    write(dir, 'specs/f/spec.md', '# Spec\n\nDepends on `specs/g`.\n');
    write(dir, 'specs/g/spec.md', '# G\n\nUsed by `specs/f`.\n');
    write(dir, 'specs/g/tasks.md', DONE_TASKS);
    write(dir, 'specs/README.md', '| [f](f/spec.md) | [g](g/spec.md) |\n');
  },
  branch: (dir) => archiveSpec({ root: dir, folder: 'f', git: archiveGit(dir) }),
  main: (dir) => archiveSpec({ root: dir, folder: 'g', git: archiveGit(dir) }),
};

test('resolve: archive reference conflicts under specs/ are resolved by the archive script, without an agent', () => withRepos(crossArchived, async (repos) => {
  const { integrated, result, output } = work(repos, { inputs: RESOLVE_INPUTS, verify: { outcome: 'success', checks: 'Markdown check' } });
  assert.deepEqual(integrated.result.conflicts, [], 'no conflict is left for an agent or a person');
  assert.ok(integrated.result.specResolved.length > 0);
  assert.match(output, /agent=false/);
  assert.equal(integrated.result.attention, undefined);
  assert.equal(result.verified, true, JSON.stringify(result));
  const github = fakeGitHub();
  const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
  assert.equal(landed.exitCode, 0, landed.reasons?.join('; '));
  assert.equal(remoteShow(repos, 'speckit/f:specs/archive/f/spec.md'), '# Spec\n\nDepends on `specs/archive/g`.\n');
  assert.equal(remoteShow(repos, 'speckit/f:specs/archive/g/spec.md'), '# G\n\nUsed by `specs/archive/f`.\n');
  assert.equal(remoteShow(repos, 'speckit/f:specs/README.md'), '| [f](archive/f/spec.md) | [g](archive/g/spec.md) |\n');
  assert.match(github.comments.at(-1).body, /- Archive reference conflicts resolved by the archive script, without an agent: /);
}));

test('resolve: conflicts in the spec\'s own artifacts go to the resolution, keep every task, and hold the merge for review', async () => {
  await withRepos({
    tasks: DONE_TASKS,
    branch: (dir) => write(dir, 'specs/f/spec.md', '# Spec\n\nbranch\n'),
    main: (dir) => write(dir, 'specs/f/spec.md', '# Spec\n\nmain\n'),
  }, async (repos) => {
    const { integrated, output } = work(repos, {
      inputs: RESOLVE_INPUTS,
      agent: (r) => write(r.work, 'specs/f/spec.md', '# Spec\n\nbranch\nmain\n'),
      verify: { outcome: 'success', checks: 'Markdown check' },
    });
    assert.deepEqual([integrated.result.conflicts, integrated.result.attention], [['specs/f/spec.md'], undefined]);
    assert.match(output, /agent=true/);
    const github = fakeGitHub();
    const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 0, landed.reasons?.join('; '));
    assert.deepEqual(baseOwnedChanges(github.comments), ['specs/f/spec.md'], 'the merge waits for a person');
    assert.match(github.comments.at(-1).body, /Resolved conflicts in this spec's own artifacts[^\n]*`specs\/f\/spec\.md`/);
  });
  await withRepos({
    tasks: TASKS,
    branch: (dir) => write(dir, 'specs/f/tasks.md', TASKS.replace('- [ ] T001', '- [x] T001')),
    main: (dir) => write(dir, 'specs/f/tasks.md', TASKS.replace('T001 Create docs/x.md', 'T001 Create docs/x.md with a title')),
  }, async (repos) => {
    work(repos, {
      inputs: RESOLVE_INPUTS,
      agent: (r) => write(r.work, 'specs/f/tasks.md', TASKS.replace('T001 Create docs/x.md', 'T001 Create docs/x.md with a title')),
    });
    const landed = await land(repos, fakeGitHub(), { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 1);
    assert.match(landed.reasons.join('; '), /the resolution of `specs\/f\/tasks\.md` unchecked tasks: T001/);
  });
});

test('a file the base branch added that links to the spec this branch archived is rewritten after the merge, without a conflict', async () => {
  const options = {
    ...crossArchived,
    main: (dir) => write(dir, 'specs/h/spec.md', '# H\n\nSee [f](../f/spec.md) and `specs/f`.\n'),
  };
  await withRepos(options, (repos) => {
    const { integrated, result } = work(repos, { inputs: MERGE_INPUTS, verify: { outcome: 'success', checks: 'Markdown check' } });
    assert.deepEqual([integrated.result.conflicts, integrated.result.reasons], [[], []], 'the reference rewrite is allowed outside the own spec folder');
    assert.deepEqual(integrated.result.referencesRefreshed, ['specs/h/spec.md']);
    assert.equal(readFileSync(path.join(repos.work, 'specs/h/spec.md'), 'utf8').replace(/\r\n/g, '\n'), '# H\n\nSee [f](../archive/f/spec.md) and `specs/archive/f`.\n');
    assert.ok(result.changedFiles.includes('specs/h/spec.md'));
  });
  await withRepos({ ...options, branch: crossArchived.branch }, async (repos) => {
    const github = fakeGitHub();
    await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: remoteSha(repos, 'speckit/f'), external_id: CHECK_SYNC_CONFLICT, status: 'completed', conclusion: 'neutral' });
    const { integrated } = work(repos, { inputs: RESOLVE_INPUTS, verify: { outcome: 'success', checks: 'Markdown check' } });
    assert.deepEqual(integrated.result.referencesRefreshed, ['specs/h/spec.md']);
    const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
    assert.equal(landed.exitCode, 0, landed.reasons?.join('; '));
    assert.equal(remoteShow(repos, 'speckit/f:specs/h/spec.md'), '# H\n\nSee [f](../archive/f/spec.md) and `specs/archive/f`.\n', 'the land replay rewrites it the same way');
    assert.match(github.comments.at(-1).body, /- References to archived specs rewritten by the archive script after the merge: `specs\/h\/spec\.md`/);
  });
});

test('merge: changes under specs/ outside the own spec must be reference rewrites', async () => {
  await withRepos({ ...crossArchived, main: undefined }, (repos) => {
    const { integrated } = work(repos, { inputs: MERGE_INPUTS });
    assert.deepEqual(integrated.result.reasons, [], 'the archive of the own spec rewrites references only');
  });
  await withRepos({ ...crossArchived, main: undefined, branch: (dir) => write(dir, 'specs/g/spec.md', '# G\n\nUsed by `specs/f`, and changed.\n') }, (repos) => {
    const { integrated } = work(repos, { inputs: MERGE_INPUTS });
    assert.match(integrated.result.reasons.join('; '), /changes under specs\/ outside specs\/f must be reference rewrites of the archive, but these differ: specs\/g\/spec\.md/);
  });
});

test('resolve: conflicts in protected paths ask the requester', () => withRepos({
  tasks: DONE_TASKS,
  branch: (dir) => write(dir, '.specify/memory/constitution.md', '# Constitution\n\nbranch\n'),
  main: (dir) => write(dir, '.specify/memory/constitution.md', '# Constitution\n\nmain\n'),
}, async (repos) => {
  const { integrated, output } = work(repos, { inputs: RESOLVE_INPUTS });
  assert.match(output, /agent=false/);
  assert.equal(integrated.result.attention, true);
  const github = fakeGitHub();
  const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
  assert.deepEqual([landed.exitCode, landed.check.external_id], [1, CHECK_LIMIT]);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:attempt [^\n]*"outcome":"attention"\} -->\n\*\*Implementation needs attention\*\* @dev: conflicts in protected paths need a person: \.specify\/memory\/constitution\.md/);
  assert.doesNotMatch(github.comments.at(-1).body, /Spec Kit orchestrate` workflow manually/);
  assert.match(github.comments.at(-1).body, /Wait for the diagnosis/);
}));

// The job "sync" recorded a best-effort conflict (before a phase or the convergence) on the head the resolution starts from.
const syncConflictOn = (github, repos) => github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: remoteSha(repos, 'speckit/f'), status: 'completed', conclusion: 'neutral', external_id: CHECK_SYNC_CONFLICT });

test('resolve before a phase or the convergence: success continues the work, and conflicts that need a person do not stop it', async () => {
  await withRepos({ ...conflicting, tasks: TASKS }, async (repos) => {
    work(repos, { inputs: RESOLVE_INPUTS, agent: resolveShared, verify: { outcome: 'success', checks: 'platform build and tests' } });
    const github = fakeGitHub();
    await syncConflictOn(github, repos);
    assert.equal((await land(repos, github, { inputs: RESOLVE_INPUTS })).exitCode, 0);
    assert.match(github.comments.at(-1).body, /\*\*Conflicts resolved\*\*[\s\S]*- Next: the work continues on the merged branch\./);
  });
  await withRepos({
    tasks: TASKS,
    branch: (dir) => write(dir, '.specify/memory/constitution.md', '# Constitution\n\nbranch\n'),
    main: (dir) => write(dir, '.specify/memory/constitution.md', '# Constitution\n\nmain\n'),
  }, async (repos) => {
    const { integrated } = work(repos, { inputs: RESOLVE_INPUTS });
    assert.equal(integrated.result.attention, true);
    const github = fakeGitHub();
    await syncConflictOn(github, repos);
    const landed = await land(repos, github, { inputs: RESOLVE_INPUTS });
    assert.deepEqual([landed.exitCode, landed.check.external_id === CHECK_LIMIT, landed.check.conclusion], [0, false, 'neutral'], 'the implementation does not stop');
    assert.match(github.comments.at(-1).body, new RegExp(`^<!-- speckit-implement:attempt [^\\n]*"outcome":"attention"\\} -->\\n<!-- speckit-implement:sync-skipped ${remoteSha(repos, 'main')} -->\\n\\*\\*\`main\` was not merged\\*\\*: conflicts in protected paths need a person`));
  });
});

// merge-land only reads the result of the merge workflow's verify job and talks to the GitHub API.
async function mergeLand(github, result, { env = {}, workResult = 'success' } = {}) {
  const resultDir = mkdtempSync(path.join(tmpdir(), 'speckit-merge-'));
  try {
    if (result) writeFileSync(path.join(resultDir, 'result.json'), JSON.stringify(result));
    const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-head', status: 'in_progress', external_id: CHECK_MERGE });
    const landed = await runMergeLand({ client: github, env: { SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_BASE_BRANCH: 'main', ...env }, inputs: { ...MERGE_INPUTS, checkRun: check.id, folder: 'f' }, resultDir, workResult, log: silent });
    return { ...landed, check: github.repo.checkRuns.find((run) => run.id === check.id) };
  } finally {
    rmSync(resultDir, { recursive: true, force: true });
  }
}

const verifiedMerge = { kind: 'merge', baseSha: 'sha-main', head: 'sha-head', conflicts: [], reasons: [], checks: [{ name: 'platform build and tests', passed: true }, { name: 'Markdown check', passed: true }], verification: 'configured', verified: true };

function mergeableGitHub() {
  const github = fakeGitHub({ body: '## Tasks (2)\n\n- [x] T001 Create docs/x.md\n- [x] T002 Create docs/y.md\n\nCloses #5\n' });
  github.setFile('sha-head', 'specs/f/tasks.md', DONE_TASKS);
  return github;
}

test('merge-land rechecks sync accepted during getPullRequest or immediately before the merge', async () => {
  for (const timing of ['getPullRequest', 'markReadyForReview']) {
    const github = mergeableGitHub();
    const original = github[timing].bind(github);
    github[timing] = async (...args) => {
      const value = await original(...args);
      await github.createComment(9, renderSyncComment(`Sync accepted during ${timing}`));
      github[timing] = original;
      return value;
    };
    const result = await mergeLand(github, verifiedMerge);
    assert.ok(result.requeue);
    assert.equal(github.repo.merges.length, 0);
    assert.ok(syncBarrier(github.comments));
  }
});

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
  assert.deepEqual(github.repo.runs.map((run) => [run.workflow, run.inputs]), [['speckit-prepare.yml', { dry_run: 'false' }], ['speckit-orchestrate.yml', {}]], 'the scheduler is started, since token events start no workflows');
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:done -->\n\*\*Merged into `main`\*\* as sha-\d+ @dev: all 2 tasks are implemented, the implementation converged, and the full verification passed \(platform build and tests, Markdown check\)\.$/);
});

test('merge-land merges into the pull request base, not the default branch, and dispatches tooling on the default branch', async () => {
  const github = mergeableGitHub();
  github.repo.pulls[0].base = { ref: 'release' };
  github.repo.branches.release = 'sha-main';
  github.repo.branches.main = 'sha-elsewhere';
  const landed = await mergeLand(github, verifiedMerge, { env: { SPECKIT_BASE_BRANCH: 'ignored' } });
  assert.equal(landed.exitCode, 0, 'the default branch moving does not matter; the base branch did not move');
  assert.equal(github.repo.merges.length, 1);
  assert.deepEqual(github.repo.runs.map((run) => [run.workflow, run.ref]), [['speckit-prepare.yml', 'main'], ['speckit-orchestrate.yml', 'main']]);
  assert.match(github.comments.at(-1).body, /\*\*Merged into `release`\*\*/);

  const moved = mergeableGitHub();
  moved.repo.pulls[0].base = { ref: 'release' };
  moved.repo.branches.release = 'sha-newer';
  const waited = await mergeLand(moved, verifiedMerge);
  assert.deepEqual([waited.moved, moved.repo.merges.length], [true, 0], 'a moved base branch is verified again');
  assert.match(waited.check.output.summary, /but release is now sha-newer/);
});

test('merge-land notes a scheduler that could not be started and still succeeds', async () => {
  const github = mergeableGitHub();
  const dispatch = github.dispatchWorkflow.bind(github);
  github.dispatchWorkflow = async (workflowFile, ref, inputs) => {
    if (workflowFile === 'speckit-orchestrate.yml') throw new Error('workflow not found');
    return dispatch(workflowFile, ref, inputs);
  };
  const landed = await mergeLand(github, verifiedMerge);
  assert.equal(landed.exitCode, 0);
  assert.equal(github.repo.merges.length, 1);
  assert.deepEqual(github.repo.runs.map((run) => run.workflow), ['speckit-prepare.yml']);
  assert.match(github.comments.at(-1).body, /\n- Spec Kit orchestrate could not be started: workflow not found$/);
});

test('merge-land waits when the base branch moved, reports conflicts, and fails unverified or moved heads', async () => {
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
  assert.equal(refused.repo.pulls[0].draft, true, 'a refused merge restores draft status before the implementation waits');
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
  assert.match(github.comments.at(-1).body, /\*\*Recommended next step\*\*: Review the listed risks and merge #9 into `main`\.[\s\S]*\*\*Why\*\*:[\s\S]*\*\*What it does\*\*: Lands the implementation on `main` and completes spec twin #5/);
  assert.equal(github.comments.at(-1).body.match(/\*\*Recommended next step\*\*/g)?.length, 1);
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

  const baseOwned = mergeableGitHub();
  await baseOwned.createComment(9, `${renderBaseOwnedMarker(['tests/Main.Tests/SettingsTests.cs'])}\n**Conflicts resolved** (attempt 1)`);
  await mergeLand(baseOwned, verifiedMerge);
  assert.equal(baseOwned.repo.merges.length, 0, 'changes to files only the base branch owns are held for review');
  assert.match(baseOwned.comments.at(-1).body, /- a conflict resolution changed files that a person must review \(`tests\/Main\.Tests\/SettingsTests\.cs`\): files of `main` this implementation does not own/);

  const forged = mergeableGitHub();
  forged.commentAuthor = 'someone';
  await forged.createComment(9, renderBaseOwnedMarker(['x.cs']));
  forged.commentAuthor = undefined;
  await mergeLand(forged, verifiedMerge);
  assert.equal(forged.repo.merges.length, 1, 'only workflow comments can hold the merge with the marker');
});

test('task checks fail closed when the default branch is not in the checkout', () => withRepos({}, (repos) => {
  git(repos.work, 'update-ref', '-d', 'refs/remotes/origin/main');
  const { result } = work(repos, { agent: (r) => agentDoesT001(r.work) });
  assert.match(result.reasons.join(), /cannot be compared with its base branch main, because main is not in the checkout/);
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
  await assert.rejects(() => main(['nope'], { env }), /package \| verdict \| land-tasks/);
  await assert.rejects(() => groupMain(['package'], { env }), /speckit-group\.mjs/);
  await assert.rejects(() => repairMain(['verdict'], { env }), /speckit-repair\.mjs/);
  await assert.rejects(() => main(['integrate'], { env }), /speckit-implement\.mjs/);
  await assert.rejects(() => convergeMain(['verdict'], { env }), /speckit-converge\.mjs/);
  await assert.rejects(() => resolveMain(['combined-verified'], { env }), /speckit-resolve\.mjs/);
  await assert.rejects(() => mergeMain(['package'], { env }), /speckit-merge\.mjs/);
});

// The merge workflow's archive job archives the spec on the implementation branch, merged with its base branch.
async function archive(repos, github, { head = remoteSha(repos, 'speckit/f'), checkRun } = {}) {
  const outputFile = path.join(repos.base, 'archive-output.txt');
  writeFileSync(outputFile, '');
  const check = checkRun ?? (await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'in_progress', external_id: CHECK_MERGE })).id;
  const env = { ...landEnv(repos), GITHUB_OUTPUT: outputFile, SPECKIT_PULL: '9', SPECKIT_HEAD: head, SPECKIT_ATTEMPT: '1', SPECKIT_CHECK_RUN: String(check) };
  const result = await runArchive({ client: github, git: defaultGit(repos.land), env, folder: 'f', log: silent });
  return { ...result, check, output: readFileSync(outputFile, 'utf8') };
}

test('archive: moves the spec on the branch merged with its base, rewrites references, and moves the merge check run', () => withRepos({
  tasks: DONE_TASKS,
  main: (dir) => write(dir, 'docs/later.md', 'Merged later: [f](../specs/f/spec.md) and specs/f/tasks.md.\n'),
}, async (repos) => {
  const github = fakeGitHub();
  const before = remoteSha(repos, 'speckit/f');
  const archived = await archive(repos, github);
  assert.equal(archived.exitCode, 0);
  const head = remoteSha(repos, 'speckit/f');
  assert.notEqual(head, before);
  assert.equal(archived.head, head);
  assert.match(archived.output, new RegExp(`head=${head}\ncheck_run=\\d+\n`));
  assert.equal(remoteShow(repos, 'speckit/f:specs/archive/f/spec.md'), '# Spec\n');
  assert.throws(() => remoteShow(repos, 'speckit/f:specs/f/spec.md'), 'the active folder is gone');
  assert.equal(remoteShow(repos, 'speckit/f:docs/later.md'), 'Merged later: [f](../specs/archive/f/spec.md) and specs/archive/f/tasks.md.\n', 'references that arrived on the base branch are rewritten too');
  const [, parents] = remoteHead(repos).split('|')[0].split(/ (.*)/);
  assert.equal(parents, `${before} ${remoteSha(repos, 'main')}`, 'the base branch is merged in the same commit');
  assert.match(remoteHead(repos), /\|Archive specs\/f to specs\/archive\/f$/);
  const moved = github.repo.checkRuns.find((check) => check.id === Number(archived.checkRun));
  assert.deepEqual([moved.head_sha, moved.external_id, moved.status], [head, CHECK_MERGE, 'in_progress']);
  assert.deepEqual(github.repo.checkRuns.find((check) => check.id === archived.check).conclusion, 'neutral');
  assert.match(github.comments.at(-1).body, /^\*\*Archived\*\* `specs\/f` → `specs\/archive\/f` for the merge \(merged `main` first\)\.\n\nReferences rewritten in `docs\/later\.md`\./);

  const again = await archive(repos, github, { head });
  assert.deepEqual([again.exitCode, again.head, remoteSha(repos, 'speckit/f')], [0, head, head], 'an archived branch is left as it is');
}));

test('archive: reference conflicts with a base branch that archived another spec are resolved, and the spec is archived', () => withRepos({
  ...crossArchived,
  branch: (dir) => write(dir, 'specs/g/spec.md', '# G\n\nUsed by `specs/archive/f`.\n'),
}, async (repos) => {
  const github = fakeGitHub();
  const before = remoteSha(repos, 'speckit/f');
  const archived = await archive(repos, github);
  assert.equal(archived.exitCode, 0);
  assert.notEqual(remoteSha(repos, 'speckit/f'), before, 'archived instead of waiting for a person');
  assert.equal(remoteShow(repos, 'speckit/f:specs/archive/g/spec.md'), '# G\n\nUsed by `specs/archive/f`.\n');
  assert.equal(remoteShow(repos, 'speckit/f:specs/archive/f/spec.md'), '# Spec\n\nDepends on `specs/archive/g`.\n');
  assert.throws(() => remoteShow(repos, 'speckit/f:specs/g/spec.md'), 'the base branch moved g to the archive');
}));

test('archive: changes nothing on conflicts with the base branch, and stops when the branch moved', async () => {
  await withRepos({ ...conflicting, tasks: DONE_TASKS }, async (repos) => {
    const github = fakeGitHub();
    const before = remoteSha(repos, 'speckit/f');
    const result = await archive(repos, github);
    assert.deepEqual([result.exitCode, result.head, remoteSha(repos, 'speckit/f')], [0, before, before]);
    assert.equal(github.comments.length, 0);
  });
  await withRepos({ tasks: DONE_TASKS }, async (repos) => {
    const result = await archive(repos, fakeGitHub(), { head: 'f'.repeat(40) });
    assert.equal(result.exitCode, 1);
  });
});

test('after the merge, twin and pull request links point to the archived spec on the base branch', async () => {
  const body = renderTwinBody({ folder: 'f', title: 'F', summary: 's', dependencyNotes: [] }, ['spec.md', 'tasks.md'], context);
  const archivedBody = pointSpecLinks(body, 'f', { branch: 'main', archived: true });
  assert.match(archivedBody, /^\*\*Spec\*\*: \[`specs\/archive\/f`\]\(https:\/\/github\.com\/octo\/repo\/tree\/main\/specs\/archive\/f\)$/m);
  assert.match(archivedBody, /blob\/main\/specs\/archive\/f\/tasks\.md/);
  assert.equal(pointSpecLinks(archivedBody, 'f', { branch: 'main', archived: true }), archivedBody, 'idempotent');
  assert.equal(pointSpecLinks('[t](https://github.com/octo/repo/blob/speckit/f/specs/f/tasks.md) `specs/f`', 'f', { branch: 'release', archived: false }),
    '[t](https://github.com/octo/repo/blob/release/specs/f/tasks.md) `specs/f`', 'links to the deleted implementation branch move to the base branch');

  const github = mergeableGitHub();
  github.setFile('sha-head', 'specs/f/tasks.md', null);
  github.setFile('sha-head', 'specs/archive/f/tasks.md', DONE_TASKS);
  github.repo.pulls[0].body = `**Spec**: [\`specs/f\`](https://github.com/octo/repo/tree/main/specs/f)\n\nSee [tasks.md](https://github.com/octo/repo/blob/speckit/f/specs/f/tasks.md).\n\nCloses #5\n`;
  const landed = await mergeLand(github, verifiedMerge);
  assert.equal(landed.exitCode, 0);
  assert.match(github.issues[0].body, /^\*\*Spec\*\*: \[`specs\/archive\/f`\]\([^)]*\/tree\/main\/specs\/archive\/f\)$/m, 'the twin links to the archive right away');
  assert.match(github.repo.pulls[0].body, /\[`specs\/archive\/f`\]\(https:\/\/github\.com\/octo\/repo\/tree\/main\/specs\/archive\/f\)[\s\S]*blob\/main\/specs\/archive\/f\/tasks\.md/);

  const release = mergeableGitHub();
  release.repo.pulls[0].base = { ref: 'release' };
  release.repo.branches.release = 'sha-main';
  release.setFile('sha-head', 'specs/f/tasks.md', null);
  release.setFile('sha-head', 'specs/archive/f/tasks.md', DONE_TASKS);
  const twinBefore = release.issues[0].body;
  await mergeLand(release, verifiedMerge);
  assert.equal(release.issues[0].body, twinBefore, 'a twin follows the default branch, which a merge into another base does not change');
});

test('archive: a base branch that only moved creates no new head, and workflow changes on it are not merged', async () => {
  await withRepos({ tasks: DONE_TASKS }, async (repos) => {
    const github = fakeGitHub();
    const first = await archive(repos, github);
    const seed = path.join(repos.base, 'seed');
    git(seed, 'fetch', '-q', repos.bare, 'main');
    git(seed, 'reset', '-q', '--hard', 'FETCH_HEAD');
    write(seed, 'docs/unrelated.md', '# Unrelated\n');
    git(seed, 'add', '-A');
    git(seed, 'commit', '-q', '-m', 'unrelated change on main');
    git(seed, 'push', '-q', repos.bare, 'HEAD:refs/heads/main');
    git(repos.land, 'fetch', '-q', 'origin');
    const again = await archive(repos, github, { head: first.head });
    assert.deepEqual([again.exitCode, again.head, remoteSha(repos, 'speckit/f')], [0, first.head, first.head], 'no new head, so merge attempts keep counting on one head');
  });
  await withRepos({ tasks: DONE_TASKS, main: (dir) => write(dir, '.github/workflows/ci.yml', 'name: ci # specs/f\n') }, async (repos) => {
    const github = fakeGitHub();
    const before = remoteSha(repos, 'speckit/f');
    const archived = await archive(repos, github);
    assert.equal(archived.exitCode, 0);
    const [, parents] = remoteHead(repos).split('|')[0].split(/ (.*)/);
    assert.equal(parents, before, 'archived without merging the base branch, whose workflow changes the token cannot push');
    assert.equal(remoteShow(repos, 'speckit/f:specs/archive/f/spec.md'), '# Spec\n');
    assert.match(github.comments.at(-1).body, /changed workflow files, which the workflow token cannot push/);
  });
});

test('archive: references to specs that the base branch archived meanwhile are pointed to the archive too', () => withRepos({
  tasks: DONE_TASKS,
  branch: (dir) => write(dir, 'docs/uses-g.md', 'See [g](../specs/g/spec.md) and specs/g/contracts/api.yaml.\n'),
  main: (dir) => {
    write(dir, 'specs/archive/g/spec.md', '# G\n');
    write(dir, 'specs/archive/g/tasks.md', '- [x] T001 Done\n');
  },
}, async (repos) => {
  const github = fakeGitHub();
  const archived = await archive(repos, github);
  assert.equal(archived.exitCode, 0);
  assert.equal(remoteShow(repos, 'speckit/f:docs/uses-g.md'), 'See [g](../specs/archive/g/spec.md) and specs/archive/g/contracts/api.yaml.\n');
  assert.match(github.comments.at(-1).body, /`docs\/uses-g\.md`/);
}));