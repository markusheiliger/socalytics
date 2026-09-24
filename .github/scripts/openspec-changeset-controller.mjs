#!/usr/bin/env node

import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  calculateRunnableFrontier,
  hashChangeset,
  parseChangesetIssue,
  renderMermaid,
} from './openspec-changeset-core.mjs';
import { GitHubClient, normalizeAgentTask } from './openspec-changeset-github.mjs';

export const LEDGER_START = '<!-- openspec-changeset-ledger:v1:start -->';
export const LEDGER_END = '<!-- openspec-changeset-ledger:v1:end -->';
export const SUMMARY_START = '<!-- openspec-changeset-summary:v1:start -->';
export const SUMMARY_END = '<!-- openspec-changeset-summary:v1:end -->';
export const PR_MARKER = 'openspec-changeset-pr:v1';
export const AUTO_MERGE_MARKER = '<!-- openspec-changeset-auto-merge:v1 -->';
export const BLOCKER_START = '<!-- openspec-change-blocker:v1:start -->';
export const BLOCKER_END = '<!-- openspec-change-blocker:v1:end -->';

const MANAGED_LABELS = new Set([
  'changeset:ready',
  'changeset:running',
  'changeset:attention',
  'changeset:complete',
]);
const LABEL_DEFINITIONS = [
  ['openspec:changeset', '1f6feb', 'OpenSpec changeset orchestration'],
  ['openspec:change-blocker', 'd93f0b', 'Human action required for an OpenSpec change'],
  ['changeset:ready', '0e8a16', 'Ready for changeset reconciliation'],
  ['changeset:running', 'fbca04', 'Changeset has active cloud-agent work'],
  ['changeset:attention', 'd93f0b', 'Changeset requires operator attention'],
  ['changeset:complete', '6f42c1', 'All changes in the changeset are archived'],
];

function markerJson(body, start, end) {
  if (typeof body !== 'string') {
    return null;
  }
  const startIndex = body.indexOf(start);
  const endIndex = body.indexOf(end, startIndex + start.length);
  if (startIndex < 0 || endIndex < 0) {
    return null;
  }
  try {
    return JSON.parse(body.slice(startIndex + start.length, endIndex).trim());
  } catch {
    return null;
  }
}

export function parseLedgerComment(comment) {
  const record = markerJson(comment.body, LEDGER_START, LEDGER_END);
  return record ? { ...record, commentId: comment.id } : null;
}

export function parsePullRequestMarker(pullRequest) {
  const pattern = new RegExp(`<!--\\s*${PR_MARKER}\\s+({[\\s\\S]*?})\\s*-->`);
  const match = pullRequest.body?.match(pattern);
  if (!match) {
    return null;
  }
  try {
    return { ...JSON.parse(match[1]), pullRequest };
  } catch {
    return null;
  }
}

export function formatLedger(record) {
  return `${LEDGER_START}\n${JSON.stringify(record, null, 2)}\n${LEDGER_END}`;
}

function labelNames(issue) {
  return (issue.labels ?? []).map((label) => typeof label === 'string' ? label : label.name);
}

async function setState(client, issue, state) {
  const labels = labelNames(issue).filter((label) => !MANAGED_LABELS.has(label));
  labels.push(state);
  await client.setIssueLabels(issue.number, [...new Set(labels)]);
}

function archiveEntries(root) {
  const archiveRoot = resolve(root, 'openspec/changes/archive');
  if (!existsSync(archiveRoot)) {
    return [];
  }
  return readdirSync(archiveRoot, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name);
}

export function isArchived(root, ref) {
  return archiveEntries(root).some((entry) => entry === ref || entry.endsWith(`-${ref}`));
}

function assertChangesExist(root, changeset) {
  for (const change of changeset.changes) {
    const activeMetadata = resolve(root, 'openspec/changes', change.ref, '.openspec.yaml');
    if (!existsSync(activeMetadata) && !isArchived(root, change.ref)) {
      throw new Error(`OpenSpec change does not exist as active or archived: ${change.ref}`);
    }
  }
}

function latestByChange(records) {
  const latest = new Map();
  for (const record of records) {
    if (record?.change && record.change !== '*') {
      latest.set(record.change, record);
    }
  }
  return latest;
}

function acceptedGraphHash(records) {
  const accepted = records.filter((record) => record?.status === 'graph-accepted');
  if (accepted.length > 0) {
    return accepted.at(-1).graphHash;
  }
  return records.find((record) => record?.graphHash)?.graphHash;
}

