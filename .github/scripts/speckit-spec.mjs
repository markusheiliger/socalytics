import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { IMPLEMENT_STAGE, hasLabel, resolveTwins, stageLabel } from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { resolveImplementRequester } from './speckit-prepare.mjs';
import { closeUnfinishedDiagnosis, diagnosisRunEnded, startDiagnosis } from './speckit-diagnose.mjs';
import { startAttemptChecks } from './speckit-step.mjs';
import {
  BOT_LOGIN,
  CHECK_DIAGNOSED,
  CHECK_DIAGNOSING,
  CHECK_LIMIT,
  CHECK_MERGE,
  CHECK_PROGRESS,
  CHECK_RUN_NAME,
  DONE_COMMENT_MARKER,
  IMPLEMENT_WORKFLOW_FILE,
  MAX_AUTO_DIAGNOSES,
  MAX_TASK_ATTEMPTS,
  RESUME_COMMENT_MARKER,
  decideNext,
  dispatchImplementation,
  implementationBranch,
  isPerTaskRunOf,
  latestCheckRun,
  latestHumanCommit,
  listTasks,
  markerTimes,
  neutralizeMarkers,
  parseAttemptCheckId,
  parseAttempts,
  parseGuidance,
  renderAttemptMarker,
  renderConvergePrompt,
  renderImplementRunName,
  renderNextSteps,
  renderResumeComment,
  renderTaskPrompt,
  segmentLabel,
  stepLabel,
  summarizeTask,
  syncPullRequestTaskList,
} from './speckit-implement-core.mjs';

// The implementation chain of one spec (workflow Spec Kit implement): every run decides the next segment from the
// state on GitHub (job "decide"), runs it (stages of a phase, the convergence, a conflict resolution, or the merge),
// and starts the next run (job "continue"), until the implementation is merged, waits for review, or needs a person.

export class SpecUsageError extends Error {}

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

export function contextFromEnv(env) {
  return {
    serverUrl: (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, ''),
    repository: env.GITHUB_REPOSITORY,
    branch: env.SPECKIT_BRANCH || 'main',
  };
}

export function autoDiagnosisEnabled(env) {
  return String(env.SPECKIT_AUTO_DIAGNOSE ?? 'true').toLowerCase() !== 'false';
}

async function isBehindDefaultBranch(client, env, folder) {
  try {
    return (await client.aheadBy(implementationBranch(folder), env.SPECKIT_BRANCH || 'main')) > 0;
  } catch {
    return false;
  }
}

// Reads the state of an open implementation pull request and decides how to continue it (decideNext). It also renders
// the task list of the pull request body from the branch's tasks.md, because amendments and convergence change tasks.
export async function decideForPull(client, { twin, folder, pullNumber, now = Date.now(), context }) {
  const pull = await client.getPullRequest(pullNumber);
  const tasksMarkdown = await client.getFileContent(`specs/${folder}/tasks.md`, pull.head.sha);
  if (tasksMarkdown === null) return { action: 'missing-tasks', pull };
  const body = syncPullRequestTaskList(pull.body, { twinNumber: twin, folder, tasksMarkdown, context });
  if (body !== String(pull.body ?? '')) {
    try {
      await client.updatePullRequest(pullNumber, { body });
    } catch {
      // The task list is a convenience; a failed update must not stop the implementation.
    }
  }
  const checks = await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME);
  const comments = await client.listIssueComments(pullNumber);
  const attempts = parseAttempts(comments);
  const resumedAt = markerTimes(comments, RESUME_COMMENT_MARKER);
  const pushedAt = latestHumanCommit(await client.listPullRequestCommits(pullNumber));
  const done = markerTimes(comments, DONE_COMMENT_MARKER).length > 0;
  const windowStart = [pull.created_at, ...resumedAt, ...(pushedAt ? [pushedAt] : [])].sort((a, b) => Date.parse(b) - Date.parse(a))[0];
  const latestResume = [...resumedAt].sort((a, b) => Date.parse(b) - Date.parse(a))[0] ?? null;
  const decision = decideNext({ tasksMarkdown, checks, attempts, windowStart, done, resumedAt: latestResume, now });
  // The ledger is not called attempts, which a limit decision uses for its count.
  const state = { pull, tasksMarkdown, checks, comments, ledger: attempts, pushedAt };
  if (decision.action === 'diagnosing' && (await diagnosisRunEnded(client, twin, latestCheckRun(checks)))) {
    const count = checks.filter((item) => [CHECK_DIAGNOSING, CHECK_DIAGNOSED].includes(item.external_id)).length;
    return { action: 'failed', diagnosis: { state: 'stale', check: latestCheckRun(checks), count }, ...state };
  }
  return { ...decision, ...state };
}

