import { randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  JSON_CONTRACTS,
  JSON_MARKER_START,
  QUEUE_CHECKPOINT_TRAILER,
  deriveReadiness,
  parseCapabilityDefinition,
  parseCapabilityTasks,
  parseChangeMarker,
  parseQueueCheckpoint,
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
const MAX_PUBLIC_DIAGNOSTIC_LENGTH = 500;

class QueueValidationError extends Error {
  constructor(code, message, {
    expected,
    observed,
    recovery = 'Inspect the selected operation evidence and retry only that bounded operation.',
  } = {}) {
    super(message);
    this.name = 'QueueValidationError';
    this.code = code;
    this.expected = expected;
    this.observed = observed;
    this.recovery = recovery;
  }
}

class BranchSettlingError extends Error {
  constructor(expected, observed) {
    super('Branch head changed while durable evidence was being validated');
    this.name = 'BranchSettlingError';
    this.expected = expected;
    this.observed = observed;
  }
}

function boundedDiagnostic(value) {
  const rendered = typeof value === 'string' ? value : JSON.stringify(value);
  return rendered
    .replaceAll('\r', ' ')
    .replaceAll('\n', ' ')
    .replaceAll('`', '\\`')
    .replaceAll('<!--', '&lt;!--')
    .slice(0, MAX_PUBLIC_DIAGNOSTIC_LENGTH);
}

function publicFailure(error) {
  if (error instanceof QueueValidationError) {
    return {
      code: error.code,
      reason: boundedDiagnostic(error.message),
      expected: error.expected,
      observed: error.observed,
      recovery: error.recovery,
    };
  }
  return {
    code: 'unexpected-validation-error',
    reason: boundedDiagnostic(error instanceof Error ? error.message : String(error)),
    recovery: 'Inspect the immutable operation ledger and retry only the bounded operation after correcting the controller or repository evidence.',
  };
}

function labelsOf(issue) {
  return issue.labels.map((label) => typeof label === 'string' ? label : label.name);
}

async function publishAttention(client, issueNumber, state, {
  code,
  reason,
  expected,
  observed,
  recovery,
  afterSha,
  ledgerCommentId,
}) {
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
    `- **Failure code:** \`${boundedDiagnostic(code)}\``,
    `- **Reason:** ${boundedDiagnostic(reason)}`,
    `- **Branch:** \`${boundedDiagnostic(state.headRef ?? state.baseRef)}\``,
    `- **Controller checkpoint:** \`${state.beforeSha}\``,
    afterSha ? `- **Observed branch SHA:** \`${afterSha}\`` : null,
    `- **Credited apply tasks:** ${state.completedApplyTaskIds?.length
      ? state.completedApplyTaskIds.map((id) => `\`${id}\``).join(', ')
      : 'none'}`,
    expected === undefined ? null : `- **Expected evidence:** \`${boundedDiagnostic(expected)}\``,
    observed === undefined ? null : `- **Observed evidence:** \`${boundedDiagnostic(observed)}\``,
    `- **Agent Task:** https://github.com/${client.owner}/${client.repo}/tasks/${state.taskId}`,
    state.pullRequestNumber ? `- **Pull request:** #${state.pullRequestNumber}` : null,
    Number.isInteger(ledgerCommentId)
      ? `- **Immutable ledger:** https://github.com/${client.owner}/${client.repo}/issues/${issueNumber}#issuecomment-${ledgerCommentId}`
      : null,
    '',
    `**Recommended recovery:** ${boundedDiagnostic(recovery)}`,
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
    .filter((comment) => comment.body?.includes(JSON_MARKER_START))
    .sort((left, right) => Date.parse(right.updated_at) - Date.parse(left.updated_at));
  for (const comment of candidates) {
    try {
      return { comment, state: parseQueueState(comment.body) };
    } catch {
      // Other schema-bearing OpenSpec comments are not queue state.
    }
  }
  return null;
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
    $schema: JSON_CONTRACTS.queueDispatch,
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
    'Use this controller-selected dispatch JSON:',
    JSON.stringify(dispatch),
    `Read and follow ${bindingSkill} as the binding workflow.`,
    'Validate the dispatch checkpoint before editing: create requires the base SHA to be an ancestor of the generated branch HEAD; continue requires the exact head ref and HEAD SHA.',
    'Work implementation-first under the fixed execution limit. Before lengthy broad validation, commit and push the smallest coherent task-scoped progress.',
    'If the selected task cannot be completed in this invocation, stop before timeout, leave its checkbox unchecked, omit the queue checkpoint trailer, and commit and push coherent partial progress so the controller can continue from that exact SHA.',
    'A successful operation is not complete until the current branch has a new final checkpoint commit and that exact commit is pushed to origin.',
    `The final checkpoint commit message must contain exactly one trailer named "${QUEUE_CHECKPOINT_TRAILER}" followed by one-line JSON matching ${JSON.stringify({
      $schema: JSON_CONTRACTS.queueCheckpoint,
      changeRef,
      operation,
      ...(applyTask ? { taskId: applyTask.id } : {}),
      verdict: 'pass',
      validation: 'replace with a single-line validation summary',
    })}.`,
    'Create the final checkpoint commit with git commit --allow-empty when the operation has no remaining file changes, push the current HEAD to its existing origin branch, and verify origin reports the same SHA before responding.',
  ];
  if (applyTask) {
    instructions.push(
      'Execute only applyTask.id in this bounded invocation; do not begin or mark any other task even though the generated workflow normally loops.',
      'Read every applyTask.capabilityPaths file and obey the complete compatible capability set.',
      'After validation, mark only the selected task complete.',
      `Your final response must contain only one JSON object with "$schema":"${JSON_CONTRACTS.capabilityResult}" conforming to that repository-relative schema. Do not include prose, Markdown fences, prefixes, suffixes, or any other content.`,
    );
  } else {
    instructions.push(
      `Your final response must contain only one JSON object with "$schema":"${JSON_CONTRACTS.operationResult}" conforming to that repository-relative schema, plus changeRef, operation, verdict, and concise validation. Do not include prose, Markdown fences, prefixes, suffixes, or any other content.`,
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
  return client.createIssueComment(issueNumber, renderLedgerEntry({
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
  if (!headRef) {
    throw new QueueValidationError(
      'branch-artifact-missing',
      'Completed Agent Task has no branch artifact',
      { expected: 'one branch artifact', observed: 'none' },
    );
  }
  const branch = await client.getBranch(headRef);
  const afterSha = branch.commit.sha;
  const pull = await pullRequestForHead(client, headRef);
  if (!pull) {
    throw new QueueValidationError(
      'pull-request-missing',
      'Queue branch has no open pull request',
      { expected: state.pullRequestNumber ?? 'one open draft pull request', observed: 'none' },
    );
  }
  if (state.pullRequestNumber !== null
    && state.pullRequestNumber !== undefined
    && pull.number !== state.pullRequestNumber) {
    throw new QueueValidationError(
      'pull-request-identity-changed',
      'Queue branch pull request changed during processing',
      { expected: state.pullRequestNumber, observed: pull.number },
    );
  }
  if (pull.base?.ref && pull.base.ref !== state.baseRef) {
    throw new QueueValidationError(
      'pull-request-base-changed',
      'Queue pull request no longer targets the creator branch',
      { expected: state.baseRef, observed: pull.base.ref },
    );
  }
  if (pull.draft === false) {
    throw new QueueValidationError(
      'pull-request-not-draft',
      'Queue pull request became ready before lifecycle completion',
      { expected: 'draft', observed: 'ready for review' },
    );
  }
  const comparison = await client.compareCommits(state.beforeSha, afterSha);
  if (comparison.status !== 'ahead') {
    throw new QueueValidationError(
      'branch-ancestry-invalid',
      'Final branch state does not descend from the controller checkpoint',
      { expected: 'ahead', observed: comparison.status },
    );
  }
  const commit = await client.getCommit(afterSha);
  let checkpoint;
  try {
    checkpoint = parseQueueCheckpoint(commit.commit?.message);
  } catch (error) {
    throw new QueueValidationError(
      'checkpoint-invalid',
      'Final branch commit does not contain a valid queue checkpoint',
      {
        expected: `${QUEUE_CHECKPOINT_TRAILER} with matching operation identity`,
        observed: error instanceof Error ? error.message : String(error),
        recovery: 'Create and push a new final checkpoint commit for only this bounded operation.',
      },
    );
  }
  const expectedCheckpoint = {
    changeRef: state.changeRef,
    operation: state.operation,
    ...(state.operation === 'apply' ? { taskId: state.applyTaskId } : {}),
  };
  const observedCheckpoint = {
    changeRef: checkpoint.changeRef,
    operation: checkpoint.operation,
    ...(checkpoint.operation === 'apply' ? { taskId: checkpoint.taskId } : {}),
  };
  if (JSON.stringify(observedCheckpoint) !== JSON.stringify(expectedCheckpoint)) {
    throw new QueueValidationError(
      'checkpoint-identity-mismatch',
      'Final branch checkpoint does not match the selected operation',
      { expected: expectedCheckpoint, observed: observedCheckpoint },
    );
  }
  if (state.operation === 'verify' || state.operation === 'sync') {
    try {
      await validateBranch(state.changeRef, state.operation, headRef, afterSha);
    } catch (error) {
      throw new QueueValidationError(
        'branch-validation-failed',
        `${state.operation} branch validation failed`,
        {
          expected: 'all branch validation checks pass',
          observed: error instanceof Error ? error.message : String(error),
          recovery: 'Inspect the immutable ledger and branch validation output, remediate the branch, and retry only this lifecycle operation.',
        },
      );
    }
  }
  const sessionId = task.sessions?.at(-1)?.id ?? 'unavailable';
  let result = null;
  let supplementalResultDiagnostic = null;
  if (sessionId !== 'unavailable') {
    try {
      result = parseQueueOperationResult(
        await getSessionLog(sessionId, agentToken, client),
      );
    } catch (error) {
      supplementalResultDiagnostic = boundedDiagnostic(
        error instanceof Error ? error.message : String(error),
      );
      result = null;
    }
  }
  const expectedSchema = state.operation === 'apply'
    ? JSON_CONTRACTS.capabilityResult
    : JSON_CONTRACTS.operationResult;
  if (result && result.$schema !== expectedSchema) {
    throw new QueueValidationError(
      'result-schema-mismatch',
      `Queue operation result schema does not match ${state.operation}`,
      { expected: expectedSchema, observed: result.$schema },
    );
  }
  if (result
    && (result.changeRef !== state.changeRef || result.operation !== state.operation)) {
    throw new QueueValidationError(
      'result-operation-mismatch',
      'Queue operation result does not match queue state',
      {
        expected: { changeRef: state.changeRef, operation: state.operation },
        observed: { changeRef: result.changeRef, operation: result.operation },
      },
    );
  }
  if (result
    && state.operation === 'apply'
    && state.applyTaskId
    && result.taskId !== state.applyTaskId) {
    throw new QueueValidationError(
      'result-task-mismatch',
      'Queue operation result does not match the selected apply task',
      { expected: state.applyTaskId, observed: result.taskId },
    );
  }
  if (result
    && state.operation === 'apply'
    && state.applyTaskCapabilities
    && JSON.stringify(result.capabilities) !== JSON.stringify(state.applyTaskCapabilities)) {
    throw new QueueValidationError(
      'result-capabilities-mismatch',
      'Capability result does not match the selected capability set',
      { expected: state.applyTaskCapabilities, observed: result.capabilities },
    );
  }
  const validationSummary = supplementalResultDiagnostic
    ? `${checkpoint.validation}; supplemental response ignored: ${supplementalResultDiagnostic}`
    : checkpoint.validation;
  if (checkpoint.verdict !== 'pass') {
    return {
      valid: false,
      validation: validationSummary,
      afterSha,
      headRef,
      sessionId,
      archivePath: null,
      diagnostic: {
        code: `checkpoint-verdict-${checkpoint.verdict}`,
        reason: `Checkpoint reported a ${checkpoint.verdict} verdict`,
        expected: 'pass',
        observed: checkpoint.verdict,
        recovery: 'Inspect the reported validation and blocking findings, then retry only this bounded operation after remediation.',
      },
    };
  }

  let tasksComplete;
  let verificationPassed;
  let specsSynchronized;
  let lifecycle = 'active';
  let archivePath = null;
  let nextApplyTask = null;
  if (state.operation === 'apply') {
    const changedPaths = comparison.files.map(({ filename }) => filename).sort();
    const reportedPaths = result ? [...result.artifactsChanged].sort() : changedPaths;
    if (result && JSON.stringify(changedPaths) !== JSON.stringify(reportedPaths)) {
      throw new QueueValidationError(
        'changed-paths-mismatch',
        'Capability result changed paths do not match repository evidence',
        { expected: changedPaths, observed: reportedPaths },
      );
    }
    const tasks = await client.getTextContent(
      `openspec/changes/${state.changeRef}/tasks.md`,
      afterSha,
    );
    const parsedTasks = parseCapabilityTasks(tasks);
    if (state.applyTaskId) {
      const previouslyCompleted = state.completedApplyTaskIds ?? [];
      const regressedTask = previouslyCompleted.find((taskId) => {
        const taskEntry = parsedTasks.find((candidate) => candidate.id === taskId);
        return !taskEntry?.completed;
      });
      if (regressedTask) {
        throw new QueueValidationError(
          'completed-task-regressed',
          `Previously completed apply task ${regressedTask} regressed`,
          { expected: 'checked', observed: `task ${regressedTask} is unchecked` },
        );
      }
      const selectedTask = parsedTasks.find((taskEntry) => taskEntry.id === state.applyTaskId);
      if (!selectedTask
        || JSON.stringify(selectedTask.capabilities) !== JSON.stringify(state.applyTaskCapabilities)) {
        throw new QueueValidationError(
          'selected-task-identity-changed',
          'Selected apply task identity changed during execution',
          {
            expected: {
              taskId: state.applyTaskId,
              capabilities: state.applyTaskCapabilities,
            },
            observed: selectedTask
              ? { taskId: selectedTask.id, capabilities: selectedTask.capabilities }
              : 'task missing',
          },
        );
      }
      const unexpectedTask = parsedTasks.find((taskEntry) => taskEntry.completed
        && taskEntry.id !== selectedTask.id
        && !previouslyCompleted.includes(taskEntry.id));
      if (unexpectedTask) {
        throw new QueueValidationError(
          'unexpected-task-completion',
          `Apply task ${unexpectedTask.id} completed outside the selected work item`,
          { expected: state.applyTaskId, observed: unexpectedTask.id },
        );
      }
      const definitions = await Promise.all(selectedTask.capabilities.map(async (capability) => (
        parseCapabilityDefinition(
          await client.getTextContent(`openspec/capabilities/${capability}.md`, afterSha),
          capability,
        )
      )));
      const policy = validateCapabilitySet(definitions, 'apply');
      if (policy.mutation === 'checkbox-only') {
        const taskPath = `openspec/changes/${state.changeRef}/tasks.md`;
        if (changedPaths.length !== 1 || changedPaths[0] !== taskPath) {
          throw new QueueValidationError(
            'capability-mutation-scope-violated',
            `Capability set may change only ${taskPath}`,
            { expected: [taskPath], observed: changedPaths },
          );
        }
        const beforeTasks = await client.getTextContent(taskPath, state.beforeSha);
        const escapedId = state.applyTaskId.replaceAll('.', '\\.');
        const checkbox = new RegExp(`^(\\s*- \\[) \\](\\s+${escapedId}\\s+)`, 'm');
        if ((beforeTasks.match(checkbox) ?? []).length === 0) {
          throw new QueueValidationError(
            'selected-task-start-state-invalid',
            'Selected task was not unchecked at the starting checkpoint',
            { expected: 'unchecked', observed: 'missing or already checked' },
          );
        }
        const expectedTasks = beforeTasks.replace(checkbox, '$1x]$2');
        if (tasks !== expectedTasks) {
          throw new QueueValidationError(
            'checkbox-only-mutation-violated',
            'Checkbox-only capability changed content beyond its selected checkbox',
            { expected: 'selected checkbox only', observed: 'additional tasks.md changes' },
          );
        }
      }
      tasksComplete = selectedTask.completed;
    } else {
      tasksComplete = parsedTasks.every((taskEntry) => taskEntry.completed);
    }
    nextApplyTask = parsedTasks.find((taskEntry) => !taskEntry.completed) ?? null;
  } else if (state.operation === 'verify') {
    verificationPassed = true;
  } else if (state.operation === 'sync') {
    specsSynchronized = true;
  } else if (state.operation === 'archive') {
    archivePath = await findArchivePath(client, state.changeRef, afterSha);
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
  if (!evidence.valid) {
    return {
      valid: false,
      validation: evidence.reason,
      afterSha,
      headRef,
      sessionId,
      pullRequestNumber: pull.number,
      archivePath,
      nextApplyTask,
      diagnostic: {
        code: 'operation-evidence-invalid',
        reason: evidence.reason,
        expected: 'valid operation evidence',
        observed: evidence.reason,
        recovery: 'Inspect the branch-visible lifecycle evidence and retry only this bounded operation after remediation.',
      },
    };
  }
  const finalBranch = await client.getBranch(headRef);
  if (finalBranch.commit.sha !== afterSha) {
    throw new BranchSettlingError(afterSha, finalBranch.commit.sha);
  }
  return {
    valid: evidence.valid,
    validation: evidence.valid ? validationSummary : evidence.reason,
    afterSha,
    headRef,
    sessionId,
    pullRequestNumber: pull.number,
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
      await publishAttention(client, issue.number, nextState, {
        code: 'pull-request-closed-unmerged',
        reason: 'The queue pull request was closed without merging',
        expected: 'open draft pull request or merged archived change',
        observed: `closed PR #${state.pullRequestNumber}`,
        recovery: 'Inspect the closed pull request, then explicitly reset or restore the active change before re-enqueueing.',
      });
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
    const ledgerComment = await appendLedger(client, issue.number, state, {
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
    await publishAttention(client, issue.number, nextState, {
      code: 'agent-task-waiting-for-user',
      reason: 'Agent Task requires a new human decision',
      expected: 'autonomous bounded completion',
      observed: 'waiting_for_user',
      recovery: 'Respond to the Agent Task, then explicitly repair or replay only this bounded queue operation.',
      afterSha: branch.commit.sha,
      ledgerCommentId: ledgerComment?.id,
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

  let durableCompletion = null;
  let durableFailure = null;
  if (task.state !== 'completed' && headRef) {
    const branch = await client.getBranch(headRef);
    if (branch.commit.sha !== state.beforeSha) {
      try {
        durableCompletion = await validateCompletedOperation({
          client,
          state,
          task,
          getSessionLog,
          agentToken,
          validateBranch,
        });
      } catch (error) {
        if (error instanceof BranchSettlingError) {
          return {
            action: 'waiting',
            reasons: ['branch-settling'],
            expectedSha: error.expected,
            observedSha: error.observed,
          };
        }
        durableFailure = publicFailure(error);
      }
    }
  }

  if (task.state !== 'completed' && durableCompletion === null) {
    const retryableProgress = durableFailure?.code === 'checkpoint-invalid';
    const decision = retryableProgress
      ? retryDecision(task.state, state.attempt)
      : (durableFailure ? 'stop' : retryDecision(task.state, state.attempt));
    const resolvedHead = headRef ?? state.headRef;
    const checkpointRef = resolvedHead ?? state.baseRef;
    const branch = await client.getBranch(checkpointRef);
    const afterSha = branch.commit.sha;
    let retryBlock = durableFailure && !retryableProgress
      ? {
        code: durableFailure.code,
        reason: durableFailure.reason,
        expected: durableFailure.expected,
        observed: durableFailure.observed,
      }
      : null;
    if (decision === 'retry' && state.operation === 'apply' && state.applyTaskId) {
      try {
        const tasks = parseCapabilityTasks(await client.getTextContent(
          `openspec/changes/${state.changeRef}/tasks.md`,
          afterSha,
        ));
        const selectedTask = tasks.find(({ id }) => id === state.applyTaskId);
        if (!selectedTask) {
          retryBlock = {
            code: 'failed-task-selection-missing',
            reason: `Selected apply task ${state.applyTaskId} is missing after the failed Agent Task`,
            expected: state.applyTaskId,
            observed: 'task missing',
          };
        } else if (selectedTask.completed) {
          retryBlock = {
            code: 'failed-task-already-complete',
            reason: `Failed Agent Task already marked apply task ${state.applyTaskId} complete`,
            expected: 'selected task remains unchecked for automatic retry',
            observed: `task ${state.applyTaskId} is checked`,
          };
        } else if (JSON.stringify(selectedTask.capabilities)
          !== JSON.stringify(state.applyTaskCapabilities)) {
          retryBlock = {
            code: 'failed-task-selection-changed',
            reason: `Selected apply task ${state.applyTaskId} changed after the failed Agent Task`,
            expected: state.applyTaskCapabilities,
            observed: selectedTask.capabilities,
          };
        } else {
          const regressedTask = (state.completedApplyTaskIds ?? []).find((taskId) => (
            !tasks.find((candidate) => candidate.id === taskId)?.completed
          ));
          if (regressedTask) {
            retryBlock = {
              code: 'failed-completed-task-regressed',
              reason: `Previously completed apply task ${regressedTask} regressed after the failed Agent Task`,
              expected: `task ${regressedTask} remains checked`,
              observed: `task ${regressedTask} is unchecked or missing`,
            };
          }
        }
      } catch (error) {
        retryBlock = {
          code: 'failed-task-state-unreadable',
          reason: 'Selected apply task state could not be validated for automatic retry',
          expected: 'valid branch-visible task state',
          observed: error instanceof Error ? error.message : String(error),
        };
      }
    }
    const automaticRetry = decision === 'retry' && retryBlock === null;
    const ledgerComment = await appendLedger(client, issue.number, state, {
      sessionId: sessionId ?? 'unavailable',
      headRef: checkpointRef,
      afterSha,
      outcome: task.state,
      validation: `Agent Task ended in ${task.state}.`,
      recovery: automaticRetry
        ? (retryableProgress
          ? 'Automatic retry dispatched from pushed partial progress.'
          : 'Automatic retry dispatched.')
        : retryBlock?.reason ?? 'Human recovery required.',
      now,
    });
    const stillAuthorized = issue.state === 'open';
    if (automaticRetry && stillAuthorized) {
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
    await publishAttention(client, issue.number, nextState, {
      code: retryBlock?.code ?? `agent-task-${task.state}`,
      reason: retryBlock?.reason ?? `Agent Task ended in ${task.state}`,
      expected: retryBlock?.expected ?? 'completed',
      observed: retryBlock?.observed ?? task.state,
      recovery: retryBlock
        ? 'Inspect the committed branch work. To retry this task, restore only its checkbox to unchecked on the same branch, then explicitly replay the bounded operation; otherwise discard the branch and restart from main.'
        : 'Inspect the Agent Task and immutable ledger, then explicitly retry only this bounded operation after remediation.',
      afterSha,
      ledgerCommentId: ledgerComment?.id,
    });
    return { action: 'needs_attention', reason: task.state };
  }

  let completed = durableCompletion;
  try {
    completed ??= await validateCompletedOperation({
      client,
      state,
      task,
      getSessionLog,
      agentToken,
      validateBranch,
    });
  } catch (error) {
    if (error instanceof BranchSettlingError) {
      return {
        action: 'waiting',
        reasons: ['branch-settling'],
        expectedSha: error.expected,
        observedSha: error.observed,
      };
    }
    const resolvedHead = headRef ?? state.headRef ?? 'main';
    const branch = await client.getBranch(resolvedHead);
    const failure = publicFailure(error);
    const ledgerComment = await appendLedger(client, issue.number, state, {
      sessionId: sessionId ?? 'unavailable',
      headRef: resolvedHead,
      afterSha: branch.commit.sha,
      outcome: 'failed',
      validation: `[${failure.code}] ${failure.reason}`,
      recovery: failure.recovery,
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
      {
        ...failure,
        afterSha: branch.commit.sha,
        ledgerCommentId: ledgerComment?.id,
      },
    );
    return { action: 'needs_attention', reason: 'invalid-result' };
  }

  const ledgerComment = await appendLedger(client, issue.number, state, {
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
    await publishAttention(client, issue.number, nextState, {
      ...completed.diagnostic,
      afterSha: completed.afterSha,
      ledgerCommentId: ledgerComment?.id,
    });
    return { action: 'needs_attention', reason: completed.validation };
  }

  const pull = { number: completed.pullRequestNumber };
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
    await publishAttention(client, issue.number, nextState, {
      code: 'change-issue-closed',
      reason: 'The OpenSpec change issue closed before queue processing completed',
      expected: 'open issue',
      observed: 'closed issue',
      recovery: 'Review the issue closure and reopen the issue before explicitly repairing or replaying the queue.',
      afterSha: completed.afterSha,
      ledgerCommentId: ledgerComment?.id,
    });
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

export async function resetQueueIssue({ client, issueNumber }) {
  const issue = await client.getIssue(issueNumber);
  parseChangeMarker(issue.body ?? '');
  const comments = await client.listIssueComments(issueNumber);
  const current = latestQueueState(comments);
  if (!current) {
    throw new Error(`Issue #${issueNumber} has no mutable queue state to reset`);
  }

  if (current.state.status !== 'needs_attention') {
    throw new Error(`Issue #${issueNumber} queue state must be needs_attention before reset`);
  }
  if (current.state.pullRequestNumber) {
    const pull = await client.getPullRequest(current.state.pullRequestNumber);
    if (pull.state !== 'closed' || pull.merged_at) {
      throw new Error(`Issue #${issueNumber} pull request must be closed without merging before reset`);
    }
  }
  const task = await client.getAgentTask(current.state.taskId);
  if (ACTIVE_STATES.includes(task.state)) {
    throw new Error(`Issue #${issueNumber} Agent Task is still ${task.state}`);
  }
  const mutableComments = comments.filter((comment) => {
    if (comment.user?.login !== 'github-actions[bot]') return false;
    if (comment.body?.includes(ATTENTION_MARKER)) return true;
    try {
      parseQueueState(comment.body);
      return true;
    } catch {
      return false;
    }
  });
  for (const comment of mutableComments) {
    await client.deleteIssueComment(comment.id);
  }
  await reconcileQueueLabels(client, issue, new Set());
  return {
    action: 'reset',
    issueNumber,
    removedCommentIds: mutableComments.map(({ id }) => id),
  };
}

export async function resumeQueueIssue({
  client,
  issueNumber,
  now = () => new Date(),
}) {
  const issue = await client.getIssue(issueNumber);
  const marker = parseChangeMarker(issue.body ?? '');
  const comments = await client.listIssueComments(issueNumber);
  const current = latestQueueState(comments);
  if (!current || current.state.status !== 'needs_attention') {
    throw new Error(`Issue #${issueNumber} queue state must be needs_attention before resume`);
  }
  const state = current.state;
  if (issue.state !== 'open') {
    throw new Error(`Issue #${issueNumber} must be open before resume`);
  }
  if (!state.headRef || !state.pullRequestNumber) {
    throw new Error(`Issue #${issueNumber} has no durable branch and pull request to resume`);
  }
  const pull = await client.getPullRequest(state.pullRequestNumber);
  if (pull.state !== 'open' || pull.merged_at || pull.head?.ref !== state.headRef) {
    throw new Error(`Issue #${issueNumber} pull request is not the open durable queue branch`);
  }
  const task = await client.getAgentTask(state.taskId);
  if (ACTIVE_STATES.includes(task.state)) {
    throw new Error(`Issue #${issueNumber} Agent Task is still ${task.state}`);
  }
  const branch = await client.getBranch(state.headRef);
  if (state.operation !== 'apply' || !state.applyTaskId) {
    throw new Error(`Issue #${issueNumber} resume currently requires a selected apply task`);
  }
  const tasks = parseCapabilityTasks(await client.getTextContent(
    `openspec/changes/${state.changeRef}/tasks.md`,
    branch.commit.sha,
  ));
  const selectedTask = tasks.find(({ id }) => id === state.applyTaskId);
  if (!selectedTask) {
    throw new Error(`Issue #${issueNumber} selected task must remain present`);
  }
  if (JSON.stringify(selectedTask.capabilities)
    !== JSON.stringify(state.applyTaskCapabilities)) {
    throw new Error(`Issue #${issueNumber} selected task capabilities changed`);
  }
  const regressedTask = (state.completedApplyTaskIds ?? []).find((taskId) => (
    !tasks.find((candidate) => candidate.id === taskId)?.completed
  ));
  if (regressedTask) {
    throw new Error(`Issue #${issueNumber} previously completed task ${regressedTask} regressed`);
  }
  const attentionComments = comments.filter((comment) => (
    comment.user?.login === 'github-actions[bot]'
      && comment.body?.includes(ATTENTION_MARKER)
  ));
  for (const comment of attentionComments) {
    await client.deleteIssueComment(comment.id);
  }
  if (selectedTask.completed) {
    if (branch.commit.sha === state.beforeSha) {
      throw new Error(`Issue #${issueNumber} checked task has no pushed checkpoint to revalidate`);
    }
    const nextState = {
      ...state,
      status: 'dispatched',
      updatedAt: now().toISOString(),
    };
    await upsertQueueState(client, issueNumber, current, nextState);
    await reconcileStateLabels(client, issue, nextState);
    return {
      action: 'revalidate',
      operation: state.operation,
      attempt: state.attempt,
    };
  }
  return dispatchOperation({
    client,
    issue,
    marker,
    current,
    operation: state.operation,
    applyTaskId: state.applyTaskId,
    completedApplyTaskIds: state.completedApplyTaskIds ?? null,
    attempt: 1,
    baseRef: state.baseRef,
    headRef: state.headRef,
    beforeSha: branch.commit.sha,
    pullRequestNumber: state.pullRequestNumber,
    now,
  });
}

function hasActiveAgentTask(results) {
  return results.some((result) => result.action === 'dispatched'
    || (result.action === 'waiting'
      && result.reasons?.some((reason) => reason.startsWith('agent-task-'))));
}

export async function reconcileUntilSettled({
  reconcile,
  initialResults = null,
  wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)),
  intervalMilliseconds = 60_000,
}) {
  let results = initialResults ?? await reconcile();
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
  const resetIndex = process.argv.indexOf('--reset-issue');
  const resumeIndex = process.argv.indexOf('--resume-issue');
  let results;
  if (resetIndex >= 0) {
    const issueNumber = Number.parseInt(process.argv[resetIndex + 1], 10);
    if (!Number.isInteger(issueNumber) || issueNumber <= 0) {
      throw new Error('--reset-issue requires a positive issue number');
    }
    results = await resetQueueIssue({ client, issueNumber });
  } else if (resumeIndex >= 0) {
    const issueNumber = Number.parseInt(process.argv[resumeIndex + 1], 10);
    if (!Number.isInteger(issueNumber) || issueNumber <= 0) {
      throw new Error('--resume-issue requires a positive issue number');
    }
    const reconcile = async () => {
      const issue = await client.getIssue(issueNumber);
      return [{
        issueNumber,
        ...(await reconcileIssue({ client, issue, agentToken })),
      }];
    };
    const resumed = await resumeQueueIssue({ client, issueNumber });
    const initialResults = resumed.action === 'revalidate'
      ? await reconcile()
      : [{ issueNumber, ...resumed }];
    results = await reconcileUntilSettled({
      reconcile,
      initialResults,
    });
  } else {
    const reconcile = () => reconcileAll({ client, agentToken });
    results = process.argv.includes('--watch')
      ? await reconcileUntilSettled({ reconcile })
      : await reconcile();
  }
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