function pullState(pulls, issueNumber, ref) {
  return pulls
    .map(parsePullRequestMarker)
    .filter(Boolean)
    .filter((marker) => marker.changeset === issueNumber && marker.change === ref)
    .sort((left, right) => new Date(right.pullRequest.updated_at) - new Date(left.pullRequest.updated_at))[0];
}

function expired(record, now) {
  if (!record?.createdAt) {
    return true;
  }
  const age = now.getTime() - new Date(record.createdAt).getTime();
  const limit = record.status === 'reserved' ? 15 * 60_000 : 2 * 60 * 60_000;
  return age > limit;
}

function replaceTemplate(template, values) {
  return Object.entries(values).reduce(
    (result, [key, value]) => result.replaceAll(`{{${key}}}`, String(value)),
    template,
  );
}

function summaryBody(changeset, states, graphHash) {
  const rows = changeset.changes.map((change) => `| ${change.ref} | ${states.get(change.ref)} |`);
  return [
    SUMMARY_START,
    `Graph hash: \`${graphHash}\``,
    '',
    '| Change | State |',
    '| --- | --- |',
    ...rows,
    '',
    '```mermaid',
    renderMermaid(changeset).trimEnd(),
    '```',
    SUMMARY_END,
  ].join('\n');
}

async function upsertSummary(client, issueNumber, comments, body) {
  const existing = comments.find((comment) => comment.body?.includes(SUMMARY_START));
  if (existing) {
    await client.updateIssueComment(existing.id, body);
  } else {
    await client.createIssueComment(issueNumber, body);
  }
}

