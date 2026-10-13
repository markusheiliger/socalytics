import { spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { specDir, stageLabelChange } from './speckit-prepare-core.mjs';
import { archiveSpec, refreshArchivedReferences } from './speckit-archive.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { resolveImplementRequester, updateIssueWithLabels } from './speckit-prepare.mjs';
import {
  CHECK_CONFLICT,
  CHECK_DONE,
  CHECK_LIMIT,
  CHECK_MERGE,
  CHECK_PROGRESS,
  CHECK_RUN_NAME,
  DONE_COMMENT_MARKER,
  MAX_CONVERGE_ROUNDS,
  PREPARE_WORKFLOW_FILE,
  appendPullRequestTasks,
  baseBranchFromEnv,
  baseBranchOf,
  behindBaseOf,
  convergenceRounds,
  defaultBranchOf,
  environmentExclusivityReasons,
  hasConflictMarkers,
  implementationBranch,
  isEnvironmentPath,
  isProtectedPath,
  kindLabel,
  listTasks,
  neutralizeMarkers,
  pointSpecLinks,
  progressOutput,
  readSpecTasks,
  attemptCheckId,
  renderAttemptMarker,
  renderNextSteps,
  renderResolvePrompt,
  renderSyncSkippedMarker,
  summarizeTask,
  syncPullRequestTaskList,
  syncPullRequestTicks,
  tasksPathOf,
  taskProgress,
  tickTask,
  validateConvergeChange,
  validateTaskChange,
} from './speckit-implement-core.mjs';

export const MAX_PATCH_BYTES = 5 * 1024 * 1024;
export const MAX_CHANGED_FILES = 500;
// GitHub lists at most this many files of a pull request.
const MAX_PULL_FILES = 3000;
export const KINDS = ['task', 'converge', 'resolve', 'merge'];
const BOT_NAME = 'github-actions[bot]';
const BOT_EMAIL = '41898282+github-actions[bot]@users.noreply.github.com';
const GIT_IDENTITY = ['-c', `user.name=${BOT_NAME}`, '-c', `user.email=${BOT_EMAIL}`];

export class TaskInputError extends Error {}

function createReporter(env, log) {
  const lines = [];
  return {
    line(text = '') {
      lines.push(text);
      log(text);
    },
    flush() {
      if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, `${lines.join('\n')}\n`);
    },
  };
}

function setOutput(env, name, value) {
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `${name}=${value}\n`);
}

// Inputs of a worker job from SPECKIT_* environment variables. SPECKIT_KIND selects what it works on (task, converge,
// resolve, or merge); SPECKIT_TASK is the task ID of a task.
export function parseTaskInputs(env) {
  const kind = String(env.SPECKIT_KIND || 'task');
  const twin = Number(env.SPECKIT_TWIN);
  const pull = Number(env.SPECKIT_PULL);
  const attempt = Number(env.SPECKIT_ATTEMPT);
  const task = kind === 'task' ? String(env.SPECKIT_TASK ?? '') : null;
  if (!KINDS.includes(kind)) throw new TaskInputError(`Unknown kind "${kind}"; expected one of ${KINDS.join(', ')}.`);
  if (![twin, pull, attempt].every((value) => Number.isInteger(value) && value > 0) || (kind === 'task' && !/^T\d{3,}$/.test(task))) {
    throw new TaskInputError('Expected positive integers for twin, pull, and attempt, and a task like T001 for a task.');
  }
  return { kind, twin, pull, task, attempt };
}

function title(inputs) {
  const label = kindLabel(inputs);
  return `${label.charAt(0).toUpperCase()}${label.slice(1)} attempt ${inputs.attempt}`;
}

// The attempt marker line of an outcome comment (see parseAttempts); merges are tracked by check runs instead.
function markerLines(inputs, outcome) {
  return inputs.kind === 'merge' ? [] : [renderAttemptMarker({ kind: inputs.kind, task: inputs.task, attempt: inputs.attempt, outcome })];
}

function tail(text, lines = 60) {
  return String(text ?? '').trimEnd().split(/\r?\n/).slice(-lines).join('\n');
}

function fence(text) {
  return ['```text', String(text).replaceAll('```', "'''"), '```'].join('\n');
}

