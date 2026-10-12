import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { IMPLEMENT_STAGE, hasLabel, resolveTwins, stageLabel } from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { resolveImplementRequester } from './speckit-prepare.mjs';
import { closeUnfinishedDiagnosis, diagnosisRunEnded, startDiagnosis } from './speckit-diagnose.mjs';
import { startAttemptChecks } from './speckit-task.mjs';
import {
  BOT_LOGIN,
  CHECK_DIAGNOSED,
  CHECK_DIAGNOSING,
  CHECK_LIMIT,
  CHECK_MERGE,
  CHECK_PROGRESS,
  CHECK_RUN_NAME,
  DONE_COMMENT_MARKER,
  MAX_AUTO_DIAGNOSES,
  MAX_TASK_ATTEMPTS,
  RESUME_COMMENT_MARKER,
  baseBranchOf,
  behindBaseOf,
  decideNext,
  dispatchImplementation,
  implementationBranch,
  kindLabel,
  latestCheckRun,
  latestHumanCommit,
  listTasks,
  markerTimes,
  neutralizeMarkers,
  parseAttemptCheckId,
  parseAttempts,
  parseGuidance,
  phaseSubject,
  readSpecTasks,
  renderAttemptMarker,
  renderConvergePrompt,
  renderImplementRunName,
  renderImplementTitle,
  renderNextSteps,
  renderResumeComment,
  renderTaskPrompt,
  specTitleOfPull,
  summarizeTask,
  syncPullRequestTaskList,
  taskJobName,
  taskSubject,
} from './speckit-implement-core.mjs';

// The implementation chain of one spec (workflow Spec Kit implement spec): every run decides what comes next from the
// state on GitHub (job "decide"), runs it (a phase in Spec Kit implement phase, the convergence or a conflict
// resolution in Spec Kit implement task, or the merge), and starts the next run (job "continue"), until the
// implementation is merged, waits for review, or needs a person.

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

// Server and repository for links; the branch of a link is chosen where it is built (the base branch of an
// implementation, or the default branch for twins).
export function contextFromEnv(env) {
  return {
    serverUrl: (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, ''),
    repository: env.GITHUB_REPOSITORY,
  };
}

export function autoDiagnosisEnabled(env) {
  return String(env.SPECKIT_AUTO_DIAGNOSE ?? 'true').toLowerCase() !== 'false';
}

// Reads the state of an open implementation pull request and decides how to continue it (decideNext). It also renders
// the task list of the pull request body from the branch's tasks.md, because amendments and convergence change tasks.
export async function decideForPull(client, { twin, folder, pullNumber, now = Date.now(), context }) {
  const pull = await client.getPullRequest(pullNumber);
  // The branch archives the spec folder right before the merge, so it may be in either place.
  const { tasksMarkdown, archived } = await readSpecTasks(client, folder, pull.head.sha);
  if (tasksMarkdown === null) return { action: 'missing-tasks', pull };
  const body = syncPullRequestTaskList(pull.body, { twinNumber: twin, folder, tasksMarkdown, context: { ...context, branch: baseBranchOf(pull), archived } });
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
  const state = { pull, tasksMarkdown, archived, checks, comments, ledger: attempts, pushedAt };
  if (decision.action === 'diagnosing' && (await diagnosisRunEnded(client, twin, latestCheckRun(checks)))) {
    const count = checks.filter((item) => [CHECK_DIAGNOSING, CHECK_DIAGNOSED].includes(item.external_id)).length;
    return { action: 'failed', diagnosis: { state: 'stale', check: latestCheckRun(checks), count }, ...state };
  }
  return { ...decision, ...state };
}

// Whether a decision needs a run of the chain: work to do, a stop to report, or a diagnosis to start.
export function needsRun(decision, env) {
  if (['tasks', 'merge', 'limit'].includes(decision.action)) return true;
  if (decision.action !== 'failed' || decision.diagnosis?.state === 'reported') return false;
  return decision.diagnosis?.state === 'stale' || (autoDiagnosisEnabled(env) && (decision.diagnosis?.count ?? 0) < MAX_AUTO_DIAGNOSES);
}

// Guidance from the latest resume (`/speckit resume <guidance>`). It applies to the stopped task, convergence, or conflict resolution only: it ends when an
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
    const label = kindLabel(attempt);
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
    output: { title: `Merge attempt ${attempt} in progress`, summary: `Merges the base branch \`${baseBranchOf(pull)}\`, runs the full verification, and merges the implementation into it.` },
  };
  const latest = latestCheckRun(await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME));
  if (latest?.status === 'queued' && latest.external_id === CHECK_PROGRESS) {
    await client.updateCheckRun(latest.id, fields);
    return latest.id;
  }
  return (await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: pull.head.sha, ...fields })).id;
}

