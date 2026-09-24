const KEBAB_CASE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const GIT_SHA = /^[a-f0-9]{40}$/;
const SUPPORTED_OWNERS = new Set([
  'soca-strategist',
  'soca-designer',
  'soca-architect',
  'soca-developer',
  'soca-verifier',
  'soca-auditor',
]);
const TERMINAL_TASK_STATES = new Set(['completed', 'failed', 'timed_out', 'cancelled']);
const ACTIVE_TASK_STATES = new Set(['queued', 'in_progress', 'idle', 'waiting_for_user']);
const RETRYABLE_TASK_STATES = new Set(['failed', 'timed_out']);

export const CHANGE_MARKER_START = '<!-- openspec-change:v1';
export const COMMENT_MARKER_END = '-->';
export const LEDGER_MARKER_START = '<!-- openspec-operation:v1';
export const DEPENDENCY_SUMMARY_START = '<!-- openspec-dependencies:v1';
export const QUEUE_STATE_START = '<!-- openspec-queue-state:v1';
export const CLOUD_RESULT_PREFIX = 'OPEN_SPEC_CLOUD_OPERATION_V1=';

function assertObject(value, path) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${path} must be an object`);
  }
}

function assertKnownKeys(value, allowed, path) {
  const unknown = Object.keys(value).filter((key) => !allowed.has(key));
  if (unknown.length > 0) {
    throw new Error(`${path} contains unknown field(s): ${unknown.join(', ')}`);
  }
}

function assertKebabCase(value, path) {
  if (typeof value !== 'string' || !KEBAB_CASE.test(value)) {
    throw new Error(`${path} must be a non-empty kebab-case identifier`);
  }
}

function assertString(value, path) {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new Error(`${path} must be a non-empty string`);
  }
}

function extractSingleJsonMarker(body, startMarker, label) {
  if (typeof body !== 'string') {
    throw new Error(`${label} body must be a string`);
  }

  const starts = body.split(startMarker).length - 1;
  if (starts !== 1) {
    throw new Error(`${label} body must contain exactly one ${startMarker} marker`);
  }

  const start = body.indexOf(startMarker) + startMarker.length;
  const end = body.indexOf(COMMENT_MARKER_END, start);
  if (end < start) {
    throw new Error(`${label} marker is not closed`);
  }

  const content = body.slice(start, end).trim();
  if (!content) {
    throw new Error(`${label} marker is empty`);
  }

  try {
    return JSON.parse(content);
  } catch (error) {
    throw new Error(`${label} marker contains invalid JSON: ${error.message}`);
  }
}

function renderJsonMarker(startMarker, value) {
  return `${startMarker}\n${JSON.stringify(value)}\n${COMMENT_MARKER_END}`;
}

function assertMarkerSafeString(value, path) {
  assertString(value, path);
  if (value.includes('<!--') || value.includes(COMMENT_MARKER_END)) {
    throw new Error(`${path} contains an HTML comment delimiter`);
  }
}

export function validateChangeMarker(value) {
  assertObject(value, 'Change marker');
  assertKnownKeys(
    value,
    new Set(['repository', 'ref', 'lifecycle', 'gitRef', 'path']),
    'Change marker',
  );
  assertString(value.repository, 'Change marker.repository');
  assertKebabCase(value.ref, 'Change marker.ref');
  if (!['active', 'archived'].includes(value.lifecycle)) {
    throw new Error('Change marker.lifecycle must be active or archived');
  }
  assertString(value.gitRef, 'Change marker.gitRef');
  assertString(value.path, 'Change marker.path');

  const activePath = `openspec/changes/${value.ref}`;
  const archivePattern = new RegExp(`^openspec/changes/archive/\\d{4}-\\d{2}-\\d{2}-${value.ref}$`);
  if (value.lifecycle === 'active' && value.path !== activePath) {
    throw new Error(`Active change path must be ${activePath}`);
  }
  if (value.lifecycle === 'archived' && !archivePattern.test(value.path)) {
    throw new Error('Archived change path must use the dated archive directory');
  }

  return {
    repository: value.repository,
    ref: value.ref,
    lifecycle: value.lifecycle,
    gitRef: value.gitRef,
    path: value.path,
  };
}

export function parseChangeMarker(issueBody) {
  return validateChangeMarker(extractSingleJsonMarker(issueBody, CHANGE_MARKER_START, 'Issue'));
}

export function renderChangeMarker(value) {
  return renderJsonMarker(CHANGE_MARKER_START, validateChangeMarker(value));
}

export function parseOwnedTasks(tasksMarkdown) {
  if (typeof tasksMarkdown !== 'string') {
    throw new Error('Tasks content must be a string');
  }

  const matches = [...tasksMarkdown.matchAll(/^\s*- \[([ xX])\]\s+(\d+(?:\.\d+)*)\s+(.+)$/gm)];
  return matches.map((match, index) => {
    const start = match.index;
    const candidateEnd = matches[index + 1]?.index ?? tasksMarkdown.length;
    const candidateBlock = tasksMarkdown.slice(start, candidateEnd);
    const heading = candidateBlock.slice(match[0].length).match(/^#{1,6}\s+/m);
    const end = heading?.index === undefined
      ? candidateEnd
      : start + match[0].length + heading.index;
    const block = tasksMarkdown.slice(start, end).trimEnd();
    const owners = [...block.matchAll(/\bOwner:\s*(soca-[a-z-]+)\b/gi)]
      .map((ownerMatch) => ownerMatch[1].toLowerCase());
    const uniqueOwners = [...new Set(owners)];

    if (uniqueOwners.length !== 1 || owners.length !== 1) {
      throw new Error(`Task ${match[2]} must declare exactly one Owner: soca-*`);
    }
    if (!SUPPORTED_OWNERS.has(uniqueOwners[0])) {
      throw new Error(`Task ${match[2]} has unsupported owner: ${uniqueOwners[0]}`);
    }

    return {
      id: match[2],
      completed: match[1].toLowerCase() === 'x',
      title: match[3].trim(),
      owner: uniqueOwners[0],
      block,
    };
  });
}

export function selectNextTask(tasksMarkdown) {
  return parseOwnedTasks(tasksMarkdown).find((task) => !task.completed) ?? null;
}

export function assertAcyclicGraph(refs, edges) {
  const knownRefs = new Set(refs);
  const dependencies = new Map(refs.map((ref) => [ref, []]));

  for (const edge of edges) {
    if (!knownRefs.has(edge.changeRef) || !knownRefs.has(edge.dependsOn)) {
      throw new Error(`Dependency edge references an unknown change: ${edge.changeRef} -> ${edge.dependsOn}`);
    }
    if (edge.changeRef === edge.dependsOn) {
      throw new Error(`Change ${edge.changeRef} cannot depend on itself`);
    }
    dependencies.get(edge.changeRef).push(edge.dependsOn);
  }

  const visited = new Set();
  const visiting = new Set();
  const path = [];

  function visit(ref) {
    if (visiting.has(ref)) {
      const cycleStart = path.indexOf(ref);
      throw new Error(`Dependency cycle: ${[...path.slice(cycleStart), ref].join(' -> ')}`);
    }
    if (visited.has(ref)) {
      return;
    }

    visiting.add(ref);
    path.push(ref);
    for (const dependency of dependencies.get(ref)) {
      visit(dependency);
    }
    path.pop();
    visiting.delete(ref);
    visited.add(ref);
  }

  for (const ref of refs) {
    visit(ref);
  }
}

function edgeKey(edge) {
  return `${edge.changeRef}\0${edge.dependsOn}`;
}

export function validateDependencyOutput(value, knownRefs, existingEdges = [], minimumConfidence = 0.85) {
  assertObject(value, 'Dependency output');
  assertKnownKeys(value, new Set(['version', 'candidates']), 'Dependency output');
  if (value.version !== 1) {
    throw new Error('Dependency output version must be 1');
  }
  if (!Array.isArray(value.candidates)) {
    throw new Error('Dependency output.candidates must be an array');
  }

  const known = new Set(knownRefs);
  const seen = new Set();
  const accepted = [];
  const review = [];

  for (const [index, candidate] of value.candidates.entries()) {
    const path = `Dependency output.candidates[${index}]`;
    assertObject(candidate, path);
    assertKnownKeys(
      candidate,
      new Set(['changeRef', 'dependsOn', 'confidence', 'evidence']),
      path,
    );
    assertKebabCase(candidate.changeRef, `${path}.changeRef`);
    assertKebabCase(candidate.dependsOn, `${path}.dependsOn`);
    if (!known.has(candidate.changeRef) || !known.has(candidate.dependsOn)) {
      throw new Error(`${path} references an unknown change`);
    }
    if (candidate.changeRef === candidate.dependsOn) {
      throw new Error(`${path} cannot be a self dependency`);
    }
    if (typeof candidate.confidence !== 'number'
      || candidate.confidence < 0
      || candidate.confidence > 1) {
      throw new Error(`${path}.confidence must be between 0 and 1`);
    }
    if (!Array.isArray(candidate.evidence) || candidate.evidence.length === 0) {
      throw new Error(`${path}.evidence must be a non-empty array`);
    }
    candidate.evidence.forEach((entry, evidenceIndex) => {
      assertString(entry, `${path}.evidence[${evidenceIndex}]`);
    });

    const normalized = {
      changeRef: candidate.changeRef,
      dependsOn: candidate.dependsOn,
      confidence: candidate.confidence,
      evidence: [...new Set(candidate.evidence)].sort(),
    };
    const key = edgeKey(normalized);
    if (seen.has(key)) {
      throw new Error(`${path} duplicates dependency ${candidate.changeRef} -> ${candidate.dependsOn}`);
    }
    seen.add(key);

    if (candidate.confidence >= minimumConfidence) {
      accepted.push(normalized);
    } else {
      review.push(normalized);
    }
  }

  const existingOpenSpecEdges = existingEdges.filter(
    (edge) => known.has(edge.changeRef) && known.has(edge.dependsOn),
  );
  assertAcyclicGraph(knownRefs, [...existingOpenSpecEdges, ...accepted]);
  return {
    accepted: accepted.sort((left, right) => edgeKey(left).localeCompare(edgeKey(right))),
    review: review.sort((left, right) => edgeKey(left).localeCompare(edgeKey(right))),
  };
}

export function calculateManagedEdgeChanges(previousManagedEdges, desiredManagedEdges, nativeEdges) {
  const previous = new Map(previousManagedEdges.map((edge) => [edgeKey(edge), edge]));
  const desired = new Map(desiredManagedEdges.map((edge) => [edgeKey(edge), edge]));
  const native = new Set(nativeEdges.map(edgeKey));

  return {
    add: [...desired.entries()]
      .filter(([key]) => !native.has(key))
      .map(([, edge]) => edge),
    remove: [...previous.entries()]
      .filter(([key]) => !desired.has(key) && native.has(key))
      .map(([, edge]) => edge),
    update: [...desired.entries()]
      .filter(([key, edge]) => previous.has(key)
        && JSON.stringify(previous.get(key)) !== JSON.stringify(edge))
      .map(([, edge]) => edge),
  };
}

export function validateDependencySummary(value) {
  assertObject(value, 'Dependency summary');
  assertKnownKeys(value, new Set(['version', 'managedEdges']), 'Dependency summary');
  if (value.version !== 1) throw new Error('Dependency summary version must be 1');
  if (!Array.isArray(value.managedEdges)) {
    throw new Error('Dependency summary.managedEdges must be an array');
  }

  const seen = new Set();
  const managedEdges = value.managedEdges.map((edge, index) => {
    const path = `Dependency summary.managedEdges[${index}]`;
    assertObject(edge, path);
    assertKnownKeys(edge, new Set(['changeRef', 'dependsOn', 'confidence', 'evidence']), path);
    assertKebabCase(edge.changeRef, `${path}.changeRef`);
    assertKebabCase(edge.dependsOn, `${path}.dependsOn`);
    if (edge.changeRef === edge.dependsOn) throw new Error(`${path} cannot be a self dependency`);
    if (typeof edge.confidence !== 'number' || edge.confidence < 0 || edge.confidence > 1) {
      throw new Error(`${path}.confidence must be between 0 and 1`);
    }
    if (!Array.isArray(edge.evidence) || edge.evidence.length === 0) {
      throw new Error(`${path}.evidence must be a non-empty array`);
    }
    edge.evidence.forEach((entry, evidenceIndex) => {
      assertMarkerSafeString(entry, `${path}.evidence[${evidenceIndex}]`);
    });
    const normalized = {
      changeRef: edge.changeRef,
      dependsOn: edge.dependsOn,
      confidence: edge.confidence,
      evidence: [...new Set(edge.evidence)].sort(),
    };
    const key = edgeKey(normalized);
    if (seen.has(key)) throw new Error(`${path} duplicates a managed edge`);
    seen.add(key);
    return normalized;
  });

  return {
    version: 1,
    managedEdges: managedEdges.sort((left, right) => edgeKey(left).localeCompare(edgeKey(right))),
  };
}

export function renderDependencySummary(value) {
  return renderJsonMarker(DEPENDENCY_SUMMARY_START, validateDependencySummary(value));
}

export function parseDependencySummary(commentBody) {
  return validateDependencySummary(
    extractSingleJsonMarker(commentBody, DEPENDENCY_SUMMARY_START, 'Dependency summary'),
  );
}

export function deriveReadiness({
  issueState,
  labels,
  lifecycle,
  blockers = [],
  activeTaskState = null,
  activeTaskAttempt = 1,
  needsDecision = false,
}) {
  const reasons = [];
  if (issueState !== 'open') reasons.push('issue-not-open');
  if (!labels.includes('openspec:enqueued')) reasons.push('not-enqueued');
  if (lifecycle !== 'active') reasons.push('change-not-active');
  if (needsDecision) reasons.push('needs-human-decision');
  if (activeTaskState && ACTIVE_TASK_STATES.has(activeTaskState)) reasons.push('agent-task-active');
  if (activeTaskState && !ACTIVE_TASK_STATES.has(activeTaskState)
    && !TERMINAL_TASK_STATES.has(activeTaskState)) {
    throw new Error(`Unknown Agent Task state: ${activeTaskState}`);
  }
  if (activeTaskState && RETRYABLE_TASK_STATES.has(activeTaskState) && activeTaskAttempt >= 2) {
    reasons.push('retries-exhausted');
  }

  for (const blocker of blockers) {
    assertObject(blocker, 'Blocker');
    if (blocker.state !== 'closed' || blocker.archivedOnMain !== true) {
      reasons.push(`blocked-by:${blocker.number}`);
    }
  }

  return { runnable: reasons.length === 0, reasons };
}

export function selectNextOperation({
  lifecycle,
  tasksComplete,
  verificationPassed,
  specsSynchronized,
}) {
  if (lifecycle === 'archived') return 'complete';
  if (lifecycle !== 'active') throw new Error(`Unknown lifecycle: ${lifecycle}`);
  if (!tasksComplete) return 'apply';
  if (!verificationPassed) return 'verify';
  if (!specsSynchronized) return 'sync';
  return 'archive';
}

export function retryDecision(state, attempt) {
  if (!Number.isInteger(attempt) || attempt < 1) {
    throw new Error('Attempt must be a positive integer');
  }
  if (RETRYABLE_TASK_STATES.has(state)) {
    return attempt < 2 ? 'retry' : 'stop';
  }
  if (state === 'cancelled' || state === 'waiting_for_user') return 'stop';
  if (state === 'completed') return 'advance';
  if (state === 'queued' || state === 'in_progress' || state === 'idle') return 'wait';
  throw new Error(`Unknown Agent Task state: ${state}`);
}

export function validateLedgerEntry(value) {
  assertObject(value, 'Ledger entry');
  assertKnownKeys(
    value,
    new Set([
      'version',
      'changeRef',
      'operation',
      'attempt',
      'taskId',
      'sessionId',
      'branch',
      'beforeSha',
      'afterSha',
      'outcome',
      'validation',
      'recovery',
      'recordedAt',
    ]),
    'Ledger entry',
  );
  if (value.version !== 1) throw new Error('Ledger entry version must be 1');
  assertKebabCase(value.changeRef, 'Ledger entry.changeRef');
  if (!['apply', 'verify', 'sync', 'archive'].includes(value.operation)) {
    throw new Error('Ledger entry.operation is invalid');
  }
  if (!Number.isInteger(value.attempt) || value.attempt < 1 || value.attempt > 2) {
    throw new Error('Ledger entry.attempt must be 1 or 2');
  }
  assertMarkerSafeString(value.taskId, 'Ledger entry.taskId');
  assertMarkerSafeString(value.sessionId, 'Ledger entry.sessionId');
  assertMarkerSafeString(value.branch, 'Ledger entry.branch');
  if (!GIT_SHA.test(value.beforeSha) || !GIT_SHA.test(value.afterSha)) {
    throw new Error('Ledger entry SHAs must be 40-character lowercase Git SHAs');
  }
  if (!['succeeded', 'failed', 'timed_out', 'cancelled', 'waiting_for_user'].includes(value.outcome)) {
    throw new Error('Ledger entry.outcome is invalid');
  }
  assertMarkerSafeString(value.validation, 'Ledger entry.validation');
  if (value.recovery !== null) assertMarkerSafeString(value.recovery, 'Ledger entry.recovery');
  if (typeof value.recordedAt !== 'string'
    || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z$/.test(value.recordedAt)
    || Number.isNaN(Date.parse(value.recordedAt))) {
    throw new Error('Ledger entry.recordedAt must be an ISO-8601 timestamp');
  }
  if (value.outcome !== 'succeeded' && value.recovery === null) {
    throw new Error('Unsuccessful ledger entries require recovery guidance');
  }
  return { ...value };
}

export function renderLedgerEntry(value) {
  return renderJsonMarker(LEDGER_MARKER_START, validateLedgerEntry(value));
}

export function parseLedgerEntry(commentBody) {
  return validateLedgerEntry(extractSingleJsonMarker(commentBody, LEDGER_MARKER_START, 'Ledger comment'));
}

export function validateQueueState(value) {
  assertObject(value, 'Queue state');
  assertKnownKeys(
    value,
    new Set([
      'version',
      'changeRef',
      'issueNumber',
      'status',
      'operation',
      'attempt',
      'taskId',
      'sessionId',
      'baseRef',
      'headRef',
      'beforeSha',
      'pullRequestNumber',
      'updatedAt',
    ]),
    'Queue state',
  );
  if (value.version !== 1) throw new Error('Queue state version must be 1');
  assertKebabCase(value.changeRef, 'Queue state.changeRef');
  if (!Number.isInteger(value.issueNumber) || value.issueNumber < 1) {
    throw new Error('Queue state.issueNumber must be a positive integer');
  }
  if (!['dispatching', 'dispatched', 'needs_attention', 'awaiting_human_review'].includes(value.status)) {
    throw new Error('Queue state.status is invalid');
  }
  if (!['apply', 'verify', 'sync', 'archive'].includes(value.operation)) {
    throw new Error('Queue state.operation is invalid');
  }
  if (!Number.isInteger(value.attempt) || value.attempt < 1 || value.attempt > 2) {
    throw new Error('Queue state.attempt must be 1 or 2');
  }
  assertMarkerSafeString(value.taskId, 'Queue state.taskId');
  if (value.sessionId !== null) assertMarkerSafeString(value.sessionId, 'Queue state.sessionId');
  assertMarkerSafeString(value.baseRef, 'Queue state.baseRef');
  if (value.headRef !== null) assertMarkerSafeString(value.headRef, 'Queue state.headRef');
  if (!GIT_SHA.test(value.beforeSha)) throw new Error('Queue state.beforeSha is invalid');
  if (value.pullRequestNumber !== null
    && (!Number.isInteger(value.pullRequestNumber) || value.pullRequestNumber < 1)) {
    throw new Error('Queue state.pullRequestNumber must be null or a positive integer');
  }
  if (typeof value.updatedAt !== 'string'
    || Number.isNaN(Date.parse(value.updatedAt))) {
    throw new Error('Queue state.updatedAt must be an ISO-8601 timestamp');
  }
  return { ...value };
}

export function renderQueueState(value) {
  return renderJsonMarker(QUEUE_STATE_START, validateQueueState(value));
}

export function parseQueueState(commentBody) {
  return validateQueueState(
    extractSingleJsonMarker(commentBody, QUEUE_STATE_START, 'Queue state comment'),
  );
}

export function parseCloudOperationResult(log) {
  if (typeof log !== 'string') throw new Error('Cloud operation log must be a string');
  const lines = log.split(/\r?\n/)
    .filter((line) => line.trim().startsWith(CLOUD_RESULT_PREFIX));
  if (lines.length !== 1) {
    throw new Error('Cloud operation log must contain exactly one result marker');
  }
  let value;
  try {
    value = JSON.parse(lines[0].trim().slice(CLOUD_RESULT_PREFIX.length));
  } catch (error) {
    throw new Error(`Cloud operation result contains invalid JSON: ${error.message}`);
  }
  assertObject(value, 'Cloud operation result');
  assertKnownKeys(
    value,
    new Set(['changeRef', 'operation', 'verdict', 'validation']),
    'Cloud operation result',
  );
  assertKebabCase(value.changeRef, 'Cloud operation result.changeRef');
  if (!['apply', 'verify', 'sync', 'archive'].includes(value.operation)) {
    throw new Error('Cloud operation result.operation is invalid');
  }
  if (!['pass', 'blocked', 'fail'].includes(value.verdict)) {
    throw new Error('Cloud operation result.verdict is invalid');
  }
  assertMarkerSafeString(value.validation, 'Cloud operation result.validation');
  return { ...value };
}

export function validateOperationEvidence({
  operation,
  outcome,
  beforeSha,
  afterSha,
  filesChanged,
  tasksComplete,
  verificationPassed,
  specsSynchronized,
  lifecycle,
}) {
  if (!GIT_SHA.test(beforeSha) || !GIT_SHA.test(afterSha)) {
    return { valid: false, reason: 'invalid-checkpoint' };
  }

  if (outcome !== 'succeeded') {
    return { valid: false, reason: `operation-${outcome}` };
  }
  if (filesChanged && beforeSha === afterSha) {
    return { valid: false, reason: 'changed-files-without-checkpoint' };
  }
  const satisfied = {
    apply: tasksComplete,
    verify: verificationPassed,
    sync: specsSynchronized,
    archive: lifecycle === 'archived',
  };
  if (!(operation in satisfied)) {
    return { valid: false, reason: 'unknown-operation' };
  }
  if (satisfied[operation] !== true) {
    return { valid: false, reason: `${operation}-evidence-missing` };
  }
  return { valid: true, reason: null };
}

function requirementsMatch(left, right) {
  if (left?.text !== right?.text) return false;
  const leftScenarios = left.scenarios?.map((scenario) => scenario.rawText) ?? [];
  const rightScenarios = right.scenarios?.map((scenario) => scenario.rawText) ?? [];
  return leftScenarios.length === rightScenarios.length
    && leftScenarios.every((scenario, index) => scenario === rightScenarios[index]);
}

export function validateSynchronizedDeltas(change, specsById) {
  if (!change || !Array.isArray(change.deltas)) {
    throw new Error('OpenSpec delta report is invalid');
  }
  if (!specsById || typeof specsById !== 'object' || Array.isArray(specsById)) {
    throw new Error('Accepted OpenSpec specs are invalid');
  }
  for (const delta of change.deltas) {
    const accepted = specsById[delta.spec];
    const requirements = accepted?.requirements ?? [];
    if (!Array.isArray(requirements)) {
      throw new Error(`Accepted spec ${delta.spec} has invalid requirements`);
    }
    const requirement = delta.requirement ?? delta.requirements?.[0];
    if (!requirement?.text) {
      throw new Error(`Delta for ${delta.spec} has no requirement`);
    }
    const present = requirements.some((candidate) => requirementsMatch(candidate, requirement));
    if (delta.operation === 'ADDED' || delta.operation === 'MODIFIED') {
      if (!present) {
        throw new Error(`${delta.operation} requirement is not synchronized to ${delta.spec}`);
      }
    } else if (delta.operation === 'REMOVED') {
      if (requirements.some((candidate) => candidate.text === requirement.text)) {
        throw new Error(`REMOVED requirement is still present in ${delta.spec}`);
      }
    } else {
      throw new Error(`Unsupported synchronization delta operation: ${delta.operation}`);
    }
  }
  return true;
}
