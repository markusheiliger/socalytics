import { randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  LEDGER_MARKER_START,
  QUEUE_STATE_START,
  deriveReadiness,
  parseChangeMarker,
  parseCloudOperationResult,
  parseOwnedTasks,
  parseQueueState,
  renderLedgerEntry,
  renderQueueState,
  retryDecision,
  validateOperationEvidence,
  validateSynchronizedDeltas,
} from './openspec-change-core.mjs';
import { GitHubChangeClient } from './openspec-change-github.mjs';
import {
  renderTwinSection,
  updateManagedTwinBody,
} from './openspec-change-sync.mjs';

const ACTIVE_STATES = ['queued', 'in_progress', 'idle', 'waiting_for_user'];

function labelsOf(issue) {
  return issue.labels.map((label) => typeof label === 'string' ? label : label.name);
}

function branchArtifact(task) {
  return task.artifacts?.find((artifact) => artifact.type === 'branch')?.data ?? null;
}

function latestQueueState(comments) {
  const candidates = comments
    .filter((comment) => comment.user?.login === 'github-actions[bot]')
    .filter((comment) => comment.body?.includes(QUEUE_STATE_START))
    .sort((left, right) => Date.parse(right.updated_at) - Date.parse(left.updated_at));
  if (candidates.length === 0) return null;
  return { comment: candidates[0], state: parseQueueState(candidates[0].body) };
}

function operationPrompt({
  changeRef,
  operation,
  issueNumber,
  baseRef,
  headRef,
  beforeSha,
  attempt,
}) {
  return [
    `Execute exactly one OpenSpec cloud operation.`,
    ``,
    `Change ref: ${changeRef}`,
    `Operation: ${operation}`,
    `Issue number: ${issueNumber}`,
    `Expected base ref: ${baseRef}`,
    `Expected head ref: ${headRef ?? '<create from base>'}`,
    `Expected starting SHA: ${beforeSha}`,
    `Attempt: ${attempt}`,
    ``,
    `Follow the OpenSpec Cloud profile and its binding generated skill.`,
    `Do not open, ready, approve, or merge a pull request.`,
  ].join('\n');
}

function defaultSessionLog(sessionId, agentToken) {
  return execFileSync('gh', ['agent-task', 'view', sessionId, '--log'], {
    encoding: 'utf8',
    env: { ...process.env, GH_TOKEN: agentToken },
    maxBuffer: 10 * 1024 * 1024,
  });
}

function runOpenSpecJson(arguments_, cwd) {
  return JSON.parse(execFileSync('openspec', arguments_, {
    cwd,
    encoding: 'utf8',
    maxBuffer: 10 * 1024 * 1024,
  }));
}

function defaultBranchValidation(changeRef, operation, headRef, expectedSha) {
  execFileSync('git', ['check-ref-format', '--branch', headRef], {
    stdio: 'ignore',
  });
  execFileSync('git', ['fetch', '--no-tags', '--depth=1', 'origin', headRef], {
    stdio: 'ignore',
  });
  const fetchedSha = execFileSync('git', ['rev-parse', 'FETCH_HEAD'], {
    encoding: 'utf8',
  }).trim();
  if (fetchedSha !== expectedSha) {
    throw new Error(`Fetched ${headRef} at ${fetchedSha}, expected ${expectedSha}`);
  }
  const temporaryRoot = mkdtempSync(join(tmpdir(), 'openspec-queue-'));
  try {
    execFileSync('git', ['worktree', 'add', '--detach', temporaryRoot, fetchedSha], {
      stdio: 'ignore',
    });
    const report = runOpenSpecJson(
      [
        'validate',
        changeRef,
        '--type',
        'change',
        '--strict',
        '--json',
        '--no-interactive',
      ],
      temporaryRoot,
    );
    if (report.summary?.totals?.failed !== 0
      || report.items?.length !== 1
      || report.items[0].valid !== true) {
      throw new Error(`Strict OpenSpec validation failed for ${changeRef}@${headRef}`);
    }
    if (operation === 'sync') {
      const change = runOpenSpecJson(
        ['show', changeRef, '--type', 'change', '--json', '--deltas-only'],
        temporaryRoot,
      );
      const specsById = {};
      for (const spec of new Set(change.deltas.map((delta) => delta.spec))) {
        specsById[spec] = runOpenSpecJson(
          ['show', spec, '--type', 'spec', '--json'],
          temporaryRoot,
        );
      }
      validateSynchronizedDeltas(change, specsById);
    }
  } finally {
    try {
      execFileSync('git', ['worktree', 'remove', '--force', temporaryRoot], {
        stdio: 'ignore',
      });
    } finally {
      rmSync(temporaryRoot, { recursive: true, force: true });
    }
  }
}

