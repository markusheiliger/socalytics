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
  parseCapabilityDefinition,
  parseCapabilityTasks,
  parseChangeMarker,
  parseQueueOperationResult,
  parseQueueState,
  renderLedgerEntry,
  renderQueueState,
  retryDecision,
  validateCapabilitySet,
  validateOperationEvidence,
  validateSynchronizedDeltas,
} from './openspec-change-core.mjs';
import { GitHubChangeClient } from './openspec-change-github.mjs';
import {
  renderTwinSection,
  updateManagedTwinBody,
} from './openspec-change-sync.mjs';

const ACTIVE_STATES = ['queued', 'in_progress', 'idle', 'waiting_for_user'];
const STAGE_LABEL_PREFIX = 'openspec:stage:';

export const QUEUE_LABEL_DEFINITIONS = Object.freeze([
  {
    name: 'openspec:processing',
    color: '1d76db',
    description: 'OpenSpec change queue operation is in progress',
  },
  ...['apply', 'verify', 'sync', 'archive'].map((stage) => ({
    name: `${STAGE_LABEL_PREFIX}${stage}`,
    color: 'bfdadc',
    description: `OpenSpec change queue is in the ${stage} stage`,
  })),
  {
    name: 'openspec:needs-attention',
    color: 'd93f0b',
    description: 'OpenSpec change queue requires human attention',
  },
  {
    name: 'openspec:awaiting-review',
    color: 'fbca04',
    description: 'OpenSpec change queue completed and awaits pull request review',
  },
]);

const MANAGED_QUEUE_LABELS = new Set([
  'openspec:enqueued',
  ...QUEUE_LABEL_DEFINITIONS.map(({ name }) => name),
]);
const ATTENTION_MARKER = '<!-- openspec-queue-attention:v1 -->';

function labelsOf(issue) {
  return issue.labels.map((label) => typeof label === 'string' ? label : label.name);
}

async function publishAttention(client, issueNumber, state, reason, recovery) {
  const comments = await client.listIssueComments(issueNumber);
  const existing = comments.find((comment) => comment.body?.includes(ATTENTION_MARKER));
  const task = state.applyTaskId
    ? `\n- **Task:** ${state.applyTaskId}`
    : '';
  const capabilities = state.applyTaskCapabilities
    ? `\n- **Capabilities:** ${state.applyTaskCapabilities.join(', ')}`
    : '';
  const body = [
    ATTENTION_MARKER,
    '## OpenSpec queue requires attention',
    '',
    `- **Change:** \`${state.changeRef}\``,
    `- **Operation:** ${state.operation}${task}${capabilities}`,
    `- **Attempt:** ${state.attempt}`,
    `- **Reason:** ${reason}`,
    `- **Agent Task:** https://github.com/${client.owner}/${client.repo}/tasks/${state.taskId}`,
    state.pullRequestNumber ? `- **Pull request:** #${state.pullRequestNumber}` : null,
    '',
    `**Recommended recovery:** ${recovery}`,
    '',
    'The immutable machine-readable operation ledger remains in the issue history.',
  ].filter((line) => line !== null).join('\n');
  if (existing) {
    await client.updateIssueComment(existing.id, body);
  } else {
    await client.createIssueComment(issueNumber, body);
  }
}

function labelsForState(state) {
  if (state.status === 'dispatched') {
    return new Set(['openspec:processing', `${STAGE_LABEL_PREFIX}${state.operation}`]);
  }
  if (state.status === 'needs_attention') {
    return new Set(['openspec:needs-attention', `${STAGE_LABEL_PREFIX}${state.operation}`]);
  }
  if (state.status === 'awaiting_human_review') {
    return new Set(['openspec:awaiting-review']);
  }
  return null;
}

export async function reconcileQueueLabels(client, issue, desiredLabels) {
  const desired = new Set(desiredLabels);
  for (const label of desired) {
    if (!MANAGED_QUEUE_LABELS.has(label)) {
      throw new Error(`Cannot reconcile unmanaged queue label: ${label}`);
    }
  }

  const existing = new Set(labelsOf(issue));
  for (const label of MANAGED_QUEUE_LABELS) {
    if (existing.has(label) && !desired.has(label)) {
      await client.removeIssueLabel(issue.number, label);
      issue.labels = issue.labels.filter(
        (value) => (typeof value === 'string' ? value : value.name) !== label,
      );
      existing.delete(label);
    }
  }
  for (const label of desired) {
    if (!existing.has(label)) {
      await client.addIssueLabel(issue.number, label);
      issue.labels.push({ name: label });
      existing.add(label);
    }
  }
}

