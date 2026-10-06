import { spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { IMPLEMENT_STAGE, hasLabel, resolveTwins, stageLabel } from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { resolveImplementRequester } from './speckit-prepare.mjs';
import {
  CHECK_ATTEMPT,
  CHECK_DONE,
  CHECK_PROGRESS,
  CHECK_RUN_NAME,
  DONE_COMMENT_MARKER,
  FINALIZE_TASK,
  implementationBranch,
  latestCheckRun,
  listTasks,
  neutralizeMarkers,
  nextTask,
  progressOutput,
  renderTaskPrompt,
  taskProgress,
  tickPullRequestBody,
  validateTaskChange,
} from './speckit-implement-core.mjs';

export const MAX_PATCH_BYTES = 5 * 1024 * 1024;
export const MAX_CHANGED_FILES = 500;
const TASK_INPUT_PATTERN = /^(T\d{3,}|finalize)$/;
const PLATFORM_SOLUTION = 'src/platform/SocAlytics.Platform.slnx';
const BOT_NAME = 'github-actions[bot]';
const BOT_EMAIL = '41898282+github-actions[bot]@users.noreply.github.com';

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

export function parseTaskInputs(env) {
  const twin = Number(env.SPECKIT_TWIN);
  const pull = Number(env.SPECKIT_PULL);
  const attempt = Number(env.SPECKIT_ATTEMPT);
  const task = String(env.SPECKIT_TASK ?? '');
  if (![twin, pull, attempt].every((value) => Number.isInteger(value) && value > 0) || !TASK_INPUT_PATTERN.test(task)) {
    throw new TaskInputError('Expected positive integers for twin, pull, and attempt, and a task like T001 or finalize.');
  }
  return { twin, pull, task, attempt, mode: task === FINALIZE_TASK ? 'finalize' : 'task' };
}

function tail(text, lines = 60) {
  return String(text ?? '').trimEnd().split(/\r?\n/).slice(-lines).join('\n');
}

function fence(text) {
  return ['```text', String(text).replaceAll('```', "'''"), '```'].join('\n');
}

// Task workflow, job "begin": checks that the requested task is still the next step of an open, flagged
// implementation and marks the attempt as in progress. Mismatches end as a no-op.
export async function runBegin({ client, env, inputs, log }) {
  const report = createReporter(env, log);
  report.line(`## ${inputs.task} attempt ${inputs.attempt} for #${inputs.twin}`);
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
  if (reasons.length === 0) {
    tasksMarkdown = await client.getFileContent(`specs/${folder}/tasks.md`, pull.head.sha);
    const expected = nextTask(tasksMarkdown)?.id ?? FINALIZE_TASK;
    if (tasksMarkdown === null) reasons.push(`\`specs/${folder}/tasks.md\` is missing on the branch`);
    else if (expected !== inputs.task) reasons.push(`the next step is ${expected}, not ${inputs.task}`);
  }
  if (reasons.length > 0) {
    report.line(`Nothing to do: ${reasons.join('; ')}.`);
    report.flush();
    setOutput(env, 'proceed', 'false');
    return { exitCode: 0, proceed: false, reasons };
  }

  const task = listTasks(tasksMarkdown).find((item) => item.id === inputs.task);
  const attemptFields = {
    status: 'in_progress',
    external_id: CHECK_ATTEMPT,
    output: {
      title: `${inputs.task} attempt ${inputs.attempt} in progress`,
      summary: task ? `${task.id} ${task.text}` : 'Final verification of the complete implementation.',
    },
  };
  // Take over the queued progress check run of this head, so no check run stays queued forever.
  const latest = latestCheckRun(await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME));
  const check = latest?.status === 'queued' && latest.external_id === CHECK_PROGRESS
    ? { ...latest, ...(await client.updateCheckRun(latest.id, attemptFields)), id: latest.id }
    : await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: pull.head.sha, ...attemptFields });
  report.line(`Working on ${inputs.task} of \`specs/${folder}\` at ${pull.head.sha}.`);
  report.flush();
  setOutput(env, 'proceed', 'true');
  setOutput(env, 'folder', folder);
  setOutput(env, 'head', pull.head.sha);
  setOutput(env, 'check_run', String(check.id));
  setOutput(env, 'mode', inputs.mode);
  setOutput(env, 'prompt', inputs.mode === 'task' ? renderTaskPrompt(inputs.task) : '');
  return { exitCode: 0, proceed: true, folder, check };
}

