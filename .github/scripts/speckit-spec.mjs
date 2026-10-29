import { pathToFileURL } from 'node:url';

import { isImplementRequested, resolveTwins } from './speckit-prepare-core.mjs';
import { resolveImplementRequester } from './speckit-prepare.mjs';
import { closeUnfinishedDiagnosis, diagnosisRunEnded, startDiagnosis } from './speckit-diagnose.mjs';
import { findAmendment } from './speckit-amend.mjs';
import { startAttemptChecks } from './speckit-implement-work.mjs';
import { createGitHubClient, createReporter, setOutput } from './speckit-script-runtime.mjs';
import {
  BOT_LOGIN,
  CHECK_CONFLICT,
  CHECK_DIAGNOSING,
  CHECK_DIAGNOSED,
  CHECK_LIMIT,
  CHECK_MERGE,
  CHECK_PROGRESS,
  CHECK_RUN_NAME,
  CHECK_SYNC_CONFLICT,
  DONE_COMMENT_MARKER,
  DIAGNOSIS_COMMENT_MARKER,
  AMEND_REQUEST_STATE_MARKER,
  FIX_CHECK_NAME,
  DIAGNOSIS_STALE_MS,
  MAX_SYNC_RESOLVE_ATTEMPTS,
  MAX_TASK_ATTEMPTS,
  RESTART_COMMENT_MARKERS,
  RETRY_COMMENT_MARKER,
  SYNC_COMMENT_MARKER,
  UNBLOCKED_MARKER,
  startsWithMarker,
  syncBarrier,
  renderSyncBarrierMarker,
  baseBranchOf,
  nextStepContext,
  openPrerequisites,
  phaseNotStarted,
  folderOfBranch,
  pendingAmendmentRequest,
  pendingCopilotHandoff,
  acceptedImplementationAction,
  trustedMarker,
  ACTION_REQUIRED_MARKER,
  supersedeActionRequired,
  decideNext,
  explainDependencyBlock,
  describeParallelGroups,
  diagnosisRunsUrl,
  dispatchImplementation,
  ensureImplementationDraft,
  implementationBranch,
  kindLabel,
  latestCheckRun,
  latestHumanCommit,
  listTasks,
  markerTimes,
  neutralizeMarkers,
  normalizeWriterGuidance,
  parseAttemptCheckId,
  parseAttempts,
  parseFixEvents,
  parseGuidance,
  phaseSubject,
  readSpecTasks,
  renderAttemptMarker,
  renderRunSubject,
  renderFlakyLines,
  renderImplementRunName,
  renderImplementTitle,
  renderNextSteps,
  renderRepairMarker,
  renderSyncSkippedMarker,
  setAttention,
  setBlocked,
  specTitleOfPull,
  summarizeTask,
  syncPullRequestTaskList,
  syncSkippedShas,
  taskJobName,
  taskSubject,
  unresolvedRepair,
  WORK_VERBS,
  implementWorkflowFor,
  IMPLEMENT_WORKFLOW_FILE,
  MAX_PARALLEL_TASKS,
} from './speckit-implement-core.mjs';

// The implementation chain of one spec (the Spec Kit implement workflows): every run decides one unit of work from the state
// on GitHub (job "decide"), runs it (a sequential or parallel group of tasks, the verification of a finished phase, a
// guided repair, the convergence, a conflict resolution, or the merge), and starts the next run (job "continue"),
// until the implementation is merged, waits for review, or needs a person.

export class SpecUsageError extends Error {}

// Server and repository for links; the branch of a link is chosen where it is built (the base branch of an
// implementation, or the default branch for twins).
export function contextFromEnv(env) {
  return {
    serverUrl: (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, ''),
    repository: env.GITHUB_REPOSITORY,
  };
}