export async function reconcileChangeset({
  client,
  issueNumber,
  root = process.cwd(),
  mode = 'reconcile',
  now = new Date(),
  taskTemplate,
  resumeTemplate,
  defaultBranch = 'main',
  blockerIssue,
}) {
  const issue = await client.getIssue(issueNumber);
  const changeset = parseChangesetIssue(issue.body);
  assertChangesExist(root, changeset);
  const graphHash = hashChangeset(changeset);
  const comments = await client.listIssueComments(issueNumber);
  const pulls = await client.listPullRequests();
  const childIssues = await client.listSubIssues(issueNumber);
  const records = comments.map(parseLedgerComment).filter(Boolean);
  const acceptedHash = acceptedGraphHash(records);
  const classifiedBlockers = childIssues.filter((child) => child.state === 'open'
    && labelNames(child).includes('openspec:change-blocker'));
  const parsedBlockers = classifiedBlockers.map(parseBlockerIssue);
  const invalidBlockers = parsedBlockers.filter((blocker) => !blocker
    || blocker.version !== 1
    || blocker.parentIssue !== issueNumber
    || !changeset.changes.some((change) => change.ref === blocker.change));

  if (acceptedHash && acceptedHash !== graphHash && mode !== 'accept-graph') {
    if (mode !== 'dry-run') {
      await setState(client, issue, 'changeset:attention');
    }
    return { issueNumber, graphHash, state: 'attention', reason: 'graph-changed', frontier: [] };
  }

  if (mode === 'accept-graph') {
    await client.createIssueComment(issueNumber, formatLedger({
      version: 1,
      change: '*',
      status: 'graph-accepted',
      graphHash,
      createdAt: now.toISOString(),
    }));
  }

  if (invalidBlockers.length > 0 && mode !== 'resume') {
    if (mode !== 'dry-run') {
      await setState(client, issue, 'changeset:attention');
    }
    return { issueNumber, graphHash, state: 'attention', reason: 'invalid-blocker', frontier: [] };
  }

  if (mode === 'resume') {
    if (!resumeTemplate) {
      throw new Error('Cloud-agent resume template is required');
    }
    const blockers = classifiedBlockers
      .map(parseBlockerIssue)
      .filter((blocker) => blocker?.version === 1 && blocker.parentIssue === issueNumber)
      .filter((blocker) => blockerIssue ? blocker.issue.number === blockerIssue : true);
    if (blockers.length !== 1) {
      throw new Error(blockers.length === 0
        ? 'No matching open change blocker was found'
        : 'Multiple open change blockers were found; specify --blocker');
    }

    const blocker = blockers[0];
    const change = changeset.changes.find((candidate) => candidate.ref === blocker.change);
    if (!change) {
      throw new Error(`Blocker references a change outside this changeset: ${blocker.change}`);
    }
    if (isArchived(root, change.ref)) {
      throw new Error(`Blocked change is already archived: ${change.ref}`);
    }
    const incompleteDependency = change.dependsOn.find((dependency) => !isArchived(root, dependency));
    if (incompleteDependency) {
      throw new Error(`Blocked change dependency is not archived: ${incompleteDependency}`);
    }
    if (!blocker.headRef || !blocker.checkpointSha || blocker.baseRef !== defaultBranch) {
      throw new Error('Blocker marker is missing a valid branch checkpoint');
    }
    const previousRecord = latestByChange(records).get(change.ref);
    if (previousRecord?.status === 'reserved' && !expired(previousRecord, now)) {
      throw new Error(`Change already has an active reservation: ${change.ref}`);
    }
    if (previousRecord?.status === 'dispatched' && previousRecord.taskId && previousRecord.taskId !== 'unknown') {
      const previousTask = normalizeAgentTask(await client.getAgentTask(previousRecord.taskId));
      if (['queued', 'in_progress', 'idle'].includes(previousTask.state)) {
        throw new Error(`Change already has an active task: ${previousRecord.taskId}`);
      }
    }
    const existingPull = pullState(pulls, issueNumber, change.ref)?.pullRequest;
    if (existingPull?.state === 'open') {
      throw new Error(`Change already has an open pull request: #${existingPull.number}`);
    }
    const comparison = await client.compareCommits(blocker.checkpointSha, blocker.headRef);
    if (!['ahead', 'identical'].includes(comparison.status)) {
      throw new Error(`Checkpoint ${blocker.checkpointSha} is not reachable from ${blocker.headRef}`);
    }

    const previousAttempt = latestByChange(records).get(change.ref)?.attempt ?? 0;
    const reservation = {
      version: 1,
      change: change.ref,
      status: 'reserved',
      graphHash,
      attempt: previousAttempt + 1,
      blockerIssue: blocker.issue.number,
      headRef: blocker.headRef,
      checkpointSha: blocker.checkpointSha,
      createdAt: now.toISOString(),
    };
    const comment = await client.createIssueComment(issueNumber, formatLedger(reservation));
    try {
      const prompt = replaceTemplate(resumeTemplate, {
        CHANGE_REF: change.ref,
        CHANGESET_ISSUE: issueNumber,
        BLOCKER_ISSUE: blocker.issue.number,
        DEFAULT_BRANCH: defaultBranch,
        HEAD_REF: blocker.headRef,
        CHECKPOINT_SHA: blocker.checkpointSha,
        PR_MARKER: `<!-- ${PR_MARKER} {"changeset":${issueNumber},"change":"${change.ref}"} -->`,
        AUTO_MERGE_MARKER,
      });
      const task = await client.createAgentTask({
        prompt,
        baseRef: defaultBranch,
        headRef: blocker.headRef,
      });
      const taskId = task?.id ?? task?.task_id ?? task?.task?.id;
      await client.updateIssueComment(comment.id, formatLedger({
        ...reservation,
        status: 'dispatched',
        taskId: taskId ?? 'unknown',
      }));
      await setState(client, issue, 'changeset:running');
      return { issueNumber, graphHash, state: 'running', resumed: change.ref, blockerIssue: blocker.issue.number };
    } catch (error) {
      await client.updateIssueComment(comment.id, formatLedger({
        ...reservation,
        status: 'failed',
        error: error.message,
      }));
      await setState(client, issue, 'changeset:attention');
      return { issueNumber, graphHash, state: 'attention', resumed: change.ref, blockerIssue: blocker.issue.number };
    }
  }

  const latest = latestByChange(records);
  const completed = [];
  const active = [];
  const attention = [];
  const autoMergeCandidates = [];
  const blockersToClose = [];
  const states = new Map();
  const blockerGroups = Map.groupBy(parsedBlockers.filter(Boolean), (blocker) => blocker.change);
  const blockersByChange = new Map([...blockerGroups]
    .filter(([, blockers]) => blockers.length === 1)
    .map(([change, blockers]) => [change, blockers[0]]));
  const duplicateBlockers = new Set([...blockerGroups]
    .filter(([, blockers]) => blockers.length > 1)
    .map(([change]) => change));

  for (const change of changeset.changes) {
    const ref = change.ref;
    const pull = pullState(pulls, issueNumber, ref)?.pullRequest;
    const record = latest.get(ref);
    const blocker = blockersByChange.get(ref);

    if (isArchived(root, ref)) {
      completed.push(ref);
      states.set(ref, 'completed');
    } else if (pull?.merged_at) {
      attention.push(ref);
      states.set(ref, 'merged-without-archive');
    } else if (pull?.state === 'open') {
      if (!pull.body?.includes(AUTO_MERGE_MARKER)) {
        active.push(ref);
        attention.push(ref);
        states.set(ref, 'pull-request-partial');
      } else {
        if (blocker) {
          blockersToClose.push(blocker.issue.number);
        }
        active.push(ref);
        if (pull.auto_merge) {
          states.set(ref, 'auto-merge-pending');
        } else {
          autoMergeCandidates.push({ ref, pull });
          states.set(ref, 'auto-merge-ready');
        }
      }
    } else if (pull?.state === 'closed' && mode !== 'retry') {
      attention.push(ref);
      states.set(ref, 'pull-request-closed');
    } else if (duplicateBlockers.has(ref)) {
      attention.push(ref);
      states.set(ref, 'duplicate-blockers');
    } else if (blocker) {
      attention.push(ref);
      const issueReference = blocker.issue.html_url
        ? `[#${blocker.issue.number}](${blocker.issue.html_url})`
        : `#${blocker.issue.number}`;
      states.set(ref, `blocked-human (${issueReference}, \`${blocker.headRef}\`)`);
    } else if (record?.status === 'failed' && mode !== 'retry') {
      attention.push(ref);
      states.set(ref, 'dispatch-failed');
    } else if (record?.status === 'dispatched') {
      const task = record.taskId && record.taskId !== 'unknown'
        ? normalizeAgentTask(await client.getAgentTask(record.taskId))
        : null;
      const terminalFailure = ['failed', 'timed_out', 'cancelled'].includes(task?.state);
      const missingOutcome = ['completed', 'idle', 'waiting_for_user'].includes(task?.state);
      if ((terminalFailure || expired(record, now)) && mode === 'retry') {
        states.set(ref, 'ready-for-retry');
      } else if (terminalFailure) {
        attention.push(ref);
        states.set(ref, `task-${task.state}`);
      } else if (missingOutcome) {
        attention.push(ref);
        states.set(ref, `task-${task.state}-without-outcome`);
      } else if (expired(record, now)) {
        attention.push(ref);
        states.set(ref, 'dispatched-expired');
      } else {
        active.push(ref);
        states.set(ref, task?.state ? `task-${task.state}` : 'dispatched');
      }
    } else if (record?.status === 'reserved') {
      if (expired(record, now) && mode !== 'retry') {
        attention.push(ref);
        states.set(ref, 'reserved-expired');
      } else if (expired(record, now) && mode === 'retry') {
        states.set(ref, 'ready-for-retry');
      } else {
        active.push(ref);
        states.set(ref, 'reserved');
      }
    } else if (mode === 'retry' && (pull?.state === 'closed' || record?.status === 'failed')) {
      states.set(ref, 'ready-for-retry');
    } else {
      states.set(ref, 'blocked');
    }
  }

  if (mode !== 'dry-run') {
    for (const blockerNumber of blockersToClose) {
      await client.updateIssue(blockerNumber, { state: 'closed', state_reason: 'completed' });
    }
    for (const { ref, pull } of autoMergeCandidates) {
      try {
        await client.enablePullRequestAutoMerge(pull.node_id);
        states.set(ref, 'auto-merge-pending');
      } catch {
        attention.push(ref);
        states.set(ref, 'auto-merge-failed');
      }
    }
  }

  if (attention.length > 0) {
    if (mode !== 'dry-run') {
      await setState(client, issue, 'changeset:attention');
      await upsertSummary(client, issueNumber, comments, summaryBody(changeset, states, graphHash));
    }
    return { issueNumber, graphHash, state: 'attention', attention, frontier: [] };
  }

  const frontier = calculateRunnableFrontier(changeset, completed, active);
  for (const ref of frontier) {
    states.set(ref, 'ready');
  }

  if (mode === 'dry-run') {
    return { issueNumber, graphHash, state: completed.length === changeset.changes.length ? 'complete' : 'dry-run', frontier, completed, active };
  }

  if (completed.length === changeset.changes.length) {
    await setState(client, issue, 'changeset:complete');
    await upsertSummary(client, issueNumber, comments, summaryBody(changeset, states, graphHash));
    await client.updateIssue(issueNumber, { state: 'closed', state_reason: 'completed' });
    return { issueNumber, graphHash, state: 'complete', frontier: [] };
  }

  if (!taskTemplate && frontier.length > 0) {
    throw new Error('Cloud-agent task template is required when changes are ready');
  }

  let failed = false;
  for (const ref of frontier) {
    const previousAttempt = latest.get(ref)?.attempt ?? 0;
    const reservation = {
      version: 1,
      change: ref,
      status: 'reserved',
      graphHash,
      attempt: previousAttempt + 1,
      createdAt: now.toISOString(),
    };
    const comment = await client.createIssueComment(issueNumber, formatLedger(reservation));
    try {
      const prompt = replaceTemplate(taskTemplate, {
        CHANGE_REF: ref,
        CHANGESET_ISSUE: issueNumber,
        DEFAULT_BRANCH: defaultBranch,
        PR_MARKER: `<!-- ${PR_MARKER} {"changeset":${issueNumber},"change":"${ref}"} -->`,
        AUTO_MERGE_MARKER,
      });
      const task = await client.createAgentTask({ prompt, baseRef: defaultBranch });
      const normalizedTask = normalizeAgentTask(task);
      await client.updateIssueComment(comment.id, formatLedger({
        ...reservation,
        status: 'dispatched',
        taskId: normalizedTask.id ?? 'unknown',
        ...(normalizedTask.branch ? {
          baseRef: normalizedTask.branch.baseRef,
          headRef: normalizedTask.branch.headRef,
        } : {}),
      }));
      states.set(ref, 'dispatched');
    } catch (error) {
      failed = true;
      await client.updateIssueComment(comment.id, formatLedger({
        ...reservation,
        status: 'failed',
        error: error.message,
      }));
      states.set(ref, 'dispatch-failed');
    }
  }

  await setState(client, issue, failed ? 'changeset:attention' : 'changeset:running');
  await upsertSummary(client, issueNumber, comments, summaryBody(changeset, states, graphHash));
  return { issueNumber, graphHash, state: failed ? 'attention' : 'running', frontier };
}

