import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import test from 'node:test';

import { TWIN_LABEL, renderTwinBody } from './speckit-prepare-core.mjs';
import { CHECK_ATTEMPT, CHECK_CONFLICT, CHECK_DONE, CHECK_LIMIT, CHECK_MERGE, CHECK_PROGRESS, CHECK_RUN_NAME, CHECK_SYNC_CONFLICT, baseOwnedChanges, pointSpecLinks, renderBaseOwnedMarker, renderGuidanceMarker, renderRepairMarker, unresolvedRepair } from './speckit-implement-core.mjs';
import {
  TaskInputError,
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
  runMergeLand,
  runPackage,
  runRepairReport,
  runVerdict,
} from './speckit-task.mjs';
import { main as convergeMain } from './speckit-converge.mjs';
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
function makeGitRepos(tasks = TASKS, { branch, main: mainChange } = {}) {
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
// and verdict with a simulated environment-verify outcome (`verify: null` means no verify action).
function work(repos, { inputs = TASK_INPUTS, agentExit = '0', agent, verify = { outcome: 'success', checks: 'Markdown check' }, env: extraEnv = {} } = {}) {
  mkdirSync(repos.resultDir, { recursive: true });
  const outputFile = path.join(repos.resultDir, 'github-output.txt');
  writeFileSync(outputFile, '');
  const env = { GITHUB_OUTPUT: outputFile, SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_BASE_BRANCH: 'main', ...extraEnv };
  const run = { git: defaultGit(repos.work), env, inputs, folder: 'f', workspace: repos.work, resultDir: repos.resultDir, log: silent };
  const integrated = ['resolve', 'merge'].includes(inputs.kind) ? runIntegrate(run) : null;
  agent?.(repos);
  if (agentExit !== null && inputs.kind !== 'merge') writeFileSync(path.join(repos.resultDir, 'agent-exit.txt'), `${agentExit}\n`);
  const packaged = inputs.kind === 'merge' ? null : runPackage(run);
  let result = packaged?.result ?? integrated.result;
  if (inputs.kind !== 'converge') {
    const ready = inputs.kind === 'merge' ? integrated.result.reasons.length === 0 && integrated.result.conflicts.length === 0 : packaged.verify;
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
    SPECKIT_HEAD: landed.head, SPECKIT_VERIFY_RESULT: 'failure', SPECKIT_AUTO_DIAGNOSE: 'false',
  };
  assert.equal((await runRepairReport({ client: github, env, log: silent })).exitCode, 1);
  assert.equal(unresolvedRepair(github.comments).id, repair.id);
  assert.match(github.comments.at(-1).body, /reverifies without running a repair agent.*pending phase verification/);
  assert.doesNotMatch(github.comments.at(-1).body, /attempt 1 landed|fresh attempt count/);
  env.SPECKIT_VERIFY_RESULT = 'success';
  env.SPECKIT_VERIFICATION_OUTCOME = 'success';
  github.repo.branches['speckit/f'] = landed.head;
  assert.equal((await runRepairReport({ client: github, env, log: silent })).exitCode, 0);
  assert.equal(unresolvedRepair(github.comments), null);
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
  const workflow = readFileSync(new URL('../workflows/speckit-implement-repair.yml', import.meta.url), 'utf8');
  const script = workflow.match(/- name: Prepare the verification tree[\s\S]*?        run: \|\n([\s\S]*?)\n      - name:/)[1].replace(/^ {10}/gm, '');
  symlinkSync(process.cwd(), path.join(repos.base, 'tooling'), 'dir');
  const evidence = path.join(repos.base, 'failed-verification');
  const output = path.join(repos.base, 'output');
  for (const available of [true, false]) {
    if (available) write(evidence, 'changed-files.txt', 'src/platform/Host.cs\n');
    else rmSync(evidence, { recursive: true });
    writeFileSync(output, '');
    const run = spawnSync('bash', ['-e', '-c', script], {
      cwd: repos.work, encoding: 'utf8',
      env: { ...process.env, SCOPE: 'phase', FAILED_HEAD: git(repos.work, 'rev-parse', 'HEAD').trim(), SPECKIT_RESULT_DIR: repos.resultDir, GITHUB_OUTPUT: output },
    });
    assert.equal(run.status, 0, run.stderr);
    assert.match(readFileSync(output, 'utf8'), available ? /mode=phase/ : /mode=finalize/);
    assert.equal(readFileSync(path.join(repos.resultDir, 'changed-files.txt'), 'utf8'), available ? 'src/platform/Host.cs\n' : '');
  }
  assert.match(workflow, /mode: \$\{\{ steps\.tree\.outputs\.mode \}\}/);
  assert.match(workflow, /path: \$\{\{ github\.workspace \}\}\/workspace\/\.speckit-repair-evidence/);
  assert.match(workflow, /echo '\/\.speckit-repair-evidence\/' >> \.git\/info\/exclude/);
  assert.match(workflow, /ref: \$\{\{ inputs\.verify_only && inputs\.head \|\| needs\.land\.outputs\.head \}\}/);
  assert.match(workflow, /Require the verification action\n        run: test -f tooling\/\.github\/actions\/environment-verify\/action\.yml/);
  assert.match(workflow, /SPECKIT_VERIFICATION_OUTCOME: \$\{\{ needs\.verify\.outputs\.verification \}\}/);
  const guard = workflow.match(/Require the verification action\n        run: ([^\n]+)/)[1];
  assert.equal(spawnSync('bash', ['-e', '-c', guard], { cwd: repos.land }).status, 1, 'missing verifier fails the job instead of skipping verification');
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
  const action = readFileSync(new URL('../actions/environment-verify/action.yml', import.meta.url), 'utf8');
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
  write(repos.work, 'src/clients/web/package.json', '{}');
  write(repos.work, 'src/clients/web/node_modules/@playwright/test/package.json', '{"version":"1.63.0"}');
  write(repos.base, 'markdown.mjs', '');
  const mode = repairVerificationScope({ scope: 'phase', repairChangedFiles: '' }).mode;
  const run = spawnSync('bash', ['-e', '-c', script], { cwd: repos.work, encoding: 'utf8', env: {
    ...process.env, PATH: `${bin}:${process.env.PATH}`, MODE: mode, WORKSPACE: repos.work,
    CHANGED_FILES: '', LOG_FILE: path.join(repos.base, 'verification.log'),
    GITHUB_OUTPUT: path.join(repos.base, 'output'), GITHUB_STEP_SUMMARY: path.join(repos.base, 'summary'),
    MARKDOWN_CHECK: path.join(repos.base, 'markdown.mjs'), CALLS: path.join(repos.base, 'calls'),
  } });
  assert.equal(run.status, 0, run.stderr);
  const calls = readFileSync(path.join(repos.base, 'calls'), 'utf8');
  assert.match(calls, /dotnet test src\/platform\/Tests\/Host.Tests.csproj --no-build/);
  assert.match(calls, /dotnet test src\/platform\/Tests\/Integration.Tests.csproj --no-build/);
  assert.match(calls, /dotnet test Worker.Tests.csproj --no-build/);
  assert.match(calls, /npm test\nnpm run test:e2e/);
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
  const result = await landTasks(repos, github, [{ task: 'T001', attempt: 1 }, { task: 'T002', attempt: 1 }], { next: { include: [{ task: 'T003', attempt: 1 }] } });
  assert.deepEqual([result.ok, result.exitCode, result.combined], [true, 0, ['docs/list.md']]);
  assert.deepEqual(result.outcomes.map((outcome) => [outcome.key, outcome.landed, outcome.combined]), [['T001', true, []], ['T002', true, ['docs/list.md']]]);
  assert.equal(remoteShow(repos, 'speckit/f:docs/list.md'), 'one\ntwo (x)\nthree\nfour\nfive (y)\nsix\n');
  assert.match(result.output, /^ok=true\nhead=sha-head\nstart=[0-9a-f]{40}\ncombined=true\n$/);
  assert.equal(github.repo.checkRuns.some((check) => String(check.external_id).includes('T003')), false, 'the next task waits for the combined verification');
}));

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
  assert.equal((await runCombinedVerified({ client: verified, env: combinedEnv({ SPECKIT_VERIFY_RESULT: 'success' }), log: silent })).exitCode, 0);
  const next = verified.repo.checkRuns.find((check) => check.external_id === 'speckit:attempt:task:T003:1');
  assert.deepEqual([next.status, next.head_sha, next.output.summary], ['in_progress', 'sha-new', 'Create docs/z.md']);
  assert.equal(verified.comments.some((comment) => /needs attention/.test(comment.body)), false);

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
    assert.match(github.comments.at(-1).body, /\*\*Recommended next step\*\*: Wait for the diagnosis[\s\S]*<summary>Other options<\/summary>[\s\S]*```text\n {2}\/speckit retry\n {2}```/);
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
  const { integrated, result, output } = work(repos, {
    inputs: RESOLVE_INPUTS,
    env: { SPECKIT_GUIDANCE: '  Preserve\nboth migration sequences. ' },
    agent: resolveShared,
    verify: { outcome: 'success', checks: 'platform build and tests' },
  });
  assert.match(output, /conflicts=true\nagent=true\nverify=false\nenvironment=false\nprompt=The working tree is in the middle of merging[^\n]*`docs\/shared\.md`/);
  assert.match(output, /Guidance from the person who retried this implementation: Preserve both migration sequences\.\n/);
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
  const { integrated, result, output } = work(repos, { inputs: RESOLVE_INPUTS, agent: renumberMigration, verify: { outcome: 'success', checks: 'platform build and tests' } });
  const forkPoint = git(repos.work, 'merge-base', integrated.result.head, integrated.result.baseSha).trim();
  assert.match(output, new RegExp(`two migrations with the same sequence number[^\\n]*\`git diff --name-only ${forkPoint} HEAD\`[^\\n]*smallest change to other files of \`main\``));
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
  assert.match(github.comments.at(-1).body, /\/speckit diagnose|Wait for the diagnosis/);
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
    branch: (dir) => write(dir, 'specs/f/spec.md', '# Spec\n\nbranch\n'),
    main: (dir) => write(dir, 'specs/f/spec.md', '# Spec\n\nmain\n'),
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