async function reconcileStateLabels(client, issue, state) {
  const desired = labelsForState(state);
  if (desired) await reconcileQueueLabels(client, issue, desired);
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
  applyTask,
  issueNumber,
  baseRef,
  headRef,
  beforeSha,
  attempt,
}) {
  const checkpoint = headRef === null
    ? {
      mode: 'create',
      baseRef,
      baseSha: beforeSha,
    }
    : {
      mode: 'continue',
      baseRef,
      headRef,
      headSha: beforeSha,
    };
  const dispatch = {
    version: 1,
    changeRef,
    operation,
    ...(applyTask ? {
      applyTask: {
        id: applyTask.id,
        capabilities: applyTask.capabilities,
        capabilityPaths: applyTask.capabilities.map(
          (capability) => `openspec/capabilities/${capability}.md`,
        ),
        block: applyTask.block,
        policy: applyTask.policy,
      },
    } : {}),
    issueNumber,
    attempt,
    checkpoint,
  };
  const bindingSkill = {
    apply: '.github/skills/openspec-apply-change/SKILL.md',
    verify: '.github/skills/openspec-verify-change/SKILL.md',
    sync: '.github/skills/openspec-sync-specs/SKILL.md',
    archive: '.github/skills/openspec-archive-change/SKILL.md',
  }[operation];
  const instructions = [
    'Execute exactly one controller-selected OpenSpec operation as the OOTB OpenSpec agent.',
    `OPEN_SPEC_CLOUD_DISPATCH_V1=${JSON.stringify(dispatch)}`,
    `Read and follow ${bindingSkill} as the binding workflow.`,
    'Validate the dispatch checkpoint before editing: create requires the base SHA to be an ancestor of the generated branch HEAD; continue requires the exact head ref and HEAD SHA.',
  ];
  if (applyTask) {
    instructions.push(
      'Execute only applyTask.id in this bounded invocation; do not begin or mark any other task even though the generated workflow normally loops.',
      'Read every applyTask.capabilityPaths file and obey the complete compatible capability set.',
      'After validation, mark only the selected task complete.',
      'Your final response must contain only one JSON object conforming to openspec/capabilities/schemas/capability-result-v1.schema.json with "schema":"capability-result-v1". Do not include prose, Markdown fences, prefixes, suffixes, or any other content.',
    );
  } else {
    instructions.push(
      'Your final response must contain only one JSON object conforming to .github/scripts/schemas/operation-result-v1.schema.json with "schema":"operation-result-v1", changeRef, operation, verdict, and concise validation. Do not include prose, Markdown fences, prefixes, suffixes, or any other content.',
    );
  }
  instructions.push(
    'Do not open, ready, approve, or merge a pull request.',
  );
  return instructions.join('\n');
}