function parseArguments(argv) {
  const [command = 'dry-run', ...rest] = argv;
  const values = {};
  for (let index = 0; index < rest.length; index += 1) {
    if (!rest[index].startsWith('--')) {
      throw new Error(`Unexpected argument: ${rest[index]}`);
    }
    values[rest[index].slice(2)] = rest[index + 1];
    index += 1;
  }
  return { command, values };
}

async function main() {
  const { command, values } = parseArguments(process.argv.slice(2));
  const repository = process.env.GITHUB_REPOSITORY;
  const client = new GitHubClient({
    repository,
    token: process.env.GITHUB_TOKEN,
    agentToken: process.env.COPILOT_AGENT_TOKEN,
    apiUrl: process.env.GITHUB_API_URL,
  });

  if (command === 'list') {
    const issues = await client.listChangesetIssues();
    const issueNumbers = values.issue ? [Number(values.issue)] : issues.map((issue) => issue.number);
    process.stdout.write(`${JSON.stringify(issueNumbers)}\n`);
    return;
  }

  if (!values.issue) {
    throw new Error('--issue is required');
  }
  if (!['validate', 'dry-run', 'reconcile', 'retry', 'resume', 'accept-graph'].includes(command)) {
    throw new Error(`Unknown command: ${command}`);
  }

  if (['reconcile', 'retry', 'resume', 'accept-graph'].includes(command)) {
    for (const definition of LABEL_DEFINITIONS) {
      await client.ensureLabel(...definition);
    }
  }

  const taskTemplatePath = resolve(process.cwd(), '.github/skills/soca-changeset/references/cloud-agent-task.md');
  const taskTemplate = existsSync(taskTemplatePath) ? readFileSync(taskTemplatePath, 'utf8') : undefined;
  const resumeTemplatePath = resolve(process.cwd(), '.github/skills/soca-changeset/references/cloud-agent-resume-task.md');
  const resumeTemplate = existsSync(resumeTemplatePath) ? readFileSync(resumeTemplatePath, 'utf8') : undefined;
  const result = await reconcileChangeset({
    client,
    issueNumber: Number(values.issue),
    mode: command === 'validate' ? 'dry-run' : command,
    taskTemplate,
    resumeTemplate,
    defaultBranch: values['default-branch'] ?? process.env.GITHUB_DEFAULT_BRANCH ?? 'main',
    blockerIssue: values.blocker ? Number(values.blocker) : undefined,
  });
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

const isEntrypoint = process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href;
if (isEntrypoint) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

export function parseBlockerIssue(issue) {
  const record = markerJson(issue.body, BLOCKER_START, BLOCKER_END);
  return record ? { ...record, issue } : null;
}