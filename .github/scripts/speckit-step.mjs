import { spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { IMPLEMENT_STAGE, hasLabel, resolveTwins, stageLabel, stageLabelChange } from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { resolveImplementRequester, updateIssueWithLabels } from './speckit-prepare.mjs';
import {
  CHECK_ATTEMPT,
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
  convergenceRounds,
  environmentExclusivityReasons,
  hasConflictMarkers,
  implementationBranch,
  isEnvironmentPath,
  isProtectedPath,
  latestCheckRun,
  listTasks,
  neutralizeMarkers,
  nextTaskGroup,
  parseMaxParallel,
  progressOutput,
  renderConvergePrompt,
  renderResolvePrompt,
  renderTaskPrompt,
  stepLabel,
  summarizeTask,
  syncPullRequestTicks,
  taskProgress,
  tickTask,
  validateConvergeChange,
  validateTaskChange,
} from './speckit-implement-core.mjs';

export const MAX_PATCH_BYTES = 5 * 1024 * 1024;
export const MAX_CHANGED_FILES = 500;
// GitHub lists at most this many files of a pull request.
const MAX_PULL_FILES = 3000;
export const STEP_NAMES = ['task', 'converge', 'resolve', 'merge'];
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

// Inputs of a worker step from SPECKIT_* environment variables. SPECKIT_STEP selects the step (task, converge,
// resolve, or merge); SPECKIT_TASK is the task ID of a task step.
export function parseStepInputs(env) {
  const step = String(env.SPECKIT_STEP || 'task');
  const twin = Number(env.SPECKIT_TWIN);
  const pull = Number(env.SPECKIT_PULL);
  const attempt = Number(env.SPECKIT_ATTEMPT);
  const task = step === 'task' ? String(env.SPECKIT_TASK ?? '') : null;
  if (!STEP_NAMES.includes(step)) throw new TaskInputError(`Unknown step "${step}"; expected one of ${STEP_NAMES.join(', ')}.`);
  if (![twin, pull, attempt].every((value) => Number.isInteger(value) && value > 0) || (step === 'task' && !/^T\d{3,}$/.test(task))) {
    throw new TaskInputError('Expected positive integers for twin, pull, and attempt, and a task like T001 for task steps.');
  }
  return { step, twin, pull, task, attempt };
}

function title(inputs) {
  const label = stepLabel(inputs);
  return `${label.charAt(0).toUpperCase()}${label.slice(1)} attempt ${inputs.attempt}`;
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

const latestMergeState = (checks) => latestCheckRun(checks.filter((check) => [CHECK_MERGE, CHECK_CONFLICT].includes(check.external_id)));

// Worker workflows, job "begin": checks that the requested step is still the next step of an open, flagged
// implementation and marks the attempt as in progress. Mismatches end as a no-op.
export async function runBegin({ client, env, inputs, log }) {
  const report = createReporter(env, log);
  report.line(`## ${title(inputs)} for #${inputs.twin}`);
  report.line();
  const reasons = [];
  const issue = await client.getIssue(inputs.twin);
  const folder = resolveTwins([issue]).byFolder.keys().next().value;
  if (!folder) reasons.push(`#${inputs.twin} is not a spec twin`);
  if (issue.state !== 'open' || !hasLabel(issue, stageLabel(IMPLEMENT_STAGE))) reasons.push(`#${inputs.twin} is not open and flagged for implementation`);
  const pull = await client.getPullRequest(inputs.pull);
  if (folder && (pull.state !== 'open' || pull.head?.ref !== implementationBranch(folder) || pull.head?.repo?.full_name !== env.GITHUB_REPOSITORY)) {
    reasons.push(`#${inputs.pull} is not the open implementation pull request of \`specs/${folder}\``);
  }
  let tasksMarkdown = null;
  let checks = [];
  let takeOver = true;
  if (reasons.length === 0) {
    tasksMarkdown = await client.getFileContent(`specs/${folder}/tasks.md`, pull.head.sha);
    checks = await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME);
    const group = nextTaskGroup(tasksMarkdown, parseMaxParallel(env.SPECKIT_MAX_PARALLEL_TASKS)).map((task) => task.id);
    const next = group[0] ?? null;
    if (tasksMarkdown === null) reasons.push(`\`specs/${folder}/tasks.md\` is missing on the branch`);
    else if (inputs.step === 'task' && !group.includes(inputs.task)) reasons.push(`the next task${group.length > 1 ? 's are' : ' is'} ${group.join(', ') || 'none'}, not ${inputs.task}`);
    else if (inputs.step !== 'task' && next) reasons.push(`task ${next} is not implemented yet`);
    else if (inputs.step === 'resolve' && latestMergeState(checks)?.external_id !== CHECK_CONFLICT) reasons.push('no merge conflict is recorded for the current head');
    // Only the first task of a group takes over the queued progress check run, so parallel tasks never share one.
    takeOver = inputs.step !== 'task' || inputs.task === next;
  }
  if (reasons.length > 0) {
    report.line(`Nothing to do: ${reasons.join('; ')}.`);
    report.flush();
    setOutput(env, 'proceed', 'false');
    return { exitCode: 0, proceed: false, reasons };
  }

  const task = inputs.step === 'task' ? listTasks(tasksMarkdown).find((item) => item.id === inputs.task) : null;
  const summary = {
    task: () => `${task.id} ${task.text}`,
    converge: () => 'Checks the implementation against the spec, plan, and tasks with /speckit-converge.',
    resolve: () => 'Resolves the merge conflicts with the default branch.',
  }[inputs.step]();
  const attemptFields = { status: 'in_progress', external_id: CHECK_ATTEMPT, output: { title: `${title(inputs)} in progress`, summary } };
  // Take over the queued progress check run of this head, so no check run stays queued forever.
  const queued = latestCheckRun(checks.filter((item) => item.status === 'queued' && item.external_id === CHECK_PROGRESS));
  const check = takeOver && queued
    ? { ...queued, ...(await client.updateCheckRun(queued.id, attemptFields)), id: queued.id }
    : await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: pull.head.sha, ...attemptFields });
  report.line(`Working on ${stepLabel(inputs)} of \`specs/${folder}\` at ${pull.head.sha}.`);
  report.flush();
  setOutput(env, 'proceed', 'true');
  setOutput(env, 'folder', folder);
  setOutput(env, 'head', pull.head.sha);
  setOutput(env, 'check_run', String(check.id));
  setOutput(env, 'prompt', inputs.step === 'task' ? renderTaskPrompt(inputs.task) : inputs.step === 'converge' ? renderConvergePrompt() : '');
  return { exitCode: 0, proceed: true, folder, check };
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