// The label of the next run of the chain (see segmentLabel); empty when the state cannot be read.
export async function labelForPull(client, env, { twin, pull, folder }) {
  try {
    return segmentLabel(await decideForPull(client, { twin, folder, pullNumber: pull, context: contextFromEnv(env) }));
  } catch {
    return '';
  }
}

// Whether a decision needs a run of the chain: work to do, a stop to report, or a diagnosis to start.
export function needsRun(decision, env) {
  if (['stages', 'merge', 'limit'].includes(decision.action)) return true;
  if (decision.action !== 'failed' || decision.diagnosis?.state === 'reported') return false;
  return decision.diagnosis?.state === 'stale' || (autoDiagnosisEnabled(env) && (decision.diagnosis?.count ?? 0) < MAX_AUTO_DIAGNOSES);
}

// Guidance from the latest resume (`/speckit resume <guidance>`). It applies to the stopped step only: it ends when an
// attempt reported success after the resume, or when a person pushed since.
function currentGuidance({ comments, ledger, pushedAt }) {
  const resume = comments
    .filter((comment) => comment.user?.login === BOT_LOGIN && String(comment.body ?? '').startsWith(RESUME_COMMENT_MARKER))
    .at(-1);
  const guidance = resume ? parseGuidance(resume.body) : null;
  if (!guidance) return null;
  if (pushedAt && Date.parse(pushedAt) > Date.parse(resume.created_at)) return null;
  if (ledger.some((attempt) => attempt.outcome === 'success' && Date.parse(attempt.at) > Date.parse(resume.created_at))) return null;
  return guidance;
}

// Attempts of earlier runs whose check run is still in progress ended without reporting (the run was cancelled, timed
// out before landing, or crashed). Runs of one chain never overlap, so such an attempt counts as failed.
async function closeOrphanedAttempts(client, pull) {
  const orphans = (await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME))
    .filter((check) => check.status !== 'completed' && parseAttemptCheckId(check.external_id));
  for (const check of orphans) {
    const attempt = parseAttemptCheckId(check.external_id);
    const label = stepLabel(attempt);
    await client.updateCheckRun(check.id, {
      status: 'completed',
      conclusion: 'failure',
      output: { title: `${label} attempt ${attempt.attempt} did not finish`, summary: 'The run ended without reporting the attempt.' },
    });
    await client.createComment(pull.number, [
      renderAttemptMarker({ ...attempt, outcome: 'failure' }),
      `**${label.charAt(0).toUpperCase()}${label.slice(1)} attempt ${attempt.attempt} did not finish**: the run ended without reporting it. It counts as a failed attempt.`,
    ].join('\n'));
  }
  return orphans.length;
}

// Opens the merge check run that marks a merge as running, taking over a queued progress check run of the head.
async function startMerge(client, { pull, attempt }) {
  const fields = {
    status: 'in_progress',
    external_id: CHECK_MERGE,
    output: { title: `Merge attempt ${attempt} in progress`, summary: 'Merges the default branch, runs the full verification, and merges the implementation.' },
  };
  const latest = latestCheckRun(await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME));
  if (latest?.status === 'queued' && latest.external_id === CHECK_PROGRESS) {
    await client.updateCheckRun(latest.id, fields);
    return latest.id;
  }
  return (await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: pull.head.sha, ...fields })).id;
}

