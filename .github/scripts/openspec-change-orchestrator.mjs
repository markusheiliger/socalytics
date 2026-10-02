import { execFileSync } from 'node:child_process';
import { appendFileSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  JSON_CONTRACTS,
  parseCapabilityDefinition,
  parseCapabilityTasks,
  parseChangeMarker,
  parseCheckpointTrailer,
  parseRunStateText,
  renderRunStateText,
  validateCapabilitySet,
  validateDispatch,
  validateSynchronizedDeltas,
} from './openspec-change-core.mjs';
import {
  authorizeCommand,
  isBotUser,
  parseCommand,
  pendingCommandComments,
} from './openspec-change-commands.mjs';
import { BRANCH_PREFIX, classifyEvent, isRunBranch } from './openspec-change-events.mjs';
import { GitHubChangeClient } from './openspec-change-github.mjs';
import {
  admittedEntry,
  closedEntry,
  commandEntry,
  findLogComment,
  gateEntry,
  humanPushEntry,
  isOverviewComment,
  nextLogNumber,
  operationCheckRun,
  renderLogEntry,
  renderOverview,
  sessionFinishedEntry,
  sessionStartedEntry,
  shortSha,
} from './openspec-change-progress.mjs';
import {
  LIFECYCLE_CHECK_NAME,
  MANAGED_ISSUE_LABELS,
  STAGE_LABELS,
  advanceCommandCursor,
  applyCommand,
  createRunState,
  creditSession,
  decideNext,
  finalizeClosedUnmerged,
  finalizeMerged,
  issueLabels,
  lifecycleCheck,
  markDispatching,
  markGateNotified,
  markRunning,
  noteHeadMoved,
  openGate,
  recoverDispatch,
  shortTaskTitle,
  stageLabel,
} from './openspec-change-state.mjs';

export const WORKFLOW_LOGIN = 'github-actions[bot]';
export const OPERATIONS = Object.freeze(['apply', 'verify', 'sync', 'archive']);
const ENQUEUED_LABEL = 'openspec:enqueued';
const TWIN_LABEL = 'openspec:change';
const ACTIVE_AGENT_STATES = new Set(['queued', 'in_progress']);
const RUN_MARKER = /<!-- openspec:run change=([a-z0-9-]+) issue=(\d+) requested-by=([A-Za-z0-9-]+(?:\[bot\])?) -->/;
const MISSING_STATE_MARKER = '<!-- openspec:missing-state -->';
const BINDING_SKILLS = Object.freeze({
  apply: '.github/skills/openspec-apply-change/SKILL.md',
  verify: '.github/skills/openspec-verify-change/SKILL.md',
  sync: '.github/skills/openspec-sync-specs/SKILL.md',
  archive: '.github/skills/openspec-archive-change/SKILL.md',
});
const LABEL_DEFINITIONS = Object.freeze({
  'openspec:processing': { color: 'fbca04', description: 'The openspec workflow is processing this change' },
  'openspec:needs-attention': { color: 'd93f0b', description: 'The openspec workflow is waiting for a human' },
  'openspec:awaiting-review': { color: '0e8a16', description: 'Archived on its pull request and waiting for human review and merge' },
  'openspec:stage:apply': { color: 'c5def5', description: 'OpenSpec pull request is in the apply stage' },
  'openspec:stage:verify': { color: 'c5def5', description: 'OpenSpec pull request is in the verify stage' },
  'openspec:stage:sync': { color: 'c5def5', description: 'OpenSpec pull request is in the sync stage' },
  'openspec:stage:archive': { color: 'c5def5', description: 'OpenSpec pull request is in the archive stage' },
});

// ---------------------------------------------------------------------------
// Paths and small helpers

export function activeChangePath(change) {
  return `openspec/changes/${change}`;
}

export function tasksPath(change) {
  return `${activeChangePath(change)}/tasks.md`;
}

export function archiveDirectoryFor(entries, change) {
  const pattern = new RegExp(`^\\d{4}-\\d{2}-\\d{2}-${change}$`);
  return entries.find((entry) => entry.type === 'dir' && pattern.test(entry.name)) ?? null;
}

function labelNames(issue) {
  return (issue.labels ?? []).map((label) => (typeof label === 'string' ? label : label.name));
}

function isWorkflowComment(comment) {
  return comment.user?.login === WORKFLOW_LOGIN;
}

function errorMessage(error) {
  return String(error?.message ?? error).slice(0, 500);
}

export function parseRunMarker(body) {
  const match = String(body ?? '').match(RUN_MARKER);
  return match ? { change: match[1], issue: Number(match[2]), requestedBy: match[3] } : null;
}

export function renderRunMarker({ change, issue, requestedBy }) {
  return `<!-- openspec:run change=${change} issue=${issue} requested-by=${requestedBy} -->`;
}

function pullRequestBody({ change, issue, requestedBy, context }) {
  const base = `${context.serverUrl ?? 'https://github.com'}/${context.owner}/${context.repo}`;
  return [
    `Implements the OpenSpec change [\`${change}\`](${base}/tree/main/${activeChangePath(change)}) for #${issue}.`,
    '',
    'This draft pull request is managed by the **openspec** workflow. The comments below are the change log: every agent session, decision, and review is recorded there in order. The **OpenSpec lifecycle** check shows the current state.',
    '',
    'To steer the workflow, comment `/openspec approve`, `/openspec retry`, `/openspec answer <text>`, or `/openspec abort` when it asks for input.',
    '',
    `Refs #${issue}`,
    '',
    renderRunMarker({ change, issue, requestedBy }),
  ].join('\n');
}

// ---------------------------------------------------------------------------
// Agent prompt