function tasksFiles(git, workspace, folder) {
  const tasksPath = `specs/${folder}/tasks.md`;
  const before = git(['show', `HEAD:${tasksPath}`]);
  const afterFile = path.join(workspace, tasksPath);
  return { before: before.status === 0 ? before.stdout : '', after: existsSync(afterFile) ? readFileSync(afterFile, 'utf8') : '' };
}

// Paths the implementation branch changes compared to the default branch, including the staged change, or null when
// the default branch is not in the checkout.
function branchPaths(git, defaultBranch) {
  const base = git(['merge-base', 'HEAD', `refs/remotes/origin/${defaultBranch}`]);
  if (base.status !== 0) return null;
  return splitZ(gitOrThrow(git, ['diff', '--cached', '--name-only', '--no-renames', '-z', base.stdout.trim()]));
}

// Validates the staged change of one task and returns the reasons it cannot be accepted.
export function checkStagedTask({ git, workspace, folder, task, patchBytes, defaultBranch = 'main' }) {
  const paths = changedPaths(git);
  const reasons = sizeReasons(patchBytes, paths);
  reasons.push(...validateTaskChange({ folder, taskId: task, ...tasksFiles(git, workspace, folder), changedPaths: paths }));
  const branch = branchPaths(git, defaultBranch);
  if (branch === null) reasons.push(`the branch cannot be compared with ${defaultBranch}, because ${defaultBranch} is not in the checkout`);
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

// Worker workflows, job "work", resolve and merge steps: merges the default branch into the checked-out
// implementation branch without committing, records the default branch SHA and any conflicted files, and prepares
// the agent prompt for conflict resolution. Conflicts in protected paths need a person. After a clean merge it
// writes the paths the implementation changes compared to the default branch for the verification.
export function runIntegrate({ git, env, inputs, folder, resultDir, log }) {
  mkdirSync(resultDir, { recursive: true });
  const defaultBranch = env.SPECKIT_BRANCH || 'main';
  const mainSha = gitOrThrow(git, ['rev-parse', `refs/remotes/origin/${defaultBranch}`]).trim();
  const head = gitOrThrow(git, ['rev-parse', 'HEAD']).trim();
  const merge = git([...GIT_IDENTITY, 'merge', '--no-ff', '--no-commit', mainSha]);
  const conflicts = unmergedPaths(git);
  const result = { step: inputs.step, mainSha, head, conflicts, reasons: [], checks: [], verified: false };
  if (merge.status !== 0 && conflicts.length === 0) result.reasons.push(`merging ${defaultBranch} failed: ${merge.stderr.trim() || merge.stdout.trim()}`);
  const protectedConflicts = conflicts.filter(isProtected);
  if (protectedConflicts.length > 0) {
    result.reasons.push(`conflicts in protected paths need a person: ${protectedConflicts.join(', ')}`);
    result.attention = true;
  }
  const paths = conflicts.length === 0 && result.reasons.length === 0
    ? splitZ(gitOrThrow(git, ['diff', '--cached', '--name-only', '--no-renames', '-z', mainSha]))
    : [];
  result.changedFiles = paths;
  result.environment = paths.some(isEnvironmentPath);
  writeFileSync(path.join(resultDir, 'changed-files.txt'), paths.map((file) => `${file}\n`).join(''));
  writeResult(resultDir, result);
  const agent = inputs.step === 'resolve' && conflicts.length > 0 && result.reasons.length === 0;
  setOutput(env, 'conflicts', String(conflicts.length > 0));
  setOutput(env, 'agent', String(agent));
  setOutput(env, 'verify', String(inputs.step === 'merge' && conflicts.length === 0 && result.reasons.length === 0));
  setOutput(env, 'environment', String(inputs.step === 'merge' && result.environment));
  setOutput(env, 'prompt', agent ? renderResolvePrompt({ folder, files: conflicts }) : '');
  log(conflicts.length > 0 ? `Conflicts with ${defaultBranch} (${mainSha}): ${conflicts.join(', ')}` : `Merged ${defaultBranch} (${mainSha}) cleanly.`);
  return { exitCode: 0, result };
}

// Worker workflows, job "work" (read-only token): packages and validates the agent's change. It writes the changed
// paths for the solution's optional environment-verify action and a preliminary result; "verdict" completes it.
// The result is a quality report; the "land" job re-validates everything it relies on before pushing.
export function runPackage({ git, env, inputs, folder, workspace, resultDir, log }) {
  mkdirSync(resultDir, { recursive: true });
  let result;
  let paths = [];
  if (inputs.step === 'task') {
    const agent = agentReasons(resultDir);
    result = { step: 'task', task: inputs.task, agentExit: agent.agentExit, reasons: [...agent.reasons], checks: [], verified: false };
    result.patchBytes = stagePatch(git, resultDir);
    const staged = checkStagedTask({ git, workspace, folder, task: inputs.task, patchBytes: result.patchBytes, defaultBranch: env.SPECKIT_BRANCH || 'main' });
    paths = staged.paths;
    result.changedFiles = paths;
    result.reasons.push(...staged.reasons);
  } else if (inputs.step === 'converge') {
    const agent = agentReasons(resultDir);
    result = { step: 'converge', agentExit: agent.agentExit, reasons: [...agent.reasons], checks: [], verified: false };
    result.patchBytes = stagePatch(git, resultDir);
    const staged = checkStagedConvergence({ git, workspace, folder, patchBytes: result.patchBytes });
    result.changedFiles = staged.paths;
    result.appended = staged.appended;
    result.limit = staged.limit;
    result.reasons.push(...staged.reasons);
    result.converged = result.reasons.length === 0 && staged.paths.length === 0;
    // Convergence changes no code, so there is nothing to verify.
    result.verified = result.reasons.length === 0;
  } else if (inputs.step === 'resolve') {
    result = readJson(path.join(resultDir, 'result.json')) ?? { step: 'resolve', conflicts: [], reasons: ['the integration did not run'], checks: [] };
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
    throw new TaskInputError(`The ${inputs.step} step has no package command.`);
  }
  // Agents must leave their changes uncommitted; a moved HEAD would hide them from the staged diff.
  if (env.SPECKIT_HEAD && gitOrThrow(git, ['rev-parse', 'HEAD']).trim() !== env.SPECKIT_HEAD) {
    result.reasons.push('the agent created commits, but agents must leave their changes uncommitted');
    result.converged = false;
    result.verified = false;
  }
  writeFileSync(path.join(resultDir, 'changed-files.txt'), paths.map((file) => `${file}\n`).join(''));
  writeResult(resultDir, result);
  const verify = inputs.step !== 'converge' && result.reasons.length === 0;
  setOutput(env, 'verify', String(verify));
  log(result.reasons.length === 0 ? `Packaged ${stepLabel(inputs)}.` : `Rejected: ${result.reasons.join('; ')}`);
  return { exitCode: 0, verify, result };
}

// Worker workflows, job "work": records the outcome of the optional environment-verify action in the result.
// SPECKIT_VERIFY_CONFIGURED tells whether the action exists; SPECKIT_VERIFY_OUTCOME and SPECKIT_VERIFY_CHECKS are the
// step's outcome and its "checks" output (comma-separated, in order; after a failure the last one failed);
// SPECKIT_VERIFY_UNCOVERED lists changed files that no check covers. For a merge that changes the environment
// actions, SPECKIT_SELFTEST_OUTCOME is the outcome of running the changed actions themselves.
export function runVerdict({ env, inputs, resultDir, log }) {
  mkdirSync(resultDir, { recursive: true });
  const resultFile = path.join(resultDir, 'result.json');
  const result = readJson(resultFile) ?? { step: inputs.step, task: inputs.task, reasons: ['the change was not packaged'], checks: [] };
  const configured = String(env.SPECKIT_VERIFY_CONFIGURED ?? '') === 'true';
  result.verification = configured ? 'configured' : 'not-configured';
  const pendingConflicts = inputs.step === 'merge' && (result.conflicts ?? []).length > 0;
  if (result.reasons.length === 0 && configured && !pendingConflicts) {
    const names = String(env.SPECKIT_VERIFY_CHECKS ?? '').split(',').map((name) => name.trim()).filter(Boolean);
    const outcome = String(env.SPECKIT_VERIFY_OUTCOME ?? '');
    if (outcome === 'success') {
      result.checks = names.map((name) => ({ name, passed: true }));
    } else {
      result.checks = names.map((name, index) => ({ name, passed: index < names.length - 1 }));
      result.reasons.push(names.length > 0 ? `${names.at(-1)} failed` : `the verification did not succeed (${outcome || 'not run'})`);
    }
    result.uncovered = String(env.SPECKIT_VERIFY_UNCOVERED ?? '').split(',').map((file) => file.trim()).filter(Boolean);
  }
  if (result.reasons.length === 0 && inputs.step === 'merge' && result.environment && !pendingConflicts) {
    const selfTest = String(env.SPECKIT_SELFTEST_OUTCOME ?? '');
    result.selfTest = selfTest || 'skipped';
    if (selfTest && !['success', 'skipped'].includes(selfTest)) result.reasons.push(`the changed environment actions failed their self-test (${selfTest})`);
  }
  result.verified = result.reasons.length === 0 && !pendingConflicts;
  writeFileSync(resultFile, `${JSON.stringify(result, null, 2)}\n`);
  if (pendingConflicts) log(`Conflicts with the default branch: ${result.conflicts.join(', ')}`);
  else if (!result.verified) log(`Not verified: ${result.reasons.join('; ')}`);
  else log(configured ? `Verified ${stepLabel(inputs)}.` : `${stepLabel(inputs)} is not verified: no environment-verify action is configured.`);
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
    `**${title(inputs)} failed:**`,
    '',
    ...safeReasons.map((reason) => `- ${reason}`),
    ...(verification.trim() ? ['', '<details><summary>Verification output (tail)</summary>', '', fence(verification), '', '</details>'] : []),
    ...agentDetails(resultDir),
    '',
    'The orchestrator retries until the attempt limit is reached.',
  ].join('\n'));
}