test('merge-land merges into the pull request base, not the default branch, and dispatches tooling on the default branch', async () => {
  const github = mergeableGitHub();
  github.repo.pulls[0].base = { ref: 'release' };
  github.repo.branches.release = 'sha-main';
  github.repo.branches.main = 'sha-elsewhere';
  const landed = await mergeLand(github, verifiedMerge, { env: { SPECKIT_BASE_BRANCH: 'ignored' } });
  assert.equal(landed.exitCode, 0, 'the default branch moving does not matter; the base branch did not move');
  assert.equal(github.repo.merges.length, 1);
  assert.deepEqual(github.repo.runs.map((run) => [run.workflow, run.ref]), [['speckit-prepare.yml', 'main']]);
  assert.match(github.comments.at(-1).body, /\*\*Merged into `release`\*\*/);

  const moved = mergeableGitHub();
  moved.repo.pulls[0].base = { ref: 'release' };
  moved.repo.branches.release = 'sha-newer';
  const waited = await mergeLand(moved, verifiedMerge);
  assert.deepEqual([waited.moved, moved.repo.merges.length], [true, 0], 'a moved base branch is verified again');
  assert.match(waited.check.output.summary, /but release is now sha-newer/);
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
  assert.match(baseOwned.comments.at(-1).body, /- a conflict resolution changed files of `main` that this implementation does not own \(`tests\/Main\.Tests\/SettingsTests\.cs`\)/);

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
  await assert.rejects(() => main(['integrate'], { env }), /speckit-task\.mjs/);
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