// Reads the state of an open implementation pull request and decides how to continue it (decideNext). It also renders
// the task list of the pull request body from the branch's tasks.md, because amendments and convergence change tasks.
// With `dryRun` it changes nothing on GitHub (no task list update, no release of a sync barrier), for naming the next run.
export async function decideForPull(client, { twin, folder, pullNumber, now = Date.now(), context, dryRun = false }) {
  const pull = await client.getPullRequest(pullNumber);
  // The branch archives the spec folder right before the merge, so it may be in either place.
  const { tasksMarkdown, archived } = await readSpecTasks(client, folder, pull.head.sha);
  if (tasksMarkdown === null) return { action: 'missing-tasks', pull };
  const body = syncPullRequestTaskList(pull.body, { twinNumber: twin, folder, tasksMarkdown, context: { ...context, branch: baseBranchOf(pull), archived } });
  if (!dryRun && body !== String(pull.body ?? '')) {
    try {
      await client.updatePullRequest(pullNumber, { body });
    } catch {
      // The task list is a convenience; a failed update must not stop the implementation.
    }
  }
  const checks = await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME);
  const comments = await client.listIssueComments(pullNumber);
  const attempts = parseAttempts(comments);
  const restartedAt = [...RESTART_COMMENT_MARKERS, SYNC_COMMENT_MARKER].flatMap((marker) => markerTimes(comments, marker));
  const pushedAt = latestHumanCommit(await client.listPullRequestCommits(pullNumber));
  const done = markerTimes(comments, DONE_COMMENT_MARKER).length > 0;
  const windowStart = [pull.created_at, ...restartedAt, ...(pushedAt ? [pushedAt] : [])].sort((a, b) => Date.parse(b) - Date.parse(a))[0];
  const latestRestart = [...restartedAt].sort((a, b) => Date.parse(b) - Date.parse(a))[0] ?? null;
  let decision = decideNext({ tasksMarkdown, checks, attempts, windowStart, done, resumedAt: latestRestart, now });
  const prerequisiteDecision = comments.some((comment) => comment.user?.login === BOT_LOGIN &&
    startsWithMarker(comment.body, UNBLOCKED_MARKER) && !comment.body.includes(RETRY_COMMENT_MARKER) &&
    Date.parse(comment.created_at) >= Date.parse(windowStart));
  if (prerequisiteDecision && decision.action !== 'diagnosing') {
    decision = { action: 'failed', diagnosis: { state: 'reported', count: 0 } };
  }
  const repair = unresolvedRepair(comments);
  if (repair) {
    // Every failed repair attempt stops for a person, so each attempt follows a restart (retry, a merged amendment, or
    // an implemented prerequisite) newer than the repair and its latest attempt. A restart gets a fresh attempt;
    // without one, the stop stands (and is reported once; a recorded stop or diagnosis is kept as it is).
    const repairAttempts = attempts.filter((attempt) => attempt.kind === 'repair' && Date.parse(attempt.at) >= Date.parse(repair.at));
    const latestRepairAttempt = repairAttempts.map((attempt) => Date.parse(attempt.at)).sort((a, b) => b - a)[0] ?? 0;
    const restart = comments
      .filter((comment) => comment.user?.login === BOT_LOGIN &&
        [...RESTART_COMMENT_MARKERS, SYNC_COMMENT_MARKER].some((marker) => startsWithMarker(comment.body, marker)) &&
        Date.parse(comment.created_at) >= Math.max(Date.parse(repair.at), latestRepairAttempt))
      .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at))[0] ?? null;
    if (restart && (!startsWithMarker(restart.body, SYNC_COMMENT_MARKER) ||
        repairAttempts.filter((attempt) => attempt.outcome === 'failure').length < MAX_TASK_ATTEMPTS)) {
      const guidance = startsWithMarker(restart.body, SYNC_COMMENT_MARKER)
        ? currentGuidance({ comments, ledger: attempts, pushedAt }) ?? ''
        : parseGuidance(restart.body) ?? '';
      decision = {
        action: 'repair',
        kind: 'repair',
        repair,
        guidance,
        verifyOnly: !guidance,
        attempt: repairAttempts.length + 1,
        stale: decision.stale ?? [],
        ...(decision.syncGivenUp ? { syncGivenUp: decision.syncGivenUp } : {}),
      };
    } else if (!['diagnosing'].includes(decision.action)) {
      decision = {
        action: 'failed',
        repair,
        stale: decision.stale ?? [],
        diagnosis: decision.diagnosis ?? { state: 'none', check: latestCheckRun(checks), count: checks.filter((check) => [CHECK_DIAGNOSING, CHECK_DIAGNOSED].includes(check.external_id)).length },
      };
    }
  }
  // The ledger is not called attempts, which a limit decision uses for its count.
  const state = { pull, tasksMarkdown, archived, checks, comments, ledger: attempts, pushedAt };
  const barrier = syncBarrier(comments);
  if (barrier) {
    const completion = barrier.completion;
    const base = baseBranchOf(pull);
    // A successful work run is only provisional until the latest base is checked again in the planner.
    let current = false;
    if (completion?.head === pull.head.sha && completion.base === base) {
      try {
        current = completion.baseSha === await client.getBranchSha(base) &&
          await client.aheadBy(pull.head.sha, completion.baseSha) === 0;
        if (current) {
          const latest = await client.getPullRequest(pullNumber);
          current = latest.head.sha === completion.head && baseBranchOf(latest) === base &&
            await client.getBranchSha(base) === completion.baseSha;
        }
      } catch {
        // An unavailable comparison cannot release a required barrier.
      }
    }
    if (current && dryRun) return { action: 'tasks', kind: 'resolve', requiredSync: barrier, order: [[{ task: null, attempt: 1 }]], ...state };
    if (current) {
      if (repair && completion.repair?.id === repair.id && completion.repair.covered === true &&
          JSON.stringify(completion.repair.snapshot) === JSON.stringify(repair)) {
        await client.createComment(pullNumber, [
          renderRepairMarker({ id: repair.id, state: 'resolved', at: new Date(now).toISOString(), head: pull.head.sha }),
          `**Original ${repair.scope} verification passed during required synchronization** on \`${pull.head.sha}\`. Existing repair attempts remain recorded; no repair attempt limit was reset.`,
        ].join('\n'));
      }
      await client.createComment(pullNumber, [
        renderSyncBarrierMarker({ ...completion, state: 'released' }),
        `**Required synchronization complete**: \`${base}\` at \`${String(completion.baseSha ?? '').slice(0, 7)}\` is merged and verified, and it has not moved since, so the implementation continues.`,
      ].join('\n'));
      // Replan from the durable acknowledgement, including any original failure that the full verification cleared.
      return decideForPull(client, { twin, folder, pullNumber, now, context });
    } else {
      const since = Math.max(Date.parse(barrier.at), Date.parse(pushedAt ?? '') || 0, ...RESTART_COMMENT_MARKERS.flatMap((marker) => markerTimes(comments, marker).map(Date.parse)));
      const list = attempts.filter((attempt) => attempt.kind === 'resolve' && Date.parse(attempt.at) >= since);
      const stop = latestCheckRun(checks);
      if ([CHECK_LIMIT, CHECK_DIAGNOSING, CHECK_DIAGNOSED].includes(stop?.external_id) &&
          Date.parse(stop.started_at ?? stop.created_at ?? 0) > since) {
        const diagnosing = stop.external_id === CHECK_DIAGNOSING && stop.status !== 'completed';
        const stale = diagnosing && (now - Date.parse(stop.started_at ?? stop.created_at ?? 0) >= DIAGNOSIS_STALE_MS ||
          await diagnosisRunEnded(client, twin, stop));
        return {
          action: diagnosing && !stale ? 'diagnosing' : 'failed',
          requiredSync: barrier,
          diagnosis: { state: stale ? 'stale' : stop.external_id === CHECK_DIAGNOSED ? 'reported' : 'none', check: stop, count: checks.filter((check) => [CHECK_DIAGNOSING, CHECK_DIAGNOSED].includes(check.external_id)).length },
          ...state,
        };
      }
      const count = list.filter((attempt) => attempt.outcome === 'failure').length;
      if (count >= MAX_TASK_ATTEMPTS || list.length >= MAX_TASK_ATTEMPTS * 2) {
        return { action: 'limit', kind: 'resolve', attempts: list.length, requiredSync: barrier, ...state };
      }
      if (list.some((attempt) => attempt.outcome === 'attention')) {
        return { action: 'failed', requiredSync: barrier, diagnosis: { state: 'reported', count: 0 }, ...state };
      }
      return {
        action: 'tasks', kind: 'resolve', requiredSync: barrier, repair,
        order: [[{ task: null, attempt: list.length + 1 }]],
        ...state,
      };
    }
  }
  if (decision.action === 'diagnosing' && (await diagnosisRunEnded(client, twin, latestCheckRun(checks)))) {
    const count = checks.filter((item) => [CHECK_DIAGNOSING, CHECK_DIAGNOSED].includes(item.external_id)).length;
    return { action: 'failed', diagnosis: { state: 'stale', check: latestCheckRun(checks), count }, ...state };
  }
  return { ...decision, ...state };
}