// Stops the implementation and asks the person who requested it, because automation cannot continue.
async function requestAttention({ client, inputs, reason, next, resultDir }) {
  const { mention } = await requesterMention(client, inputs.twin);
  await client.updateCheckRun(inputs.checkRun, {
    status: 'completed',
    conclusion: 'failure',
    external_id: CHECK_LIMIT,
    output: { title: `${title(inputs)} needs a person`, summary: neutralizeMarkers(reason) },
  });
  await client.createComment(inputs.pull, [
    `**Implementation needs attention**${mention ? ` ${mention}` : ''}: ${neutralizeMarkers(reason)}`,
    '',
    next,
    ...(resultDir ? agentDetails(resultDir) : []),
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
// everything is validated again. A change that no longer applies, or touches files that changed on the branch since
// the task started, has to be redone from the new head: that is a requeue, not a failed attempt.
function pushTask({ git, env, folder, task, message, resultDir, workspace }) {
  const branch = implementationBranch(folder);
  const tasksPath = `specs/${folder}/tasks.md`;
  const patchFile = path.join(resultDir, 'changes.patch');
  const base = gitOrThrow(git, ['rev-parse', 'HEAD~1']).trim();
  const ours = splitZ(gitOrThrow(git, ['diff', '--name-only', '--no-renames', '-z', base, 'HEAD'])).filter((file) => file !== tasksPath);
  let onto = base;
  for (let tries = 0; tries < MAX_LAND_TRIES; tries += 1) {
    const pushed = git(pushArgs(env, branch));
    if (pushed.status === 0) return { head: gitOrThrow(git, ['rev-parse', 'HEAD']).trim() };
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
    if (overlap.length > 0) return { requeue: [`${overlap.map((file) => `\`${file}\``).join(', ')} changed on the branch since the task started`] };
    gitOrThrow(git, ['checkout', '-q', '--detach', newHead]);
    const applied = git(['apply', '--index', '--whitespace=nowarn', `--exclude=${tasksPath}`, patchFile]);
    if (applied.status !== 0) return { requeue: [`the change no longer applies to the branch (${applied.stderr.trim().split('\n').at(-1)})`] };
    const tasksFile = path.join(workspace, tasksPath);
    const ticked = tickTask(readFileSync(tasksFile, 'utf8'), task);
    if (ticked === null) return { requeue: [`${task} is already checked on the branch`] };
    writeFileSync(tasksFile, ticked);
    gitOrThrow(git, ['add', '--', tasksPath]);
    const staged = checkStagedTask({ git, workspace, folder, task, patchBytes: statSync(patchFile).size, defaultBranch: env.SPECKIT_BRANCH || 'main' });
    if (staged.reasons.length > 0) return { reasons: staged.reasons };
    gitOrThrow(git, [...GIT_IDENTITY, 'commit', '--no-verify', ...message.flatMap((part) => ['-m', part])]);
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

const RESUME_HINT = 'Fix the cause (for example by pushing to the implementation branch, which continues automatically), or run the '
  + '`Spec Kit orchestrate` workflow manually with `twin` set to the twin issue number. To abandon the implementation, close this pull request.';

// Worker workflows, job "land" (write token, never runs agent-written code): re-validates the change, commits and
// pushes it, and reports progress.
export async function runLand({ client, git, env, inputs, folder, workspace, resultDir, workResult, log }) {
  const report = createReporter(env, log);
  const result = readJson(path.join(resultDir, 'result.json'));
  const reasons = [];
  if (!result) reasons.push(`the work job did not finish (result: ${workResult || 'unknown'})`);
  else reasons.push(...result.reasons);
  if (result && !result.verified && result.reasons.length === 0) reasons.push('the change was not verified');

  if (result?.attention || result?.limit) {
    const next = result.limit
      ? `Implement the remaining gaps yourself on the implementation branch, or adjust the spec and tasks. ${RESUME_HINT}`
      : `Resolve the conflicts on the implementation branch and push; the implementation then continues automatically. ${RESUME_HINT}`;
    await requestAttention({ client, inputs, reason: reasons.join('; '), next, resultDir });
    report.line(`${title(inputs)} needs a person: ${reasons.join('; ')}`);
    report.flush();
    return { exitCode: 1, reasons };
  }
  if (reasons.length === 0 && inputs.step === 'task') {
    reasons.push(...applyPatch({ git, resultDir, check: (patchBytes) => checkStagedTask({ git, workspace, folder, task: inputs.task, patchBytes, defaultBranch: env.SPECKIT_BRANCH || 'main' }).reasons }));
  }
  let appendedTasks = [];
  if (reasons.length === 0 && inputs.step === 'converge' && !result.converged) {
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
      await requestAttention({ client, inputs, reason: reasons.join('; '), next: `Implement the remaining gaps yourself, or adjust the spec and tasks. ${RESUME_HINT}`, resultDir });
      report.flush();
      return { exitCode: 1, reasons };
    }
  }
  if (reasons.length === 0 && inputs.step === 'resolve') reasons.push(...replayResolution({ git, env, result, resultDir }));
  if (reasons.length > 0) {
    await reportFailure({ client, inputs, reasons, resultDir });
    report.line(`${title(inputs)} failed: ${reasons.join('; ')}`);
    report.flush();
    return { exitCode: 1, reasons };
  }

  const pull = await client.getPullRequest(inputs.pull);
  if (inputs.step === 'converge' && result.converged) {
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'success',
      output: { title: 'Converged', summary: 'The implementation satisfies the spec, plan, and tasks. Next: merge into the default branch.' },
    });
    await client.createComment(inputs.pull, [
      `**Converged** (attempt ${inputs.attempt}): the implementation satisfies the spec, plan, and tasks. Next: merge into the default branch.`,
      ...agentDetails(resultDir, 'Convergence report'),
    ].join('\n'));
    report.line('Converged.');
    report.flush();
    return { exitCode: 0 };
  }

  const tasksPath = path.join(workspace, 'specs', folder, 'tasks.md');
  const tasksMarkdown = readFileSync(tasksPath, 'utf8');
  let progress = taskProgress(tasksMarkdown);
  let message;
  let checkTitle;
  let comment;
  let body = pull.body;
  if (inputs.step === 'task') {
    const task = listTasks(tasksMarkdown).find((item) => item.id === inputs.task);
    message = [`feat(${folder}): ${inputs.task} ${task?.text ?? ''}`.trimEnd(), `Implemented by the Spec Kit implement workflow (attempt ${inputs.attempt}).`];
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
      ...((result.uncovered ?? []).length > 0 ? [`- Not covered by any check: ${result.uncovered.map((file) => `\`${neutralizeMarkers(file)}\``).join(', ')}`] : []),
    ];
  } else if (inputs.step === 'converge') {
    const round = convergenceRounds(tasksMarkdown);
    const heading = tasksMarkdown.split(/\r?\n/).filter((line) => /^##\s+Phase\s+\d+\s*:\s*Convergence\b/i.test(line)).at(-1)?.replace(/^##\s+/, '') ?? `Convergence round ${round}`;
    const appended = appendedTasks;
    message = [`chore(${folder}): convergence round ${round}`, `/speckit-converge appended ${appended.length} task(s) (attempt ${inputs.attempt}).`];
    checkTitle = `Convergence round ${round}: ${appended.length} task(s) appended`;
    body = appendPullRequestTasks(pull.body, appended, heading);
    comment = [
      `**Convergence round ${round} of at most ${MAX_CONVERGE_ROUNDS}** found gaps; ${appended.length} task(s) were appended and are implemented next:`,
      '',
      ...appended.map((task) => `- ${task.id} ${neutralizeMarkers(summarizeTask(task.text))}`),
      ...agentDetails(resultDir, 'Convergence report'),
    ];
  } else {
    const files = result.conflicts ?? [];
    message = [
      `Merge ${env.SPECKIT_BRANCH || 'main'} into ${implementationBranch(folder)}`,
      files.length > 0 ? `Conflicts in ${files.join(', ')} resolved by Copilot (attempt ${inputs.attempt}).` : 'No conflicts remained.',
    ];
    checkTitle = files.length > 0 ? 'Merge conflicts resolved' : 'Default branch merged';
    const checks = (result.checks ?? []).map((check) => `${check.name} passed`);
    comment = [
      `**Conflicts resolved** (attempt ${inputs.attempt}): ${files.map((file) => `\`${file}\``).join(', ') || 'none remained'}.`,
      '',
      `- Verification: ${result.verification === 'not-configured' ? 'no verification configured (no environment-verify action)' : checks.join(', ') || 'no checks'}`,
      '- Next: merge into the default branch.',
    ];
  }
  gitOrThrow(git, [...GIT_IDENTITY, 'commit', '--no-verify', ...message.flatMap((part) => ['-m', part])]);
  let head;
  if (inputs.step === 'task') {
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
  await client.createComment(inputs.pull, comment.join('\n'));
  report.line(`${checkTitle}; pushed as ${head}.`);
  report.flush();
  return { exitCode: 0, head };
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
// commit must be on the default branch, because the work result that names it is not trusted.
function replayResolution({ git, env, result, resultDir }) {
  if (!/^[0-9a-f]{40}$/.test(String(result.mainSha ?? ''))) return ['the default branch SHA is missing'];
  if (git(['cat-file', '-e', `${result.mainSha}^{commit}`]).status !== 0) return [`commit ${result.mainSha} is not available`];
  const defaultBranch = env.SPECKIT_BRANCH || 'main';
  if (git(['merge-base', '--is-ancestor', result.mainSha, `refs/remotes/origin/${defaultBranch}`]).status !== 0) {
    return [`commit ${result.mainSha} is not on the default branch ${defaultBranch}`];
  }
  git([...GIT_IDENTITY, 'merge', '--no-ff', '--no-commit', result.mainSha]);
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
  if (reasons.length === 0 && git(['rev-parse', '-q', '--verify', 'MERGE_HEAD']).status !== 0) reasons.push('there is nothing to merge from the default branch');
  return reasons;
}

// Orchestrator, job "merge-land" (write token, no checkout of workspace code): reports the verified integration
// and squash-merges the implementation into the default branch, or asks for review when automatic merging is off.
export async function runMergeLand({ client, env, inputs, resultDir, workResult, log }) {
  const report = createReporter(env, log);
  const result = readJson(path.join(resultDir, 'result.json'));
  const defaultBranch = env.SPECKIT_BRANCH || 'main';
  const pull = await client.getPullRequest(inputs.pull);
  const reasons = result ? [...result.reasons] : [`the merge verification did not finish (result: ${workResult || 'unknown'})`];
  if (result?.attention) {
    await requestAttention({ client, inputs, reason: reasons.join('; '), next: `Resolve the conflicts on the implementation branch and push. ${RESUME_HINT}` });
    report.flush();
    return { exitCode: 1, reasons };
  }
  if (reasons.length === 0 && (result.conflicts ?? []).length > 0) {
    const files = result.conflicts.map((file) => `\`${file}\``).join(', ');
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'neutral',
      external_id: CHECK_CONFLICT,
      output: { title: `Conflicts with ${defaultBranch}`, summary: `Merging ${defaultBranch} at ${result.mainSha} conflicts in ${files}.` },
    });
    await client.createComment(inputs.pull, `**Merge conflicts with \`${defaultBranch}\`** in ${files}. \`Spec Kit resolve\` resolves them next.`);
    report.line(`Conflicts with ${defaultBranch}: ${result.conflicts.join(', ')}`);
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

  const tasksMarkdown = await client.getFileContent(`specs/${inputs.folder}/tasks.md`, result.head) ?? '';
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

  const mainNow = await client.getBranchSha(defaultBranch);
  if (mainNow !== result.mainSha) {
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'neutral',
      output: { title: `${defaultBranch} moved during verification`, summary: `Verified against ${result.mainSha}, but ${defaultBranch} is now ${mainNow}. The merge is verified again.` },
    });
    report.line(`${defaultBranch} moved from ${result.mainSha} to ${mainNow}; the merge is verified again.`);
    report.flush();
    return { exitCode: 0, moved: true };
  }
  const taskLines = listTasks(tasksMarkdown).map((task) => `- ${task.id} ${summarizeTask(task.text)}`);
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
    output: progressOutput(progress, `Merged into ${defaultBranch} as ${merged.sha}.`),
  });
  const twin = await client.getIssue(inputs.twin);
  await updateIssueWithLabels(client, twin, stageLabelChange(twin, 'implemented'), twin.state === 'open' ? { state: 'closed', state_reason: 'completed' } : {});
  const notes = [];
  try {
    await client.deleteBranch(implementationBranch(inputs.folder));
  } catch (error) {
    notes.push(`The branch could not be deleted: ${error.message}`);
  }
  try {
    await client.dispatchWorkflow(PREPARE_WORKFLOW_FILE, defaultBranch, { dry_run: 'false' });
  } catch (error) {
    notes.push(`Spec Kit prepare could not be started: ${error.message}`);
  }
  await client.createComment(inputs.pull, [
    DONE_COMMENT_MARKER,
    `**Merged into \`${defaultBranch}\`** as ${merged.sha}${mention ? ` ${mention}` : ''}: all ${progress.total} tasks are implemented, the implementation converged, and ${verification}.`,
    ...(notes.length > 0 ? ['', ...notes.map((note) => `- ${note}`)] : []),
  ].join('\n'));
  report.line(`Merged #${inputs.pull} into ${defaultBranch} as ${merged.sha}.`);
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