async function upsertQueueState(client, issueNumber, current, nextState) {
  const body = renderQueueState(nextState);
  if (current) {
    await client.updateIssueComment(current.comment.id, body);
    return { comment: current.comment, state: nextState };
  } else {
    const comment = await client.createIssueComment(issueNumber, body);
    if (!Number.isInteger(comment?.id)) {
      throw new Error('Queue-state comment creation returned no comment id');
    }
    return { comment, state: nextState };
  }
}

async function dispatchOperation({
  client,
  issue,
  marker,
  current,
  operation,
  attempt,
  baseRef,
  headRef,
  beforeSha,
  pullRequestNumber,
  now,
}) {
  const pendingState = {
    version: 1,
    changeRef: marker.ref,
    issueNumber: issue.number,
    status: 'dispatching',
    operation,
    attempt,
    taskId: `pending-${randomUUID()}`,
    sessionId: null,
    baseRef,
    headRef,
    beforeSha,
    pullRequestNumber,
    updatedAt: now().toISOString(),
  };
  const persisted = await upsertQueueState(
    client,
    issue.number,
    current,
    pendingState,
  );
  const task = await client.startAgentTask({
    prompt: operationPrompt({
      changeRef: marker.ref,
      operation,
      issueNumber: issue.number,
      baseRef,
      headRef,
      beforeSha,
      attempt,
    }),
    customAgent: 'openspec-cloud',
    baseRef,
    headRef,
    createPullRequest: !headRef,
  });
  const nextState = {
    ...pendingState,
    status: 'dispatched',
    taskId: task.id,
    updatedAt: now().toISOString(),
  };
  await upsertQueueState(client, issue.number, persisted, nextState);
  return { action: 'dispatched', operation, attempt, taskId: task.id };
}

async function appendLedger(client, issueNumber, state, {
  sessionId,
  headRef,
  afterSha,
  outcome,
  validation,
  recovery,
  now,
}) {
  await client.createIssueComment(issueNumber, renderLedgerEntry({
    version: 1,
    changeRef: state.changeRef,
    operation: state.operation,
    attempt: state.attempt,
    taskId: state.taskId,
    sessionId,
    branch: headRef,
    beforeSha: state.beforeSha,
    afterSha,
    outcome,
    validation,
    recovery,
    recordedAt: now().toISOString(),
  }));
}

async function findArchivePath(client, ref, headRef) {
  const entries = await client.getRepositoryContent('openspec/changes/archive', headRef);
  if (!Array.isArray(entries)) return null;
  const matches = entries.filter((entry) => entry.type === 'dir'
    && new RegExp(`^\\d{4}-\\d{2}-\\d{2}-${ref}$`).test(entry.name));
  if (matches.length > 1) throw new Error(`Multiple archive directories found for ${ref}`);
  return matches[0]?.path ?? null;
}

async function projectArchiveOnBranch(client, issue, ref, archivePath, headRef) {
  const entries = await client.getRepositoryContent(archivePath, headRef);
  if (!Array.isArray(entries)) {
    throw new Error(`Archive path is not a directory: ${archivePath}`);
  }
  const byName = new Map(entries.map((entry) => [entry.name, entry]));
  const artifacts = [
    ['Proposal', 'proposal.md'],
    ['Design', 'design.md'],
    ['Tasks', 'tasks.md'],
    ['Specifications', 'specs'],
  ].filter(([, name]) => byName.has(name))
    .map(([label, name]) => [label, `${archivePath}/${name}`]);
  const section = renderTwinSection({
    ref,
    lifecycle: 'archived',
    path: archivePath,
    artifacts,
  }, `${client.owner}/${client.repo}`, headRef);
  await client.updateIssue(issue.number, {
    body: updateManagedTwinBody(issue.body, section),
  });
}

async function projectActiveOnMain(client, issue, ref) {
  const path = `openspec/changes/${ref}`;
  const entries = await client.getRepositoryContent(path, 'main');
  if (!Array.isArray(entries)) {
    throw new Error(`Active change path is not a directory: ${path}`);
  }
  const names = new Set(entries.map((entry) => entry.name));
  const artifacts = [
    ['Proposal', 'proposal.md'],
    ['Design', 'design.md'],
    ['Tasks', 'tasks.md'],
    ['Specifications', 'specs'],
  ].filter(([, name]) => names.has(name))
    .map(([label, name]) => [label, `${path}/${name}`]);
  const section = renderTwinSection({
    ref,
    lifecycle: 'active',
    path,
    artifacts,
  }, `${client.owner}/${client.repo}`, 'main');
  await client.updateIssue(issue.number, {
    body: updateManagedTwinBody(issue.body, section),
  });
}