export function buildAgentPrompt(dispatch) {
  const envelope = validateDispatch(dispatch);
  const operation = envelope.operation;
  const example = {
    $schema: JSON_CONTRACTS.changeCheckpoint,
    change: envelope.change,
    operation,
    ...(operation === 'apply' ? { task: envelope.task.id } : {}),
    verdict: 'complete',
    summary: 'Two or three plain sentences for the humans reading the pull request.',
    validation: 'One line naming the checks you ran and their results.',
    ...(operation === 'verify' ? { findings: { critical: 0, warning: 0, suggestion: 0, items: [] } } : {}),
  };
  const lines = [
    `You are running one OpenSpec ${operation} step for the openspec workflow on branch \`${envelope.branch}\` (pull request #${envelope.pr}, issue #${envelope.issue}).`,
    '',
    'Dispatch (selected by the workflow; treat it as authoritative):',
    JSON.stringify(envelope),
    '',
    'Rules:',
    `1. Work only on \`${envelope.branch}\`. Before editing, confirm \`git rev-parse HEAD\` is \`${envelope.expectedHeadSha}\`. If it differs, stop without committing.`,
    `2. Read and follow ${BINDING_SKILLS[operation]} as the binding workflow for this step.`,
  ];
  if (operation === 'apply') {
    lines.push(
      `3. Execute only task ${envelope.task.id}. Read every file in task.capabilityPaths and obey the combined capability contracts. Do not start, edit, or check off any other task. After its validation passes, check off only task ${envelope.task.id}.`,
      '   Your final chat response is the capability result JSON required by openspec/config.yaml; the workflow reads only the checkpoint trailer.',
    );
  } else if (operation === 'verify') {
    lines.push(
      '3. Do not change any files. Report every CRITICAL, WARNING, and SUGGESTION from the skill in the checkpoint `findings`: exact counts, and every finding as an item (the 20 most important if there are more). Findings are not a failure: use verdict `complete` whenever verification ran.',
    );
  } else if (operation === 'sync') {
    lines.push('3. Synchronize every delta spec of this change into openspec/specs. Do not archive the change.');
  } else {
    lines.push(
      '3. All tasks are done, verification is reviewed, and specs are already synchronized. Archive the change on this branch without asking about synchronization. Do not merge anything.',
    );
  }
  if (envelope.answers.length > 0) {
    lines.push('4. Humans answered earlier questions. Treat these answers as binding decisions and record them in the change artifacts where relevant:');
    for (const answer of envelope.answers) {
      lines.push(`   - Q: ${answer.question}`, `     A (${answer.by}): ${answer.text}`);
    }
  }
  lines.push(
    '5. Never ask questions in chat and never wait for input. If you need a decision that the change artifacts, the answers above, and repository guidance do not settle, stop and finish with verdict `needs_decision` and one clear `question` that includes your recommended option.',
    '6. Work implementation-first and push coherent progress early. If you cannot finish in this session, push your progress and finish with verdict `partial`, leaving the task unchecked.',
    `7. End every session, successful or not, with exactly one final commit whose message contains exactly one line starting with \`OpenSpec-JSON: \` followed by one-line JSON matching ${JSON_CONTRACTS.changeCheckpoint}. Example:`,
    `   OpenSpec-JSON: ${JSON.stringify(example)}`,
    '   verdict is one of complete, partial, needs_decision, failed. Add `question` only for needs_decision. Add `findings` only for a complete verify.',
    `   Use \`git commit --allow-empty\` when there are no file changes. Push it to origin/${envelope.branch}, confirm origin has that SHA, and push nothing after it.`,
    '8. Do not open, edit, retitle, ready, approve, or merge pull requests, and do not comment on issues or pull requests.',
  );
  return lines.join('\n');
}

// ---------------------------------------------------------------------------
// Context

export function createContext({ env = process.env, client = null, now = () => new Date(), validator = null, dryRun = false } = {}) {
  const repository = env.GITHUB_REPOSITORY;
  if (!repository || !repository.includes('/')) throw new Error('GITHUB_REPOSITORY is required');
  const [owner, repo] = repository.split('/');
  return {
    owner,
    repo,
    baseRef: env.OPENSPEC_BASE_REF || 'main',
    render: { owner, repo, serverUrl: env.GITHUB_SERVER_URL || 'https://github.com' },
    client: client ?? new GitHubChangeClient({
      owner,
      repo,
      repositoryToken: env.GITHUB_TOKEN,
      agentToken: env.COPILOT_AGENT_TOKEN || undefined,
    }),
    now,
    dryRun,
    maxActive: Number.parseInt(env.OPENSPEC_MAX_ACTIVE_CHANGES ?? '', 10) || Infinity,
    validator: validator ?? defaultValidator,
    log: (message) => console.log(message),
    summary: [],
  };
}

function report(ctx, line) {
  ctx.summary.push(line);
  ctx.log(line);
}

// ---------------------------------------------------------------------------
// Runs: a draft pull request plus its state in the lifecycle check run

function isRunPullRequest(ctx, pr) {
  return isRunBranch(pr.head?.ref)
    && pr.base?.ref === ctx.baseRef
    && pr.head?.repo?.full_name === `${ctx.owner}/${ctx.repo}`
    && pr.user?.login === WORKFLOW_LOGIN;
}

export const MAX_HISTORY_WALK = 200;

function toCommit(gitCommit) {
  return {
    sha: gitCommit.sha,
    commit: { message: gitCommit.message ?? '' },
    parents: (gitCommit.parents ?? []).map((parent) => ({ sha: parent.sha })),
    author: { login: null, name: gitCommit.author?.name ?? null },
  };
}

async function stateAt(ctx, pr, sha) {
  const runs = (await ctx.client.listCheckRuns(sha, LIFECYCLE_CHECK_NAME))
    .filter((run) => run.app?.slug === 'github-actions')
    .sort((left, right) => right.id - left.id);
  for (const checkRun of runs) {
    try {
      const state = parseRunStateText(checkRun.output?.text ?? '');
      if (state.pr === pr.number && checkRun.external_id === state.change) return { state, checkRun, sha };
    } catch {
      // Not a valid state record.
    }
  }
  return null;
}

// Walks first parents from the head, so merges from main and long branches are handled without the
// 250-commit limit of the pull request commits API. Stops once the state and the commits it refers to are found.
export async function readBranchHistory(ctx, pr) {
  const newestFirst = [];
  let found = null;
  let sha = pr.head.sha;
  for (let step = 0; sha && step < MAX_HISTORY_WALK; step += 1) {
    const commit = toCommit(await ctx.client.getGitCommit(sha));
    newestFirst.push(commit);
    if (!found) found = await stateAt(ctx, pr, sha);
    if (found) {
      const needed = [found.state.headSha, found.state.current?.startSha, found.state.current?.baselineSha].filter(Boolean);
      if (needed.every((needle) => newestFirst.some((entry) => entry.sha === needle))) break;
    }
    if (commit.commit.message.startsWith('chore(openspec): start ')) break;
    sha = commit.parents[0]?.sha;
  }
  return { commits: newestFirst.reverse(), found };
}

export async function readRunState(ctx, pr) {
  return (await readBranchHistory(ctx, pr)).found;
}

async function loadRun(ctx, pr) {
  const { commits, found } = await readBranchHistory(ctx, pr);
  return {
    pr,
    commits,
    headSha: pr.head.sha,
    state: found?.state ?? null,
    checkRun: found?.checkRun ?? null,
    sha: found?.sha ?? null,
    comments: null,
    tasks: undefined,
  };
}

export const CLOSED_LOOKBACK_MS = 24 * 60 * 60 * 1000;

export async function loadRuns(ctx, { closed = false, change = null } = {}) {
  const prs = closed
    ? (await ctx.client.listRecentlyClosedPullRequests())
      .filter((pr) => ctx.now().getTime() - Date.parse(pr.closed_at ?? 0) < CLOSED_LOOKBACK_MS)
    : await ctx.client.listOpenPullRequests();
  const branchPattern = change ? new RegExp(`^${BRANCH_PREFIX}${change}(?:-r\\d+)?$`) : null;
  const runs = [];
  for (const pr of prs.filter((candidate) => isRunPullRequest(ctx, candidate))) {
    if (branchPattern && !branchPattern.test(pr.head.ref)) continue;
    const run = await loadRun(ctx, pr);
    if (change && (run.state?.change ?? parseRunMarker(pr.body)?.change) !== change) continue;
    runs.push(run);
  }
  return runs;
}

async function findRun(ctx, change, { closed = false } = {}) {
  return (await loadRuns(ctx, { closed, change }))[0] ?? null;
}

async function runComments(ctx, run) {
  if (!run.comments) run.comments = await ctx.client.listIssueComments(run.pr.number);
  return run.comments;
}