export function defaultRunner() {
  return (command, args, { cwd, env }) => {
    const result = spawnSync(command, args, { cwd, env, encoding: 'utf8', windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
    const output = `${result.stdout ?? ''}${result.stderr ?? ''}${result.error ? `\n${result.error.message}` : ''}`;
    return { status: result.error ? 1 : result.status, output };
  };
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

function changedPaths(git) {
  return gitOrThrow(git, ['diff', '--cached', '--name-only', '--no-renames', '-z', 'HEAD']).split('\0').filter(Boolean);
}

// Validates the staged change of one task and returns the reasons it cannot be accepted.
export function checkStagedTask({ git, workspace, folder, task, patchBytes }) {
  const paths = changedPaths(git);
  const reasons = [];
  if (patchBytes > MAX_PATCH_BYTES) reasons.push(`the change is larger than ${MAX_PATCH_BYTES} bytes`);
  if (paths.length > MAX_CHANGED_FILES) reasons.push(`the change touches more than ${MAX_CHANGED_FILES} files`);
  const tasksPath = `specs/${folder}/tasks.md`;
  const before = git(['show', `HEAD:${tasksPath}`]);
  const afterFile = path.join(workspace, tasksPath);
  reasons.push(...validateTaskChange({
    folder,
    taskId: task,
    before: before.status === 0 ? before.stdout : '',
    after: existsSync(afterFile) ? readFileSync(afterFile, 'utf8') : '',
    changedPaths: paths,
  }));
  return { reasons, paths };
}

function verificationPlan(mode, paths, toolingDir) {
  const platform = mode === 'finalize' || paths.some((file) => file.startsWith('src/platform/'));
  const markdown = mode === 'finalize' || paths.some((file) => file.endsWith('.md'));
  return [
    ...(platform ? [{
      name: 'platform build and tests',
      steps: [
        ['dotnet', ['restore', PLATFORM_SOLUTION]],
        ['dotnet', ['build', PLATFORM_SOLUTION, '--no-restore']],
        ['dotnet', ['test', PLATFORM_SOLUTION, '--no-build']],
      ],
    }] : []),
    ...(markdown ? [{ name: 'Markdown check', steps: [['node', [path.join(toolingDir, '.github', 'scripts', 'check-markdown.mjs')]]] }] : []),
  ];
}

// Environment for agent-written code during verification: no tokens.
function verificationEnv(env) {
  const clean = { ...env };
  for (const name of Object.keys(clean)) {
    if (/TOKEN|^ACTIONS_ID_TOKEN|^ACTIONS_RUNTIME/i.test(name)) delete clean[name];
  }
  return clean;
}

// Task workflow, job "work" (read-only token): packages the agent's change, validates it, and runs the verification.
// The result is a quality report; the "land" job re-validates everything it relies on before pushing.
export function runWork({ git, run, env, inputs, folder, workspace, resultDir, toolingDir, log }) {
  mkdirSync(resultDir, { recursive: true });
  const result = { mode: inputs.mode, task: inputs.task, reasons: [], checks: [], verified: false };
  let paths = [];
  if (inputs.mode === 'task') {
    const exitFile = path.join(resultDir, 'agent-exit.txt');
    const agentExit = existsSync(exitFile) ? Number(readFileSync(exitFile, 'utf8').trim()) : null;
    result.agentExit = agentExit;
    if (agentExit === 124) result.reasons.push('the agent did not finish within 60 minutes');
    else if (agentExit !== 0) result.reasons.push(agentExit === null ? 'the agent did not run' : `the agent exited with code ${agentExit}`);
    gitOrThrow(git, ['add', '-A']);
    const patch = gitOrThrow(git, ['diff', '--cached', '--binary', '--no-renames', 'HEAD']);
    writeFileSync(path.join(resultDir, 'changes.patch'), patch);
    result.patchBytes = Buffer.byteLength(patch);
    const staged = checkStagedTask({ git, workspace, folder, task: inputs.task, patchBytes: result.patchBytes });
    paths = staged.paths;
    result.changedFiles = paths;
    result.reasons.push(...staged.reasons);
  }

  const logLines = [];
  if (result.reasons.length === 0) {
    for (const check of verificationPlan(inputs.mode, paths, toolingDir)) {
      let passed = true;
      for (const [command, args] of check.steps) {
        logLines.push(`$ ${command} ${args.join(' ')}`);
        const outcome = run(command, args, { cwd: workspace, env: verificationEnv(env) });
        logLines.push(outcome.output.trimEnd());
        if (outcome.status !== 0) {
          passed = false;
          break;
        }
      }
      result.checks.push({ name: check.name, passed });
      if (!passed) {
        result.reasons.push(`${check.name} failed`);
        break;
      }
    }
  }
  result.verified = result.reasons.length === 0;
  writeFileSync(path.join(resultDir, 'verification.log'), `${logLines.join('\n')}\n`);
  writeFileSync(path.join(resultDir, 'result.json'), `${JSON.stringify(result, null, 2)}\n`);
  log(result.verified ? `Verified ${inputs.task}.` : `Not verified: ${result.reasons.join('; ')}`);
  return { exitCode: 0, result };
}

function readJson(file) {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

// The agent's last message from the Copilot CLI JSONL log, for failure reports.
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

function readOptional(file) {
  return existsSync(file) ? readFileSync(file, 'utf8') : '';
}

async function reportFailure({ client, inputs, reasons, resultDir }) {
  const verification = neutralizeMarkers(tail(readOptional(path.join(resultDir, 'verification.log'))));
  const agent = neutralizeMarkers(lastAgentMessage(readOptional(path.join(resultDir, 'agent.jsonl'))));
  const safeReasons = reasons.map(neutralizeMarkers);
  const details = [
    ...(verification.trim() ? ['', '<details><summary>Verification output (tail)</summary>', '', fence(verification), '', '</details>'] : []),
    ...(agent ? ['', '<details><summary>Agent summary</summary>', '', agent.slice(0, 4000), '', '</details>'] : []),
  ];
  await client.updateCheckRun(inputs.checkRun, {
    status: 'completed',
    conclusion: 'failure',
    output: { title: `${inputs.task} attempt ${inputs.attempt} failed`, summary: safeReasons.map((reason) => `- ${reason}`).join('\n') },
  });
  await client.createComment(inputs.pull, [
    `**${inputs.task} attempt ${inputs.attempt} failed:**`,
    '',
    ...safeReasons.map((reason) => `- ${reason}`),
    ...details,
    '',
    'The orchestrator retries the task until its attempt limit is reached.',
  ].join('\n'));
}

function pushArgs(env, branch) {
  const server = (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '');
  const basic = Buffer.from(`x-access-token:${env.GITHUB_TOKEN}`).toString('base64');
  return ['-c', `http.${server}/.extraheader=AUTHORIZATION: basic ${basic}`, 'push', `${server}/${env.GITHUB_REPOSITORY}.git`, `HEAD:refs/heads/${branch}`];
}

// Task workflow, job "land" (write token, never runs agent-written code): re-validates the change, commits and
// pushes it, and reports progress; in finalize mode it hands the pull request to review.
export async function runLand({ client, git, env, inputs, folder, workspace, resultDir, workResult, log }) {
  const report = createReporter(env, log);
  const result = readJson(path.join(resultDir, 'result.json'));
  const reasons = [];
  if (!result) reasons.push(`the work job did not finish (result: ${workResult || 'unknown'})`);
  else reasons.push(...result.reasons);
  if (result && !result.verified && result.reasons.length === 0) reasons.push('the change was not verified');

  const branch = implementationBranch(folder);
  if (reasons.length === 0 && inputs.mode === 'task') {
    const patchFile = path.join(resultDir, 'changes.patch');
    const patchBytes = existsSync(patchFile) ? statSync(patchFile).size : 0;
    if (patchBytes === 0) {
      reasons.push('the agent made no changes');
    } else {
      const applied = git(['apply', '--index', '--whitespace=nowarn', patchFile]);
      if (applied.status !== 0) reasons.push(`the change could not be applied: ${applied.stderr.trim()}`);
      else reasons.push(...checkStagedTask({ git, workspace, folder, task: inputs.task, patchBytes }).reasons);
    }
  }
  if (reasons.length > 0) {
    await reportFailure({ client, inputs, reasons, resultDir });
    report.line(`${inputs.task} attempt ${inputs.attempt} failed: ${reasons.join('; ')}`);
    report.flush();
    return { exitCode: 1, reasons };
  }

  const pull = await client.getPullRequest(inputs.pull);
  if (inputs.mode === 'task') {
    const tasksMarkdown = readFileSync(path.join(workspace, 'specs', folder, 'tasks.md'), 'utf8');
    const task = listTasks(tasksMarkdown).find((item) => item.id === inputs.task);
    gitOrThrow(git, ['-c', `user.name=${BOT_NAME}`, '-c', `user.email=${BOT_EMAIL}`, 'commit', '--no-verify', '-m', `feat(${folder}): ${inputs.task} ${task?.text ?? ''}`.trimEnd(), '-m', `Implemented by the Spec Kit implement workflow (attempt ${inputs.attempt}).`]);
    const pushed = git(pushArgs(env, branch));
    if (pushed.status !== 0) {
      const pushReasons = [`the push to \`${branch}\` was rejected; the branch probably moved (${pushed.stderr.trim().split('\n').at(-1)})`];
      await reportFailure({ client, inputs, reasons: pushReasons, resultDir });
      report.flush();
      return { exitCode: 1, reasons: pushReasons };
    }
    const head = gitOrThrow(git, ['rev-parse', 'HEAD']).trim();
    const progress = taskProgress(tasksMarkdown);
    await client.updatePullRequest(inputs.pull, { body: tickPullRequestBody(pull.body, inputs.task) });
    await client.updateCheckRun(inputs.checkRun, {
      status: 'completed',
      conclusion: 'success',
      output: { title: `${inputs.task} implemented`, summary: `${inputs.task} ${task?.text ?? ''}`.trim() },
    });
    await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: head, status: 'queued', external_id: CHECK_PROGRESS, output: progressOutput(progress) });
    const checks = (result.checks ?? []).map((check) => `${check.name} passed`);
    await client.createComment(inputs.pull, [
      `**${inputs.task} implemented** (attempt ${inputs.attempt}): ${task?.text ?? ''}`.trimEnd(),
      '',
      `- Changed files: ${(result.changedFiles ?? []).map((file) => `\`${file}\``).join(', ') || 'none'}`,
      `- Verification: ${checks.join(', ') || 'no checks apply to these files'}`,
      `- Progress: ${progress.done} of ${progress.total} tasks implemented`,
    ].join('\n'));
    report.line(`${inputs.task} implemented and pushed as ${head}.`);
    report.flush();
    return { exitCode: 0, head };
  }

  const tasksMarkdown = readFileSync(path.join(workspace, 'specs', folder, 'tasks.md'), 'utf8');
  const progress = taskProgress(tasksMarkdown);
  await client.updateCheckRun(inputs.checkRun, {
    status: 'completed',
    conclusion: 'success',
    external_id: CHECK_DONE,
    output: progressOutput(progress, 'All tasks are implemented and the full verification passed. Ready for review.'),
  });
  if (pull.draft) await client.markReadyForReview(pull.node_id);
  const requester = await resolveImplementRequester(client, inputs.twin);
  let reviewNote = '';
  if (requester?.login) {
    try {
      await client.requestReviewers(inputs.pull, [requester.login]);
      reviewNote = ` Review requested from @${requester.login}.`;
    } catch (error) {
      reviewNote = ` Could not request a review from @${requester.login}: ${error.message}`;
    }
  }
  await client.createComment(inputs.pull, [
    DONE_COMMENT_MARKER,
    `**All ${progress.total} tasks are implemented** and the full verification passed (${(result.checks ?? []).map((check) => check.name).join(', ')}).`,
    '',
    `This pull request is ready for review.${reviewNote}`,
  ].join('\n'));
  report.line(`Finalized #${inputs.pull}.${reviewNote}`);
  report.flush();
  return { exitCode: 0 };
}

function githubClient(env) {
  return new GitHubClient({
    token: env.GITHUB_TOKEN || env.GH_TOKEN,
    repository: env.GITHUB_REPOSITORY,
    apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
    graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
  });
}

export async function main(argv, { env = process.env, log = console.log, client, git, run } = {}) {
  const [command] = argv;
  const inputs = parseTaskInputs(env);
  const workspace = path.resolve(env.SPECKIT_WORKSPACE || 'workspace');
  const resultDir = path.resolve(env.SPECKIT_RESULT_DIR || 'result');
  if (command === 'begin') return (await runBegin({ client: client ?? githubClient(env), env, inputs, log })).exitCode;
  if (command === 'work') {
    return runWork({
      git: git ?? defaultGit(workspace),
      run: run ?? defaultRunner(),
      env,
      inputs,
      folder: env.SPECKIT_FOLDER,
      workspace,
      resultDir,
      toolingDir: path.resolve(env.SPECKIT_TOOLING || 'tooling'),
      log,
    }).exitCode;
  }
  if (command === 'land') {
    return (await runLand({
      client: client ?? githubClient(env),
      git: git ?? defaultGit(workspace),
      env,
      inputs: { ...inputs, checkRun: Number(env.SPECKIT_CHECK_RUN) },
      folder: env.SPECKIT_FOLDER,
      workspace,
      resultDir,
      workResult: env.SPECKIT_WORK_RESULT,
      log,
    })).exitCode;
  }
  throw new TaskInputError('Usage: speckit-implement.mjs <begin | work | land> (inputs come from SPECKIT_* environment variables)');
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