// Whether a decision needs a run of the chain: work to do, a stop to report, or a diagnosis to start.
export function needsRun(decision, env) {
  if (['tasks', 'verify', 'repair', 'merge', 'limit'].includes(decision.action)) return true;
  if (decision.action !== 'failed' || decision.diagnosis?.state === 'reported') return false;
  return true;
}

export async function implementationAttention(client, decision, { blocked = false, now = Date.now() } = {}) {
  if (blocked || !decision.pull) return null;
  if (!['failed', 'done', 'tasks', 'repair', 'verify', 'merge', 'wait', 'diagnosing', 'limit'].includes(decision.action)) return null;
  if (decision.action === 'failed' && decision.diagnosis?.state !== 'reported') return null;
  let needed = decision.action === 'done' || decision.action === 'failed';
  let successor = null;
  const comments = decision.comments ?? [];
  const fixEvents = parseFixEvents(comments);
  const authorRequest = fixEvents.filter((event) => event.event === 'requested').at(-1);
  const authoring = authorRequest && authorRequest.link !== false
    ? latestCheckRun(await client.listCheckRuns(decision.pull.head.sha, FIX_CHECK_NAME))
    : null;
  let recommendation = [...comments].reverse().find((comment) => comment.user?.login === BOT_LOGIN &&
    startsWithMarker(comment.body, DIAGNOSIS_COMMENT_MARKER)) ?? null;
  if (recommendation && (acceptedImplementationAction(comments, recommendation.created_at) ||
      (decision.pushedAt && Date.parse(decision.pushedAt) > Date.parse(recommendation.created_at)))) {
    const boundary = recommendation.created_at;
    recommendation = null;
    if (decision.action === 'failed') {
      const failures = trustedMarker(comments, AMEND_REQUEST_STATE_MARKER)
        .filter((item) => ['failed', 'stale'].includes(item.state)).map((item) => item.markerComment);
      const discarded = fixEvents.filter((event) => event.event === 'discarded').map((event) => event.commentId);
      failures.push(...comments.filter((comment) => comment.user?.login === BOT_LOGIN &&
        (/^<!-- speckit-amend:closed \d+ -->/.test(comment.body ?? '') || discarded.includes(comment.id))));
      recommendation = failures.filter((comment) => Date.parse(comment.created_at) > Date.parse(boundary) &&
        !acceptedImplementationAction(comments, comment.created_at) &&
        !(decision.pushedAt && Date.parse(decision.pushedAt) > Date.parse(comment.created_at)))
        .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at))[0] ?? null;
      const check = latestCheckRun(decision.checks ?? []);
      const diagnosisFailed = check?.external_id === CHECK_DIAGNOSED && check.conclusion === 'failure' &&
        Date.parse(check.started_at) > Date.parse(boundary) && !acceptedImplementationAction(comments, check.started_at);
      const authoringFailed = authoring?.conclusion === 'failure' &&
        !acceptedImplementationAction(comments, authoring.completed_at ?? authorRequest.at);
      needed = Boolean(recommendation || diagnosisFailed || authoringFailed);
    }
  }
  if (!recommendation) recommendation = [...comments].reverse().find((comment) => comment.user?.login === BOT_LOGIN &&
    startsWithMarker(comment.body, UNBLOCKED_MARKER) && !comment.body.includes(RETRY_COMMENT_MARKER) &&
    !acceptedImplementationAction(comments, comment.created_at)) ?? null;
  if (needed) {
    const handoff = pendingCopilotHandoff(comments, { pushedAt: decision.pushedAt, now });
    const folder = folderOfBranch(decision.pull.head?.ref);
    if (handoff?.state === 'working' || pendingAmendmentRequest(comments) ||
        (authoring && authoring.status !== 'completed')) needed = false;
    else if (folder) {
      successor = await findAmendment(client, folder);
      if (successor) needed = false;
    }
  }
  return { needed, recommendation, successor };
}