async function runTasks(ctx, run) {
  if (run.tasks !== undefined) return run.tasks;
  const change = run.state.change;
  let text = await ctx.client.getOptionalTextContent(tasksPath(change), run.headSha);
  if (text === null) {
    const archived = archiveDirectoryFor(await ctx.client.listDirectory('openspec/changes/archive', run.headSha), change);
    if (archived) text = await ctx.client.getOptionalTextContent(`${archived.path}/tasks.md`, run.headSha);
  }
  try {
    run.tasks = text === null ? null : parseCapabilityTasks(text);
  } catch {
    run.tasks = null;
  }
  return run.tasks;
}

export async function writeRunState(ctx, run, state) {
  run.state = state;
  if (ctx.dryRun) return run;
  const pr = await ctx.client.getPullRequest(run.pr.number);
  run.headSha = pr.head.sha;
  const tasks = await runTasks(ctx, run);
  const view = lifecycleCheck(state);
  const completed = view.status === 'completed';
  const gateUrl = state.gate?.log ? `${pr.html_url}#issuecomment-${state.gate.log}` : pr.html_url;
  const output = {
    title: view.title,
    summary: renderOverview({ state, tasks, context: ctx.render, includeMarker: false }).slice(0, 60000),
    text: renderRunStateText(state),
  };
  const body = {
    status: view.status,
    details_url: gateUrl,
    output,
    ...(completed ? { conclusion: view.conclusion, completed_at: ctx.now().toISOString() } : {}),
  };
  const reopening = run.checkRun?.status === 'completed' && !completed;
  if (run.checkRun && run.sha === run.headSha && !reopening) {
    run.checkRun = await ctx.client.updateCheckRun(run.checkRun.id, body);
    return run;
  }
  const previous = run.checkRun;
  const previousSha = run.sha;
  run.checkRun = await ctx.client.createCheckRun({
    name: LIFECYCLE_CHECK_NAME,
    head_sha: run.headSha,
    external_id: state.change,
    ...body,
  });
  run.sha = run.headSha;
  if (previous) {
    // Superseded runs keep no state, so a branch reset can never resurrect an older state.
    await ctx.client.updateCheckRun(previous.id, {
      status: 'completed',
      conclusion: 'neutral',
      completed_at: ctx.now().toISOString(),
      output: {
        title: `Superseded by ${shortSha(run.headSha)}`,
        summary: `The current OpenSpec state is on ${shortSha(run.headSha)}${previousSha === run.headSha ? ' in a newer check run' : ''}.`,
        text: 'Superseded. This check run no longer holds OpenSpec state.',
      },
    });
  }
  return run;
}

// ---------------------------------------------------------------------------
// Change-log comments

export async function postLog(ctx, run, entry, { update = false } = {}) {
  const comments = await runComments(ctx, run);
  const ours = comments.filter(isWorkflowComment);
  const existing = findLogComment(ours, run.state.change, entry.event);
  if (existing && !update) return existing;
  const n = existing ? Number(existing.body.match(/ n=(\d+) /)[1]) : nextLogNumber(ours, run.state.change);
  const body = renderLogEntry({ change: run.state.change, n, ...entry });
  if (ctx.dryRun) {
    ctx.log(`[dry-run] log #${n}: ${entry.title}`);
    return existing ?? { id: null, body };
  }
  if (existing) {
    if (existing.body === body) return existing;
    const updated = await ctx.client.updateIssueComment(existing.id, body);
    Object.assign(existing, updated ?? { body });
    return existing;
  }
  const created = await ctx.client.createIssueComment(run.pr.number, body);
  comments.push(created);
  return created;
}

async function upsertOverview(ctx, run) {
  if (ctx.dryRun) return;
  const comments = await runComments(ctx, run);
  const tasks = await runTasks(ctx, run);
  const body = renderOverview({ state: run.state, tasks, context: ctx.render });
  const existing = comments.find((comment) => isWorkflowComment(comment) && isOverviewComment(comment.body, run.state.change));
  if (!existing) {
    comments.push(await ctx.client.createIssueComment(run.pr.number, body));
  } else if (existing.body !== body) {
    Object.assign(existing, await ctx.client.updateIssueComment(existing.id, body) ?? { body });
  }
}

async function commentOnIssue(ctx, issueNumber, body) {
  if (ctx.dryRun) {
    ctx.log(`[dry-run] comment on #${issueNumber}: ${body.split('\n')[0]}`);
    return;
  }
  await ctx.client.createIssueComment(issueNumber, body);
}

// ---------------------------------------------------------------------------
// Labels

async function ensureLabels(ctx) {
  if (ctx.dryRun) return;
  for (const [name, definition] of Object.entries(LABEL_DEFINITIONS)) {
    await ctx.client.ensureLabel({ name, ...definition });
  }
}

async function syncLabels(ctx, number, current, desired, managed) {
  const want = new Set(desired);
  for (const label of managed) {
    const has = current.includes(label);
    if (has && !want.has(label)) {
      if (!ctx.dryRun) await ctx.client.removeIssueLabel(number, label);
    } else if (!has && want.has(label)) {
      if (!ctx.dryRun) await ctx.client.addIssueLabel(number, label);
    }
  }
}

async function syncRunLabels(ctx, run) {
  const issue = await ctx.client.getIssue(run.state.issue);
  await syncLabels(ctx, issue.number, labelNames(issue), issueLabels(run.state), [...MANAGED_ISSUE_LABELS, ...STAGE_LABELS]);
  const stage = stageLabel(run.state);
  await syncLabels(ctx, run.pr.number, labelNames(run.pr), stage ? [stage] : [], STAGE_LABELS);
}

// ---------------------------------------------------------------------------
// Evidence validation at an exact commit

function runOpenSpecJson(args, cwd) {
  return JSON.parse(execFileSync('openspec', args, { cwd, encoding: 'utf8', maxBuffer: 20 * 1024 * 1024 }));
}

function assertValidationPassed(report, label) {
  if (report.summary?.totals?.failed !== 0) throw new Error(`Strict OpenSpec validation failed for ${label}`);
}

export function defaultValidator({ sha, branch, change, kind }) {
  try {
    execFileSync('git', ['fetch', '--no-tags', 'origin', `+refs/heads/${branch}:refs/remotes/origin/${branch}`], { stdio: 'ignore' });
  } catch {
    // The full-history checkout already contains the branch; a refresh is only best effort.
  }
  const worktree = mkdtempSync(join(tmpdir(), 'openspec-'));
  try {
    execFileSync('git', ['worktree', 'add', '--detach', worktree, sha], { stdio: 'ignore' });
    if (kind === 'archive') {
      assertValidationPassed(runOpenSpecJson(['validate', '--specs', '--strict', '--json', '--no-interactive'], worktree), 'specs');
      return;
    }
    assertValidationPassed(
      runOpenSpecJson(['validate', change, '--type', 'change', '--strict', '--json', '--no-interactive'], worktree),
      change,
    );
    if (kind === 'sync') {
      const delta = runOpenSpecJson(['show', change, '--type', 'change', '--json', '--deltas-only'], worktree);
      const specsById = {};
      for (const spec of new Set(delta.deltas.map((entry) => entry.spec))) {
        specsById[spec] = runOpenSpecJson(['show', spec, '--type', 'spec', '--json'], worktree);
      }
      validateSynchronizedDeltas(delta, specsById);
    }
  } finally {
    try {
      execFileSync('git', ['worktree', 'remove', '--force', worktree], { stdio: 'ignore' });
    } finally {
      rmSync(worktree, { recursive: true, force: true });
    }
  }
}