async function stopAtLimit(client, env, { pull, folder, requester }, decision) {
  const label = kindLabel(decision);
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
    ...renderNextSteps({ autoDiagnosis: autoDiagnosisEnabled(env), behindBase: await behindBaseOf(client, pull, folder) }),
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
      ...(allowed ? ['A new diagnosis starts automatically.'] : renderNextSteps({ diagnosed: true, behindBase: await behindBaseOf(client, pull, folder) })),
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

// The matrix entries of each entry of a decision's task order (decideNext, action "tasks"): prompts, verify modes, and
// task summaries. Guidance from a resume applies to the stopped task, convergence, or conflict resolution, which is always the first entry. Every task is
// verified on its own (mode `task`); the phase gets its full verification in its own job after its last task, so a
// failure there is never charged to the task that happens to come last.
function prepareOrder(decision, folder) {
  const guidance = currentGuidance(decision);
  const withGuidance = (prompt) => (guidance ? `${prompt} Guidance from the person who resumed this implementation: ${guidance.replace(/\s+/g, ' ').slice(0, 1500)}` : prompt);
  const texts = new Map(listTasks(decision.tasksMarkdown).map((task) => [task.id, task.text]));
  return decision.order.map((entry, index) => ({
    include: entry.map(({ task, attempt }) => {
      const prompt = decision.kind === 'task' ? renderTaskPrompt(task, folder, { archived: decision.archived }) : decision.kind === 'converge' ? renderConvergePrompt() : '';
      return {
        task: task ?? '',
        attempt,
        prompt: index === 0 && prompt ? withGuidance(prompt) : prompt,
        verify_mode: decision.kind === 'resolve' ? 'finalize' : 'task',
        summary: task ? neutralizeMarkers(summarizeTask(texts.get(task) ?? '')).slice(0, 300) : '',
      };
    }),
  }));
}

function reportOrder(report, kind, order) {
  for (const entry of order) {
    const attempts = entry.include.map((item) => `${kindLabel({ kind, task: item.task || null })} attempt ${item.attempt}`);
    report.line(`- ${attempts.join(', ')}${attempts.length > 1 ? ' in parallel' : ''}`);
  }
}

// Spec Kit implement spec, job "decide" (write access to check runs, issues, and pull requests; never runs agent
// code): validates the twin and its pull request, reports orphaned attempts, decides what comes next, reports stops
// (attempt limit, diagnosis), and prepares it: the phase to run (Spec Kit implement phase plans its tasks),
// the convergence or a conflict resolution with its check run, or the merge check run. The outputs drive
// the jobs of the run; `continue` tells whether the run starts its successor.
export async function runDecide({ client, env, log, now = Date.now }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  const finish = (action, outputs = {}) => {
    report.flush();
    setOutput(env, 'action', action);
    setOutput(env, 'continue', String(['phase', 'converge', 'resolve', 'merge'].includes(action)));
    for (const [name, value] of Object.entries(outputs)) setOutput(env, name, value);
    return { exitCode: 0, action, ...outputs };
  };

  const issue = await client.getIssue(twin);
  const folder = resolveTwins([issue]).byFolder.keys().next().value;
  const listed = await client.getPullRequest(pullNumber);
  report.line(`## ${renderImplementRunName(twin, specTitleOfPull(listed))}`);
  report.line();
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
  const common = { folder, head: pull.head.sha, base_branch: baseBranchOf(pull) };
  if (decision.action === 'limit') {
    const requester = (await resolveImplementRequester(client, twin))?.login ?? null;
    await stopAtLimit(client, env, { pull, folder, requester }, decision);
    report.line(`Stopped: ${kindLabel(decision)} reached the attempt limit.`);
    await maybeDiagnose(client, env, { twin, pull, folder }, { state: 'none', count: 0 }, report);
    return finish('stop', common);
  }
  if (decision.action === 'failed') {
    if (decision.diagnosis?.state !== 'reported') await maybeDiagnose(client, env, { twin, pull, folder }, decision.diagnosis, report);
    report.line(decision.diagnosis?.state === 'reported' ? 'Stopped; the diagnosis waits for a person\'s decision.' : 'Stopped and waiting for a person.');
    return finish('stop', common);
  }
  if (decision.action === 'merge') {
    const checkRun = await startMerge(client, { pull, attempt: decision.attempt });
    report.line(`Merging into \`${common.base_branch}\`, attempt ${decision.attempt}.`);
    return finish('merge', { ...common, attempt: String(decision.attempt), check_run: String(checkRun) });
  }
  if (decision.action !== 'tasks') {
    report.line({
      wait: 'Waiting: a merge is running.',
      done: 'Implemented and waiting for review.',
      diagnosing: 'Stopped; a diagnosis is running.',
      'missing-tasks': `\`tasks.md\` of \`${folder}\` is missing on the branch (neither in \`specs/\` nor in \`specs/archive/\`).`,
    }[decision.action] ?? decision.action);
    return finish(decision.action === 'done' ? 'done' : 'none', common);
  }
  if (decision.kind === 'task') {
    report.line(`Next: ${renderImplementTitle('phase', twin, phaseSubject(decision.phase))}.`);
    return finish('phase', { ...common, phase: decision.phase ?? '', job_name: phaseSubject(decision.phase) });
  }

  const order = prepareOrder(decision, folder);
  await startAttemptChecks(client, { head: pull.head.sha, kind: decision.kind, entries: order[0].include });
  const subject = taskSubject(decision.kind, order[0].include);
  report.line(`Next: ${renderImplementTitle('task', twin, subject)}.`);
  reportOrder(report, decision.kind, order);
  return finish(decision.kind, { ...common, order: JSON.stringify(order), job_name: subject });
}

// Spec Kit implement phase, job "plan" (write access to check runs and pull requests; never runs agent code): decides
// again from the state on GitHub and, while the phase still comes next, plans the order of its tasks: each
// entry is a task, or consecutive `[P]` tasks in parallel, with their prompts, verify modes, and summaries; the matrix
// of the tasks job, one entry each with the job name it runs under; and the check runs of the first entry. It plans
// nothing when the state changed since the spec run decided. `phase_check` tells whether this run finishes the phase,
// whose result then gets the phase's full verification in the phase workflow's verify job.
export async function runPlanPhase({ client, env, log, now = Date.now }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  const folder = String(env.SPECKIT_FOLDER ?? '');
  if (!folder) throw new SpecUsageError('Expected SPECKIT_FOLDER.');
  const phase = String(env.SPECKIT_PHASE ?? '');
  report.line(`## ${renderImplementTitle('phase', twin, phaseSubject(phase))}`);
  report.line();
  const finish = (outputs) => {
    report.flush();
    for (const [name, value] of Object.entries(outputs)) setOutput(env, name, value);
    return { exitCode: 0, ...outputs };
  };

  const decision = await decideForPull(client, { twin, folder, pullNumber, now: now(), context: contextFromEnv(env) });
  const head = decision.pull.head.sha;
  if (decision.action !== 'tasks' || decision.kind !== 'task' || (decision.phase ?? '') !== phase) {
    report.line('Nothing to plan: the phase no longer comes next.');
    return finish({ count: '0', head, order: '[]', task_matrix: JSON.stringify({ include: [] }), phase_check: 'false' });
  }
  const order = prepareOrder(decision, folder);
  await startAttemptChecks(client, { head, kind: 'task', entries: order[0].include });
  const phaseCheck = decision.phaseEnd;
  reportOrder(report, 'task', order);
  if (phaseCheck) report.line('- Then the full verification of the phase.');
  else report.line('- The rest of the phase continues in the next run.');
  return finish({
    count: String(order.length),
    head,
    order: JSON.stringify(order),
    // The matrix of the tasks job: one job per entry of the order, run one at a time.
    task_matrix: JSON.stringify({ include: order.map((entry, index) => ({ index, name: taskJobName(entry.include) })) }),
    phase_check: String(phaseCheck),
  });
}

// Spec Kit implement spec, job "continue": reports a failed phase verification as a stop (the next run then diagnoses
// it) and starts the next run of the chain.
export async function runContinue({ client, env, log }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  const folder = String(env.SPECKIT_FOLDER ?? '');
  const pull = await client.getPullRequest(pullNumber);
  if (String(env.SPECKIT_PHASE_CHECK ?? '') === 'failure') {
    const requester = (await resolveImplementRequester(client, twin))?.login ?? null;
    const run = env.GITHUB_RUN_ID ? `${contextFromEnv(env).serverUrl}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : null;
    await client.createCheckRun({
      name: CHECK_RUN_NAME,
      head_sha: pull.head.sha,
      status: 'completed',
      conclusion: 'failure',
      external_id: CHECK_LIMIT,
      output: { title: 'The phase verification failed', summary: 'The full verification at the end of the phase failed, although each task passed its own verification.' },
    });
    await client.createComment(pullNumber, [
      `**Implementation needs attention**${requester ? ` @${requester}` : ''}: the full verification at the end of the phase failed${run ? ` (see the [run](${run}))` : ''}. Each task passed its own verification, which covers only the tests it touched, so a change of the phase breaks tests elsewhere, or the tasks break something together.`,
      '',
      ...renderNextSteps({ autoDiagnosis: autoDiagnosisEnabled(env), behindBase: folder ? await behindBaseOf(client, pull, folder) : null }),
    ].join('\n'));
    report.line('The phase verification failed; the implementation stops for a person.');
  }
  const title = specTitleOfPull(pull);
  await dispatchImplementation(client, env, { twin, pull: pullNumber, title });
  report.line(`Started the next run: ${renderImplementRunName(twin, title)}.`);
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
  const handlers = { decide: runDecide, 'plan-phase': runPlanPhase, continue: runContinue };
  if (!handlers[command]) throw new SpecUsageError('Usage: speckit-spec.mjs <decide | plan-phase | continue> (inputs come from SPECKIT_* environment variables)');
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