export async function reconcileImplementationAttention(client, decision, options = {}) {
  const attention = await implementationAttention(client, decision, options);
  if (attention) await setAttention(client, decision.pull.number, attention.needed);
  if ((decision.comments ?? []).some((comment) => comment.user?.login === BOT_LOGIN &&
      String(comment.body ?? '').startsWith(`<!-- ${ACTION_REQUIRED_MARKER} `))) {
    await supersedeActionRequired(client, decision.pull.number, { successor: attention?.successor });
  }
  return attention;
}

// Guidance from the latest restart or sync ends after the first prompted work succeeds, or a person pushes.
// A clean/no-op sync has no prompt and carries its unconsumed guidance into the first continued work.
function currentGuidance({ comments, ledger, pushedAt }) {
  const restart = comments
    .filter((comment) => comment.user?.login === BOT_LOGIN && [...RESTART_COMMENT_MARKERS, SYNC_COMMENT_MARKER].some((marker) => startsWithMarker(comment.body, marker)))
    .at(-1);
  const guidance = restart ? parseGuidance(restart.body) : null;
  if (!guidance) return null;
  if (pushedAt && Date.parse(pushedAt) > Date.parse(restart.created_at)) return null;
  const sync = startsWithMarker(restart.body, SYNC_COMMENT_MARKER) ? syncBarrier(comments, { includeReleased: true }) : null;
  if (sync?.completion?.guidanceConsumed) return null;
  // A clean/no-op sync has no Copilot prompt; only work after its acknowledgement consumes the guidance.
  const since = sync?.completionAt ?? restart.created_at;
  if (ledger.some((attempt) => attempt.outcome === 'success' && Date.parse(attempt.at) > Date.parse(since))) return null;
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
    ...renderNextSteps({ diagnosisUrl: diagnosisRunsUrl(env), ...(await nextStepContext(client, pull, folder)) }),
  ].join('\n'));
}

// Starts a diagnosis of a stopped implementation by itself unless one already reported for this stop. A diagnosis
// that ended without a report is closed with a comment first, so it never blocks the implementation.
async function maybeDiagnose(client, env, { twin, pull, folder }, diagnosis, report) {
  if (diagnosis.state === 'stale') {
    await closeUnfinishedDiagnosis(client, diagnosis.check);
    const runs = diagnosisRunsUrl(env);
    await client.createComment(pull.number, [
      `**The diagnosis did not finish** (see the [diagnose runs](${runs})).`,
      '',
      'A new diagnosis starts automatically.',
    ].join('\n'));
    report.line('Closed the unfinished diagnosis.');
  }
  if (diagnosis.state === 'reported') return false;
  const started = await startDiagnosis(client, env, { twin, pull: pull.number, folder, head: pull.head.sha, auto: true });
  report.line(started.started ? 'Started a diagnosis.' : `Warning: could not start a diagnosis: ${started.error}`);
  return started.started;
}

function readSpecInputs(env) {
  const twin = Number(env.SPECKIT_TWIN);
  const pull = Number(env.SPECKIT_PULL);
  if (![twin, pull].every((value) => Number.isInteger(value) && value > 0)) throw new SpecUsageError('Expected SPECKIT_TWIN and SPECKIT_PULL as positive integers.');
  return { twin, pull };
}