async function loadCapabilitySet(ctx, capabilities) {
  const definitions = [];
  for (const id of capabilities) {
    const content = await ctx.client.getTextContent(`openspec/capabilities/${id}.md`, ctx.baseRef);
    definitions.push(parseCapabilityDefinition(content, id));
  }
  return validateCapabilitySet(definitions, 'apply');
}

async function changedFiles(ctx, before, after) {
  if (before === after) return [];
  const comparison = await ctx.client.compareCommits(before, after);
  return comparison.files.map((file) => file.filename);
}

export async function validateEvidence(ctx, state, checkpointSha) {
  const { current, change, branch } = state;
  try {
    if (current.operation === 'apply') {
      const path = tasksPath(change);
      const before = parseCapabilityTasks(await ctx.client.getTextContent(path, current.baselineSha));
      const after = parseCapabilityTasks(await ctx.client.getTextContent(path, checkpointSha));
      if (before.length !== after.length || before.some((task, index) => task.id !== after[index].id)) {
        return { ok: false, reason: 'tasks were added, removed, or reordered' };
      }
      for (const [index, task] of after.entries()) {
        if (task.id === current.task.id) {
          if (!task.completed) return { ok: false, reason: `task ${task.id} is still unchecked` };
        } else if (task.completed !== before[index].completed) {
          return { ok: false, reason: `task ${task.id} changed its checkbox, but only ${current.task.id} may change` };
        }
      }
      const set = await loadCapabilitySet(ctx, current.task.capabilities);
      if (set.mutation === 'checkbox-only') {
        const other = (await changedFiles(ctx, current.baselineSha, checkpointSha)).filter((file) => file !== path);
        if (other.length > 0) {
          return { ok: false, reason: `a ${set.ids.join(' + ')} task may only check its box, but it changed ${other.slice(0, 5).join(', ')}` };
        }
      }
      await ctx.validator({ sha: checkpointSha, branch, change, kind: 'apply' });
    } else if (current.operation === 'verify') {
      const files = await changedFiles(ctx, current.baselineSha, checkpointSha);
      if (files.length > 0) return { ok: false, reason: `verification must not change files, but changed ${files.slice(0, 5).join(', ')}` };
      await ctx.validator({ sha: checkpointSha, branch, change, kind: 'verify' });
    } else if (current.operation === 'sync') {
      await ctx.validator({ sha: checkpointSha, branch, change, kind: 'sync' });
    } else {
      const active = await ctx.client.listDirectory(activeChangePath(change), checkpointSha);
      if (active.length > 0) return { ok: false, reason: `${activeChangePath(change)} still exists` };
      const archived = archiveDirectoryFor(await ctx.client.listDirectory('openspec/changes/archive', checkpointSha), change);
      if (!archived) return { ok: false, reason: 'no dated archive directory was found' };
      await ctx.validator({ sha: checkpointSha, branch, change, kind: 'archive' });
    }
    return { ok: true };
  } catch (error) {
    return { ok: false, reason: errorMessage(error) };
  }
}

// ---------------------------------------------------------------------------
// Checkpoint discovery

export function findCheckpoint(state, commits) {
  const startIndex = commits.findIndex((commit) => commit.sha === state.current.startSha);
  if (startIndex < 0) return { historyChanged: true };
  const newer = commits.slice(startIndex + 1);
  for (const commit of [...newer].reverse()) {
    let checkpoint;
    try {
      checkpoint = parseCheckpointTrailer(commit.commit?.message ?? '');
    } catch (error) {
      return { checkpointError: errorMessage(error), checkpointSha: commit.sha, newCommits: newer.length };
    }
    if (!checkpoint) continue;
    const expectedTask = state.current.task?.id;
    if (checkpoint.change !== state.change
      || checkpoint.operation !== state.current.operation
      || checkpoint.task !== expectedTask) {
      const expected = `${state.change} ${state.current.operation}${expectedTask ? ` ${expectedTask}` : ''}`;
      const found = `${checkpoint.change} ${checkpoint.operation}${checkpoint.task ? ` ${checkpoint.task}` : ''}`;
      return { checkpointError: `checkpoint is for ${found}, expected ${expected}`, checkpointSha: commit.sha, newCommits: newer.length };
    }
    return { checkpoint, checkpointSha: commit.sha, newCommits: newer.length };
  }
  return { newCommits: newer.length };
}

// ---------------------------------------------------------------------------
// Admission

async function enqueuedBy(ctx, issueNumber) {
  const events = await ctx.client.listIssueEvents(issueNumber);
  const labeled = events.filter((event) => event.event === 'labeled' && event.label?.name === ENQUEUED_LABEL);
  return labeled.at(-1)?.actor ?? null;
}

async function blockersSatisfied(ctx, issueNumber) {
  const blockers = await ctx.client.listBlockedBy(issueNumber);
  const open = [];
  for (const blocker of blockers) {
    if (blocker.state !== 'closed') {
      open.push(blocker.number);
      continue;
    }
    if (labelNames(blocker).includes(TWIN_LABEL)) {
      try {
        if (parseChangeMarker(blocker.body ?? '').lifecycle !== 'archived') open.push(blocker.number);
      } catch {
        open.push(blocker.number);
      }
    }
  }
  return { ok: open.length === 0, open, all: blockers.map((blocker) => blocker.number) };
}

export async function listAdmissionCandidates(ctx, openRuns) {
  const activeChanges = new Map(openRuns
    .filter((run) => run.state && run.state.status !== 'closed')
    .map((run) => [run.state.change, run]));
  const twins = await ctx.client.listIssueTwins();
  const candidates = [];
  for (const issue of twins.filter((twin) => twin.state === 'open' && labelNames(twin).includes(ENQUEUED_LABEL))) {
    let marker;
    try {
      marker = parseChangeMarker(issue.body ?? '');
    } catch {
      continue;
    }
    if (marker.lifecycle !== 'active') continue;
    candidates.push({ issue, change: marker.ref, active: activeChanges.get(marker.ref) ?? null });
  }
  return candidates.sort((left, right) => left.issue.number - right.issue.number);
}