function readJson(file) {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

function readOptional(file) {
  return existsSync(file) ? readFileSync(file, 'utf8') : '';
}

function writeResult(resultDir, result) {
  mkdirSync(resultDir, { recursive: true });
  writeFileSync(path.join(resultDir, 'result.json'), `${JSON.stringify(result, null, 2)}\n`);
}

export function defaultGit(cwd) {
  return (args) => {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
    if (result.error) throw new Error(`git failed to start: ${result.error.message}`);
    return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  };
}

function gitOrThrow(git, args) {
  const result = git(args);
  if (result.status !== 0) throw new Error(`git ${args[0]} failed: ${result.stderr.trim()}`);
  return result.stdout;
}

const splitZ = (text) => text.split('\0').filter(Boolean);

function changedPaths(git) {
  return splitZ(gitOrThrow(git, ['diff', '--cached', '--name-only', '--no-renames', '-z', 'HEAD']));
}

function unmergedPaths(git) {
  return [...new Set(splitZ(gitOrThrow(git, ['diff', '--name-only', '--diff-filter=U', '-z'])))].sort();
}

function sizeReasons(patchBytes, paths) {
  return [
    ...(patchBytes > MAX_PATCH_BYTES ? [`the change is larger than ${MAX_PATCH_BYTES} bytes`] : []),
    ...(paths.length > MAX_CHANGED_FILES ? [`the change touches more than ${MAX_CHANGED_FILES} files`] : []),
  ];
}

// Whether the implementation archived the spec folder in this checkout (it does so right before the merge).
function isArchivedIn(workspace, folder) {
  return !existsSync(path.join(workspace, specDir(folder))) && existsSync(path.join(workspace, specDir(folder, { archived: true })));
}

function tasksFiles(git, workspace, folder) {
  const archived = isArchivedIn(workspace, folder);
  const tasksPath = tasksPathOf(folder, { archived });
  const before = git(['show', `HEAD:${tasksPath}`]);
  const afterFile = path.join(workspace, tasksPath);
  return { archived, before: before.status === 0 ? before.stdout : '', after: existsSync(afterFile) ? readFileSync(afterFile, 'utf8') : '' };
}

// Paths the implementation branch changes compared to its base branch, including the staged change, or null when
// the base branch is not in the checkout.
function branchPaths(git, baseBranch) {
  const base = git(['merge-base', 'HEAD', `refs/remotes/origin/${baseBranch}`]);
  if (base.status !== 0) return null;
  return splitZ(gitOrThrow(git, ['diff', '--cached', '--name-only', '--no-renames', '-z', base.stdout.trim()]));
}

// Validates the staged change of one task and returns the reasons it cannot be accepted.
export function checkStagedTask({ git, workspace, folder, task, patchBytes, baseBranch }) {
  const paths = changedPaths(git);
  const reasons = sizeReasons(patchBytes, paths);
  reasons.push(...validateTaskChange({ folder, taskId: task, ...tasksFiles(git, workspace, folder), changedPaths: paths }));
  const branch = branchPaths(git, baseBranch);
  if (branch === null) reasons.push(`the branch cannot be compared with its base branch ${baseBranch}, because ${baseBranch} is not in the checkout`);
  else reasons.push(...environmentExclusivityReasons({ folder, branchPaths: branch }));
  return { reasons, paths };
}

// Validates a staged /speckit-converge change.
export function checkStagedConvergence({ git, workspace, folder, patchBytes }) {
  const paths = changedPaths(git);
  const validation = validateConvergeChange({ folder, ...tasksFiles(git, workspace, folder), changedPaths: paths });
  return { ...validation, reasons: [...sizeReasons(patchBytes, paths), ...validation.reasons], paths };
}

function agentReasons(resultDir) {
  const exitFile = path.join(resultDir, 'agent-exit.txt');
  const agentExit = existsSync(exitFile) ? Number(readFileSync(exitFile, 'utf8').trim()) : null;
  if (agentExit === 124) return { agentExit, reasons: ['the agent did not finish within 60 minutes'] };
  if (agentExit !== 0) return { agentExit, reasons: [agentExit === null ? 'the agent did not run' : `the agent exited with code ${agentExit}`] };
  return { agentExit, reasons: [] };
}

function stagePatch(git, resultDir) {
  gitOrThrow(git, ['add', '-A']);
  const patch = gitOrThrow(git, ['diff', '--cached', '--binary', '--no-renames', 'HEAD']);
  writeFileSync(path.join(resultDir, 'changes.patch'), patch);
  return Buffer.byteLength(patch);
}

const isProtected = isProtectedPath;

// Spec Kit implement task (resolve) and Spec Kit implement spec (merge-verify), job "work": merges the base branch
// (SPECKIT_BASE_BRANCH) into the checked-out implementation branch without committing, records the base branch SHA and
// any conflicted files, and prepares the agent prompt for conflict resolution. Conflicts in protected paths need a
// person. After a clean merge it writes the paths the implementation changes compared to the base branch for the
// verification.
export function runIntegrate({ git, env, inputs, folder, resultDir, log }) {
  mkdirSync(resultDir, { recursive: true });
  const baseBranch = baseBranchFromEnv(env);
  const baseSha = gitOrThrow(git, ['rev-parse', `refs/remotes/origin/${baseBranch}`]).trim();
  const head = gitOrThrow(git, ['rev-parse', 'HEAD']).trim();
  const merge = git([...GIT_IDENTITY, 'merge', '--no-ff', '--no-commit', baseSha]);
  const conflicts = unmergedPaths(git);
  const result = { kind: inputs.kind, baseSha, head, conflicts, reasons: [], checks: [], verified: false };
  if (merge.status !== 0 && conflicts.length === 0) result.reasons.push(`merging ${baseBranch} failed: ${merge.stderr.trim() || merge.stdout.trim()}`);
  const protectedConflicts = conflicts.filter(isProtected);
  if (protectedConflicts.length > 0) {
    result.reasons.push(`conflicts in protected paths need a person: ${protectedConflicts.join(', ')}`);
    result.attention = true;
  }
  const paths = conflicts.length === 0 && result.reasons.length === 0
    ? splitZ(gitOrThrow(git, ['diff', '--cached', '--name-only', '--no-renames', '-z', baseSha]))
    : [];
  result.changedFiles = paths;
  result.environment = paths.some(isEnvironmentPath);
  writeFileSync(path.join(resultDir, 'changed-files.txt'), paths.map((file) => `${file}\n`).join(''));
  writeResult(resultDir, result);
  const agent = inputs.kind === 'resolve' && conflicts.length > 0 && result.reasons.length === 0;
  setOutput(env, 'conflicts', String(conflicts.length > 0));
  setOutput(env, 'agent', String(agent));
  setOutput(env, 'verify', String(inputs.kind === 'merge' && conflicts.length === 0 && result.reasons.length === 0));
  setOutput(env, 'environment', String(inputs.kind === 'merge' && result.environment));
  setOutput(env, 'prompt', agent ? renderResolvePrompt({ folder, files: conflicts, base: baseBranch, archived: isArchivedIn(gitOrThrow(git, ['rev-parse', '--show-toplevel']).trim(), folder) }) : '');
  log(conflicts.length > 0 ? `Conflicts with ${baseBranch} (${baseSha}): ${conflicts.join(', ')}` : `Merged ${baseBranch} (${baseSha}) cleanly.`);
  return { exitCode: 0, result };
}

// Spec Kit implement task, job "work" (read-only token): packages and validates the agent's change. It writes the changed
// paths for the solution's optional environment-verify action and a preliminary result; "verdict" completes it.
// The result is a quality report; the "land" job re-validates everything it relies on before pushing.
export function runPackage({ git, env, inputs, folder, workspace, resultDir, log }) {
  mkdirSync(resultDir, { recursive: true });
  let result;
  let paths = [];
  if (inputs.kind === 'task') {
    const agent = agentReasons(resultDir);
    result = { kind: 'task', task: inputs.task, agentExit: agent.agentExit, reasons: [...agent.reasons], checks: [], verified: false };
    result.patchBytes = stagePatch(git, resultDir);
    const staged = checkStagedTask({ git, workspace, folder, task: inputs.task, patchBytes: result.patchBytes, baseBranch: baseBranchFromEnv(env) });
    paths = staged.paths;
    result.changedFiles = paths;
    result.reasons.push(...staged.reasons);
  } else if (inputs.kind === 'converge') {
    const agent = agentReasons(resultDir);
    result = { kind: 'converge', agentExit: agent.agentExit, reasons: [...agent.reasons], checks: [], verified: false };
    result.patchBytes = stagePatch(git, resultDir);
    const staged = checkStagedConvergence({ git, workspace, folder, patchBytes: result.patchBytes });
    result.changedFiles = staged.paths;
    result.appended = staged.appended;
    result.limit = staged.limit;
    result.reasons.push(...staged.reasons);
    result.converged = result.reasons.length === 0 && staged.paths.length === 0;
    // Convergence changes no code, so there is nothing to verify.
    result.verified = result.reasons.length === 0;
  } else if (inputs.kind === 'resolve') {
    result = readJson(path.join(resultDir, 'result.json')) ?? { kind: 'resolve', conflicts: [], reasons: ['the integration did not run'], checks: [] };
    if (result.reasons.length === 0 && result.conflicts.length > 0) {
      const agent = agentReasons(resultDir);
      result.agentExit = agent.agentExit;
      result.reasons.push(...agent.reasons);
      const conflicts = new Set(result.conflicts);
      const touched = [
        ...splitZ(gitOrThrow(git, ['diff', '--name-only', '-z'])),
        ...splitZ(gitOrThrow(git, ['ls-files', '--others', '--exclude-standard', '-z'])),
      ];
      const outside = [...new Set(touched.filter((file) => !conflicts.has(file)))].sort();
      if (outside.length > 0) result.reasons.push(`the agent changed files outside the conflicts: ${outside.join(', ')}`);
      const resolvedDir = path.join(resultDir, 'resolved');
      rmSync(resolvedDir, { recursive: true, force: true });
      mkdirSync(resolvedDir, { recursive: true });
      result.resolutions = result.conflicts.map((file, index) => {
        const absolute = path.join(workspace, file);
        if (!existsSync(absolute)) {
          gitOrThrow(git, ['rm', '--quiet', '--cached', '--ignore-unmatch', '--', file]);
          return { path: file, deleted: true };
        }
        const content = readFileSync(absolute);
        if (hasConflictMarkers(content.toString('utf8'))) result.reasons.push(`\`${file}\` still contains conflict markers`);
        writeFileSync(path.join(resolvedDir, String(index)), content);
        gitOrThrow(git, ['add', '--', file]);
        return { path: file, deleted: false, blob: String(index) };
      });
    }
  } else {
    throw new TaskInputError(`The ${kindLabel(inputs)} has no package command.`);
  }
  // Agents must leave their changes uncommitted; a moved HEAD would hide them from the staged diff.
  if (env.SPECKIT_HEAD && gitOrThrow(git, ['rev-parse', 'HEAD']).trim() !== env.SPECKIT_HEAD) {
    result.reasons.push('the agent created commits, but agents must leave their changes uncommitted');
    result.converged = false;
    result.verified = false;
  }
  // The commit the work started from, so the land job can land the change from there.
  result.base = env.SPECKIT_HEAD || result.head || null;
  writeFileSync(path.join(resultDir, 'changed-files.txt'), paths.map((file) => `${file}\n`).join(''));
  writeResult(resultDir, result);
  const verify = inputs.kind !== 'converge' && result.reasons.length === 0;
  setOutput(env, 'verify', String(verify));
  log(result.reasons.length === 0 ? `Packaged ${kindLabel(inputs)}.` : `Rejected: ${result.reasons.join('; ')}`);
  return { exitCode: 0, verify, result };
}

// Spec Kit implement task and the merge verification, job "work": records the outcome of the optional environment-verify action in the result.
// SPECKIT_VERIFY_CONFIGURED tells whether the action exists; SPECKIT_VERIFY_OUTCOME and SPECKIT_VERIFY_CHECKS are the
// verification's outcome and its "checks" output (comma-separated, in order; after a failure the last one failed);
// SPECKIT_VERIFY_UNCOVERED lists changed files that no check covers; SPECKIT_VERIFY_RETRIED tells whether these come from
// the re-run after a failed first run, so a pass then points to a flaky test. For a merge that changes the environment
// actions, SPECKIT_SELFTEST_OUTCOME is the outcome of running the changed actions themselves.
export function runVerdict({ env, inputs, resultDir, log }) {
  mkdirSync(resultDir, { recursive: true });
  const resultFile = path.join(resultDir, 'result.json');
  const result = readJson(resultFile) ?? { kind: inputs.kind, task: inputs.task, reasons: ['the change was not packaged'], checks: [] };
  const configured = String(env.SPECKIT_VERIFY_CONFIGURED ?? '') === 'true';
  result.verification = configured ? 'configured' : 'not-configured';
  const pendingConflicts = inputs.kind === 'merge' && (result.conflicts ?? []).length > 0;
  if (result.reasons.length === 0 && configured && !pendingConflicts) {
    const names = String(env.SPECKIT_VERIFY_CHECKS ?? '').split(',').map((name) => name.trim()).filter(Boolean);
    const outcome = String(env.SPECKIT_VERIFY_OUTCOME ?? '');
    if (outcome === 'success') {
      result.checks = names.map((name) => ({ name, passed: true }));
      if (String(env.SPECKIT_VERIFY_RETRIED ?? '') === 'true') result.flaky = true;
    } else {
      result.checks = names.map((name, index) => ({ name, passed: index < names.length - 1 }));
      result.reasons.push(names.length > 0 ? `${names.at(-1)} failed` : `the verification did not succeed (${outcome || 'not run'})`);
    }
    result.uncovered = String(env.SPECKIT_VERIFY_UNCOVERED ?? '').split(',').map((file) => file.trim()).filter(Boolean);
  }
  if (result.reasons.length === 0 && inputs.kind === 'merge' && result.environment && !pendingConflicts) {
    const selfTest = String(env.SPECKIT_SELFTEST_OUTCOME ?? '');
    result.selfTest = selfTest || 'skipped';
    if (selfTest && !['success', 'skipped'].includes(selfTest)) result.reasons.push(`the changed environment actions failed their self-test (${selfTest})`);
  }
  result.verified = result.reasons.length === 0 && !pendingConflicts;
  writeFileSync(resultFile, `${JSON.stringify(result, null, 2)}\n`);
  if (pendingConflicts) log(`Conflicts with the base branch: ${result.conflicts.join(', ')}`);
  else if (!result.verified) log(`Not verified: ${result.reasons.join('; ')}`);
  else log(configured ? `Verified ${kindLabel(inputs)}.` : `${kindLabel(inputs)} is not verified: no environment-verify action is configured.`);
  return { exitCode: 0, result };
}

// The agent's last message from the Copilot CLI JSONL log, for reports.
export function lastAgentMessage(jsonl) {
  let message = '';
  for (const line of String(jsonl ?? '').split(/\r?\n/)) {
    try {
      const event = JSON.parse(line);
      if (event.type === 'assistant.message' && String(event.data?.content ?? '').trim()) message = String(event.data.content).trim();
    } catch {
      // Ignore partial lines.
    }
  }
  return message;
}

function agentDetails(resultDir, summary = 'Agent summary') {
  const agent = neutralizeMarkers(lastAgentMessage(readOptional(path.join(resultDir, 'agent.jsonl'))));
  return agent ? ['', `<details><summary>${summary}</summary>`, '', agent.slice(0, 4000), '', '</details>'] : [];
}

async function requesterMention(client, twin) {
  const requester = await resolveImplementRequester(client, twin);
  return requester?.login ? { login: requester.login, mention: `@${requester.login}` } : { login: null, mention: '' };
}

async function reportFailure({ client, inputs, reasons, resultDir }) {
  const verification = neutralizeMarkers(tail(readOptional(path.join(resultDir, 'verification.log'))));
  const safeReasons = reasons.map(neutralizeMarkers);
  await client.updateCheckRun(inputs.checkRun, {
    status: 'completed',
    conclusion: 'failure',
    output: { title: `${title(inputs)} failed`, summary: safeReasons.map((reason) => `- ${reason}`).join('\n') },
  });
  await client.createComment(inputs.pull, [
    ...markerLines(inputs, 'failure'),
    `**${title(inputs)} failed:**`,
    '',
    ...safeReasons.map((reason) => `- ${reason}`),
    ...(verification.trim() ? ['', '<details><summary>Verification output (tail)</summary>', '', fence(verification), '', '</details>'] : []),
    ...agentDetails(resultDir),
    '',
    'It is retried until the attempt limit is reached.',
  ].join('\n'));
}

// Stops the implementation and asks the person who requested it, because automation cannot continue. The comment
// ends with the commands that can continue from here; a diagnosis starts automatically unless it is turned off.
async function requestAttention({ client, env = {}, inputs, reason, next, resultDir }) {
  const { mention } = await requesterMention(client, inputs.twin);
  await client.updateCheckRun(inputs.checkRun, {
    status: 'completed',
    conclusion: 'failure',
    external_id: CHECK_LIMIT,
    output: { title: `${title(inputs)} needs a person`, summary: neutralizeMarkers(reason) },
  });
  const behindBase = inputs.folder ? await behindBaseOf(client, await client.getPullRequest(inputs.pull), inputs.folder) : null;
  await client.createComment(inputs.pull, [
    ...markerLines(inputs, 'attention'),
    `**Implementation needs attention**${mention ? ` ${mention}` : ''}: ${neutralizeMarkers(reason)}`,
    '',
    next.trim(),
    ...(resultDir ? agentDetails(resultDir) : []),
    '',
    ...renderNextSteps({ autoDiagnosis: String(env.SPECKIT_AUTO_DIAGNOSE ?? 'true').toLowerCase() !== 'false', behindBase }),
  ].join('\n'));
}

function remoteAuth(env) {
  const server = (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '');
  const basic = Buffer.from(`x-access-token:${env.GITHUB_TOKEN}`).toString('base64');
  return { config: ['-c', `http.${server}/.extraheader=AUTHORIZATION: basic ${basic}`], url: `${server}/${env.GITHUB_REPOSITORY}.git` };
}

function pushArgs(env, branch) {
  const { config, url } = remoteAuth(env);
  return [...config, 'push', url, `HEAD:refs/heads/${branch}`];
}

const LANDED_HEAD_REF = 'refs/speckit/land-head';

function fetchArgs(env, branch) {
  const { config, url } = remoteAuth(env);
  return [...config, 'fetch', '--quiet', url, `+refs/heads/${branch}:${LANDED_HEAD_REF}`];
}

// How often a task is rebuilt on a moved branch before it is redone from the newest head.
const MAX_LAND_TRIES = 5;

// Pushes the committed task. When the branch moved (a parallel task or a person pushed first), the task is rebuilt
// on the new head: its change without tasks.md is applied again and its tick is set in the new tasks.md, then
// everything is validated again. Files that also changed on the branch since the task started (a parallel task
// edited them too) are merged three-way; the combination is verified after the parallel tasks landed (see
// runCombinedVerified). Only
// a change that no longer applies, or conflicts with those edits, has to be redone from the new head: that is a
// requeue, not a failed attempt.
function pushTask({ git, env, folder, task, message, resultDir, workspace }) {
  const branch = implementationBranch(folder);
  const tasksPath = tasksPathOf(folder, { archived: isArchivedIn(workspace, folder) });
  const patchFile = path.join(resultDir, 'changes.patch');
  const base = gitOrThrow(git, ['rev-parse', 'HEAD~1']).trim();
  const ours = splitZ(gitOrThrow(git, ['diff', '--name-only', '--no-renames', '-z', base, 'HEAD'])).filter((file) => file !== tasksPath);
  let onto = base;
  let combined = [];
  for (let tries = 0; tries < MAX_LAND_TRIES; tries += 1) {
    const pushed = git(pushArgs(env, branch));
    if (pushed.status === 0) return { head: gitOrThrow(git, ['rev-parse', 'HEAD']).trim(), combined };
    const rejection = pushed.stderr.trim().split('\n').at(-1);
    const fetched = git(fetchArgs(env, branch));
    if (fetched.status !== 0) {
      return { reasons: [`the push to \`${branch}\` was rejected (${rejection}) and the branch could not be read (${fetched.stderr.trim().split('\n').at(-1)})`] };
    }
    const newHead = gitOrThrow(git, ['rev-parse', LANDED_HEAD_REF]).trim();
    // An unmoved branch means the push itself was refused (rules, hooks, permissions), which a retry cannot fix.
    if (newHead === onto) return { reasons: [`the push to \`${branch}\` was rejected: ${rejection}`] };
    if (git(['merge-base', '--is-ancestor', base, newHead]).status !== 0) return { requeue: [`the branch \`${branch}\` was rewritten since the task started`] };
    const moved = new Set(splitZ(gitOrThrow(git, ['diff', '--name-only', '--no-renames', '-z', base, newHead])));
    const overlap = ours.filter((file) => moved.has(file));
    gitOrThrow(git, ['checkout', '-q', '--detach', newHead]);
    // --binary patches record full blob ids, so --3way can merge the overlapping files with the branch's versions.
    const applied = git(['apply', ...(overlap.length > 0 ? ['--3way'] : []), '--index', '--whitespace=nowarn', `--exclude=${tasksPath}`, patchFile]);
    if (applied.status !== 0) {
      git(['reset', '-q', '--hard']);
      if (overlap.length > 0) return { requeue: [`${overlap.map((file) => `\`${file}\``).join(', ')} changed on the branch since the task started, and the changes conflict`] };
      return { requeue: [`the change no longer applies to the branch (${applied.stderr.trim().split('\n').at(-1)})`] };
    }
    combined = [...new Set([...combined, ...overlap])].sort();
    const tasksFile = path.join(workspace, tasksPath);
    const ticked = tickTask(readFileSync(tasksFile, 'utf8'), task);
    if (ticked === null) return { requeue: [`${task} is already checked on the branch`] };
    writeFileSync(tasksFile, ticked);
    gitOrThrow(git, ['add', '--', tasksPath]);
    const staged = checkStagedTask({ git, workspace, folder, task, patchBytes: statSync(patchFile).size, baseBranch: baseBranchFromEnv(env) });
    if (staged.reasons.length > 0) return { reasons: staged.reasons };
    const note = combined.length > 0 ? [`Combined with parallel changes to ${combined.join(', ')}; verified together after the parallel tasks.`] : [];
    gitOrThrow(git, [...GIT_IDENTITY, 'commit', '--no-verify', ...[...message, ...note].flatMap((part) => ['-m', part])]);
    onto = newHead;
  }
  return { requeue: [`the branch \`${branch}\` kept moving during ${MAX_LAND_TRIES} tries`] };
}

async function reportRequeue({ client, inputs, reasons }) {
  const safeReasons = reasons.map(neutralizeMarkers);
  await client.updateCheckRun(inputs.checkRun, {
    status: 'completed',
    conclusion: 'neutral',
    output: { title: `${title(inputs)} is redone from the new head`, summary: safeReasons.map((reason) => `- ${reason}`).join('\n') },
  });
  await client.createComment(inputs.pull, [
    ...markerLines(inputs, 'requeue'),
    `**${title(inputs)} runs again** from the new head of the implementation branch, because a parallel task or a person changed it first:`,
    '',
    ...safeReasons.map((reason) => `- ${reason}`),
    '',
    'This does not count as a failed attempt.',
  ].join('\n'));
}

// A queued progress check run of the commit a task landed on is no longer needed once a newer head exists.
async function completeSupersededProgress(client, parent, head) {
  for (const check of await client.listCheckRuns(parent, CHECK_RUN_NAME)) {
    if (check.status === 'queued' && check.external_id === CHECK_PROGRESS) {
      await client.updateCheckRun(check.id, { status: 'completed', conclusion: 'neutral', output: { title: 'Superseded', summary: `The implementation continued on ${head}.` } });
    }
  }
}

async function push({ client, git, env, inputs, folder, resultDir, report }) {
  const branch = implementationBranch(folder);
  const pushed = git(pushArgs(env, branch));
  if (pushed.status === 0) return gitOrThrow(git, ['rev-parse', 'HEAD']).trim();
  const reasons = [`the push to \`${branch}\` was rejected; the branch probably moved (${pushed.stderr.trim().split('\n').at(-1)})`];
  await reportFailure({ client, inputs, reasons, resultDir });
  report.flush();
  return null;
}

// The next-steps block of an attention comment lists the commands that continue from here.
const RESUME_HINT = '';

// Whether tasks.md of the spec on the checked-out branch still has unticked tasks: a conflict resolution then comes
// before a phase (best effort) rather than before the merge.
function openTasksIn(workspace, folder) {
  const file = path.join(workspace, tasksPathOf(folder, { archived: isArchivedIn(workspace, folder) }));
  return existsSync(file) && listTasks(readFileSync(file, 'utf8')).some((task) => !task.done);
}

// Lands the result of one task (write token, never runs agent-written code): re-validates the change, commits and
// pushes it, and reports progress.
export async function runLand({ client, git, env, inputs, folder, workspace, resultDir, workResult, log }) {
  const report = createReporter(env, log);
  const result = readJson(path.join(resultDir, 'result.json'));
  const reasons = [];
  if (!result) reasons.push(`the work job did not finish (result: ${workResult || 'unknown'})`);
  else reasons.push(...result.reasons);
  if (result && !result.verified && result.reasons.length === 0) reasons.push('the change was not verified');

  if (result?.attention && inputs.kind === 'resolve' && openTasksIn(workspace, folder)) {
    // A resolution before a phase is best effort: the implementation continues on its current base.
    const baseBranch = baseBranchFromEnv(env);
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'neutral',
      output: { title: `${title(inputs)}: ${baseBranch} not merged`, summary: neutralizeMarkers(reasons.join('; ')) },
    });
    await client.createComment(inputs.pull, [
      ...markerLines(inputs, 'attention'),
      ...(/^[0-9a-f]{40}$/.test(String(result.baseSha ?? '')) ? [renderSyncSkippedMarker(result.baseSha)] : []),
      `**\`${baseBranch}\` was not merged before the next phase**: ${neutralizeMarkers(reasons.join('; '))}. The implementation continues on its current base; the conflicts are resolved when it merges.`,
    ].join('\n'));
    report.line(`${title(inputs)}: ${baseBranch} not merged before the phase: ${reasons.join('; ')}`);
    report.flush();
    return { exitCode: 0, reasons };
  }
  if (result?.attention || result?.limit) {
    const next = result.limit
      ? `Implement the remaining gaps yourself on the implementation branch, or adjust the spec and tasks. ${RESUME_HINT}`
      : `Resolve the conflicts on the implementation branch and push; the implementation then continues automatically. ${RESUME_HINT}`;
    await requestAttention({ client, env, inputs, reason: reasons.join('; '), next, resultDir });
    report.line(`${title(inputs)} needs a person: ${reasons.join('; ')}`);
    report.flush();
    return { exitCode: 1, reasons };
  }
  if (reasons.length === 0 && inputs.kind === 'task') {
    reasons.push(...applyPatch({ git, resultDir, check: (patchBytes) => checkStagedTask({ git, workspace, folder, task: inputs.task, patchBytes, baseBranch: baseBranchFromEnv(env) }).reasons }));
  }
  let appendedTasks = [];
  if (reasons.length === 0 && inputs.kind === 'converge' && !result.converged) {
    let limit = false;
    reasons.push(...applyPatch({
      git,
      resultDir,
      check: (patchBytes) => {
        const validation = checkStagedConvergence({ git, workspace, folder, patchBytes });
        limit = validation.limit;
        appendedTasks = validation.appended;
        return validation.reasons;
      },
    }));
    if (limit) {
      await requestAttention({ client, env, inputs, reason: reasons.join('; '), next: `Implement the remaining gaps yourself, or adjust the spec and tasks. ${RESUME_HINT}`, resultDir });
      report.flush();
      return { exitCode: 1, reasons };
    }
  }
  if (reasons.length === 0 && inputs.kind === 'resolve') reasons.push(...replayResolution({ git, env, result, resultDir }));
  if (reasons.length > 0) {
    await reportFailure({ client, inputs, reasons, resultDir });
    report.line(`${title(inputs)} failed: ${reasons.join('; ')}`);
    report.flush();
    return { exitCode: 1, reasons };
  }

  const pull = await client.getPullRequest(inputs.pull);
  if (inputs.kind === 'converge' && result.converged) {
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'success',
      output: { title: 'Converged', summary: `The implementation satisfies the spec, plan, and tasks. Next: merge into the base branch ${baseBranchOf(pull)}.` },
    });
    await client.createComment(inputs.pull, [
      ...markerLines(inputs, 'success'),
      `**Converged** (attempt ${inputs.attempt}): the implementation satisfies the spec, plan, and tasks. Next: merge into the base branch \`${baseBranchOf(pull)}\`.`,
      ...agentDetails(resultDir, 'Convergence report'),
    ].join('\n'));
    report.line('Converged.');
    report.flush();
    return { exitCode: 0 };
  }

  const archived = isArchivedIn(workspace, folder);
  const tasksPath = path.join(workspace, tasksPathOf(folder, { archived }));
  const tasksMarkdown = readFileSync(tasksPath, 'utf8');
  let progress = taskProgress(tasksMarkdown);
  let message;
  let checkTitle;
  let comment;
  let body = pull.body;
  if (inputs.kind === 'task') {
    const task = listTasks(tasksMarkdown).find((item) => item.id === inputs.task);
    message = [`feat(${folder}): ${inputs.task} ${task?.text ?? ''}`.trimEnd(), `Implemented by the Spec Kit implement task workflow (attempt ${inputs.attempt}).`];
    checkTitle = `${inputs.task} implemented`;
    const checks = (result.checks ?? []).map((check) => `${check.name} passed`);
    const verification = result.verification === 'not-configured'
      ? 'no verification configured (no environment-verify action)'
      : checks.join(', ') || 'no checks apply to these files';
    comment = [
      `**${inputs.task} implemented** (attempt ${inputs.attempt}): ${task?.text ?? ''}`.trimEnd(),
      '',
      `- Changed files: ${(result.changedFiles ?? []).map((file) => `\`${file}\``).join(', ') || 'none'}`,
      `- Verification: ${verification}`,
      ...(result.flaky ? ['- The first verification failed and the re-run passed, so a test may be flaky'] : []),
      ...((result.uncovered ?? []).length > 0 ? [`- Not covered by any check: ${result.uncovered.map((file) => `\`${neutralizeMarkers(file)}\``).join(', ')}`] : []),
    ];
  } else if (inputs.kind === 'converge') {
    const round = convergenceRounds(tasksMarkdown);
    const heading = tasksMarkdown.split(/\r?\n/).filter((line) => /^##\s+Phase\s+\d+\s*:\s*Convergence\b/i.test(line)).at(-1)?.replace(/^##\s+/, '') ?? `Convergence round ${round}`;
    const appended = appendedTasks;
    message = [`chore(${folder}): convergence round ${round}`, `/speckit-converge appended ${appended.length} task(s) (attempt ${inputs.attempt}).`];
    checkTitle = `Convergence round ${round}: ${appended.length} task(s) appended`;
    const synced = syncPullRequestTaskList(pull.body, {
      twinNumber: inputs.twin,
      folder,
      tasksMarkdown,
      context: { serverUrl: (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, ''), repository: env.GITHUB_REPOSITORY, branch: baseBranchOf(pull), archived },
    });
    body = synced !== String(pull.body ?? '') ? synced : appendPullRequestTasks(pull.body, appended, heading);
    comment = [
      `**Convergence round ${round} of at most ${MAX_CONVERGE_ROUNDS}** found gaps; ${appended.length} task(s) were appended and are implemented next:`,
      '',
      ...appended.map((task) => `- ${task.id} ${neutralizeMarkers(summarizeTask(task.text))}`),
      ...agentDetails(resultDir, 'Convergence report'),
    ];
  } else {
    const files = result.conflicts ?? [];
    message = [
      `Merge ${baseBranchOf(pull)} into ${implementationBranch(folder)}`,
      files.length > 0 ? `Conflicts in ${files.join(', ')} resolved by Copilot (attempt ${inputs.attempt}).` : 'No conflicts remained.',
    ];
    checkTitle = files.length > 0 ? 'Merge conflicts resolved' : 'Base branch merged';
    const checks = (result.checks ?? []).map((check) => `${check.name} passed`);
    comment = [
      `**Conflicts resolved** (attempt ${inputs.attempt}): ${files.map((file) => `\`${file}\``).join(', ') || 'none remained'}.`,
      '',
      `- Verification: ${result.verification === 'not-configured' ? 'no verification configured (no environment-verify action)' : checks.join(', ') || 'no checks'}`,
      openTasksIn(workspace, folder) ? '- Next: the next phase, on the merged branch.' : `- Next: merge into the base branch \`${baseBranchOf(pull)}\`.`,
    ];
  }
  gitOrThrow(git, [...GIT_IDENTITY, 'commit', '--no-verify', ...message.flatMap((part) => ['-m', part])]);
  let head;
  let combined = [];
  if (inputs.kind === 'task') {
    const landed = pushTask({ git, env, folder, task: inputs.task, message, resultDir, workspace });
    if (landed.requeue) {
      await reportRequeue({ client, inputs, reasons: landed.requeue });
      report.line(`${title(inputs)} is redone from the new head: ${landed.requeue.join('; ')}`);
      report.flush();
      return { exitCode: 0, requeue: landed.requeue };
    }
    if (landed.reasons) {
      await reportFailure({ client, inputs, reasons: landed.reasons, resultDir });
      report.line(`${title(inputs)} failed: ${landed.reasons.join('; ')}`);
      report.flush();
      return { exitCode: 1, reasons: landed.reasons };
    }
    head = landed.head;
    combined = landed.combined;
    if (combined.length > 0) {
      comment.push(`- Combined with the parallel changes to ${combined.map((file) => `\`${neutralizeMarkers(file)}\``).join(', ')}; the parallel tasks are verified together next`);
    }
    // The branch may now hold the ticks of parallel tasks that landed first.
    const landedTasks = readFileSync(tasksPath, 'utf8');
    progress = taskProgress(landedTasks);
    comment.push(`- Progress: ${progress.done} of ${progress.total} tasks implemented`);
    const current = String((await client.getPullRequest(inputs.pull)).body ?? '');
    body = syncPullRequestTicks(current, landedTasks);
    pull.body = current;
    await completeSupersededProgress(client, gitOrThrow(git, ['rev-parse', 'HEAD~1']).trim(), head);
  } else {
    head = await push({ client, git, env, inputs, folder, resultDir, report });
    if (!head) return { exitCode: 1, reasons: ['push rejected'] };
  }
  if (body !== pull.body) await client.updatePullRequest(inputs.pull, { body });
  await client.updateCheckRun(inputs.checkRun, { status: 'completed', conclusion: 'success', output: { title: checkTitle, summary: message.join('\n\n') } });
  await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'queued', external_id: CHECK_PROGRESS, output: progressOutput(progress) });
  await client.createComment(inputs.pull, [...markerLines(inputs, 'success'), ...comment].join('\n'));
  report.line(`${checkTitle}; pushed as ${head}.`);
  report.flush();
  return { exitCode: 0, head, combined };
}

const KIND_SUMMARIES = {
  converge: 'Checks the implementation against the spec, plan, and tasks with /speckit-converge.',
  resolve: 'Resolves the merge conflicts with the base branch.',
};

// Shows the attempts of the next tasks as in-progress check runs on `head`. The first one takes over the queued progress
// check run of the head, so it does not stay queued forever. Returns the check run IDs by task (by kind for a
// convergence or a resolution).
export async function startAttemptChecks(client, { head, kind, entries }) {
  const checks = {};
  let queued = (await client.listCheckRuns(head, CHECK_RUN_NAME))
    .filter((check) => check.status === 'queued' && check.external_id === CHECK_PROGRESS)
    .sort((a, b) => b.id - a.id)[0] ?? null;
  for (const entry of entries) {
    const inputs = { kind, task: entry.task || null, attempt: Number(entry.attempt) };
    const fields = {
      status: 'in_progress',
      external_id: attemptCheckId(inputs),
      output: { title: `${title(inputs)} in progress`, summary: entry.summary || KIND_SUMMARIES[kind] || title(inputs) },
    };
    if (queued) {
      await client.updateCheckRun(queued.id, fields);
      checks[inputs.task ?? kind] = queued.id;
      queued = null;
    } else {
      checks[inputs.task ?? kind] = (await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, ...fields })).id;
    }
  }
  return checks;
}

function parseJsonInput(value, name) {
  try {
    return JSON.parse(String(value ?? '').trim() || 'null');
  } catch {
    throw new TaskInputError(`${name} is not valid JSON.`);
  }
}

// The in-progress check run of an attempt on `head` (created by the decide job or after the previous tasks), or a new one.
async function attemptCheck(client, { head, kind, entry }) {
  const id = attemptCheckId({ kind, task: entry.task || null, attempt: Number(entry.attempt) });
  const existing = (await client.listCheckRuns(head, CHECK_RUN_NAME)).filter((check) => check.external_id === id && check.status !== 'completed');
  if (existing.length > 0) return existing.sort((a, b) => b.id - a.id)[0].id;
  return (await startAttemptChecks(client, { head, kind, entries: [entry] }))[entry.task || kind];
}

// Spec Kit implement task, job "land" (write token, never runs agent-written code): lands the results of the tasks at
// SPECKIT_ORDER_INDEX of SPECKIT_ORDER (a task, or consecutive `[P]` tasks) in task order. Each task is landed from the
// commit its work job started from (recorded in its
// result; it must be on the branch, otherwise the branch tip is used); a task whose siblings landed first is rebuilt on
// the moved branch, with files they changed too merged three-way, or redone from the new head when those edits
// conflict (see pushTask). Parallel tasks that combined such edits report `combined`, and their verification follows
// (runCombinedVerified). When every task landed without a combination, the attempts of the next tasks are shown as in
// progress on the new head. Otherwise the job fails on purpose,
// so the phase's tasks matrix (fail-fast, one job at a time) cancels the tasks after these, and the next run retries.
export async function runLandTasks({ client, git, env, folder, workspace, resultRoot, log }) {
  const kind = String(env.SPECKIT_KIND || 'task');
  const twin = Number(env.SPECKIT_TWIN);
  const pull = Number(env.SPECKIT_PULL);
  const order = parseJsonInput(env.SPECKIT_ORDER, 'SPECKIT_ORDER');
  const index = Number(env.SPECKIT_ORDER_INDEX);
  const current = Array.isArray(order) && Number.isInteger(index) && index >= 0 ? order[index] : null;
  if (!['task', 'converge', 'resolve'].includes(kind) || ![twin, pull].every((value) => Number.isInteger(value) && value > 0) || !Array.isArray(current?.include) || current.include.length === 0) {
    throw new TaskInputError('Expected SPECKIT_KIND, SPECKIT_TWIN, SPECKIT_PULL, and SPECKIT_ORDER with an entry at SPECKIT_ORDER_INDEX.');
  }
  const tip = gitOrThrow(git, ['rev-parse', 'HEAD']).trim();
  const outcomes = [];
  for (const entry of current.include) {
    const key = entry.task || kind;
    // download-artifact extracts a single matching artifact straight into its path, several into one folder each.
    const own = path.join(resultRoot, `speckit-result-${key}`);
    const resultDir = current.include.length === 1 && !existsSync(own) ? resultRoot : own;
    const recorded = String(readJson(path.join(resultDir, 'result.json'))?.base ?? '');
    const base = /^[0-9a-f]{40}$/.test(recorded) && git(['merge-base', '--is-ancestor', recorded, tip]).status === 0 ? recorded : tip;
    git(['merge', '--abort']);
    gitOrThrow(git, ['checkout', '-q', '--force', '--detach', base]);
    gitOrThrow(git, ['clean', '-fdq']);
    const checkRun = await attemptCheck(client, { head: base, kind, entry });
    const inputs = { kind, twin, pull, task: entry.task || null, attempt: Number(entry.attempt), checkRun, folder };
    const landed = await runLand({ client, git, env, inputs, folder, workspace, resultDir, workResult: env.SPECKIT_WORK_RESULT, log });
    outcomes.push({ key, landed: landed.exitCode === 0 && !landed.requeue, combined: landed.combined ?? [] });
  }
  const ok = outcomes.every((outcome) => outcome.landed);
  const combined = [...new Set(outcomes.flatMap((outcome) => outcome.combined))].sort();
  const head = (await client.getBranchSha(implementationBranch(folder))) ?? tip;
  const next = order[index + 1];
  // A combination is verified first; the job that reports that verification shows the next tasks (runCombinedVerified).
  if (ok && combined.length === 0 && Array.isArray(next?.include) && next.include.length > 0) await startAttemptChecks(client, { head, kind, entries: next.include });
  setOutput(env, 'ok', String(ok));
  setOutput(env, 'head', head);
  setOutput(env, 'start', tip);
  setOutput(env, 'combined', String(combined.length > 0));
  log(`${outcomes.map((outcome) => `${outcome.key} ${outcome.landed ? 'pushed' : 'not pushed'}`).join(', ')}.`);
  if (combined.length > 0) log(`Parallel changes to ${combined.join(', ')} were combined; the parallel tasks are verified together next.`);
  if (!ok) log('Not every task was pushed, so the tasks after these are cancelled; the next run continues from here.');
  return { exitCode: ok ? 0 : 1, ok, head, combined, outcomes };
}

// Spec Kit implement task, job "report" (write token, never runs agent-written code): after parallel tasks combined
// changes to the same files, their verification decides how the phase continues. Verified: the next tasks are shown as
// in progress on the new head. Otherwise the implementation stops for a person, like a failed phase verification (the
// next run diagnoses it), and the job fails, so the matrix cancels the tasks after these.
export async function runCombinedVerified({ client, env, log }) {
  const report = createReporter(env, log);
  const twin = Number(env.SPECKIT_TWIN);
  const pull = Number(env.SPECKIT_PULL);
  const order = parseJsonInput(env.SPECKIT_ORDER, 'SPECKIT_ORDER');
  const index = Number(env.SPECKIT_ORDER_INDEX);
  const head = String(env.SPECKIT_HEAD ?? '').trim();
  const current = Array.isArray(order) && Number.isInteger(index) && index >= 0 ? order[index] : null;
  if (![twin, pull].every((value) => Number.isInteger(value) && value > 0) || !head || !Array.isArray(current?.include)) {
    throw new TaskInputError('Expected SPECKIT_TWIN, SPECKIT_PULL, SPECKIT_HEAD, and SPECKIT_ORDER with an entry at SPECKIT_ORDER_INDEX.');
  }
  const tasks = current.include.map((entry) => entry.task).filter(Boolean).join(', ');
  const verification = String(env.SPECKIT_VERIFY_RESULT ?? '');
  if (verification === 'success') {
    const next = order[index + 1];
    if (String(env.SPECKIT_LAND_OK) === 'true' && Array.isArray(next?.include) && next.include.length > 0) {
      await startAttemptChecks(client, { head, kind: 'task', entries: next.include });
    }
    report.line(`${tasks} verified together on ${head}.`);
    report.flush();
    return { exitCode: 0, verified: true };
  }
  const outcome = verification === 'failure' ? 'failed' : `did not finish (${verification || 'unknown'})`;
  const server = (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '');
  const run = env.GITHUB_RUN_ID && env.GITHUB_REPOSITORY ? ` (see the [run](${server}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}))` : '';
  await client.createCheckRun({
    name: CHECK_RUN_NAME,
    head_sha: head,
    status: 'completed',
    conclusion: 'failure',
    external_id: CHECK_LIMIT,
    output: { title: 'The combined verification failed', summary: `The verification of ${tasks} together ${outcome}. Their changes to the same files merged cleanly, but the combination does not pass.` },
  });
  const { mention } = await requesterMention(client, twin);
  const behindBase = env.SPECKIT_FOLDER ? await behindBaseOf(client, await client.getPullRequest(pull), env.SPECKIT_FOLDER) : null;
  await client.createComment(pull, [
    `**Implementation needs attention**${mention ? ` ${mention}` : ''}: the verification of ${tasks} together ${outcome}${run}. Each task passed its own verification and their changes to the same files merged cleanly, so the combination breaks something.`,
    '',
    ...renderNextSteps({ autoDiagnosis: String(env.SPECKIT_AUTO_DIAGNOSE ?? 'true').toLowerCase() !== 'false', behindBase }),
  ].join('\n'));
  report.line(`The verification of ${tasks} together ${outcome}; the implementation stops for a person.`);
  report.flush();
  return { exitCode: 1, verified: false };
}
function applyPatch({ git, resultDir, check }) {
  const patchFile = path.join(resultDir, 'changes.patch');
  const patchBytes = existsSync(patchFile) ? statSync(patchFile).size : 0;
  if (patchBytes === 0) return ['the agent made no changes'];
  const applied = git(['apply', '--index', '--whitespace=nowarn', patchFile]);
  if (applied.status !== 0) return [`the change could not be applied: ${applied.stderr.trim()}`];
  return check(patchBytes);
}

// Re-creates the merge in the land job and applies the agent's versions of the conflicted files only. The merged
// commit must be on the base branch, because the work result that names it is not trusted.
function replayResolution({ git, env, result, resultDir }) {
  if (!/^[0-9a-f]{40}$/.test(String(result.baseSha ?? ''))) return ['the base branch SHA is missing'];
  if (git(['cat-file', '-e', `${result.baseSha}^{commit}`]).status !== 0) return [`commit ${result.baseSha} is not available`];
  const baseBranch = baseBranchFromEnv(env);
  if (git(['merge-base', '--is-ancestor', result.baseSha, `refs/remotes/origin/${baseBranch}`]).status !== 0) {
    return [`commit ${result.baseSha} is not on the base branch ${baseBranch}`];
  }
  git([...GIT_IDENTITY, 'merge', '--no-ff', '--no-commit', result.baseSha]);
  const conflicts = unmergedPaths(git);
  const expected = [...(result.conflicts ?? [])].sort();
  if (conflicts.join('\0') !== expected.join('\0')) return [`the merge conflicts changed: expected ${expected.join(', ') || 'none'}, found ${conflicts.join(', ') || 'none'}`];
  const reasons = [];
  const resolutions = new Map((result.resolutions ?? []).map((item) => [item.path, item]));
  for (const file of conflicts) {
    if (isProtected(file)) {
      reasons.push(`conflicts in protected paths need a person: ${file}`);
      continue;
    }
    const resolution = resolutions.get(file);
    if (!resolution) {
      reasons.push(`no resolution for \`${file}\``);
    } else if (resolution.deleted) {
      gitOrThrow(git, ['rm', '--quiet', '--', file]);
    } else {
      const source = path.join(resultDir, 'resolved', path.basename(String(resolution.blob)));
      if (!existsSync(source)) {
        reasons.push(`no resolution for \`${file}\``);
        continue;
      }
      const content = readFileSync(source);
      if (hasConflictMarkers(content.toString('utf8'))) reasons.push(`\`${file}\` still contains conflict markers`);
      const target = gitOrThrow(git, ['rev-parse', '--show-toplevel']).trim();
      writeFileSync(path.join(target, file), content);
      gitOrThrow(git, ['add', '--', file]);
    }
  }
  if (reasons.length === 0 && unmergedPaths(git).length > 0) reasons.push('unresolved conflicts remain');
  if (reasons.length === 0 && git(['rev-parse', '-q', '--verify', 'MERGE_HEAD']).status !== 0) reasons.push('there is nothing to merge from the base branch');
  return reasons;
}