function defaultSessionLog(sessionId, _agentToken, client) {
  return client.getAgentSessionLog(sessionId);
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
  applyTaskId = null,
  completedApplyTaskIds = null,
  attempt,
  baseRef,
  headRef,
  beforeSha,
  pullRequestNumber,
  now,
}) {
  let applyTask = null;
  let completedTaskIds = null;
  if (operation === 'apply') {
    const ref = headRef ?? baseRef;
    const tasks = parseCapabilityTasks(await client.getTextContent(
      `openspec/changes/${marker.ref}/tasks.md`,
      ref,
    ));
    completedTaskIds = completedApplyTaskIds
      ?? tasks.filter((taskEntry) => taskEntry.completed).map((taskEntry) => taskEntry.id);
    const regressedTask = completedTaskIds.find((taskId) => {
      const taskEntry = tasks.find((candidate) => candidate.id === taskId);
      return !taskEntry?.completed;
    });
    if (regressedTask) {
      throw new Error(`Previously completed apply task ${regressedTask} is no longer complete`);
    }
    applyTask = applyTaskId === null
      ? tasks.find((taskEntry) => !taskEntry.completed)
      : tasks.find((taskEntry) => taskEntry.id === applyTaskId);
    if (!applyTask) {
      throw new Error(`No pending apply task found for ${marker.ref}`);
    }
    if (applyTask.completed) {
      throw new Error(`Apply task ${applyTask.id} is already complete`);
    }
    const definitions = await Promise.all(applyTask.capabilities.map(async (capability) => (
      parseCapabilityDefinition(
        await client.getTextContent(`openspec/capabilities/${capability}.md`, ref),
        capability,
      )
    )));
    applyTask = {
      ...applyTask,
      policy: validateCapabilitySet(definitions, operation),
    };
  }
  const pendingState = {
    version: 1,
    changeRef: marker.ref,
    issueNumber: issue.number,
    status: 'dispatching',
    operation,
    attempt,
    taskId: `pending-${randomUUID()}`,
    sessionId: null,
    applyTaskId: applyTask?.id ?? null,
    applyTaskCapabilities: applyTask?.capabilities ?? null,
    ...(completedTaskIds ? { completedApplyTaskIds: completedTaskIds } : {}),
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
      applyTask,
      issueNumber: issue.number,
      baseRef,
      headRef,
      beforeSha,
      attempt,
    }),
    customAgent: 'openspec',
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
  await reconcileStateLabels(client, issue, nextState);
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
    applyTaskId: state.applyTaskId ?? null,
    ...(state.applyTaskOwner
      ? { applyTaskOwner: state.applyTaskOwner }
      : { applyTaskCapabilities: state.applyTaskCapabilities ?? null }),
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
  const response = await getSessionLog(session.id, agentToken, client);
  const result = parseQueueOperationResult(response);
  const expectedSchema = state.operation === 'apply'
    ? 'capability-result-v1'
    : 'operation-result-v1';
  if (result.schema !== expectedSchema) {
    throw new Error(`Queue operation result schema does not match ${state.operation}`);
  }
  if (result.changeRef !== state.changeRef || result.operation !== state.operation) {
    throw new Error('Queue operation result does not match queue state');
  }
  if (state.operation === 'apply'
    && state.applyTaskId
    && result.taskId !== state.applyTaskId) {
    throw new Error('Queue operation result does not match the selected apply task');
  }
  if (state.operation === 'apply'
    && state.applyTaskCapabilities
    && JSON.stringify(result.capabilities) !== JSON.stringify(state.applyTaskCapabilities)) {
    throw new Error('Capability result does not match the selected capability set');
  }
  if (state.operation === 'apply' && result.resultingSha !== afterSha) {
    throw new Error('Capability result resulting SHA does not match branch state');
  }
  const validationSummary = state.operation === 'apply'
    ? (result.validation.map(({ command, outcome }) => `${command}: ${outcome}`).join('; ')
      || result.summary)
    : result.validation;
  if (result.verdict !== 'pass') {
    return {
      valid: false,
      validation: validationSummary,
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
  let nextApplyTask = null;
  if (state.operation === 'apply') {
    if (state.headRef !== null && result.startingSha !== state.beforeSha) {
      throw new Error('Capability result starting SHA does not match the continuation checkpoint');
    }
    const comparison = await client.compareCommits(state.beforeSha, afterSha);
    if (comparison.status !== 'ahead') {
      throw new Error('Capability result branch does not descend from the controller checkpoint');
    }
    const changedPaths = comparison.files.map(({ filename }) => filename).sort();
    const reportedPaths = [...result.artifactsChanged].sort();
    if (JSON.stringify(changedPaths) !== JSON.stringify(reportedPaths)) {
      throw new Error('Capability result changed paths do not match repository evidence');
    }
    const tasks = await client.getTextContent(
      `openspec/changes/${state.changeRef}/tasks.md`,
      headRef,
    );
    const parsedTasks = parseCapabilityTasks(tasks);
    if (state.applyTaskId) {
      const previouslyCompleted = state.completedApplyTaskIds ?? [];
      const regressedTask = previouslyCompleted.find((taskId) => {
        const taskEntry = parsedTasks.find((candidate) => candidate.id === taskId);
        return !taskEntry?.completed;
      });
      if (regressedTask) {
        throw new Error(`Previously completed apply task ${regressedTask} regressed`);
      }
      const selectedTask = parsedTasks.find((taskEntry) => taskEntry.id === state.applyTaskId);
      if (!selectedTask
        || JSON.stringify(selectedTask.capabilities) !== JSON.stringify(state.applyTaskCapabilities)) {
        throw new Error('Selected apply task identity changed during execution');
      }
      const unexpectedTask = parsedTasks.find((taskEntry) => taskEntry.completed
        && taskEntry.id !== selectedTask.id
        && !previouslyCompleted.includes(taskEntry.id));
      if (unexpectedTask) {
        throw new Error(`Apply task ${unexpectedTask.id} completed outside the selected work item`);
      }
      const definitions = await Promise.all(selectedTask.capabilities.map(async (capability) => (
        parseCapabilityDefinition(
          await client.getTextContent(`openspec/capabilities/${capability}.md`, headRef),
          capability,
        )
      )));
      const policy = validateCapabilitySet(definitions, 'apply');
      if (policy.mutation === 'checkbox-only') {
        const taskPath = `openspec/changes/${state.changeRef}/tasks.md`;
        if (changedPaths.length !== 1 || changedPaths[0] !== taskPath) {
          throw new Error(`Capability set may change only ${taskPath}`);
        }
        const beforeTasks = await client.getTextContent(taskPath, state.beforeSha);
        const escapedId = state.applyTaskId.replaceAll('.', '\\.');
        const checkbox = new RegExp(`^(\\s*- \\[) \\](\\s+${escapedId}\\s+)`, 'm');
        if ((beforeTasks.match(checkbox) ?? []).length === 0) {
          throw new Error('Selected task was not unchecked at the starting checkpoint');
        }
        const expectedTasks = beforeTasks.replace(checkbox, '$1x]$2');
        if (tasks !== expectedTasks) {
          throw new Error('Checkbox-only capability changed content beyond its selected checkbox');
        }
      }
      tasksComplete = selectedTask.completed;
    } else {
      tasksComplete = parsedTasks.every((taskEntry) => taskEntry.completed);
    }
    nextApplyTask = parsedTasks.find((taskEntry) => !taskEntry.completed) ?? null;
  } else if (state.operation === 'verify') {
    if (afterSha !== state.beforeSha) {
      throw new Error('Lifecycle verify must not change the branch SHA');
    }
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
    validation: evidence.valid ? validationSummary : evidence.reason,
    afterSha,
    headRef,
    sessionId: session.id,
    archivePath,
    nextApplyTask,
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

  if (!state) {
    const queued = labelsOf(issue).includes('openspec:enqueued');
    await reconcileQueueLabels(
      client,
      issue,
      queued ? new Set(['openspec:enqueued']) : new Set(),
    );
    const readiness = deriveReadiness({
      issueState: issue.state,
      labels: labelsOf(issue),
      lifecycle: marker.lifecycle,
      blockers: blockerStates,
    });
    if (!readiness.runnable) return { action: 'waiting', reasons: readiness.reasons };
    const main = await client.getBranch('main');
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
      const nextState = {
        ...state,
        status: 'needs_attention',
        updatedAt: now().toISOString(),
      };
      await upsertQueueState(client, issue.number, current, nextState);
      await reconcileStateLabels(client, issue, nextState);
      return { action: 'needs_attention', reason: 'pull-request-closed-unmerged' };
    }
    if (pull.merged_at && marker.lifecycle === 'archived') {
      try {
        await client.getRepositoryContent(marker.path, 'main');
        await reconcileQueueLabels(client, issue, new Set());
        return { action: 'complete', reason: 'archive-merged' };
      } catch {
        await reconcileStateLabels(client, issue, state);
        return { action: 'waiting', reasons: ['archive-not-on-main'] };
      }
    }
  }
  if (state.status !== 'dispatched') {
    await reconcileStateLabels(client, issue, state);
    return { action: 'waiting', reasons: [state.status] };
  }
  await reconcileStateLabels(client, issue, state);

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
    const nextState = {
      ...state,
      status: 'needs_attention',
      headRef,
      sessionId,
      updatedAt: now().toISOString(),
    };
    await upsertQueueState(client, issue.number, current, nextState);
    await reconcileStateLabels(client, issue, nextState);
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
    const stillAuthorized = issue.state === 'open';
    if (decision === 'retry' && stillAuthorized) {
      const pull = await pullRequestForHead(client, resolvedHead);
      const retryAttempt = state.operation === 'apply' && !state.applyTaskId
        ? 1
        : state.attempt + 1;
      return dispatchOperation({
        client,
        issue,
        marker,
        current,
        operation: state.operation,
        applyTaskId: state.applyTaskId ?? null,
        completedApplyTaskIds: state.completedApplyTaskIds ?? null,
        attempt: retryAttempt,
        baseRef: state.baseRef,
        headRef: resolvedHead,
        beforeSha: afterSha,
        pullRequestNumber: pull?.number ?? state.pullRequestNumber,
        now,
      });
    }
    const nextState = {
      ...state,
      status: 'needs_attention',
      headRef: resolvedHead,
      sessionId,
      updatedAt: now().toISOString(),
    };
    await upsertQueueState(client, issue.number, current, nextState);
    await reconcileStateLabels(client, issue, nextState);
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
    const nextState = {
      ...state,
      status: 'needs_attention',
      headRef: resolvedHead,
      sessionId,
      updatedAt: now().toISOString(),
    };
    await upsertQueueState(client, issue.number, current, nextState);
    await reconcileStateLabels(client, issue, nextState);
    await publishAttention(
      client,
      issue.number,
      nextState,
      error.message,
      'Inspect the selected task result and correct or retry only that bounded operation.',
    );
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
    const nextState = {
      ...state,
      status: 'needs_attention',
      headRef: completed.headRef,
      sessionId: completed.sessionId,
      updatedAt: now().toISOString(),
    };
    await upsertQueueState(client, issue.number, current, nextState);
    await reconcileStateLabels(client, issue, nextState);
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
    const nextState = {
      ...state,
      status: 'awaiting_human_review',
      headRef: completed.headRef,
      sessionId: completed.sessionId,
      pullRequestNumber: pull?.number ?? state.pullRequestNumber,
      updatedAt: now().toISOString(),
    };
    await upsertQueueState(client, issue.number, current, nextState);
    await reconcileStateLabels(client, issue, nextState);
    return {
      action: 'awaiting_human_review',
      pullRequestNumber: pull?.number ?? state.pullRequestNumber,
      archivePath: completed.archivePath,
    };
  }

  if (issue.state !== 'open') {
    const nextState = {
      ...state,
      status: 'needs_attention',
      headRef: completed.headRef,
      sessionId: completed.sessionId,
      pullRequestNumber: pull?.number ?? state.pullRequestNumber,
      updatedAt: now().toISOString(),
    };
    await upsertQueueState(client, issue.number, current, nextState);
    await reconcileStateLabels(client, issue, nextState);
    return { action: 'needs_attention', reason: 'issue-closed' };
  }

  const nextOperation = {
    apply: completed.nextApplyTask ? 'apply' : 'verify',
    verify: 'sync',
    sync: 'archive',
  }[state.operation];
  return dispatchOperation({
    client,
    issue,
    marker,
    current,
    operation: nextOperation,
    applyTaskId: nextOperation === 'apply' ? completed.nextApplyTask.id : null,
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
  for (const definition of QUEUE_LABEL_DEFINITIONS) {
    await client.ensureLabel(definition);
  }
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

function hasActiveAgentTask(results) {
  return results.some((result) => result.action === 'dispatched'
    || (result.action === 'waiting'
      && result.reasons?.some((reason) => reason.startsWith('agent-task-'))));
}

export async function reconcileUntilSettled({
  reconcile,
  wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)),
  intervalMilliseconds = 60_000,
}) {
  let results = await reconcile();
  while (hasActiveAgentTask(results)) {
    await wait(intervalMilliseconds);
    results = await reconcile();
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
  const reconcile = () => reconcileAll({ client, agentToken });
  const results = process.argv.includes('--watch')
    ? await reconcileUntilSettled({ reconcile })
    : await reconcile();
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