async function validateCompletedOperation({
  client,
  state,
  task,
  getSessionLog,
  agentToken,
  validateBranch,
}) {
  const artifact = branchArtifact(task);
  const headRef = artifact?.head_ref ?? state.headRef;
  if (!headRef) throw new Error('Completed Agent Task has no branch artifact');
  const branch = await client.getBranch(headRef);
  const afterSha = branch.commit.sha;
  if (state.operation === 'verify' || state.operation === 'sync') {
    await validateBranch(state.changeRef, state.operation, headRef, afterSha);
  }
  const session = task.sessions?.at(-1);
  if (!session?.id) throw new Error('Completed Agent Task has no session');
  const log = await getSessionLog(session.id, agentToken);
  const result = parseCloudOperationResult(log);
  if (result.changeRef !== state.changeRef || result.operation !== state.operation) {
    throw new Error('Cloud operation result does not match queue state');
  }
  if (result.verdict !== 'pass') {
    return {
      valid: false,
      validation: result.validation,
      afterSha,
      headRef,
      sessionId: session.id,
      archivePath: null,
    };
  }

  let tasksComplete;
  let verificationPassed;
  let specsSynchronized;
  let lifecycle = 'active';
  let archivePath = null;
  if (state.operation === 'apply') {
    const tasks = await client.getTextContent(
      `openspec/changes/${state.changeRef}/tasks.md`,
      headRef,
    );
    tasksComplete = parseOwnedTasks(tasks).every((taskEntry) => taskEntry.completed);
  } else if (state.operation === 'verify') {
    verificationPassed = true;
  } else if (state.operation === 'sync') {
    specsSynchronized = true;
  } else if (state.operation === 'archive') {
    archivePath = await findArchivePath(client, state.changeRef, headRef);
    lifecycle = archivePath ? 'archived' : 'active';
  }

  const evidence = validateOperationEvidence({
    operation: state.operation,
    outcome: 'succeeded',
    beforeSha: state.beforeSha,
    afterSha,
    filesChanged: state.beforeSha !== afterSha,
    tasksComplete,
    verificationPassed,
    specsSynchronized,
    lifecycle,
  });
  return {
    valid: evidence.valid,
    validation: evidence.valid ? result.validation : evidence.reason,
    afterSha,
    headRef,
    sessionId: session.id,
    archivePath,
  };
}

async function pullRequestForHead(client, headRef) {
  if (!headRef) return null;
  const pulls = await client.listOpenPullRequestsForHead(headRef);
  if (pulls.length > 1) throw new Error(`Multiple open pull requests target ${headRef}`);
  return pulls[0] ?? null;
}