// Spec Kit implement spec, job "merge-land" (write token, no checkout of workspace code): reports the verified integration
// and squash-merges the implementation into its base branch (the pull request's base), or asks for review when
// automatic merging is off.
export async function runMergeLand({ client, env, inputs, resultDir, workResult, log }) {
  const report = createReporter(env, log);
  const result = readJson(path.join(resultDir, 'result.json'));
  const pull = await client.getPullRequest(inputs.pull);
  const baseBranch = baseBranchOf(pull);
  const reasons = result ? [...result.reasons] : [`the merge verification did not finish (result: ${workResult || 'unknown'})`];
  if (result?.attention) {
    await requestAttention({ client, env, inputs, reason: reasons.join('; '), next: `Resolve the conflicts on the implementation branch and push. ${RESUME_HINT}` });
    report.flush();
    return { exitCode: 1, reasons };
  }
  if (reasons.length === 0 && (result.conflicts ?? []).length > 0) {
    const files = result.conflicts.map((file) => `\`${file}\``).join(', ');
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'neutral',
      external_id: CHECK_CONFLICT,
      output: { title: `Conflicts with ${baseBranch}`, summary: `Merging ${baseBranch} at ${result.baseSha} conflicts in ${files}.` },
    });
    await client.createComment(inputs.pull, `**Merge conflicts with \`${baseBranch}\`** in ${files}. They are resolved next.`);
    report.line(`Conflicts with ${baseBranch}: ${result.conflicts.join(', ')}`);
    report.flush();
    return { exitCode: 0, conflicts: result.conflicts };
  }
  if (result && !result.verified && reasons.length === 0) reasons.push('the merged result was not verified');
  if (reasons.length === 0 && pull.head.sha !== result.head) reasons.push(`the implementation branch moved during verification (${result.head} → ${pull.head.sha})`);
  if (reasons.length > 0) {
    await reportFailure({ client, inputs, reasons, resultDir });
    report.line(`Merge attempt ${inputs.attempt} failed: ${reasons.join('; ')}`);
    report.flush();
    return { exitCode: 1, reasons };
  }

  const { tasksMarkdown: specTasks, archived } = await readSpecTasks(client, inputs.folder, result.head);
  const tasksMarkdown = specTasks ?? '';
  const progress = taskProgress(tasksMarkdown);
  const checkNames = (result.checks ?? []).map((check) => check.name).join(', ');
  const verification = result.verification === 'not-configured' ? 'no verification is configured (no environment-verify action)' : `the full verification passed (${checkNames || 'no checks'})`;
  const { login, mention } = await requesterMention(client, inputs.twin);
  if (pull.draft) await client.markReadyForReview(pull.node_id);

  // Decided from the GitHub API, not from the work result, which agent-written code could forge. Renames count with
  // both paths; a list cut off at GitHub's limit is held for review because it may hide environment changes.
  const pullFiles = await client.listPullRequestFiles(inputs.pull);
  const environmentFiles = [...new Set(pullFiles.flatMap((file) => [file.filename, file.previous_filename]).filter((file) => file && isEnvironmentPath(file)))];
  const reviewReasons = [
    ...(String(env.SPECKIT_AUTO_MERGE ?? 'true').toLowerCase() === 'false' ? ['automatic merging is off (`SPECKIT_AUTO_MERGE=false`)'] : []),
    ...(environmentFiles.length > 0 ? [`it changes the environment actions (${environmentFiles.map((file) => `\`${file}\``).join(', ')}), which every later implementation is verified with, so a person reviews it`] : []),
    ...(pullFiles.length >= MAX_PULL_FILES ? [`it changes ${MAX_PULL_FILES} or more files, more than GitHub lists for a pull request`] : []),
    ...((result.uncovered ?? []).length > 0 ? [`no check covers ${result.uncovered.map((file) => `\`${neutralizeMarkers(file)}\``).join(', ')}; extend the environment actions with a standalone environment spec`] : []),
  ];
  if (reviewReasons.length > 0) {
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'success',
      external_id: CHECK_DONE,
      output: progressOutput(progress, `All tasks are implemented, converged, and ${verification}. Ready for review.`),
    });
    let reviewNote = '';
    if (login) {
      try {
        await client.requestReviewers(inputs.pull, [login]);
        reviewNote = ` Review requested from ${mention}.`;
      } catch (error) {
        reviewNote = ` Could not request a review from ${mention}: ${error.message}`;
      }
    }
    await client.createComment(inputs.pull, [
      DONE_COMMENT_MARKER,
      `**All ${progress.total} tasks are implemented**, the implementation converged, and ${verification}.`,
      '',
      'This pull request is ready for review instead of being merged automatically, because:',
      '',
      ...reviewReasons.map((reason) => `- ${reason}`),
      '',
      `Merge it to complete the implementation.${reviewNote}`,
    ].join('\n'));
    report.line(`Ready for review: #${inputs.pull}: ${reviewReasons.join('; ')}.${reviewNote}`);
    report.flush();
    return { exitCode: 0, review: reviewReasons };
  }

  const baseNow = await client.getBranchSha(baseBranch);
  if (baseNow !== result.baseSha) {
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'neutral',
      output: { title: `${baseBranch} moved during verification`, summary: `Verified against ${result.baseSha}, but ${baseBranch} is now ${baseNow}. The merge is verified again.` },
    });
    report.line(`${baseBranch} moved from ${result.baseSha} to ${baseNow}; the merge is verified again.`);
    report.flush();
    return { exitCode: 0, moved: true };
  }
  const taskLines = listTasks(tasksMarkdown).map((task) => `- ${task.id} ${summarizeTask(task.text)}`);
  // GitHub's merge endpoint cannot merge a stacked pull request; Spec Kit pull requests are never stacked.
  if (pull.stack?.number) {
    try {
      await client.unstackPullRequests(pull.stack.number);
    } catch {
      // The merge reports the refusal.
    }
  }
  const merged = await client.mergePullRequest(inputs.pull, {
    sha: result.head,
    merge_method: 'squash',
    commit_title: `${pull.title.replace(/^Implement:\s*/, '')} (#${inputs.pull})`,
    commit_message: [...taskLines, '', `Implemented with Spec Kit in #${inputs.pull}.`, `Closes #${inputs.twin}`].join('\n'),
  });
  if (!merged.merged) {
    await reportFailure({ client, inputs, reasons: [`GitHub did not merge the pull request: ${merged.message ?? 'unknown reason'}`], resultDir });
    report.flush();
    return { exitCode: 1 };
  }
  await client.updateCheckRun(inputs.checkRun, {
    status: 'completed',
    conclusion: 'success',
    external_id: CHECK_DONE,
    output: progressOutput(progress, `Merged into ${baseBranch} as ${merged.sha}.`),
  });
  const twin = await client.getIssue(inputs.twin);
  const notes = [];
  await updateIssueWithLabels(client, twin, stageLabelChange(twin, 'implemented'), {
    ...(twin.state === 'open' ? { state: 'closed', state_reason: 'completed' } : {}),
    ...twinLinksAfterMerge(twin, inputs.folder, { baseBranch, defaultBranch: defaultBranchOf(env), archived }),
  });
  await pointPullRequestLinks(client, pull, inputs.folder, { baseBranch, archived }, notes);
  try {
    await client.deleteBranch(implementationBranch(inputs.folder));
  } catch (error) {
    notes.push(`The branch could not be deleted: ${error.message}`);
  }
  try {
    await client.dispatchWorkflow(PREPARE_WORKFLOW_FILE, defaultBranchOf(env), { dry_run: 'false' });
  } catch (error) {
    notes.push(`Spec Kit prepare could not be started: ${error.message}`);
  }
  await client.createComment(inputs.pull, [
    DONE_COMMENT_MARKER,
    `**Merged into \`${baseBranch}\`** as ${merged.sha}${mention ? ` ${mention}` : ''}: all ${progress.total} tasks are implemented, the implementation converged, and ${verification}.`,
    ...(notes.length > 0 ? ['', ...notes.map((note) => `- ${note}`)] : []),
  ].join('\n'));
  report.line(`Merged #${inputs.pull} into ${baseBranch} as ${merged.sha}.`);
  report.flush();
  return { exitCode: 0, merged: merged.sha };
}