async function stopAtLimit(client, env, { pull, folder, requester }, decision) {
  const label = stepLabel(decision);
  await client.createCheckRun({
    name: CHECK_RUN_NAME,
    head_sha: pull.head.sha,
    status: 'completed',
    conclusion: 'failure',
    external_id: CHECK_LIMIT,
    output: { title: `${label} reached the attempt limit`, summary: `The implementation stopped at ${label} after ${decision.attempts} attempts.` },
  });
  await client.createComment(pull.number, [
    `**Implementation needs attention**${requester ? ` @${requester}` : ''}: ${label} did not succeed in ${decision.attempts} attempts (at most ${MAX_TASK_ATTEMPTS} failed attempts are allowed).`,
    '',
    'See the comments above for the reasons.',
    '',
    ...renderNextSteps({ autoDiagnosis: autoDiagnosisEnabled(env), behindMain: await isBehindDefaultBranch(client, env, folder) }),
  ].join('\n'));
}

// Starts a diagnosis of a stopped implementation by itself, unless automatic diagnosis is off, a diagnosis already
// reported for this stop, or the head already had the maximum number of diagnoses. A diagnosis that ended without a
// report is closed with a comment first, so it never blocks the implementation.
async function maybeDiagnose(client, env, { twin, pull, folder }, diagnosis, report) {
  const allowed = autoDiagnosisEnabled(env) && diagnosis.count < MAX_AUTO_DIAGNOSES;
  if (diagnosis.state === 'stale') {
    await closeUnfinishedDiagnosis(client, diagnosis.check);
    const runs = `${contextFromEnv(env).serverUrl}/${env.GITHUB_REPOSITORY}/actions/workflows/speckit-diagnose.lock.yml`;
    await client.createComment(pull.number, [
      `**The diagnosis did not finish** (see the [diagnose runs](${runs})).`,
      '',
      ...(allowed ? ['A new diagnosis starts automatically.'] : renderNextSteps({ diagnosed: true, behindMain: await isBehindDefaultBranch(client, env, folder) })),
    ].join('\n'));
    report.line('Closed the unfinished diagnosis.');
    if (!allowed) return;
  }
  if (!allowed || diagnosis.state === 'reported') return;
  const started = await startDiagnosis(client, env, { twin, pull: pull.number, folder, head: pull.head.sha, auto: true });
  report.line(started.started ? 'Started a diagnosis.' : `Warning: could not start a diagnosis: ${started.error}`);
}

function readSpecInputs(env) {
  const twin = Number(env.SPECKIT_TWIN);
  const pull = Number(env.SPECKIT_PULL);
  if (![twin, pull].every((value) => Number.isInteger(value) && value > 0)) throw new SpecUsageError('Expected SPECKIT_TWIN and SPECKIT_PULL as positive integers.');
  return { twin, pull };
}