const COMMANDS = 'begin | integrate | package | verdict | land | merge-land';

export async function main(argv, { env = process.env, log = console.log, client, git } = {}) {
  const [command] = argv;
  const inputs = parseStepInputs(env);
  const workspace = path.resolve(env.SPECKIT_WORKSPACE || 'workspace');
  const resultDir = path.resolve(env.SPECKIT_RESULT_DIR || 'result');
  const folder = env.SPECKIT_FOLDER;
  const withChecks = { ...inputs, checkRun: Number(env.SPECKIT_CHECK_RUN), folder };
  if (command === 'begin') return (await runBegin({ client: client ?? githubClient(env), env, inputs, log })).exitCode;
  if (command === 'integrate') return runIntegrate({ git: git ?? defaultGit(workspace), env, inputs, folder, resultDir, log }).exitCode;
  if (command === 'package') return runPackage({ git: git ?? defaultGit(workspace), env, inputs, folder, workspace, resultDir, log }).exitCode;
  if (command === 'verdict') return runVerdict({ env, inputs, resultDir, log }).exitCode;
  if (command === 'land') {
    return (await runLand({
      client: client ?? githubClient(env),
      git: git ?? defaultGit(workspace),
      env,
      inputs: withChecks,
      folder,
      workspace,
      resultDir,
      workResult: env.SPECKIT_WORK_RESULT,
      log,
    })).exitCode;
  }
  if (command === 'merge-land') {
    return (await runMergeLand({ client: client ?? githubClient(env), env, inputs: withChecks, resultDir, workResult: env.SPECKIT_WORK_RESULT, log })).exitCode;
  }
  throw new TaskInputError(`Usage: speckit-step.mjs <${COMMANDS}> (inputs come from SPECKIT_* environment variables)`);
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
