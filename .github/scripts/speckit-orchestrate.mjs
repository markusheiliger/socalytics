import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  IMPLEMENT_STAGE,
  IMPLEMENTING_STAGE,
  TWIN_LABEL,
  buildSpecEntry,
  deriveStage,
  hasLabel,
  implementBlockers,
  isImplementRequested,
  isValidFolderName,
  requesterBlockers,
  resolveTwins,
  stageLabel,
  stageLabelChange,
  specDir,
} from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { discoverSpecs, readSpecFolder, resolveImplementRequester, updateIssueWithLabels } from './speckit-prepare.mjs';
import { maintainAmendment } from './speckit-amend.mjs';
import { maintainBacklog } from './speckit-backlog.mjs';
import { maintainFixSpec, maintainIndependentSpecs, startFixAuthoring } from './speckit-fix.mjs';
import { contextFromEnv, decideForPull, needsRun } from './speckit-spec.mjs';
import { pointPullRequestLinks, twinLinksAfterMerge } from './speckit-merge.mjs';
import { createGitHubClient, createReporter, setOutput } from './speckit-script-runtime.mjs';
import {
  BLOCKED_LABEL,
  BOT_LOGIN,
  ROADBLOCK_LABEL,
  ROADBLOCK_MARKER,
  BRANCH_PREFIX,
  CHECK_PROGRESS,
  CHECK_RUN_NAME,
  IMPLEMENT_CHAIN_WORKFLOW_FILES,
  LEGACY_IMPLEMENT_WORKFLOW_FILES,
  ORCHESTRATE_WORKFLOW_FILE,
  START_COMMENT_MARKER,
  baseBranchOf,
  checkRunOutput,
  decideLifecycle,
  defaultBranchOf,
  dispatchImplementation,
  maintainDependencyBlock,
  maintainCopilotHandoff,
  extractTasks,
  implementationBranch,
  isImplementRunOf,
  kindLabel,
  neutralizeMarkers,
  parseFlakyMarkers,
  parseFlakySpecMarkers,
  parseMaxActiveSpecs,
  renderFlakySpecMarker,
  renderPullRequestBody,
  renderPullRequestTitle,
  readSpecTasks,
  renderStartComment,
  setAttention,
  setBlocked,
  specTitleOfPull,
} from './speckit-implement-core.mjs';
export class UsageError extends Error {}

function openBlockers(blockers) {
  return blockers.filter((blocker) => blocker.state === 'open');
}

function blockerList(blockers) {
  return blockers.map((blocker) => `#${blocker.number}`).join(', ');
}

// Server side: lists flagged twins that are consistent with the default branch and have no open blockers,
// and resolves what their implementation PRs mean (in progress, closed without merge, or ready to start).
// Which implementations in progress wait for each twin, directly or through a chain of blocked implementations
// (A waits for B, whose implementation waits for C: C unblocks both). `inProgress` entries carry their twin `number`
// and their open `blockers` (twin numbers). Returns a map from a twin to the set of waiting twin numbers.
export function waitingSets(inProgress) {
  const waiting = new Map();
  for (const twin of inProgress) {
    for (const blocker of twin.blockers ?? []) waiting.set(blocker, [...(waiting.get(blocker) ?? []), twin.number]);
  }
  const sets = new Map();
  for (const blocker of waiting.keys()) {
    const seen = new Set();
    const queue = [...waiting.get(blocker)];
    while (queue.length > 0) {
      const next = queue.shift();
      if (seen.has(next) || next === blocker) continue;
      seen.add(next);
      queue.push(...(waiting.get(next) ?? []));
    }
    sets.set(blocker, seen);
  }
  return sets;
}

// How many implementations in progress wait for each twin (waitingSets).
export function waitingBehind(inProgress) {
  return new Map([...waitingSets(inProgress)].map(([twin, set]) => [twin, set.size]));
}

// How many pull requests carry ROADBLOCK_LABEL at most (repository variable SPECKIT_ROADBLOCK_PULLS, default 3; 0
// turns the label off).
export const DEFAULT_ROADBLOCK_PULLS = 3;
export function parseRoadblockPulls(value) {
  const text = String(value ?? '').trim();
  const number = Number(text);
  return text !== '' && Number.isInteger(number) && number >= 0 ? number : DEFAULT_ROADBLOCK_PULLS;
}