export async function reconcileIssue({
  client,
  issue,
  agentToken,
  getSessionLog = defaultSessionLog,
  validateBranch = defaultBranchValidation,
  now = () => new Date(),
}) {
  const marker = parseChangeMarker(issue.body ?? '');
  if (marker.repository !== `${client.owner}/${client.repo}`) {
    throw new Error(`Issue #${issue.number} marker targets another repository`);
  }
  const comments = await client.listIssueComments(issue.number);
  const current = latestQueueState(comments);
  const state = current?.state ?? null;
  const blockers = await client.listBlockedBy(issue.number);
  const blockerStates = [];
  for (const blocker of blockers) {
    let blockerMarker = null;
    try {
      blockerMarker = parseChangeMarker(blocker.body ?? '');
    } catch {
      // A manual non-OpenSpec blocker remains blocking until explicitly closed.
    }
    let archivedOnMain = blocker.state === 'closed' && blockerMarker === null;
    if (blocker.state === 'closed'
      && blockerMarker?.lifecycle === 'archived'
      && blockerMarker.gitRef === 'main') {
      try {
        await client.getRepositoryContent(blockerMarker.path, 'main');
        archivedOnMain = true;
      } catch {
        archivedOnMain = false;
      }
    }
    blockerStates.push({
      number: blocker.number,
      state: blocker.state,
      archivedOnMain,
    });
  }
  const readiness = deriveReadiness({
    issueState: issue.state,
    labels: labelsOf(issue),
    lifecycle: marker.lifecycle,
    blockers: blockerStates,
    activeTaskState: state?.status === 'dispatched' ? 'in_progress' : null,
    activeTaskAttempt: state?.attempt ?? 1,
    needsDecision: state?.status === 'needs_attention',
  });

  if (!state) {
    if (!readiness.runnable) return { action: 'waiting', reasons: readiness.reasons };
    const main = await client.getBranch('main');
    const tasks = await client.getTextContent(
      `openspec/changes/${marker.ref}/tasks.md`,
      'main',
    );
    parseOwnedTasks(tasks);
    return dispatchOperation({
      client,
      issue,
      marker,
      current,
      operation: 'apply',
      attempt: 1,
      baseRef: 'main',
      headRef: null,
      beforeSha: main.commit.sha,
      pullRequestNumber: null,
      now,
    });
  }

  if (state.status === 'awaiting_human_review' && state.pullRequestNumber) {
    const pull = await client.getPullRequest(state.pullRequestNumber);
    if (pull.state === 'closed' && !pull.merged_at) {
      await projectActiveOnMain(client, issue, state.changeRef);
      await upsertQueueState(client, issue.number, current, {
        ...state,
        status: 'needs_attention',
        updatedAt: now().toISOString(),
      });
      return { action: 'needs_attention', reason: 'pull-request-closed-unmerged' };
    }
  }
  if (state.status !== 'dispatched') {
    return { action: 'waiting', reasons: [state.status] };
  }

  const task = await client.getAgentTask(state.taskId);
  const artifact = branchArtifact(task);
  const headRef = artifact?.head_ref ?? state.headRef;
  const sessionId = task.sessions?.at(-1)?.id ?? state.sessionId;
  if (task.state === 'waiting_for_user') {
    const resolvedHead = headRef ?? state.baseRef;
    const branch = await client.getBranch(resolvedHead);
    await appendLedger(client, issue.number, state, {
      sessionId: sessionId ?? 'unavailable',
      headRef: resolvedHead,
      afterSha: branch.commit.sha,
      outcome: 'waiting_for_user',
      validation: 'Agent Task requires a new human decision.',
      recovery: 'Respond to the Agent Task, then explicitly repair or replay the queue state.',
      now,
    });
    await upsertQueueState(client, issue.number, current, {
      ...state,
      status: 'needs_attention',
      headRef,
      sessionId,
      updatedAt: now().toISOString(),
    });
    return { action: 'needs_attention', reason: 'waiting_for_user' };
  }
  if (ACTIVE_STATES.includes(task.state)) {
    if (headRef !== state.headRef || sessionId !== state.sessionId) {
      await upsertQueueState(client, issue.number, current, {
        ...state,
        headRef,
        sessionId,
        updatedAt: now().toISOString(),
      });
    }
    return { action: 'waiting', reasons: [`agent-task-${task.state}`] };
  }

  if (task.state !== 'completed') {
    const decision = retryDecision(task.state, state.attempt);
    const resolvedHead = headRef ?? state.headRef;
    const checkpointRef = resolvedHead ?? state.baseRef;
    const branch = await client.getBranch(checkpointRef);
    const afterSha = branch.commit.sha;
    await appendLedger(client, issue.number, state, {
      sessionId: sessionId ?? 'unavailable',
      headRef: checkpointRef,
      afterSha,
      outcome: task.state,
      validation: `Agent Task ended in ${task.state}.`,
      recovery: decision === 'retry' ? 'Automatic retry dispatched.' : 'Human recovery required.',
      now,
    });
    const stillAuthorized = issue.state === 'open'
      && labelsOf(issue).includes('openspec:enqueued');
    if (decision === 'retry' && stillAuthorized) {
      const pull = await pullRequestForHead(client, resolvedHead);
      return dispatchOperation({
        client,
        issue,
        marker,
        current,
        operation: state.operation,
        attempt: state.attempt + 1,
        baseRef: state.baseRef,
        headRef: resolvedHead,
        beforeSha: afterSha,
        pullRequestNumber: pull?.number ?? state.pullRequestNumber,
        now,
      });
    }
    await upsertQueueState(client, issue.number, current, {
      ...state,
      status: 'needs_attention',
      headRef: resolvedHead,
      sessionId,
      updatedAt: now().toISOString(),
    });
    return { action: 'needs_attention', reason: task.state };
  }

  let completed;
  try {
    completed = await validateCompletedOperation({
      client,
      state,
      task,
      getSessionLog,
      agentToken,
      validateBranch,
    });
  } catch (error) {
    const resolvedHead = headRef ?? state.headRef ?? 'main';
    const branch = await client.getBranch(resolvedHead);
    await appendLedger(client, issue.number, state, {
      sessionId: sessionId ?? 'unavailable',
      headRef: resolvedHead,
      afterSha: branch.commit.sha,
      outcome: 'failed',
      validation: error.message,
      recovery: 'Human inspection is required because durable result validation failed.',
      now,
    });
    await upsertQueueState(client, issue.number, current, {
      ...state,
      status: 'needs_attention',
      headRef: resolvedHead,
      sessionId,
      updatedAt: now().toISOString(),
    });
    return { action: 'needs_attention', reason: 'invalid-result' };
  }

  await appendLedger(client, issue.number, state, {
    sessionId: completed.sessionId,
    headRef: completed.headRef,
    afterSha: completed.afterSha,
    outcome: completed.valid ? 'succeeded' : 'failed',
    validation: completed.validation,
    recovery: completed.valid ? null : 'Human remediation is required before replay.',
    now,
  });
  if (!completed.valid) {
    await upsertQueueState(client, issue.number, current, {
      ...state,
      status: 'needs_attention',
      headRef: completed.headRef,
      sessionId: completed.sessionId,
      updatedAt: now().toISOString(),
    });
    return { action: 'needs_attention', reason: completed.validation };
  }

  const pull = await pullRequestForHead(client, completed.headRef);
  if (state.operation === 'archive') {
    await projectArchiveOnBranch(
      client,
      issue,
      state.changeRef,
      completed.archivePath,
      completed.headRef,
    );
    await upsertQueueState(client, issue.number, current, {
      ...state,
      status: 'awaiting_human_review',
      headRef: completed.headRef,
      sessionId: completed.sessionId,
      pullRequestNumber: pull?.number ?? state.pullRequestNumber,
      updatedAt: now().toISOString(),
    });
    return {
      action: 'awaiting_human_review',
      pullRequestNumber: pull?.number ?? state.pullRequestNumber,
      archivePath: completed.archivePath,
    };
  }

  if (issue.state !== 'open' || !labelsOf(issue).includes('openspec:enqueued')) {
    await upsertQueueState(client, issue.number, current, {
      ...state,
      status: 'needs_attention',
      headRef: completed.headRef,
      sessionId: completed.sessionId,
      pullRequestNumber: pull?.number ?? state.pullRequestNumber,
      updatedAt: now().toISOString(),
    });
    return { action: 'needs_attention', reason: 'enqueue-authorization-removed' };
  }

  const nextOperation = {
    apply: 'verify',
    verify: 'sync',
    sync: 'archive',
  }[state.operation];
  return dispatchOperation({
    client,
    issue,
    marker,
    current,
    operation: nextOperation,
    attempt: 1,
    baseRef: state.baseRef,
    headRef: completed.headRef,
    beforeSha: completed.afterSha,
    pullRequestNumber: pull?.number ?? state.pullRequestNumber,
    now,
  });
}