// Spec Kit implement, job "decide" (write access to check runs, issues, and pull requests; never runs agent code):
// validates the twin and its pull request, reports orphaned attempts, decides the next segment, reports stops (attempt
// limit, diagnosis), and prepares the segment: the stages with their prompts and the check runs of the first stage, or
// the merge check run. The outputs drive the jobs of the run; `continue` tells whether the run starts its successor.
export async function runDecide({ client, env, log, now = Date.now }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  report.line(`## ${renderImplementRunName(twin)}`);
  report.line();
  const finish = (action, outputs = {}) => {
    report.flush();
    setOutput(env, 'action', action);
    setOutput(env, 'continue', String(['stages', 'merge'].includes(action)));
    for (const [name, value] of Object.entries(outputs)) setOutput(env, name, value);
    return { exitCode: 0, action, ...outputs };
  };

  const issue = await client.getIssue(twin);
  const folder = resolveTwins([issue]).byFolder.keys().next().value;
  const listed = await client.getPullRequest(pullNumber);
  const reasons = [];
  if (!folder) reasons.push(`#${twin} is not a spec twin`);
  if (issue.state !== 'open' || !hasLabel(issue, stageLabel(IMPLEMENT_STAGE))) reasons.push(`#${twin} is not open and flagged for implementation`);
  if (folder && (listed.state !== 'open' || listed.head?.ref !== implementationBranch(folder) || listed.head?.repo?.full_name !== env.GITHUB_REPOSITORY)) {
    reasons.push(`#${pullNumber} is not the open implementation pull request of \`specs/${folder}\``);
  }
  if (reasons.length > 0) {
    report.line(`Nothing to do: ${reasons.join('; ')}.`);
    return finish('none');
  }
  // Runs of the per-task workflow from before the chain may still be finishing; they hand back to the scheduler.
  const others = (await client.listWorkflowRuns(IMPLEMENT_WORKFLOW_FILE, listed.created_at))
    .filter((run) => String(run.id) !== String(env.GITHUB_RUN_ID ?? '') && run.status !== 'completed' && isPerTaskRunOf(run.display_title, twin));
  if (others.length > 0) {
    report.line(`Waiting: an earlier implementation run of #${twin} is still active.`);
    return finish('wait');
  }
  if (String(env.SPECKIT_RESUME ?? '').toLowerCase() === 'true') {
    await client.createComment(pullNumber, renderResumeComment('Implementation resumed on request; the attempt count starts over.', env.SPECKIT_GUIDANCE));
    report.line('Resumed on request.');
  }
  const orphaned = await closeOrphanedAttempts(client, listed);
  if (orphaned > 0) report.line(`Recorded ${orphaned} attempt(s) of an earlier run that ended without reporting as failed.`);

  const decision = await decideForPull(client, { twin, folder, pullNumber, now: now(), context: contextFromEnv(env) });
  const { pull } = decision;
  for (const stale of decision.stale ?? []) {
    await client.updateCheckRun(stale.id, { status: 'completed', conclusion: 'failure', output: { title: 'Merge attempt abandoned', summary: 'The merge did not finish within 2 hours.' } });
  }
  const base = { folder, head: pull.head.sha };
  if (decision.action === 'limit') {
    const requester = (await resolveImplementRequester(client, twin))?.login ?? null;
    await stopAtLimit(client, env, { pull, folder, requester }, decision);
    report.line(`Stopped: ${stepLabel(decision)} reached the attempt limit.`);
    await maybeDiagnose(client, env, { twin, pull, folder }, { state: 'none', count: 0 }, report);
    return finish('stop', base);
  }
  if (decision.action === 'failed') {
    if (decision.diagnosis?.state !== 'reported') await maybeDiagnose(client, env, { twin, pull, folder }, decision.diagnosis, report);
    report.line(decision.diagnosis?.state === 'reported' ? 'Stopped; the diagnosis waits for a person\'s decision.' : 'Stopped and waiting for a person.');
    return finish('stop', base);
  }
  if (decision.action === 'merge') {
    const checkRun = await startMerge(client, { pull, attempt: decision.attempt });
    report.line(`Merging, attempt ${decision.attempt}.`);
    return finish('merge', { ...base, attempt: String(decision.attempt), check_run: String(checkRun) });
  }
  if (decision.action !== 'stages') {
    report.line({
      wait: 'Waiting: a merge is running.',
      done: 'Implemented and waiting for review.',
      diagnosing: 'Stopped; a diagnosis is running.',
      'missing-tasks': `\`specs/${folder}/tasks.md\` is missing on the branch.`,
    }[decision.action] ?? decision.action);
    return finish(decision.action === 'done' ? 'done' : 'none', base);
  }

  const guidance = currentGuidance(decision);
  const withGuidance = (prompt) => (guidance ? `${prompt} Guidance from the person who resumed this implementation: ${guidance.replace(/\s+/g, ' ').slice(0, 1500)}` : prompt);
  const texts = new Map(listTasks(decision.tasksMarkdown).map((task) => [task.id, task.text]));
  const stages = decision.stages.map((stage, index) => ({
    include: stage.map(({ task, attempt }) => {
      const last = index === decision.stages.length - 1 && decision.phaseEnd && stage.length === 1;
      const prompt = decision.step === 'task' ? renderTaskPrompt(task) : decision.step === 'converge' ? renderConvergePrompt() : '';
      return {
        task: task ?? '',
        attempt,
        // Guidance applies to the stopped step, which is always the first stage.
        prompt: index === 0 && prompt ? withGuidance(prompt) : prompt,
        verify_mode: decision.step === 'resolve' ? 'finalize' : last ? 'phase' : 'task',
        summary: task ? neutralizeMarkers(summarizeTask(texts.get(task) ?? '')).slice(0, 300) : '',
      };
    }),
  }));
  await startAttemptChecks(client, { head: pull.head.sha, step: decision.step, entries: stages[0].include });
  const phaseCheck = decision.step === 'task' && decision.lastIsGroup;
  for (const [index, stage] of stages.entries()) {
    report.line(`- Stage ${index + 1}: ${stage.include.map((entry) => `${stepLabel({ step: decision.step, task: entry.task || null })} attempt ${entry.attempt}`).join(', ')}`);
  }
  if (phaseCheck) report.line('- Then the full verification of the phase, because its last stage is a `[P]` group.');
  return finish('stages', {
    ...base,
    step: decision.step,
    stages: JSON.stringify(stages),
    // The matrix of the stages job: one entry per stage, run one at a time in this order.
    stage_matrix: JSON.stringify({ include: stages.map((_, index) => ({ number: index + 1, index })) }),
    phase_check: String(phaseCheck),
  });
}