// Labels the implementations to process first: those that are not blocked themselves and have the most
// implementations waiting for them (waitingSets), at most SPECKIT_ROADBLOCK_PULLS, ties by the lower twin number. A
// pull request that gets the label also gets a comment that names the implementations waiting for it; the label is
// removed from every other open implementation pull request. Runs on every scheduler run, so the label follows the
// queue. Returns the summary lines.
export async function maintainRoadblocks(client, env, inProgress) {
  const limit = parseRoadblockPulls(env.SPECKIT_ROADBLOCK_PULLS);
  const sets = waitingSets(inProgress);
  const pullOf = new Map(inProgress.map((twin) => [twin.number, twin.pull]));
  const ranked = inProgress
    .filter((twin) => !twin.blocked && (sets.get(twin.number)?.size ?? 0) > 0)
    .sort((a, b) => sets.get(b.number).size - sets.get(a.number).size || a.number - b.number)
    .slice(0, limit);
  const chosen = new Map(ranked.map((twin) => [twin.pull, twin]));
  const lines = [];
  let pulls;
  try {
    pulls = (await client.listOpenPullRequests()).filter((pull) => String(pull.head?.ref ?? '').startsWith(BRANCH_PREFIX));
  } catch (error) {
    return [`- Warning: could not update the \`${ROADBLOCK_LABEL}\` label: ${error.message}`];
  }
  for (const pull of pulls) {
    const has = (pull.labels ?? []).some((label) => (label?.name ?? label) === ROADBLOCK_LABEL);
    const twin = chosen.get(pull.number);
    try {
      if (twin && !has) {
        const waiting = [...sets.get(twin.number)].sort((a, b) => (pullOf.get(a) ?? a) - (pullOf.get(b) ?? b));
        const refs = waiting.map((number) => (pullOf.has(number) ? `#${pullOf.get(number)} (spec #${number})` : `spec #${number}`));
        await client.ensureLabel({ name: ROADBLOCK_LABEL, color: '0e8a16', description: 'Merging it unblocks other implementations; process it first' });
        await client.addLabels(pull.number, [ROADBLOCK_LABEL]);
        await client.createComment(pull.number, [
          ROADBLOCK_MARKER,
          `**Roadblock**: ${waiting.length} implementation${waiting.length === 1 ? '' : 's'} wait${waiting.length === 1 ? 's' : ''} for this one, directly or through a chain: ${refs.join(', ')}. It is not blocked itself, so getting it merged first gains the most progress.`,
          '',
          `The label \`${ROADBLOCK_LABEL}\` follows the queue: the scheduler moves it to the ${limit === 1 ? 'implementation' : `${limit} implementations`} that unblock the most whenever the open implementations change.`,
        ].join('\n'));
        lines.push(`- Labelled #${pull.number} \`${ROADBLOCK_LABEL}\`: ${waiting.length} implementation${waiting.length === 1 ? '' : 's'} wait${waiting.length === 1 ? 's' : ''} for it.`);
      } else if (!twin && has) {
        await client.removeLabel(pull.number, ROADBLOCK_LABEL);
        lines.push(`- Removed \`${ROADBLOCK_LABEL}\` from #${pull.number}: others unblock more, it is blocked itself, or nothing waits for it any more.`);
      }
    } catch (error) {
      lines.push(`- Warning: could not update \`${ROADBLOCK_LABEL}\` on #${pull.number}: ${error.message}`);
    }
  }
  return lines;
}