export async function reconcileAll({
  client,
  agentToken,
  getSessionLog = defaultSessionLog,
  validateBranch = defaultBranchValidation,
  now = () => new Date(),
}) {
  const issues = await client.listIssueTwins();
  const refs = new Set();
  for (const issue of issues) {
    const marker = parseChangeMarker(issue.body ?? '');
    if (refs.has(marker.ref)) throw new Error(`Duplicate issue twins for ${marker.ref}`);
    refs.add(marker.ref);
  }
  const results = [];
  for (const issue of issues) {
    results.push({
      issueNumber: issue.number,
      ...(await reconcileIssue({
        client,
        issue,
        agentToken,
        getSessionLog,
        validateBranch,
        now,
      })),
    });
  }
  return results;
}

async function main() {
  const repository = process.env.GITHUB_REPOSITORY;
  const repositoryToken = process.env.GITHUB_TOKEN;
  const agentToken = process.env.COPILOT_AGENT_TOKEN;
  if (!repository || !repositoryToken || !agentToken) {
    throw new Error('GITHUB_REPOSITORY, GITHUB_TOKEN, and COPILOT_AGENT_TOKEN are required');
  }
  const [owner, repo] = repository.split('/');
  const client = new GitHubChangeClient({
    owner,
    repo,
    repositoryToken,
    agentToken,
  });
  const results = await reconcileAll({ client, agentToken });
  process.stdout.write(`${JSON.stringify(results, null, 2)}\n`);
}

const isEntrypoint = process.argv[1]
  && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isEntrypoint) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