// Spec Kit implement, job "continue": reports a failed phase verification as a stop (the next run then diagnoses it)
// and starts the next run of the chain.
export async function runContinue({ client, env, log }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  const folder = String(env.SPECKIT_FOLDER ?? '');
  if (String(env.SPECKIT_PHASE_CHECK ?? '') === 'failure') {
    const pull = await client.getPullRequest(pullNumber);
    const requester = (await resolveImplementRequester(client, twin))?.login ?? null;
    const run = env.GITHUB_RUN_ID ? `${contextFromEnv(env).serverUrl}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : null;
    await client.createCheckRun({
      name: CHECK_RUN_NAME,
      head_sha: pull.head.sha,
      status: 'completed',
      conclusion: 'failure',
      external_id: CHECK_LIMIT,
      output: { title: 'The phase verification failed', summary: 'The full verification at the end of the phase failed after its `[P]` tasks landed together.' },
    });
    await client.createComment(pullNumber, [
      `**Implementation needs attention**${requester ? ` @${requester}` : ''}: the full verification at the end of the phase failed after its \`[P]\` tasks landed together${run ? ` (see the [run](${run}))` : ''}. Each task passed its own verification, so the combination breaks something.`,
      '',
      ...renderNextSteps({ autoDiagnosis: autoDiagnosisEnabled(env), behindMain: folder ? await isBehindDefaultBranch(client, env, folder) : false }),
    ].join('\n'));
    report.line('The phase verification failed; the implementation stops for a person.');
  }
  const label = await labelForPull(client, env, { twin, pull: pullNumber, folder });
  await dispatchImplementation(client, env, { twin, pull: pullNumber, label });
  report.line(`Started the next run: ${renderImplementRunName(twin, label)}.`);
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

export async function main(argv, { env = process.env, log = console.log, client } = {}) {
  const [command] = argv;
  const handlers = { decide: runDecide, continue: runContinue };
  if (!handlers[command]) throw new SpecUsageError('Usage: speckit-spec.mjs <decide | continue> (inputs come from SPECKIT_* environment variables)');
  return (await handlers[command]({ client: client ?? githubClient(env), env, log })).exitCode;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof SpecUsageError ? 2 : 1;
    },
  );
}