export async function runSelect({ client, rootDir, env, log, now = Date.now }) {
  const report = createReporter(env, log);
  const specs = discoverSpecs(rootDir);
  const { byFolder } = resolveTwins(await client.listTwinIssues(TWIN_LABEL));
  const ready = [];
  const blocked = [];
  const inconsistent = [];
  const inProgress = [];
  const fallback = [];
  const merged = [];
  const amendmentNotes = [];
  let amendmentActions = 0;
  for (const [folder, issue] of [...byFolder.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    const open = issue.state === 'open';
    const entry = specs.get(folder);
    const computedStage = entry ? deriveStage(entry.artifacts, entry.tasks) : null;
    // Requested twins wait at `implement` and are `implementing` once their pull request is open. A closed twin counts
    // by its label alone, since its spec may already be archived.
    if (!isImplementRequested(issue, open ? computedStage : null)) continue;
    // A person who merges a pull request held for review closes the twin through "Closes #<twin>", which leaves
    // the flag and the branch behind; such twins are finalized here like an automatic merge.
    if (!open && issue.state_reason !== 'completed') continue;
    const requester = entry || !open ? await resolveImplementRequester(client, issue.number) : null;
    const lifecycle = requester
      ? decideLifecycle({ pulls: await client.listPullRequestsForHead(implementationBranch(folder)), flaggedAt: requester.labeledAt })
      : null;
    if (lifecycle?.state === 'merged') {
      merged.push({ number: issue.number, folder, pull: lifecycle.pull.number, ...(await finalizeMerged(client, env, issue, folder, lifecycle.pull)) });
      continue;
    }
    if (!open) continue;
    const reasons = entry
      ? [
        ...requesterBlockers(requester),
        ...implementBlockers({ folder, computedStage, openChecklistItems: entry.openChecklistItems }),
      ]
      : [`\`specs/${folder}\` does not exist on the default branch`];
    if (reasons.length > 0) {
      inconsistent.push({ number: issue.number, folder, reasons });
      continue;
    }

    if (lifecycle.state === 'in-progress') {
      // The implementation pull request is open, so the twin is at `implementing` (also when a person opened it or
      // set the flag again).
      await markImplementing(client, issue);
      // Amendments first: a merged one retries the implementation, which the decision must see.
      try {
        const amendment = await maintainAmendment(client, env, { folder, implementation: lifecycle.pull, twin: issue.number }, { now: now(), report: (line) => amendmentNotes.push(line) });
        if (amendment.outcome === 'rework' || amendment.outcome === 'checking') amendmentActions += 1;
      } catch (error) {
        amendmentNotes.push(`- Warning: could not check the amendment of #${issue.number}: ${error.message}`);
      }
      // Prerequisites: a spec authored by `/speckit author` holds the implementation until the twin depends on it;
      // open dependencies of the twin block it, and once none is open, the implementation retries.
      const { blocked: isBlocked, blockers } = await maintainBlock(client, env, { implementation: lifecycle.pull, twin: issue.number, rootDir }, { now: now(), notes: amendmentNotes, count: () => { amendmentActions += 1; } });
      const decision = await decideForPull(client, { twin: issue.number, folder, pullNumber: lifecycle.pull.number, now: now(), context: contextFromEnv(env) });
      const active = await chainActive(client, issue.number, lifecycle.pull.created_at);
      // A handoff to Copilot that ended without a push asks for help again.
      try {
        const ended = await maintainCopilotHandoff(client, {
          pull: lifecycle.pull,
          comments: decision.comments ?? [],
          pushedAt: decision.pushedAt ?? null,
          active,
          continues: needsRun(decision, env),
          blocked: isBlocked,
          now: now(),
        });
        if (ended) amendmentNotes.push(`- The handoff of #${issue.number} to Copilot ended without a push (${ended}); it asks for help again.`);
      } catch (error) {
        amendmentNotes.push(`- Warning: could not check the Copilot handoff of #${issue.number}: ${error.message}`);
      }
      inProgress.push({ number: issue.number, folder, pull: lifecycle.pull.number, requester: requester.login, decision, active, blocked: isBlocked, blockers });
      continue;
    }
    if (lifecycle.state === 'fallback') {
      await setAttention(client, lifecycle.pull.number, false);
      await setBlocked(client, lifecycle.pull.number, false);
      await updateIssueWithLabels(client, issue, stageLabelChange(issue, computedStage));
      await client.createComment(issue.number, [
        `Implementation pull request #${lifecycle.pull.number} was closed without merging, so the implementation request was removed.`,
        '',
        `The twin is back at its computed stage \`${computedStage}\`. Comment \`/speckit implement\` on this issue to restart; the branch \`${implementationBranch(folder)}\` is then reset.`,
      ].join('\n'));
      fallback.push({ number: issue.number, folder, pull: lifecycle.pull.number });
      continue;
    }

    const openBlocking = openBlockers(await client.listBlockedBy(issue.number));
    if (openBlocking.length > 0) blocked.push({ number: issue.number, folder, blockers: openBlocking });
    else ready.push({ number: issue.number, folder, requester: requester.login, reset: lifecycle.reset, prerequisite: false });
  }
  const unblocked = await sweepBlocked(client, env, { handled: new Set(inProgress.map((twin) => twin.pull)), now: now(), notes: amendmentNotes, rootDir });
  amendmentActions += unblocked;
  amendmentActions += await maintainIndependentSpecs(client, env, { now: now(), report: (line) => amendmentNotes.push(line) });
  // Requested prerequisites of running implementations first, however the dependency came about: each unblocks an
  // implementation that waits for it. A prerequisite that is not requested never starts; the Blocked comment on the
  // waiting implementation recommends `/speckit implement` on its twin.
  // The one that unblocks the most waiting implementations, also through chains of blocked implementations, starts first.
  const unblocks = waitingBehind(inProgress);
  for (const twin of ready) {
    twin.unblocks = unblocks.get(twin.number) ?? 0;
    twin.prerequisite = twin.unblocks > 0;
  }
  ready.sort((a, b) => b.unblocks - a.unblocks);

  report.line('## Spec implementation selection');
  report.line();
  for (const twin of ready) report.line(`- Ready: #${twin.number} \`${twin.folder}\`${twin.reset ? ' (the branch is reset)' : ''}${twin.unblocks > 0 ? ` (unblocks ${twin.unblocks} waiting implementation${twin.unblocks === 1 ? '' : 's'})` : ''}`);
  for (const twin of inProgress) {
    report.line(`- In progress: #${twin.number} \`${twin.folder}\` in pull request #${twin.pull}: ${describeDecision(twin)}`);
  }
  for (const twin of blocked) report.line(`- Blocked: #${twin.number} \`${twin.folder}\` by ${blockerList(twin.blockers)}`);
  for (const twin of fallback) {
    report.line(`- Flag removed: #${twin.number} \`${twin.folder}\` because pull request #${twin.pull} was closed without merging`);
  }
  for (const twin of merged) {
    report.line(`- Finalized: #${twin.number} \`${twin.folder}\` merged in pull request #${twin.pull}; the twin is \`${stageLabel('implemented')}\`${twin.branchDeleted ? ' and the branch was deleted' : ''}${twin.note ? ` (${twin.note})` : ''}`);
  }
  for (const twin of inconsistent) {
    report.line(`- Inconsistent (the next sync revokes the flag): #${twin.number} \`${twin.folder}\`: ${twin.reasons.join('; ')}`);
  }
  for (const line of amendmentNotes) report.line(line);
  const total = ready.length + inProgress.length + blocked.length + fallback.length + merged.length + inconsistent.length;
  if (total === 0) report.line('- No spec twin is flagged for implementation.');
  report.flush();
  setOutput(env, 'count', String(ready.length));
  setOutput(env, 'matrix', JSON.stringify({ include: ready }));
  return { exitCode: 0, ready, blocked, inconsistent, inProgress, fallback, merged, amendmentActions };
}

// Maintains what holds an open implementation pull request: the spec `/speckit author` authored for it (maintainFixSpec)
// and the open dependencies of its twin (maintainDependencyBlock). Returns whether it is blocked and by which twins.
async function maintainBlock(client, env, { implementation, twin, rootDir = null }, { now, notes, count }) {
  const report = (line) => notes.push(line);
  let hold = false;
  try {
    const fix = await maintainFixSpec(client, env, { implementation, twin, rootDir }, { now, report });
    if (['rework', 'checking', 'linked'].includes(fix.outcome)) count();
    hold = fix.blocked === true;
  } catch (error) {
    notes.push(`- Warning: could not check the prerequisite spec of #${implementation.number}: ${error.message}`);
  }
  try {
    const dependency = await maintainDependencyBlock(client, env, { implementation, twin, hold }, { report });
    if (['retried', 'stopped'].includes(dependency.outcome)) count();
    return { blocked: dependency.blocked, blockers: dependency.blockers, outcome: dependency.outcome };
  } catch (error) {
    notes.push(`- Warning: could not check the prerequisites of #${implementation.number}: ${error.message}`);
    return { blocked: hold, blockers: [], outcome: 'unknown' };
  }
}

// Every open implementation pull request labelled BLOCKED_LABEL that the twin loop did not handle (for example because
// its twin is inconsistent) is maintained as well, so an implemented prerequisite always lifts the block and retries
// it. Returns the number of actions taken.
async function sweepBlocked(client, env, { handled, now, notes, rootDir = null }) {
  let actions = 0;
  let pulls;
  try {
    pulls = await client.listOpenPullRequests();
  } catch (error) {
    notes.push(`- Warning: could not list the blocked implementations: ${error.message}`);
    return 0;
  }
  for (const pull of pulls) {
    if (handled.has(pull.number) || !String(pull.head?.ref ?? '').startsWith('speckit/')) continue;
    if (pull.head?.repo?.full_name && pull.base?.repo?.full_name && pull.head.repo.full_name !== pull.base.repo.full_name) continue;
    if (!(pull.labels ?? []).some((label) => (typeof label === 'string' ? label : label.name) === BLOCKED_LABEL)) continue;
    const twin = Number(String(pull.body ?? '').match(/^Closes #(\d+)$/m)?.[1]);
    if (!twin) continue;
    const result = await maintainBlock(client, env, { implementation: pull, twin, rootDir }, { now, notes, count: () => { actions += 1; } });
    if (!result.blocked) notes.push(`- Lifted the block of #${pull.number} (${result.outcome}).`);
  }
  return actions;
}

async function findOpenPull(client, branch) {
  return (await client.listPullRequestsForHead(branch)).find((pull) => pull.state === 'open') ?? null;
}

// Moves a requested twin from `implement` to `implementing` once its implementation pull request is open. The label
// history keeps who requested it and when (resolveImplementRequester).
async function markImplementing(client, issue) {
  const change = stageLabelChange(issue, IMPLEMENTING_STAGE);
  if (change.add.length > 0 || change.remove.length > 0) await updateIssueWithLabels(client, issue, change);
}

// Completes what the merge does after an automatic merge for a pull request that a person merged:
// marks the twin implemented (closing it if needed) and deletes the implementation branch.
async function finalizeMerged(client, env, issue, folder, pull) {
  const pullNumber = pull.number;
  const baseBranch = baseBranchOf(pull);
  await setAttention(client, pullNumber, false);
  await setBlocked(client, pullNumber, false);
  const { archived } = await readSpecTasks(client, folder, pull.merge_commit_sha || baseBranch);
  await updateIssueWithLabels(client, issue, stageLabelChange(issue, 'implemented'), {
    ...(issue.state === 'open' ? { state: 'closed', state_reason: 'completed' } : {}),
    ...twinLinksAfterMerge(issue, folder, { baseBranch, defaultBranch: defaultBranchOf(env), archived }),
  });
  await pointPullRequestLinks(client, pull, folder, { baseBranch, archived });
  const branch = implementationBranch(folder);
  let branchDeleted = false;
  let note = null;
  try {
    if (await client.getBranchSha(branch)) {
      await client.deleteBranch(branch);
      branchDeleted = true;
    }
  } catch (error) {
    note = `the branch \`${branch}\` could not be deleted: ${error.message}`;
  }
  await client.createComment(issue.number, [
    `Implementation pull request #${pullNumber} was merged into \`${baseBranchOf(pull)}\`, so the twin is now \`${stageLabel('implemented')}\`${branchDeleted ? ` and the branch \`${branch}\` was deleted` : ''}.`,
    ...(note ? ['', `Note: ${note}.`] : []),
  ].join('\n'));
  return { branchDeleted, note };
}

function describeDecision(twin) {
  const { decision } = twin;
  if (twin.active) return 'its implementation run is active';
  if (twin.blocked) return `blocked; it waits for ${twin.blockers.length > 0 ? `its prerequisites ${twin.blockers.map((number) => `#${number}`).join(', ')}` : 'its prerequisite spec'}`;
  switch (decision.action) {
    case 'wait': return 'a merge is running';
    case 'done': return 'implemented and waiting for review';
    case 'diagnosing': return 'stopped; a diagnosis is running';
    case 'failed': return decision.diagnosis?.state === 'reported'
      ? 'stopped; the diagnosis waits for a person\'s decision'
      : 'stopped while its automatic diagnosis determines the next step';
    case 'limit': return `${kindLabel(decision)} reached the attempt limit`;
    case 'tasks': return `next is ${decision.order[0].map(({ task, attempt }) => `${kindLabel({ kind: decision.kind, task })} attempt ${attempt}`).join(', ')}`;
    case 'merge': return `next is the merge, attempt ${decision.attempt}`;
    default: return decision.action;
  }
}

// Whether a run of the twin's implementation chain is queued or running, also one of the chain before groups that
// started before it was replaced (LEGACY_IMPLEMENT_WORKFLOW_FILES).
async function chainActive(client, twin, since) {
  for (const file of [...IMPLEMENT_CHAIN_WORKFLOW_FILES, ...LEGACY_IMPLEMENT_WORKFLOW_FILES]) {
    let runs = [];
    try {
      runs = await client.listWorkflowRuns(file, since);
    } catch {
      // A removed workflow file may not be listed any more.
    }
    if (runs.some((run) => run.status !== 'completed' && isImplementRunOf(run.display_title, twin))) return true;
  }
  return false;
}

// Scheduler: selects flagged twins, starts new implementations while fewer than SPECKIT_MAX_ACTIVE_SPECS are active,
// and restarts the implementation chain of a twin in progress when no run of it is active although there is something
// to do (the chain broke, a person pushed, or a merged amendment or a command asked to continue). Each implementation
// is driven by its own chain of Spec Kit implement spec runs; the scheduler never drives tasks itself.
export async function runOrchestrate({ client, rootDir, env, log, now = Date.now }) {
  const selection = await runSelect({ client, rootDir, env, log, now });
  const report = createReporter(env, log);
  report.line();
  report.line('### Actions');
  report.line();
  let actions = selection.amendmentActions ?? 0;
  const maxActive = parseMaxActiveSpecs(env.SPECKIT_MAX_ACTIVE_SPECS);
  // A blocked implementation is not continued: it waits for its prerequisites and retries once they are implemented. It
  // still holds its slot, so the scheduler starts the prerequisites that unblock it (they start even at the limit)
  // instead of new specs that would most likely run into the same problems.
  const continues = (twin) => !twin.active && !twin.blocked && needsRun(twin.decision, env);
  let active = selection.inProgress.filter((twin) => twin.active || twin.blocked || continues(twin)).length;
  for (const twin of selection.inProgress) {
    if (!continues(twin)) continue;
    await dispatchImplementation(client, env, { twin: twin.number, pull: twin.pull });
    report.line(`- Continued #${twin.number} (pull request #${twin.pull}): ${describeDecision(twin)}.`);
    actions += 1;
  }
  for (const twin of selection.ready) {
    // A prerequisite of a running implementation starts even at the limit: the implementation it unblocks holds a slot
    // while it waits.
    if (active >= maxActive && !twin.prerequisite) {
      report.line(`- Waiting: #${twin.number} \`${twin.folder}\` starts when fewer than ${maxActive} implementations are active.`);
      continue;
    }
    const started = await runStart({ client, rootDir, env, issueNumber: twin.number, folder: twin.folder, requester: twin.requester, reset: twin.reset, log });
    if (!started.pull) continue;
    await dispatchImplementation(client, env, { twin: twin.number, pull: started.pull.number, title: specTitleOfPull(started.pull) });
    report.line(`- Started the implementation of #${twin.number} (pull request #${started.pull.number}).`);
    active += 1;
    actions += 1;
  }
  if (actions === 0) report.line('- Nothing to do.');
  const roadblocks = await maintainRoadblocks(client, env, selection.inProgress);
  if (roadblocks.length > 0) {
    report.line();
    report.line(`### \`${ROADBLOCK_LABEL}\` label`);
    report.line();
    for (const line of roadblocks) report.line(line);
  }
  const flaky = await maintainFlakyTests(client, env, { now: now() });
  if (flaky.lines.length > 0) {
    report.line();
    report.line('### Flaky tests');
    report.line();
    for (const line of flaky.lines) report.line(line);
  }
  const backlog = await maintainBacklog(client, env, { rootDir, now: now() });
  if (backlog.lines.length > 0) {
    report.line();
    report.line('### Spec backlog');
    report.line();
    for (const line of backlog.lines) report.line(line);
  }
  report.flush();
  return { exitCode: 0, selection, flaky, backlog };
}

// Flaky-test ledger: a test that passed only on its rerun (the flaky markers of verification comments) in at least
// SPECKIT_FLAKY_THRESHOLD (default 3) distinct implementation pull requests (the open ones and those merged in the last
// FLAKY_WINDOW_MS) gets one spec that makes it deterministic. It is started with `/speckit author`'s authoring entry on
// the open implementation pull request where the test was flaky most recently, without blocking that implementation
// (`link: false`), and recorded there with a flaky-spec marker, so the test is never started again while that marker
// is in the window. Returns the summary lines and the tests it started specs for.
export const FLAKY_WINDOW_MS = 14 * 24 * 60 * 60 * 1000;
export const DEFAULT_FLAKY_THRESHOLD = 3;
export function parseFlakyThreshold(value) {
  const number = Number(String(value ?? '').trim());
  return Number.isInteger(number) && number > 0 ? number : DEFAULT_FLAKY_THRESHOLD;
}

const isImplementationPull = (pull) => String(pull.head?.ref ?? '').startsWith(BRANCH_PREFIX) &&
  !(pull.head?.repo?.full_name && pull.base?.repo?.full_name && pull.head.repo.full_name !== pull.base.repo.full_name);

export async function maintainFlakyTests(client, env, { now = Date.now() } = {}) {
  const lines = [];
  const started = [];
  const threshold = parseFlakyThreshold(env.SPECKIT_FLAKY_THRESHOLD);
  const since = new Date(now - FLAKY_WINDOW_MS).toISOString();
  let pulls;
  try {
    const open = (await client.listOpenPullRequests()).filter(isImplementationPull);
    const merged = (await client.listClosedPullRequestsSince(since))
      .filter((pull) => isImplementationPull(pull) && pull.merged_at && Date.parse(pull.merged_at) >= Date.parse(since));
    pulls = [...open, ...merged.filter((pull) => !open.some((item) => item.number === pull.number))];
  } catch (error) {
    return { lines: [`- Warning: could not read the flaky tests: ${error.message}`], started };
  }
  const seen = new Map();
  const covered = new Set();
  for (const pull of pulls) {
    const comments = await client.listIssueComments(pull.number);
    for (const test of parseFlakySpecMarkers(comments)) covered.add(test);
    for (const { test, at } of parseFlakyMarkers(comments)) {
      const byPull = seen.get(test) ?? new Map();
      const previous = byPull.get(pull.number);
      if (!previous || Date.parse(at) > Date.parse(previous.at)) byPull.set(pull.number, { pull, at });
      seen.set(test, byPull);
    }
  }
  const candidates = [...seen.entries()]
    .filter(([test, byPull]) => byPull.size >= threshold && !covered.has(test))
    .sort(([a], [b]) => a.localeCompare(b));
  const busy = new Set();
  for (const [test, byPull] of candidates) {
    const occurrences = [...byPull.values()].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
    const numbers = occurrences.map((item) => `#${item.pull.number}`).sort((a, b) => Number(a.slice(1)) - Number(b.slice(1)));
    const target = occurrences.find((item) => item.pull.state === 'open')?.pull;
    const name = `\`${neutralizeMarkers(test).replaceAll('`', "'")}\``;
    if (!target) {
      lines.push(`- ${name} was flaky in ${numbers.join(', ')}; no spec started: none of them is open.`);
      continue;
    }
    if (busy.has(target.number)) {
      lines.push(`- ${name} was flaky in ${numbers.join(', ')}; its spec starts on #${target.number} in a later run.`);
      continue;
    }
    busy.add(target.number);
    const twin = Number(String(target.body ?? '').match(/^Closes #(\d+)$/m)?.[1]);
    if (!Number.isInteger(twin) || twin <= 0) {
      lines.push(`- ${name} was flaky in ${numbers.join(', ')}; no spec started: #${target.number} names no twin.`);
      continue;
    }
    const implementation = await client.getPullRequest(target.number);
    const actor = (await resolveImplementRequester(client, twin))?.login ?? implementation.user?.login ?? BOT_LOGIN;
    const guidance = [
      `The test \`${test}\` failed and then passed on its one rerun in the verifications of ${occurrences.length} implementation pull requests (${numbers.join(', ')}), so it is flaky.`,
      'Make it deterministic: find and remove the cause of the flakiness (timing, ordering, shared state, external dependencies) in the test or in the code it covers.',
      'Do not weaken, skip, or delete the test or its assertions, and do not add retries or longer timeouts as the fix.',
    ].join(' ');
    let result;
    try {
      result = await startFixAuthoring(client, env, { twin, implementation, guidance, actor, now, link: false });
    } catch (error) {
      result = { started: false, reason: error.message };
    }
    if (!result.started) {
      lines.push(`- ${name} was flaky in ${numbers.join(', ')}; the spec could not start on #${target.number}: ${neutralizeMarkers(result.reason)}.`);
      continue;
    }
    await client.createComment(target.number, [
      renderFlakySpecMarker(test),
      `**Flaky test** ${name} passed only on its rerun in ${occurrences.length} implementations (${numbers.join(', ')}), so a spec that makes it deterministic is being authored from here. It does not block this implementation.`,
    ].join('\n'));
    started.push({ test, pull: target.number, pulls: occurrences.map((item) => item.pull.number) });
    lines.push(`- ${name} was flaky in ${numbers.join(', ')}; started a spec to make it deterministic on #${target.number}.`);
  }
  return { lines, started };
}
// Server side: prepares the implementation workspace of one ready twin. Each part checks what already exists,
// so a rerun after a partial failure completes the work instead of duplicating it.
export async function runStart({ client, rootDir, env, issueNumber, folder, requester, reset, log }) {
  const report = createReporter(env, log);
  report.line(`## Implementation workspace for #${issueNumber}`);
  report.line();
  const branch = implementationBranch(folder);
  // Implementations start from the default branch, whose specs are mirrored as twins; it becomes the pull request's
  // base, which every later merge and comparison of this implementation uses.
  const baseBranch = defaultBranchOf(env);
  const issue = await client.getIssue(issueNumber);
  if (issue.state !== 'open' || !isImplementRequested(issue)) {
    report.line(`#${issueNumber} is no longer open and flagged; nothing to start.`);
    report.flush();
    return { exitCode: 0 };
  }
  const entry = readSpecFolder(rootDir, folder);
  if (!entry || entry.tasks === null) throw new Error(`specs/${folder} has no tasks.md on the default branch`);
  const tasks = extractTasks(entry.tasks);

  let pull = await findOpenPull(client, branch);
  if (pull) {
    report.line(`- Pull request #${pull.number} is already open for \`${branch}\`.`);
  } else {
    const baseSha = await client.getBranchSha(baseBranch);
    if (reset && (await client.getBranchSha(branch))) {
      await client.deleteBranch(branch);
      report.line(`- Deleted the stale branch \`${branch}\`.`);
    }
    if (!(await client.getBranchSha(branch))) {
      try {
        await client.createLinkedBranch(issue.node_id, baseSha, branch);
        report.line(`- Created \`${branch}\`, linked to #${issueNumber}.`);
      } catch (error) {
        await client.createBranch(branch, baseSha);
        report.line(`- Created \`${branch}\` without an issue link (${error.message}).`);
      }
    }
    if ((await client.aheadBy(baseBranch, branch)) === 0) {
      const head = await client.getBranchSha(branch);
      const commit = await client.createEmptyCommit(head, `chore(speckit): start implementation of ${folder}`);
      await client.updateBranch(branch, commit);
      report.line('- Added the start commit.');
    }
    let createError = null;
    try {
      pull = await client.createPullRequest({
        title: renderPullRequestTitle(entry.spec),
        head: branch,
        base: baseBranch,
        body: renderPullRequestBody({ twinNumber: issueNumber, folder, tasks, context: { ...contextFromEnv(env), branch: baseBranch } }),
        draft: true,
      });
    } catch (error) {
      createError = error;
    }
    pull ??= await findOpenPull(client, branch);
    if (!pull) throw new Error(`Could not create or find the pull request for ${branch}${createError ? `: ${createError.message}` : ''}`);
    report.line(`- Opened draft pull request #${pull.number}.`);
  }
  await markImplementing(client, issue);

  try {
    await client.addAssignees(pull.number, [requester]);
  } catch (error) {
    report.line(`- Warning: could not assign @${requester}: ${error.message}`);
  }
  if ((await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME)).length === 0) {
    await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: pull.head.sha, status: 'queued', external_id: CHECK_PROGRESS, output: checkRunOutput(tasks.count) });
    report.line(`- Created the \`${CHECK_RUN_NAME}\` check run.`);
  }
  const comments = await client.listIssueComments(pull.number);
  if (!comments.some((comment) => comment.body?.includes(START_COMMENT_MARKER))) {
    await client.createComment(pull.number, renderStartComment({ twinNumber: issueNumber, folder, taskCount: tasks.count, base: baseBranchOf(pull) }));
    report.line('- Posted the start comment.');
  }
  report.flush();
  return { exitCode: 0, pull };
}

export function defaultGit(cwd) {
  return (args) => {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 20 * 1024 * 1024 });
    if (result.error) throw new UsageError(`git is required: ${result.error.message}`);
    return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  };
}

export function resolveFolder({ folder, env, rootDir }) {
  let candidate = folder || env.SPECIFY_FEATURE_DIRECTORY;
  if (!candidate) {
    const featureFile = path.join(rootDir, '.specify', 'feature.json');
    if (existsSync(featureFile)) {
      try {
        candidate = JSON.parse(readFileSync(featureFile, 'utf8')).feature_directory;
      } catch {
        throw new UsageError('.specify/feature.json is not valid JSON.');
      }
    }
  }
  if (!candidate) throw new UsageError('No feature folder given. Pass --folder <folder> or select a feature with /speckit-specify.');
  const name = path.basename(String(candidate).replaceAll('\\', '/').replace(/\/+$/, ''));
  if (!isValidFolderName(name)) throw new UsageError(`"${candidate}" is not a valid spec folder name.`);
  return name;
}

// Reads one spec folder from a remote branch, or returns null when it is not merged there.
export function readSpecFromRef(git, ref, folder) {
  const show = (file) => {
    const result = git(['show', `${ref}:specs/${folder}/${file}`]);
    return result.status === 0 ? result.stdout : null;
  };
  const specMarkdown = show('spec.md');
  if (specMarkdown === null) return null;
  const listing = git(['ls-tree', '-z', '--full-tree', '--name-only', ref, '--', `specs/${folder}/checklists/`]);
  const checklistFiles = listing.status === 0
    ? listing.stdout.split('\0').filter((file) => file.endsWith('.md'))
    : [];
  return buildSpecEntry(folder, {
    specMarkdown,
    hasPlan: show('plan.md') !== null,
    tasksMarkdown: show('tasks.md'),
    checklistMarkdowns: checklistFiles.map((file) => {
      const result = git(['show', `${ref}:${file}`]);
      return result.status === 0 ? result.stdout : '';
    }),
  });
}

// Client side: checks a spec against the remote default branch and requests its implementation with
// `/speckit implement` on its twin.
export async function runRequest({ client, git, rootDir, env, folder, log }) {
  const name = resolveFolder({ folder, env, rootDir });
  // Implementation is requested for specs on the repository's default branch, which GitHub defines.
  const branch = env.SPECKIT_DEFAULT_BRANCH || await client.getDefaultBranch();
  const fetch = git(['fetch', '--quiet', 'origin', branch]);
  if (fetch.status !== 0) throw new UsageError(`git fetch origin ${branch} failed: ${fetch.stderr.trim()}`);
  const ref = `origin/${branch}`;
  log(`Spec folder: specs/${name} (checked against ${ref})`);

  const entry = readSpecFromRef(git, ref, name);
  if (!entry) {
    if (git(['cat-file', '-e', `${ref}:${specDir(name, { archived: true })}/spec.md`]).status === 0) {
      log(`Not requested: ${specDir(name)} is already implemented and archived in ${specDir(name, { archived: true })}.`);
      return { exitCode: 1 };
    }
    log(`Not requested: specs/${name} is not merged to ${branch} yet. Merge the spec, plan, and tasks first.`);
    return { exitCode: 1 };
  }
  const computedStage = deriveStage(entry.artifacts, entry.tasks);
  const twin = resolveTwins(await client.listTwinIssues(TWIN_LABEL)).byFolder.get(name);
  if (!twin || twin.state !== 'open') {
    log(`Not requested: no open spec twin exists for specs/${name} yet. The Spec Kit prepare workflow creates it after the merge.`);
    return { exitCode: 1 };
  }
  const url = twin.html_url ?? `#${twin.number}`;
  const implementLabel = stageLabel(IMPLEMENT_STAGE);
  const reportBlockers = async () => {
    const open = openBlockers(await client.listBlockedBy(twin.number));
    log(open.length > 0
      ? `Open blockers: ${blockerList(open)}. The speckit-orchestrate workflow starts after they are closed.`
      : 'No open blockers: the speckit-orchestrate workflow can pick it up.');
  };

  if (isImplementRequested(twin, computedStage)) {
    log(`Already requested: ${url} carries ${hasLabel(twin, implementLabel) ? implementLabel : stageLabel(IMPLEMENTING_STAGE)}.`);
    await reportBlockers();
    return { exitCode: 0 };
  }
  const reasons = implementBlockers({ folder: name, computedStage, openChecklistItems: entry.openChecklistItems });
  if (reasons.length > 0) {
    log(`Not requested for ${url}:`);
    for (const reason of reasons) log(`- ${reason}`);
    return { exitCode: 1 };
  }
  // Implementation is requested with the comment command on the twin (with the user's token), so the request is
  // checked on GitHub and records who requested it.
  const comment = await client.createComment(twin.number, '/speckit implement');
  log(`Requested: posted \`/speckit implement\` on ${url}${comment?.html_url ? ` (${comment.html_url})` : ''}. The Spec Kit commands workflow checks it on GitHub, records you as the requester, and sets ${implementLabel}.`);
  await reportBlockers();
  return { exitCode: 0, comment: comment ?? null };
}

function parseArgs(argv) {
  const [command, ...rest] = argv;
  const options = { command };
  for (let index = 0; index < rest.length; index += 1) {
    const arg = rest[index];
    if (arg === '--folder') options.folder = rest[++index];
    else if (arg === '--issue') options.issue = Number(rest[++index]);
    else if (arg === '--requester') options.requester = rest[++index];
    else if (arg === '--reset') options.reset = rest[index + 1] === 'true' || rest[index + 1] === 'false' ? rest[++index] === 'true' : true;
    else throw new UsageError(`Unknown argument: ${arg}`);
  }
  return options;
}

function repositoryFromGit(git) {
  const result = git(['remote', 'get-url', 'origin']);
  const match = result.stdout.trim().match(/github\.com[:/]([^/]+\/[^/]+?)(?:\.git)?$/);
  if (!match) throw new UsageError('Could not determine the GitHub repository from the origin remote; set GITHUB_REPOSITORY.');
  return match[1];
}

function tokenFromGh() {
  const result = spawnSync('gh', ['auth', 'token'], { encoding: 'utf8', windowsHide: true });
  if (result.error || result.status !== 0 || !result.stdout.trim()) {
    throw new UsageError('No GitHub token available. Run `gh auth login` or set GH_TOKEN.');
  }
  return result.stdout.trim();
}

export async function main(argv, { env = process.env, rootDir = process.cwd(), log = console.log, client, git } = {}) {
  const options = parseArgs(argv);
  const actionsClient = () => client ?? createGitHubClient(env);
  if (options.command === 'select') {
    return (await runSelect({ client: actionsClient(), rootDir, env, log })).exitCode;
  }
  if (options.command === 'orchestrate') {
    return (await runOrchestrate({ client: actionsClient(), rootDir, env, log })).exitCode;
  }
  if (options.command === 'start') {
    if (!Number.isInteger(options.issue) || options.issue <= 0 || !isValidFolderName(options.folder ?? '') || !options.requester) {
      throw new UsageError('start requires --issue <number>, --folder <folder>, and --requester <login>');
    }
    return (await runStart({
      client: actionsClient(),
      rootDir,
      env,
      issueNumber: options.issue,
      folder: options.folder,
      requester: options.requester,
      reset: Boolean(options.reset),
      log,
    })).exitCode;
  }
  if (options.command === 'request') {
    const gitRunner = git ?? defaultGit(rootDir);
    const githubClient = client ?? new GitHubClient({
      token: env.GH_TOKEN || env.GITHUB_TOKEN || tokenFromGh(),
      repository: env.GITHUB_REPOSITORY || repositoryFromGit(gitRunner),
      apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
      graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
    });
    return (await runRequest({ client: githubClient, git: gitRunner, rootDir, env, folder: options.folder, log })).exitCode;
  }
  throw new UsageError('Usage: speckit-orchestrate.mjs <select | orchestrate | start --issue <number> --folder <folder> --requester <login> [--reset [true|false]] | request [--folder <folder>]>');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof UsageError ? 2 : 1;
    },
  );
}