async function screenCandidate(ctx, candidate) {
  const { issue, change, active } = candidate;
  if (active) {
    await commentOnIssue(ctx, issue.number, `This change is already being processed in #${active.pr.number}, so the \`${ENQUEUED_LABEL}\` label was removed.`);
    if (!ctx.dryRun) await ctx.client.removeIssueLabel(issue.number, ENQUEUED_LABEL);
    return { eligible: false };
  }
  const actor = await enqueuedBy(ctx, issue.number);
  const permission = actor && !isBotUser(actor) ? await ctx.client.getCollaboratorPermission(actor.login) : 'none';
  if (!['admin', 'maintain', 'write'].includes(permission)) {
    await commentOnIssue(ctx, issue.number, `Only users with write access can request processing, so the \`${ENQUEUED_LABEL}\` label was removed.`);
    if (!ctx.dryRun) await ctx.client.removeIssueLabel(issue.number, ENQUEUED_LABEL);
    return { eligible: false };
  }
  const blockers = await blockersSatisfied(ctx, issue.number);
  if (!blockers.ok) {
    ctx.log(`#${issue.number} ${change}: waiting for ${blockers.open.map((number) => `#${number}`).join(', ')}`);
    return { eligible: false, waiting: true };
  }
  return { eligible: true, requestedBy: actor.login, blockers: blockers.all };
}

async function chooseBranch(ctx, change) {
  for (let attempt = 1; attempt < 50; attempt += 1) {
    const branch = attempt === 1 ? `${BRANCH_PREFIX}${change}` : `${BRANCH_PREFIX}${change}-r${attempt}`;
    if (!await ctx.client.branchExists(branch)) return { branch, reuse: false };
    const head = await ctx.client.getBranch(branch);
    const open = await ctx.client.listOpenPullRequestsForHead(branch);
    if (open.length === 0 && head.commit?.commit?.message?.startsWith(`chore(openspec): start ${change}`)) {
      return { branch, reuse: true, sha: head.commit.sha, baseSha: head.commit.parents?.[0]?.sha };
    }
  }
  throw new Error(`No free branch name for ${change}`);
}

export async function admitChange(ctx, change) {
  const openRuns = await loadRuns(ctx);
  const candidate = (await listAdmissionCandidates(ctx, openRuns)).find((entry) => entry.change === change);
  if (!candidate) return report(ctx, `- ${change}: no longer enqueued`);
  const screened = await screenCandidate(ctx, candidate);
  if (!screened.eligible) return report(ctx, `- ${change}: not admitted`);
  const { issue } = candidate;
  const main = await ctx.client.getBranch(ctx.baseRef);
  const tasksText = await ctx.client.getOptionalTextContent(tasksPath(change), main.commit.sha);
  if (tasksText === null) {
    await commentOnIssue(ctx, issue.number, `\`${activeChangePath(change)}/tasks.md\` is missing on \`${ctx.baseRef}\`, so processing cannot start.`);
    if (!ctx.dryRun) await ctx.client.removeIssueLabel(issue.number, ENQUEUED_LABEL);
    return report(ctx, `- ${change}: tasks.md missing`);
  }
  if (ctx.dryRun) return report(ctx, `- ${change}: would start from #${issue.number}`);
  await ensureLabels(ctx);

  const choice = await chooseBranch(ctx, change);
  let headSha = choice.sha;
  let baseSha = choice.baseSha ?? main.commit.sha;
  if (!choice.reuse) {
    const parent = await ctx.client.getGitCommit(main.commit.sha);
    const commit = await ctx.client.createGitCommit({
      message: `chore(openspec): start ${change}\n\nRequested by ${screened.requestedBy} in #${issue.number}.`,
      tree: parent.tree.sha,
      parents: [main.commit.sha],
    });
    await ctx.client.createGitRef(choice.branch, commit.sha);
    headSha = commit.sha;
    baseSha = main.commit.sha;
  }
  const pr = await ctx.client.createPullRequest({
    title: `OpenSpec: ${change}`,
    body: pullRequestBody({ change, issue: issue.number, requestedBy: screened.requestedBy, context: ctx.render }),
    head: choice.branch,
    base: ctx.baseRef,
    draft: true,
  });
  const run = {
    pr,
    commits: [{ sha: headSha }],
    headSha,
    state: null,
    checkRun: null,
    sha: null,
    comments: [],
    tasks: parseCapabilityTasks(tasksText),
  };
  const state = createRunState({
    change,
    issue: issue.number,
    pr: pr.number,
    branch: choice.branch,
    baseRef: ctx.baseRef,
    baseSha,
    headSha,
    requestedBy: screened.requestedBy,
    now: ctx.now(),
  });
  await writeRunState(ctx, run, state);
  await upsertOverview(ctx, run);
  await postLog(ctx, run, admittedEntry({ state, context: ctx.render, blockers: screened.blockers }));
  await ctx.client.removeIssueLabel(issue.number, ENQUEUED_LABEL);
  await commentOnIssue(ctx, issue.number, `Processing started in #${pr.number}. Follow the change log there.`);
  report(ctx, `- ${change}: started in #${pr.number}`);
  await dispatchNext(ctx, run);
  return run;
}

// ---------------------------------------------------------------------------
// Dispatch

async function buildDispatchTask(ctx, run, decision) {
  const text = await ctx.client.getTextContent(tasksPath(run.state.change), run.headSha);
  const selected = parseCapabilityTasks(text).find((task) => task.id === decision.task.id);
  if (!selected) throw new Error(`Task ${decision.task.id} no longer exists`);
  if (selected.completed && run.state.current === null) throw new Error(`Task ${decision.task.id} is already checked`);
  await loadCapabilitySet(ctx, selected.capabilities);
  return {
    id: selected.id,
    title: shortTaskTitle(selected.title),
    capabilities: selected.capabilities,
    capabilityPaths: selected.capabilities.map((id) => `openspec/capabilities/${id}.md`),
    block: selected.block,
  };
}

export async function dispatchNext(ctx, run, { only = null } = {}) {
  const tasks = run.state.phase === 'apply' && !run.state.current ? await runTasks(ctx, run) : null;
  if (run.state.phase === 'apply' && !run.state.current && tasks === null) {
    const gated = openGate(run.state, { kind: 'failure', operation: 'apply', task: null, reason: `${tasksPath(run.state.change)} is missing or invalid on the branch.`, now: ctx.now() });
    await writeRunState(ctx, run, gated);
    return { dispatched: false };
  }
  const decision = decideNext(run.state, { tasks });
  if (decision.action !== 'dispatch' || (only && decision.operation !== only)) return { dispatched: false, decision };
  if (ctx.dryRun) {
    report(ctx, `- ${run.state.change}: would start ${decision.operation}${decision.task ? ` ${decision.task.id}` : ''}`);
    return { dispatched: false, decision };
  }
  const pr = await ctx.client.getPullRequest(run.pr.number);
  run.headSha = pr.head.sha;
  let dispatchTask;
  try {
    if (decision.operation === 'apply') dispatchTask = await buildDispatchTask(ctx, run, decision);
  } catch (error) {
    const gated = openGate(run.state, {
      kind: 'failure', operation: 'apply', task: decision.task, reason: `Cannot start task ${decision.task.id}: ${errorMessage(error)}`, now: ctx.now(),
    });
    await writeRunState(ctx, run, gated);
    return { dispatched: false, decision };
  }
  const runTask = dispatchTask
    ? { id: dispatchTask.id, title: dispatchTask.title, capabilities: dispatchTask.capabilities }
    : null;
  let state = markDispatching(run.state, {
    operation: decision.operation, task: runTask, attempt: decision.attempt, startSha: run.headSha, now: ctx.now(),
  });
  await writeRunState(ctx, run, state);
  const envelope = {
    $schema: JSON_CONTRACTS.changeDispatch,
    change: state.change,
    operation: decision.operation,
    issue: state.issue,
    pr: state.pr,
    branch: state.branch,
    baseRef: state.base.ref,
    expectedHeadSha: run.headSha,
    attempt: decision.attempt,
    ...(dispatchTask ? { task: dispatchTask } : {}),
    answers: state.answers.map(({ question, text, by }) => ({ question, text, by })),
  };
  let agentTask;
  try {
    agentTask = await ctx.client.startAgentTask({
      prompt: buildAgentPrompt(envelope),
      customAgent: 'openspec',
      baseRef: state.base.ref,
      headRef: state.branch,
      createPullRequest: false,
    });
  } catch (error) {
    state = openGate(state, { kind: 'failure', reason: `Could not start the agent session: ${errorMessage(error)}`, now: ctx.now() });
    await writeRunState(ctx, run, state);
    return { dispatched: false, decision };
  }
  state = markRunning(state, {
    agentTask: { id: agentTask.id, state: agentTask.state, url: agentTask.html_url },
    now: ctx.now(),
  });
  await writeRunState(ctx, run, state);
  await postLog(ctx, run, sessionStartedEntry({ state, context: ctx.render }));
  report(ctx, `- ${state.change}: started ${decision.operation}${runTask ? ` ${runTask.id}` : ''} (attempt ${decision.attempt})`);
  return { dispatched: true, decision };
}

export async function dispatchChange(ctx, change, operation) {
  const run = await findRun(ctx, change);
  if (!run?.state) return report(ctx, `- ${change}: no run found`);
  const result = await dispatchNext(ctx, run, { only: operation });
  if (!result.dispatched) report(ctx, `- ${change}: nothing to start for ${operation}`);
  return result;
}

// ---------------------------------------------------------------------------
// Observe: commands, recovery, pushes, and the credit list

function matchesDispatch(task, state) {
  const branch = task.artifacts?.find((artifact) => artifact.type === 'branch')?.data;
  if (branch?.head_ref !== state.branch) return false;
  return Date.parse(task.created_at) >= Date.parse(state.current.dispatchedAt) - 60_000;
}

async function processCommands(ctx, run) {
  const comments = await runComments(ctx, run);
  for (const comment of pendingCommandComments(comments, run.state.commandCursor)) {
    const parsed = parseCommand(comment.body);
    let accepted = false;
    let message;
    let state = run.state;
    if (parsed?.error) {
      message = parsed.error;
      state = advanceCommandCursor(state, { commentId: comment.id, now: ctx.now() });
    } else {
      const permission = await ctx.client.getCollaboratorPermission(comment.user.login);
      const authorization = authorizeCommand(comment, permission);
      if (!authorization.ok) {
        message = authorization.reason;
        state = advanceCommandCursor(state, { commentId: comment.id, now: ctx.now() });
      } else {
        ({ state, accepted, message } = applyCommand(state, {
          name: parsed.name, text: parsed.text, by: comment.user.login, commentId: comment.id, now: ctx.now(),
        }));
      }
    }
    if (ctx.dryRun) {
      report(ctx, `- ${run.state.change}: would ${accepted ? 'accept' : 'reject'} comment ${comment.id}`);
      continue;
    }
    await writeRunState(ctx, run, state);
    await ctx.client.addCommentReaction(comment.id, accepted ? '+1' : 'confused');
    await postLog(ctx, run, commandEntry({ comment, accepted, message, after: state, context: ctx.render }));
    report(ctx, `- ${state.change}: ${accepted ? 'accepted' : 'rejected'} command from ${comment.user.login}`);
  }
}

async function bootstrapMissingState(ctx, run) {
  const marker = parseRunMarker(run.pr.body);
  if (!marker || run.commits.length !== 1) return false;
  const [admission] = run.commits;
  if (!admission.commit?.message?.startsWith(`chore(openspec): start ${marker.change}`)) return false;
  if (ctx.dryRun) return true;
  const comments = await runComments(ctx, run);
  const created = createRunState({
    change: marker.change,
    issue: marker.issue,
    pr: run.pr.number,
    branch: run.pr.head.ref,
    baseRef: ctx.baseRef,
    baseSha: admission.parents?.[0]?.sha ?? run.pr.base.sha,
    headSha: admission.sha,
    requestedBy: marker.requestedBy,
    now: ctx.now(),
  });
  // Never replay commands that were posted before the state was lost.
  const state = { ...created, commandCursor: comments.reduce((max, comment) => Math.max(max, comment.id), 0) };
  await writeRunState(ctx, run, state);
  await upsertOverview(ctx, run);
  await postLog(ctx, run, admittedEntry({ state, context: ctx.render }));
  report(ctx, `- ${state.change}: restored the initial state of #${run.pr.number}`);
  return true;
}

export async function observeRun(ctx, run) {
  if (!run.state) {
    if (await bootstrapMissingState(ctx, run)) return { credit: false };
    const comments = await runComments(ctx, run);
    if (!ctx.dryRun && !comments.some((comment) => isWorkflowComment(comment) && comment.body.includes(MISSING_STATE_MARKER))) {
      await ctx.client.createIssueComment(run.pr.number, [
        '### ⛔ OpenSpec state is missing',
        '',
        'The workflow cannot find a valid **OpenSpec lifecycle** state for this pull request, so it will not continue here. Close this pull request without merging and add the `openspec:enqueued` label to the issue again to start over.',
        '',
        MISSING_STATE_MARKER,
      ].join('\n'));
    }
    report(ctx, `- #${run.pr.number}: no valid OpenSpec state found`);
    return { credit: false };
  }
  if (run.state.status === 'closed') return { credit: false };
  // Commands also wake the workflow themselves; other events skip the scan unless a gate is waiting.
  if (ctx.scanComments !== false || run.state.status === 'gated') await processCommands(ctx, run);
  if (run.state.status === 'closed') return { credit: false };

  if (run.state.status === 'dispatching') {
    const tasks = (await ctx.client.listAgentTasks()).filter((task) => matchesDispatch(task, run.state));
    const recovery = recoverDispatch(run.state, { matches: tasks, now: ctx.now() });
    if (recovery.action !== 'wait' && !ctx.dryRun) {
      await writeRunState(ctx, run, recovery.state);
      if (recovery.action === 'adopted') await postLog(ctx, run, sessionStartedEntry({ state: recovery.state, context: ctx.render }));
    }
    report(ctx, `- ${run.state.change}: interrupted dispatch → ${recovery.action}`);
    return { credit: false };
  }

  if (run.state.status === 'running') {
    const found = findCheckpoint(run.state, run.commits);
    if (found.historyChanged || found.checkpoint || found.checkpointError) return { credit: true };
    let agentState = null;
    try {
      agentState = (await ctx.client.getAgentTask(run.state.current.agentTask.id)).state;
    } catch (error) {
      ctx.log(`- ${run.state.change}: cannot read agent task (${errorMessage(error)})`);
    }
    return { credit: agentState !== null && !ACTIVE_AGENT_STATES.has(agentState) };
  }

  const issue = await ctx.client.getIssue(run.state.issue);
  if (issue.state === 'closed' && run.state.status !== 'gated') {
    const state = openGate(run.state, {
      kind: 'failure',
      operation: run.state.current?.operation ?? null,
      reason: `Issue #${issue.number} was closed. Reply \`/openspec abort\` to stop, or reopen the issue and reply \`/openspec retry\`.`,
      now: ctx.now(),
    });
    if (!ctx.dryRun) await writeRunState(ctx, run, state);
    return { credit: false };
  }

  if (run.headSha !== run.state.headSha) {
    const before = run.state;
    const moved = noteHeadMoved(run.state, { headSha: run.headSha, now: ctx.now() });
    if (moved.moved && !ctx.dryRun) {
      const index = run.commits.findIndex((commit) => commit.sha === before.headSha);
      const authors = [...new Set(run.commits.slice(index + 1).map((commit) => commit.author?.login ?? commit.author?.name).filter(Boolean))];
      await writeRunState(ctx, run, moved.state);
      await postLog(ctx, run, humanPushEntry({ before, after: moved.state, authors, context: ctx.render }));
    }
  }
  return { credit: false };
}

export async function observe(ctx, { eventName = null, payload = {} } = {}) {
  if (eventName) {
    const classified = classifyEvent(eventName, payload);
    report(ctx, `Event: ${eventName} · ${classified.reason}`);
    if (!classified.relevant) return { relevant: false, credit: [], touched: [] };
    ctx.scanComments = ['issue_comment', 'schedule', 'workflow_dispatch'].includes(eventName);
  }
  const credit = [];
  const touched = [];
  for (const run of await loadRuns(ctx)) {
    try {
      const revision = run.state?.revision;
      const result = await observeRun(ctx, run);
      if (result.credit) credit.push(run.state.change);
      if (run.state && run.state.revision !== revision) touched.push(run.state.change);
    } catch (error) {
      report(ctx, `- #${run.pr.number}: observe failed (${errorMessage(error)})`);
    }
  }
  return { relevant: true, credit, touched };
}

// ---------------------------------------------------------------------------
// Credit a finished session

export async function creditChange(ctx, change) {
  const run = await findRun(ctx, change);
  if (!run?.state || run.state.status !== 'running') return report(ctx, `- ${change}: no running session`);
  const before = run.state;
  const found = findCheckpoint(before, run.commits);
  let outcome;
  if (found.historyChanged) {
    outcome = {
      state: openGate(before, { kind: 'failure', reason: 'The branch history changed during the session: its start commit is gone.', now: ctx.now() }),
      result: { kind: 'gate', gate: 'failure', reason: 'The branch history changed during the session.' },
    };
  } else {
    let agentState = null;
    try {
      agentState = (await ctx.client.getAgentTask(before.current.agentTask.id)).state;
    } catch (error) {
      ctx.log(`- ${change}: cannot read agent task (${errorMessage(error)})`);
    }
    const evidence = found.checkpoint?.verdict === 'complete'
      ? await validateEvidence(ctx, before, found.checkpointSha)
      : null;
    const comments = await runComments(ctx, run);
    const sessionLog = findLogComment(comments.filter(isWorkflowComment), change, `session:${before.current.agentTask.id}`);
    outcome = creditSession(before, {
      agentState,
      headSha: run.headSha,
      checkpoint: found.checkpoint ?? null,
      checkpointSha: found.checkpointSha ?? null,
      checkpointError: found.checkpointError ?? null,
      evidence,
      sessionLog: sessionLog?.id ?? null,
    }, ctx.now());
  }
  if (outcome.result.kind === 'wait') return report(ctx, `- ${change}: session still running`);
  if (ctx.dryRun) return report(ctx, `- ${change}: would record ${outcome.result.kind}`);
  await writeRunState(ctx, run, outcome.state);
  if (before.current.agentTask) {
    await postLog(ctx, run, sessionFinishedEntry({
      before,
      after: outcome.state,
      result: outcome.result,
      checkpoint: found.checkpoint ?? null,
      checkpointSha: found.checkpointSha ?? null,
      context: ctx.render,
    }), { update: true });
  }
  const checkRun = operationCheckRun({ before, result: outcome.result, checkpoint: found.checkpoint ?? null, context: ctx.render });
  await ctx.client.createCheckRun({
    name: checkRun.name,
    head_sha: found.checkpointSha ?? run.headSha,
    external_id: change,
    status: 'completed',
    conclusion: checkRun.conclusion,
    completed_at: ctx.now().toISOString(),
    details_url: run.pr.html_url,
    output: { title: checkRun.title, summary: checkRun.summary },
  });
  report(ctx, `- ${change}: ${outcome.result.kind}${outcome.result.gate ? ` (${outcome.result.gate} gate)` : ''}`);
  return outcome;
}

// ---------------------------------------------------------------------------
// Plan

export async function plan(ctx) {
  const buckets = { admit: [], apply: [], verify: [], sync: [], archive: [], gate: [], finalize: [] };
  const openRuns = await loadRuns(ctx);
  let active = 0;
  for (const run of openRuns) {
    if (!run.state) continue;
    const { change } = run.state;
    if (run.state.status === 'closed') {
      buckets.finalize.push(change);
      continue;
    }
    active += 1;
    const tasks = run.state.phase === 'apply' && !run.state.current ? await runTasks(ctx, run) : null;
    let decision;
    try {
      decision = decideNext(run.state, { tasks: tasks ?? [] });
    } catch (error) {
      report(ctx, `- ${change}: cannot plan (${errorMessage(error)})`);
      continue;
    }
    if (run.state.phase === 'apply' && !run.state.current && tasks === null) decision = { action: 'dispatch', operation: 'apply' };
    if (decision.action === 'dispatch') buckets[decision.operation].push(change);
    if (decision.action === 'notify-gate') buckets.gate.push(change);
  }
  for (const run of await loadRuns(ctx, { closed: true })) {
    if (!run.state) continue;
    const issue = await ctx.client.getIssue(run.state.issue);
    const needsMergeFinalize = Boolean(run.pr.merged_at) && (run.state.outcome !== 'merged' || issue.state === 'open');
    const needsCloseFinalize = !run.pr.merged_at && run.state.status !== 'closed';
    if ((needsMergeFinalize || needsCloseFinalize) && !buckets.finalize.includes(run.state.change)) {
      buckets.finalize.push(run.state.change);
    }
  }
  const candidates = await listAdmissionCandidates(ctx, openRuns);
  for (const candidate of candidates) {
    if (candidate.active) {
      buckets.admit.push(candidate.change);
      continue;
    }
    const blockers = await blockersSatisfied(ctx, candidate.issue.number);
    if (!blockers.ok) {
      report(ctx, `- ${candidate.change}: waiting for ${blockers.open.map((number) => `#${number}`).join(', ')}`);
      continue;
    }
    if (active >= ctx.maxActive) {
      report(ctx, `- ${candidate.change}: waiting for a free slot (limit ${ctx.maxActive})`);
      continue;
    }
    buckets.admit.push(candidate.change);
    active += 1;
  }
  for (const [name, changes] of Object.entries(buckets)) {
    if (changes.length > 0) report(ctx, `- ${name}: ${changes.join(', ')}`);
  }
  return buckets;
}

// ---------------------------------------------------------------------------
// Gates, finalize, publish

export async function notifyGate(ctx, change) {
  const run = await findRun(ctx, change);
  if (!run?.state || run.state.status !== 'gated' || run.state.gate.notified) return report(ctx, `- ${change}: no new gate`);
  if (ctx.dryRun) return report(ctx, `- ${change}: would announce the ${run.state.gate.kind} gate`);
  const comment = await postLog(ctx, run, gateEntry({ state: run.state, context: ctx.render }));
  const state = markGateNotified(run.state, { log: comment.id, now: ctx.now() });
  await writeRunState(ctx, run, state);
  const link = `${run.pr.html_url}#issuecomment-${comment.id}`;
  const text = state.gate.kind === 'merge'
    ? `The change is archived on #${run.pr.number} and [ready for human review and merge](${link}).`
    : `Processing in #${run.pr.number} is [waiting for input](${link}).`;
  await commentOnIssue(ctx, state.issue, text);
  report(ctx, `- ${change}: announced the ${state.gate.kind} gate`);
  return state;
}

export async function finalizeChange(ctx, change) {
  const runs = [...await loadRuns(ctx, { change }), ...await loadRuns(ctx, { closed: true, change })]
    .filter((run) => run.state);
  if (runs.length === 0) return report(ctx, `- ${change}: no run found`);
  for (const run of runs) await finalizeRun(ctx, run);
}

async function finalizeRun(ctx, run) {
  const { change } = run.state;
  const issue = await ctx.client.getIssue(run.state.issue);
  if (run.pr.state === 'open') {
    if (run.state.status !== 'closed') return;
    if (ctx.dryRun) return report(ctx, `- ${change}: would close #${run.pr.number}`);
    await postLog(ctx, run, closedEntry({ state: run.state, context: ctx.render }));
    await ctx.client.updatePullRequest(run.pr.number, { state: 'closed' });
    await syncRunLabels(ctx, run);
    await upsertOverview(ctx, run);
    await commentOnIssue(ctx, issue.number, `Processing in #${run.pr.number} was aborted. Add the \`${ENQUEUED_LABEL}\` label again to start over.`);
    return report(ctx, `- ${change}: closed #${run.pr.number} after abort`);
  }
  if (run.pr.merged_at) {
    if (run.state.outcome === 'merged' && issue.state === 'closed') return;
    const archived = archiveDirectoryFor(await ctx.client.listDirectory('openspec/changes/archive', ctx.baseRef), change);
    if (!archived) return report(ctx, `- ${change}: merged; waiting for the archive on ${ctx.baseRef}`);
    if (ctx.dryRun) return report(ctx, `- ${change}: would finish`);
    const state = finalizeMerged(run.state, { now: ctx.now() });
    if (state !== run.state) await writeRunState(ctx, run, state);
    await postLog(ctx, run, closedEntry({ state, context: ctx.render, archivePath: archived.path }));
    await syncRunLabels(ctx, run);
    await upsertOverview(ctx, run);
    if (issue.state === 'open') {
      await commentOnIssue(ctx, issue.number, `Merged in #${run.pr.number} and archived at \`${archived.path}\`.`);
      await ctx.client.updateIssue(issue.number, { state: 'closed', state_reason: 'completed' });
    }
    return report(ctx, `- ${change}: finished`);
  }
  if (run.state.status === 'closed') return;
  if (ctx.dryRun) return report(ctx, `- ${change}: would record the closed pull request`);
  const state = finalizeClosedUnmerged(run.state, { now: ctx.now() });
  await writeRunState(ctx, run, state);
  await postLog(ctx, run, closedEntry({ state, context: ctx.render }));
  await syncRunLabels(ctx, run);
  await ctx.client.addIssueLabel(issue.number, 'openspec:needs-attention');
  await commentOnIssue(ctx, issue.number, `#${run.pr.number} was closed without merging, so processing stopped. Add the \`${ENQUEUED_LABEL}\` label again to start over.`);
  return report(ctx, `- ${change}: recorded the closed pull request`);
}

export async function publish(ctx, changes = null) {
  const wanted = changes ? new Set(changes) : null;
  for (const run of await loadRuns(ctx)) {
    if (!run.state || (wanted && !wanted.has(run.state.change))) continue;
    try {
      await upsertOverview(ctx, run);
      await syncRunLabels(ctx, run);
    } catch (error) {
      report(ctx, `- ${run.state.change}: publish failed (${errorMessage(error)})`);
    }
  }
}

// ---------------------------------------------------------------------------
// CLI

function argument(args, name) {
  const index = args.indexOf(`--${name}`);
  return index >= 0 ? args[index + 1] : null;
}

function writeOutputs(outputs) {
  const file = process.env.GITHUB_OUTPUT;
  const lines = Object.entries(outputs).map(([key, value]) => `${key}=${JSON.stringify(value)}`);
  if (file) appendFileSync(file, `${lines.join('\n')}\n`);
  else console.log(lines.join('\n'));
}

function writeSummary(title, lines) {
  const file = process.env.GITHUB_STEP_SUMMARY;
  if (!file) return;
  appendFileSync(file, `### ${title}\n\n${lines.length > 0 ? lines.join('\n') : 'Nothing to do.'}\n\n`);
}

export async function main(args = process.argv.slice(2)) {
  const [command] = args;
  const ctx = createContext({ dryRun: args.includes('--dry-run') || process.env.OPENSPEC_DRY_RUN === 'true' });
  const change = argument(args, 'change');
  switch (command) {
    case 'observe': {
      const eventPath = process.env.GITHUB_EVENT_PATH;
      const payload = eventPath ? JSON.parse(readFileSync(eventPath, 'utf8')) : {};
      const { relevant, credit, touched } = await observe(ctx, { eventName: process.env.GITHUB_EVENT_NAME ?? null, payload });
      writeOutputs({ relevant, credit, touched });
      writeSummary('Read current state', ctx.summary);
      return;
    }
    case 'credit':
      await creditChange(ctx, change);
      writeSummary(`Check agent result · ${change}`, ctx.summary);
      return;
    case 'plan': {
      const buckets = await plan(ctx);
      writeOutputs(buckets);
      writeSummary('Decide next steps', ctx.summary);
      return;
    }
    case 'admit':
      await admitChange(ctx, change);
      writeSummary(`Start change · ${change}`, ctx.summary);
      return;
    case 'dispatch': {
      const operation = argument(args, 'operation');
      if (!OPERATIONS.includes(operation)) throw new Error(`Unknown operation: ${operation}`);
      await dispatchChange(ctx, change, operation);
      writeSummary(`${operation} · ${change}`, ctx.summary);
      return;
    }
    case 'gate':
      await notifyGate(ctx, change);
      writeSummary(`Ask for human input · ${change}`, ctx.summary);
      return;
    case 'finalize':
      await finalizeChange(ctx, change);
      writeSummary(`Finish change · ${change}`, ctx.summary);
      return;
    case 'publish': {
      const lists = JSON.parse(process.env.OPENSPEC_TOUCHED || '[]');
      const changes = [...new Set(lists.flatMap((list) => (typeof list === 'string' ? JSON.parse(list || '[]') : list)))];
      await publish(ctx, changes);
      writeSummary('Update PR and issue status', ctx.summary);
      return;
    }
    default:
      throw new Error(`Unknown command: ${command ?? '(none)'}`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
}