function githubClient(env) {
  return new GitHubClient({
    token: env.GITHUB_TOKEN || env.GH_TOKEN,
    repository: env.GITHUB_REPOSITORY,
    apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
    graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
  });
}

// After the merge the twin's links follow the spec: its links point to the default branch, so they are rewritten right
// away when the implementation merged there (Spec Kit prepare regenerates them as well); otherwise prepare updates them
// once the spec reaches the default branch.
export function twinLinksAfterMerge(twin, folder, { baseBranch, defaultBranch, archived }) {
  if (!archived || baseBranch !== defaultBranch) return {};
  const body = pointSpecLinks(twin.body, folder, { branch: defaultBranch, archived: true });
  return body === String(twin.body ?? '') ? {} : { body };
}

// After the merge the implementation pull request's links point to the spec on the base branch (archived or not)
// instead of the deleted implementation branch.
export async function pointPullRequestLinks(client, pull, folder, { baseBranch, archived }, notes = []) {
  const body = pointSpecLinks(pull.body, folder, { branch: baseBranch, archived });
  if (body === String(pull.body ?? '')) return;
  try {
    await client.updatePullRequest(pull.number, { body });
  } catch (error) {
    notes.push(`The pull request links could not be updated: ${error.message}`);
  }
}

// Spec Kit implement spec, job "merge-archive" (write token, trusted tooling; it never runs agent-written code): archives
// the implemented spec on the implementation branch right before the merge, so the move to specs/archive/<folder> and
// every rewritten reference land with the implementation. It works on the branch merged with the base branch, because
// specs merged there since may link to this one, and it also points references to specs archived there since to the
// archive. When that merge conflicts, it changes nothing: the merge verification reports the conflicts, and the spec is
// archived once they are resolved. When the base branch changed workflow files, which the workflow token cannot push,
// it archives without merging it. It pushes only when the archive itself changes something, so a moving base branch
// does not create new heads (each would restart the count of merge attempts); after a push the merge check run moves
// to the new head, which the verification and the merge then use.
export async function runArchive({ client, git, env, folder, log }) {
  const report = createReporter(env, log);
  const baseBranch = baseBranchFromEnv(env);
  const pullNumber = Number(env.SPECKIT_PULL);
  const checkRun = Number(env.SPECKIT_CHECK_RUN);
  const expected = String(env.SPECKIT_HEAD ?? '').trim();
  const finish = (exitCode, head, check, line) => {
    report.line(line);
    report.flush();
    setOutput(env, 'head', head);
    setOutput(env, 'check_run', String(check));
    return { exitCode, head, checkRun: check };
  };
  const head = gitOrThrow(git, ['rev-parse', 'HEAD']).trim();
  if (expected && head !== expected) return finish(1, head, checkRun, `The implementation branch moved during the merge (${expected} → ${head}); the merge runs again.`);
  const workspace = gitOrThrow(git, ['rev-parse', '--show-toplevel']).trim();
  const undo = () => {
    git(['merge', '--abort']);
    git(['reset', '-q', '--hard', head]);
    git(['clean', '-fdq']);
  };
  const merge = git([...GIT_IDENTITY, 'merge', '--no-ff', '--no-commit', `refs/remotes/origin/${baseBranch}`]);
  if (unmergedPaths(git).length > 0) {
    undo();
    return finish(0, head, checkRun, `\`${baseBranch}\` conflicts with the implementation branch; the spec is archived once the conflicts are resolved.`);
  }
  if (merge.status !== 0) {
    undo();
    return finish(1, head, checkRun, `Merging \`${baseBranch}\` failed: ${merge.stderr.trim() || merge.stdout.trim()}`);
  }
  let merging = git(['rev-parse', '-q', '--verify', 'MERGE_HEAD']).status === 0;
  const notes = [];
  if (merging) {
    const workflows = splitZ(gitOrThrow(git, ['diff', '--cached', '--name-only', '--no-renames', '-z', 'HEAD'])).filter((file) => file.startsWith('.github/workflows/'));
    if (workflows.length > 0) {
      undo();
      merging = false;
      notes.push(`\`${baseBranch}\` changed workflow files, which the workflow token cannot push, so the spec was archived without merging it; the merge verification checks the links after merging it.`);
    }
  }
  const before = gitOrThrow(git, ['write-tree']).trim();
  const archived = archiveSpec({ root: workspace, folder, git });
  if (archived.exitCode !== 0) {
    undo();
    return finish(1, head, checkRun, `The spec could not be archived: ${archived.reasons.join('; ')}.`);
  }
  const refreshed = refreshArchivedReferences({ root: workspace, git, except: folder });
  gitOrThrow(git, ['add', '-A']);
  if (gitOrThrow(git, ['write-tree']).trim() === before) {
    undo();
    return finish(0, head, checkRun, `\`${specDir(folder, { archived: true })}\` is already archived and every reference points there.`);
  }
  const edited = [...new Set([...archived.changes.filter((change) => change.edited && change.file === change.from).map((change) => change.file), ...refreshed])].sort();
  gitOrThrow(git, [...GIT_IDENTITY, 'commit', '--no-verify',
    '-m', archived.moved ? `Archive ${specDir(folder)} to ${specDir(folder, { archived: true })}` : `Point new references to ${specDir(folder, { archived: true })}`,
    '-m', `${merging ? `Merges ${baseBranch} first, so references that arrived there are rewritten too. ` : ''}Rewrites the references in ${edited.length} file(s).`]);
  const branch = implementationBranch(folder);
  const pushed = git(pushArgs(env, branch));
  if (pushed.status !== 0) return finish(1, head, checkRun, `The archived spec could not be pushed to \`${branch}\`: ${pushed.stderr.trim().split('\n').at(-1)}`);
  const newHead = gitOrThrow(git, ['rev-parse', 'HEAD']).trim();
  let movedCheck = checkRun;
  if (Number.isInteger(checkRun) && checkRun > 0) {
    movedCheck = (await client.createCheckRun({
      name: CHECK_RUN_NAME,
      head_sha: newHead,
      status: 'in_progress',
      external_id: CHECK_MERGE,
      output: { title: `Merge attempt ${Number(env.SPECKIT_ATTEMPT) || 1} in progress`, summary: `Archived the spec; merges \`${baseBranch}\`, runs the full verification, and merges the implementation into it.` },
    })).id;
    await client.updateCheckRun(checkRun, { status: 'completed', conclusion: 'neutral', output: { title: 'Continued after archiving the spec', summary: `The spec was archived in ${newHead}; the merge continues there.` } });
  }
  await client.createComment(pullNumber, [
    `**Archived** \`${specDir(folder)}\` → \`${specDir(folder, { archived: true })}\` for the merge${merging ? ` (merged \`${baseBranch}\` first)` : ''}.`,
    '',
    edited.length > 0 ? `References rewritten in ${edited.map((file) => `\`${neutralizeMarkers(file)}\``).join(', ')}.` : 'No other file referenced the spec.',
    ...notes.flatMap((note) => ['', note]),
  ].join('\n'));
  return finish(0, newHead, movedCheck, `Archived ${specDir(folder)} in ${newHead}${merging ? ` (merged ${baseBranch} first)` : ''}; rewrote ${edited.length} file(s).`);
}

const COMMANDS = 'integrate | package | verdict | land-tasks | combined-verified | archive | merge-land';

export async function main(argv, { env = process.env, log = console.log, client, git } = {}) {
  const [command] = argv;
  const workspace = path.resolve(env.SPECKIT_WORKSPACE || 'workspace');
  const resultDir = path.resolve(env.SPECKIT_RESULT_DIR || 'result');
  const folder = env.SPECKIT_FOLDER;
  if (command === 'combined-verified') return (await runCombinedVerified({ client: client ?? githubClient(env), env, log })).exitCode;
  if (command === 'archive') return (await runArchive({ client: client ?? githubClient(env), git: git ?? defaultGit(workspace), env, folder, log })).exitCode;
  if (command === 'land-tasks') {
    return (await runLandTasks({
      client: client ?? githubClient(env),
      git: git ?? defaultGit(workspace),
      env,
      folder,
      workspace,
      resultRoot: path.resolve(env.SPECKIT_RESULT_ROOT || 'results'),
      log,
    })).exitCode;
  }
  if (!['integrate', 'package', 'verdict', 'merge-land'].includes(command)) {
    throw new TaskInputError(`Usage: speckit-task.mjs <${COMMANDS}> (inputs come from SPECKIT_* environment variables)`);
  }
  const inputs = parseTaskInputs(env);
  if (command === 'integrate') return runIntegrate({ git: git ?? defaultGit(workspace), env, inputs, folder, resultDir, log }).exitCode;
  if (command === 'package') return runPackage({ git: git ?? defaultGit(workspace), env, inputs, folder, workspace, resultDir, log }).exitCode;
  if (command === 'verdict') return runVerdict({ env, inputs, resultDir, log }).exitCode;
  const withChecks = { ...inputs, checkRun: Number(env.SPECKIT_CHECK_RUN), folder };
  return (await runMergeLand({ client: client ?? githubClient(env), env, inputs: withChecks, resultDir, workResult: env.SPECKIT_WORK_RESULT, log })).exitCode;
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof TaskInputError ? 2 : 1;
    },
  );
}