// The work items of each entry of a decision's task order (decideNext, action "tasks"), the input `entry` of Spec Kit
// implement work: the task, attempt, guidance, and task summary, and for a conflict resolution that completes a
// /speckit sync request `required_sync` and `sync_repair`; the agents' prompts live in the gha Spec Kit workflows the
// work jobs run. Guidance from a retry
// applies to the stopped task, convergence, or conflict resolution, which is always the first entry. Every task is
// verified on its own (mode `task`, a conflict resolution with mode `finalize`); the phase gets its full verification in its own job after its last task, so a
// failure there is never charged to the task that happens to come last.
function prepareOrder(decision) {
  const guidance = normalizeWriterGuidance(
    decision.requiredSync?.completion?.guidanceConsumed ? '' : decision.requiredSync?.guidance || currentGuidance(decision),
  );
  const texts = new Map(listTasks(decision.tasksMarkdown).map((task) => [task.id, task.text]));
  return decision.order.map((entry, index) => ({
    include: entry.map(({ task, attempt }) => {
      const activeGuidance = index === 0 ? guidance : '';
      return {
        task: task ?? '',
        attempt,
        guidance: activeGuidance,
        ...(decision.requiredSync ? { required_sync: decision.requiredSync.request } : {}),
        ...(decision.requiredSync && decision.repair ? { sync_repair: decision.repair } : {}),
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

export function syncBaseEnabled(env) {
  return String(env.SPECKIT_SYNC_BASE ?? 'true').toLowerCase() !== 'false';
}

export { phaseNotStarted };

// Which work the base branch is merged before, or null: a phase that has not started, the convergence, and the merge,
// Before the merge integration is required; before other work it is best effort.
export function integrationTarget(decision) {
  if (decision.action === 'merge') return 'merge';
  if (decision.action === 'repair') return null;
  if (decision.action !== 'tasks' || decision.syncGivenUp) return null;
  if (decision.kind === 'converge') return 'convergence';
  if (decision.kind === 'task' && phaseNotStarted(decision.tasksMarkdown)) return 'phase';
  return null;
}

// Brings the base branch into the implementation branch before the work that comes next (pull model; the job "sync"
// of every chain run): 'merged' after a clean merge through the GitHub API, 'conflict' when it conflicts, 'skipped'
// when GitHub refuses it, and 'none' when nothing is due (integrationTarget), the base branch is not ahead, or this
// base commit was skipped before a phase or the convergence. A conflict before the merge is recorded like the merge's
// own (CHECK_CONFLICT, resolution required); before other work as CHECK_SYNC_CONFLICT (best effort). Either way the
// decide job of the same run then starts the conflict resolution.
export async function integrateBase(client, env, { decision, folder, report }) {
  // Required sync always uses the isolated resolve/verify/land workflow, including clean and no-op merges.
  if (decision.requiredSync) return 'required';
  if (!syncBaseEnabled(env)) return 'none';
  const target = integrationTarget(decision);
  if (!target) return 'none';
  const { pull } = decision;
  const base = baseBranchOf(pull);
  const branch = implementationBranch(folder);
  const baseSha = await client.getBranchSha(base);
  if (!baseSha || (await client.aheadBy(branch, base)) === 0) return 'none';
  const required = target === 'merge';
  if (!required && syncSkippedShas(decision.comments).has(baseSha)) {
    report.line(`\`${base}\` at ${baseSha.slice(0, 7)} was not merged before earlier work; the merge integrates it at the end.`);
    return 'none';
  }
  let result;
  try {
    if (syncBarrier(await client.listIssueComments(pull.number))) return 'required';
    result = await client.mergeBranch(branch, base, `Merge ${base} into ${branch} before the ${target}`);
  } catch (error) {
    const reason = `GitHub refused the merge (${neutralizeMarkers(String(error.message).slice(0, 300))})`;
    if (required) {
      report.line(`Did not merge \`${base}\` before the merge: ${reason}. The merge integrates it.`);
      return 'skipped';
    }
    await client.createComment(pull.number, [
      renderSyncSkippedMarker(baseSha),
      `**\`${base}\` was not merged before the ${target}**: ${reason}. The implementation continues on its current base; the merge at the end integrates \`${base}\`.`,
    ].join('\n'));
    report.line(`Did not merge \`${base}\` before the ${target}: ${reason}.`);
    return 'skipped';
  }
  if (result === 'up-to-date') return 'none';
  if (result === 'merged') {
    report.line(`Merged \`${base}\` at ${baseSha.slice(0, 7)} into \`${branch}\` before the ${target}.`);
    return 'merged';
  }
  await client.createCheckRun({
    name: CHECK_RUN_NAME,
    head_sha: pull.head.sha,
    status: 'completed',
    conclusion: 'neutral',
    external_id: required ? CHECK_CONFLICT : CHECK_SYNC_CONFLICT,
    output: { title: `Conflicts with ${base}`, summary: `Merging \`${base}\` at ${baseSha} before the ${target} conflicts; Copilot resolves the conflicts first.` },
  });
  await client.createComment(pull.number, required
    ? `**\`${base}\` conflicts with \`${branch}\`**: Copilot resolves the conflicts now, before the merge.`
    : `**\`${base}\` conflicts with \`${branch}\`**: Copilot resolves the conflicts now, before the ${target}. If that does not succeed in ${MAX_SYNC_RESOLVE_ATTEMPTS} attempts or needs a person, the implementation continues on its current base and the conflicts are resolved when it merges.`);
  report.line(`Merging \`${base}\` at ${baseSha.slice(0, 7)} conflicts; Copilot resolves the conflicts first.`);
  return 'conflict';
}

// The best-effort conflict resolution before a phase or the convergence was given up (decideNext): records the base
// commit as skipped, once.
async function reportSyncGivenUp(client, { decision, report }) {
  const check = decision.syncGivenUp;
  const since = Date.parse(check.started_at ?? check.created_at ?? 0);
  const recorded = decision.comments.some((comment) => comment.user?.login === BOT_LOGIN && syncSkippedShas([comment]).size > 0 && Date.parse(comment.created_at) >= since);
  const baseSha = String(check.output?.summary ?? '').match(/\b([0-9a-f]{40})\b/)?.[1];
  if (recorded || !baseSha) return;
  const base = baseBranchOf(decision.pull);
  await client.createComment(decision.pull.number, [
    renderSyncSkippedMarker(baseSha),
    `**\`${base}\` was not merged**: resolving its conflicts did not succeed or needs a person. The implementation continues on its current base; the conflicts are resolved when it merges.`,
  ].join('\n'));
  report.line(`Gave up merging \`${base}\`; the implementation continues on its current base.`);
}

// Spec Kit implement prepare, job "decide" (write access to check runs, issues, and pull requests; never runs agent code):
// validates the twin and its pull request, reports orphaned attempts, decides what comes next, reports stops (attempt
// limit, diagnosis), and prepares it with its check runs: a group of tasks (`mode` sequential or parallel, `entries`
// the matrix of the group's jobs), the verification of a finished phase, a writer-approved repair, the convergence or
// a conflict resolution (a sequential group of one), or the merge. The outputs drive the jobs of the run; `continue`
// tells whether the run starts its successor.
export async function runDecide({ client, env, log, now = Date.now }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  const finish = (action, outputs = {}) => {
    report.flush();
    setOutput(env, 'action', action);
    setOutput(env, 'continue', String(['group', 'verify', 'repair', 'merge', 'handover'].includes(action)));
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
  if (issue.state !== 'open' || !isImplementRequested(issue)) reasons.push(`#${twin} is not open and flagged for implementation`);
  if (folder && (listed.state !== 'open' || listed.head?.ref !== implementationBranch(folder) || listed.head?.repo?.full_name !== env.GITHUB_REPOSITORY)) {
    reasons.push(`#${pullNumber} is not the open implementation pull request of \`specs/${folder}\``);
  }
  if (reasons.length > 0) {
    report.line(`Nothing to do: ${reasons.join('; ')}.`);
    return finish('none');
  }
  const orphaned = await closeOrphanedAttempts(client, listed);
  if (orphaned > 0) report.line(`Recorded ${orphaned} attempt(s) of an earlier run that ended without reporting as failed.`);
  // An open dependency of the twin, also one a person added while the chain runs, ends the chain before its next work;
  // the scheduler retries the implementation once the prerequisites are implemented (maintainDependencyBlock).
  const prerequisites = await openPrerequisites(client, twin);
  if (prerequisites.length > 0) {
    await setBlocked(client, pullNumber, true, { explain: await explainDependencyBlock(client, prerequisites) });
    report.line(`Blocked: waits for the prerequisite(s) ${prerequisites.map((blocker) => `#${blocker.number}`).join(', ')}.`);
    return finish('none');
  }

  const decision = await decideForPull(client, { twin, folder, pullNumber, now: now(), context: contextFromEnv(env) });
  const { pull } = decision;
  for (const stale of decision.stale ?? []) {
    await client.updateCheckRun(stale.id, { status: 'completed', conclusion: 'failure', output: { title: 'Merge attempt abandoned', summary: 'The merge did not finish within 2 hours.' } });
  }
  const common = { folder, head: pull.head.sha, base_branch: baseBranchOf(pull) };
  // Work of another kind than this workflow's (SPECKIT_WORKFLOW): the state changed after the run was started. The run
  // hands over to the right workflow before it starts any attempt check, so no attempt is charged to it.
  const workflow = implementWorkflowFor(decision.action);
  if (env.SPECKIT_WORKFLOW && workflow !== IMPLEMENT_WORKFLOW_FILE && workflow !== env.SPECKIT_WORKFLOW) {
    report.line(`Handover: the next work, ${renderRunSubject(decision, { summaries: taskSummaries(decision.tasksMarkdown) })}, runs in \`${workflow}\`.`);
    return finish('handover', common);
  }
  if (decision.action !== 'done') await ensureImplementationDraft(client, pull);
  await reconcileImplementationAttention(client, decision, { now: now() });
  if (decision.action === 'limit') {
    const requester = (await resolveImplementRequester(client, twin))?.login ?? null;
    await stopAtLimit(client, env, { pull, folder, requester }, decision);
    report.line(`Stopped: ${kindLabel(decision)} reached the attempt limit.`);
    const diagnosing = await maybeDiagnose(client, env, { twin, pull, folder }, { state: 'none', count: 0 }, report);
    await setAttention(client, pull.number, !diagnosing);
    return finish('stop', common);
  }
  if (decision.action === 'failed') {
    if (decision.requiredSync) await setAttention(client, pull.number, true);
    if (decision.diagnosis?.state !== 'reported') {
      const diagnosing = await maybeDiagnose(client, env, { twin, pull, folder }, decision.diagnosis, report);
      await setAttention(client, pull.number, !diagnosing);
    }
    report.line(decision.diagnosis?.state === 'reported' ? 'Stopped; the diagnosis waits for a person\'s decision.' : 'Stopped and waiting for a person.');
    return finish('stop', common);
  }
  report.line(`Run: ${renderRunSubject(decision, { summaries: taskSummaries(decision.tasksMarkdown) })}.`);
  report.line();
  if (decision.action === 'merge') {
    const checkRun = await startMerge(client, { pull, attempt: decision.attempt });
    report.line(`Merging into \`${common.base_branch}\`, attempt ${decision.attempt}.`);
    return finish('merge', { ...common, attempt: String(decision.attempt), check_run: String(checkRun) });
  }
  if (decision.action === 'repair') {
    // A retried repair continues on its current base.
    if (decision.syncGivenUp) await reportSyncGivenUp(client, { decision, report });
    const entry = { task: null, attempt: decision.attempt, summary: `Repair the failed ${decision.repair.scope} verification.` };
    const checkRun = (await startAttemptChecks(client, { head: common.head, kind: 'repair', entries: [entry] })).repair;
    // The repair's work item, the single entry of the stage "implement" (Spec Kit implement task).
    const item = {
      index: 0,
      name: `Failed ${decision.repair.scope} verification`,
      task: '',
      attempt: decision.attempt,
      guidance: normalizeWriterGuidance(decision.guidance),
      repair: decision.repair,
    };
    report.line(`Repairing the failed ${decision.repair.scope} verification, attempt ${decision.attempt}.`);
    return finish('repair', {
      ...common,
      attempt: String(decision.attempt),
      check_run: String(checkRun),
      repair: JSON.stringify(decision.repair),
      // The order Spec Kit implement task lands the repair from: one entry without a task; and the work item.
      order: JSON.stringify([{ include: [{ task: null, attempt: decision.attempt }] }]),
      entry: JSON.stringify(item),
      kind: WORK_VERBS.repair,
      mode: 'sequential',
      max_parallel: '1',
      entries: JSON.stringify([item]),
      verify_only: String(decision.verifyOnly),
      job_name: item.name,
    });
  }
  if (decision.action === 'verify') {
    const entry = { task: null, attempt: decision.attempt, summary: `Verify ${phaseSubject(decision.phase)} after its last task.` };
    const checkRun = (await startAttemptChecks(client, { head: common.head, kind: 'verify', entries: [entry] })).verify;
    report.line(`Verifying ${phaseSubject(decision.phase)}, attempt ${decision.attempt}.`);
    return finish('verify', {
      ...common,
      phase: decision.phase ?? '',
      tasks: JSON.stringify(decision.tasks),
      attempt: String(decision.attempt),
      check_run: String(checkRun),
      job_name: `Verify ${phaseSubject(decision.phase)}`,
    });
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
  if (decision.syncGivenUp) await reportSyncGivenUp(client, { decision, report });

  // A group of tasks, or the convergence or a conflict resolution as a sequential group of one. A sequential group runs
  // one matrix job per entry of its order, one at a time; a parallel group one matrix job per task of its single entry.
  const order = prepareOrder(decision);
  await startAttemptChecks(client, { head: common.head, kind: decision.kind, entries: order[0].include });
  const mode = decision.kind === 'task' ? decision.mode : 'sequential';
  const entries = mode === 'parallel'
    ? order[0].include.map((entry) => ({ ...entry, index: 0, name: entry.task, defer_progress: true }))
    : order.map((entry, index) => ({ ...entry.include[0], index, name: entry.include[0].task || taskSubject(decision.kind, entry.include) }));
  const subject = renderRunSubject(decision, { summaries: taskSummaries(decision.tasksMarkdown) });
  reportOrder(report, decision.kind, order);
  if (decision.kind === 'task' && decision.completesPhase) report.line(`- Then ${phaseSubject(decision.phase)} is verified in the next run.`);
  return finish('group', {
    ...common,
    kind: WORK_VERBS[decision.kind],
    mode,
    max_parallel: String(mode === 'parallel' ? MAX_PARALLEL_TASKS : 1),
    order: JSON.stringify(order),
    entries: JSON.stringify(entries),
    phase: decision.phase ?? '',
    job_name: subject,
  });
}

// The summarized text of every task (summarizeTask), for run names.
function taskSummaries(tasksMarkdown) {
  return new Map(listTasks(tasksMarkdown ?? '').map((task) => [task.id, neutralizeMarkers(summarizeTask(task.text))]));
}

// What the next run of a twin's chain will work on, decided without changing anything: its subject for the run name
// (renderRunSubject) and the workflow of its kind (implementWorkflowFor); Spec Kit implement decide when it cannot be told.
export async function predictNextRun(client, env, { twin, pull }) {
  try {
    const folder = resolveTwins([await client.getIssue(Number(twin))]).byFolder.keys().next().value;
    if (!folder) return { subject: '', workflow: IMPLEMENT_WORKFLOW_FILE };
    const decision = await decideForPull(client, { twin: Number(twin), folder, pullNumber: Number(pull), context: contextFromEnv(env), dryRun: true });
    return { subject: renderRunSubject(decision, { summaries: taskSummaries(decision.tasksMarkdown) }), workflow: implementWorkflowFor(decision.action) };
  } catch {
    return { subject: '', workflow: IMPLEMENT_WORKFLOW_FILE };
  }
}

// The subject of the next run (predictNextRun).
export async function predictRunSubject(client, env, options) {
  return (await predictNextRun(client, env, options)).subject;
}

// Spec Kit implement spec, job "sync" ("Merge the base branch"; write access to contents, check runs, and pull
// requests; never runs agent code). It runs first in every run of the chain (pull model): when the next work is a phase
// that has not started, the convergence, or the merge, it merges the base
// branch into the implementation branch (integrateBase). A conflict is recorded on the head, so the decide job of the
// same run starts the conflict resolution with Copilot CLI.
export async function runSync({ client, env, log, now = Date.now }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  report.line(`## ${renderImplementTitle('spec', twin, 'Merge the base branch')}`);
  report.line();
  const finish = (result) => {
    report.flush();
    setOutput(env, 'result', result);
    return { exitCode: 0, result };
  };
  const folder = resolveTwins([await client.getIssue(twin)]).byFolder.keys().next().value;
  const listed = await client.getPullRequest(pullNumber);
  if (!folder || listed.state !== 'open' || listed.head?.ref !== implementationBranch(folder)) {
    report.line('Nothing to merge: no open implementation pull request.');
    return finish('none');
  }
  const decision = await decideForPull(client, { twin, folder, pullNumber, now: now(), context: contextFromEnv(env) });
  const result = await integrateBase(client, env, { decision, folder, report });
  if (result === 'none') report.line('Nothing to merge: the branch is up to date, or the next work does not merge the base branch.');
  return finish(result);
}

// Spec Kit implement verify, job "Record the phase verification" (write access to check runs and pull requests; never runs
// agent code): records the verification of a finished phase (SPECKIT_VERIFY_RESULT is the verify job's result). A pass
// is recorded as a successful verify attempt of the phase, so the chain moves on; a failure stops the implementation
// for a person with a pending repair of the phase (or of its only task), like a failed task verification.
export async function runVerifyReport({ client, env, log }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  const folder = String(env.SPECKIT_FOLDER ?? '');
  const phase = String(env.SPECKIT_PHASE ?? '');
  const attempt = Number(env.SPECKIT_ATTEMPT);
  const checkRun = Number(env.SPECKIT_CHECK_RUN);
  if (!Number.isInteger(attempt) || attempt <= 0 || !Number.isInteger(checkRun) || checkRun <= 0) throw new SpecUsageError('Expected SPECKIT_ATTEMPT and SPECKIT_CHECK_RUN as positive integers.');
  let tasks = [];
  try {
    tasks = JSON.parse(String(env.SPECKIT_TASKS || '[]')).filter((task) => /^T\d{3,}$/.test(task));
  } catch {
    // The repair still records the failed phase when its tasks are unavailable.
  }
  const pull = await client.getPullRequest(pullNumber);
  const head = String(env.SPECKIT_HEAD || pull.head.sha);
  const subject = phaseSubject(phase);
  const flaky = renderFlakyLines(env.SPECKIT_VERIFY_FLAKY);
  const result = String(env.SPECKIT_VERIFY_RESULT ?? '');
  if (result === 'success') {
    await client.updateCheckRun(checkRun, { status: 'completed', conclusion: 'success', output: { title: `${subject} verified`, summary: `The full verification of ${subject} passed on ${head}.` } });
    await client.createComment(pullNumber, [
      renderAttemptMarker({ kind: 'verify', attempt, outcome: 'success', phase: phase || null }),
      `**The phase verification passed** for ${neutralizeMarkers(subject)} on \`${head}\`.`,
      ...(flaky.length > 0 ? ['', ...flaky] : []),
    ].join('\n'));
    report.line(`${subject} verified.`);
    report.flush();
    return { exitCode: 0, verified: true };
  }
  const requester = (await resolveImplementRequester(client, twin))?.login ?? null;
  const run = env.GITHUB_RUN_ID ? `${contextFromEnv(env).serverUrl}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : null;
  const scope = tasks.length === 1 ? 'task' : 'phase';
  await client.updateCheckRun(checkRun, {
    status: 'completed',
    conclusion: 'failure',
    external_id: CHECK_LIMIT,
    output: { title: 'The phase verification failed', summary: `The full verification of ${subject} ${result === 'failure' ? 'failed' : `did not finish (${result || 'unknown'})`}, although each task passed its own verification.` },
  });
  await client.createComment(pullNumber, [
    renderAttemptMarker({ kind: 'verify', attempt, outcome: 'failure', phase: phase || null }),
    renderRepairMarker({
      id: `phase:${env.GITHUB_RUN_ID || 'run'}:${head}`,
      state: 'pending',
      scope,
      phase,
      tasks,
      head,
      run: String(env.GITHUB_RUN_ID || ''),
      artifact: 'speckit-phase-verification',
      at: new Date().toISOString(),
    }),
    `**Implementation needs attention**${requester ? ` @${requester}` : ''}: the full verification of ${neutralizeMarkers(subject)} failed${run ? ` (see the [run](${run}))` : ''}. Each task passed its own verification, which covers only the tests it touched, so a change of the phase breaks tests elsewhere, or the tasks break something together.`,
    ...(flaky.length > 0 ? ['', ...flaky] : []),
    '',
    ...renderNextSteps({
      diagnosisUrl: diagnosisRunsUrl(env),
      ...(await nextStepContext(client, pull, folder || null)),
      continuation: { repair: { scope } },
    }),
  ].join('\n'));
  report.line(`The verification of ${subject} failed; the implementation stops for a person.`);
  report.flush();
  return { exitCode: 0, verified: false };
}

// Every workflow of the chain, job "Start the next run": dispatches the next run of the chain, in the workflow of its
// kind.
export async function runContinue({ client, env, log }) {
  const report = createReporter(env, log);
  const { twin, pull: pullNumber } = readSpecInputs(env);
  const pull = await client.getPullRequest(pullNumber);
  const title = specTitleOfPull(pull);
  await dispatchImplementation(client, env, { twin, pull: pullNumber, title });
  report.line(`Started the next run: ${renderImplementRunName(twin, title)}.`);
  report.flush();
  return { exitCode: 0 };
}

export async function main(argv, { env = process.env, log = console.log, client } = {}) {
  const [command] = argv;
  const handlers = { decide: runDecide, sync: runSync, 'verify-report': runVerifyReport, continue: runContinue };
  if (!handlers[command]) throw new SpecUsageError('Usage: speckit-spec.mjs <decide | sync | verify-report | continue> (inputs come from SPECKIT_* environment variables)');
  return (await handlers[command]({ client: client ?? createGitHubClient(env), env, log })).exitCode;
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
