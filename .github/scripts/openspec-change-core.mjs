const KEBAB_CASE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const CAPABILITY_ID = /^[a-z][a-z0-9]*$/;
const GIT_SHA = /^[a-f0-9]{40}$/;
const SHA256 = /^[a-f0-9]{64}$/;
const CAPABILITY_OPERATIONS = new Set(['propose', 'update', 'apply', 'verify', 'archive']);
const TERMINAL_TASK_STATES = new Set(['completed', 'failed', 'timed_out', 'cancelled']);
const ACTIVE_TASK_STATES = new Set(['queued', 'in_progress', 'idle', 'waiting_for_user']);
const RETRYABLE_TASK_STATES = new Set(['failed', 'timed_out']);

export const CHANGE_MARKER_START = '<!-- openspec-change:v1';
export const COMMENT_MARKER_END = '-->';
export const LEDGER_MARKER_START = '<!-- openspec-operation:v1';
export const DEPENDENCY_SUMMARY_START = '<!-- openspec-dependencies:v1';
export const DEPENDENCY_CHECKPOINT_VERSION = 1;
export const DEPENDENCY_PATCH_VERSION = 2;
export const QUEUE_STATE_START = '<!-- openspec-queue-state:v1';

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

function assertCapabilityIds(value, path) {
  if (!Array.isArray(value) || value.length === 0) {
    throw new Error(`${path} must be a non-empty array`);
  }
  const uniqueIds = new Set(value);
  if (uniqueIds.size !== value.length
    || value.some((id) => typeof id !== 'string' || !CAPABILITY_ID.test(id))
    || value.some((id, index) => index > 0 && value[index - 1].localeCompare(id) >= 0)) {
    throw new Error(`${path} must contain unique sorted capability identifiers`);
  }
}

function assertApplyTask(value, path) {
  if (value.applyTaskId === undefined
    && value.applyTaskOwner === undefined
    && value.applyTaskCapabilities === undefined) return;
  const hasId = typeof value.applyTaskId === 'string';
  const hasLegacyOwner = typeof value.applyTaskOwner === 'string';
  const hasCapabilities = Array.isArray(value.applyTaskCapabilities);
  if (hasId !== (hasLegacyOwner || hasCapabilities) || (hasLegacyOwner && hasCapabilities)) {
    throw new Error(`${path} apply task id and exactly one capability contract must be present`);
  }
  if (!hasId) {
    if (value.applyTaskId !== null
      || (value.applyTaskOwner !== undefined && value.applyTaskOwner !== null)
      || (value.applyTaskCapabilities !== undefined && value.applyTaskCapabilities !== null)) {
      throw new Error(`${path} apply task fields must be populated together or null`);
    }
    return;
  }
  if (!/^\d+(?:\.\d+)*$/.test(value.applyTaskId)) {
    throw new Error(`${path}.applyTaskId is invalid`);
  }
  if (hasLegacyOwner && !/^soca-[a-z-]+$/.test(value.applyTaskOwner)) {
    throw new Error(`${path}.applyTaskOwner is invalid`);
  }
  if (hasCapabilities) {
    assertCapabilityIds(value.applyTaskCapabilities, `${path}.applyTaskCapabilities`);
  }
  if (value.operation !== 'apply') {
    throw new Error(`${path} apply task is valid only for apply operations`);
  }
}

function assertCompletedApplyTasks(value, path) {
  if (value.completedApplyTaskIds === undefined) return;
  if (value.operation !== 'apply' || !Array.isArray(value.completedApplyTaskIds)) {
    throw new Error(`${path}.completedApplyTaskIds is invalid`);
  }
  const uniqueIds = new Set(value.completedApplyTaskIds);
  if (uniqueIds.size !== value.completedApplyTaskIds.length
    || value.completedApplyTaskIds.some((id) => !/^\d+(?:\.\d+)*$/.test(id))) {
    throw new Error(`${path}.completedApplyTaskIds is invalid`);
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

export function parseCapabilityDefinition(content, expectedId = null) {
  if (typeof content !== 'string') {
    throw new Error('Capability definition must be a string');
  }
  const match = content.match(/^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/);
  if (!match) throw new Error('Capability definition must have YAML frontmatter');
  const value = {};
  for (const [index, line] of match[1].split(/\r?\n/).entries()) {
    if (line.trim() === '') continue;
    const entry = line.match(/^([A-Za-z][A-Za-z0-9]*):\s*(.+)$/);
    if (!entry) {
      throw new Error(`Capability frontmatter line ${index + 1} is unsupported`);
    }
    const [, key, rawValue] = entry;
    if (key in value) throw new Error(`Capability frontmatter contains duplicate key: ${key}`);
    if (/^\[.*\]$/.test(rawValue)) {
      const inner = rawValue.slice(1, -1).trim();
      value[key] = inner === '' ? [] : inner.split(',').map((item) => item.trim());
    } else if (/^\d+$/.test(rawValue)) {
      value[key] = Number(rawValue);
    } else {
      value[key] = rawValue.trim();
    }
  }
  assertKnownKeys(
    value,
    new Set([
      'id',
      'version',
      'operations',
      'composition',
      'mutation',
      'isolation',
      'resultSchema',
    ]),
    'Capability definition',
  );
  if (typeof value.id !== 'string' || !CAPABILITY_ID.test(value.id)) {
    throw new Error('Capability definition.id is invalid');
  }
  if (expectedId !== null && value.id !== expectedId) {
    throw new Error(`Capability definition id ${value.id} does not match ${expectedId}`);
  }
  if (value.version !== 1) throw new Error('Capability definition.version must be 1');
  if (!Array.isArray(value.operations)
    || value.operations.length === 0
    || new Set(value.operations).size !== value.operations.length
    || value.operations.some((operation) => !CAPABILITY_OPERATIONS.has(operation))) {
    throw new Error('Capability definition.operations is invalid');
  }
  if (!['composable', 'exclusive'].includes(value.composition)) {
    throw new Error('Capability definition.composition is invalid');
  }
  if (!['scoped', 'checkbox-only', 'none'].includes(value.mutation)) {
    throw new Error('Capability definition.mutation is invalid');
  }
  if (!['shared', 'required'].includes(value.isolation)) {
    throw new Error('Capability definition.isolation is invalid');
  }
  if (value.resultSchema !== 'schemas/capability-result-v1.schema.json') {
    throw new Error('Capability definition.resultSchema is invalid');
  }
  return { ...value };
}

export function validateCapabilitySet(definitions, operation = 'apply') {
  if (!Array.isArray(definitions) || definitions.length === 0) {
    throw new Error('Capability set must not be empty');
  }
  const ids = definitions.map(({ id }) => id).sort();
  assertCapabilityIds(ids, 'Capability set');
  for (const definition of definitions) {
    if (!definition.operations.includes(operation)) {
      throw new Error(`Capability ${definition.id} does not support ${operation}`);
    }
  }
  if (definitions.length > 1) {
    const exclusive = definitions.find(({ composition }) => composition === 'exclusive');
    if (exclusive) throw new Error(`Capability ${exclusive.id} is exclusive`);
    for (const field of ['mutation', 'isolation', 'resultSchema']) {
      if (new Set(definitions.map((definition) => definition[field])).size !== 1) {
        throw new Error(`Capability set has incompatible ${field} contracts`);
      }
    }
  }
  return {
    ids,
    mutation: definitions[0].mutation,
    isolation: definitions[0].isolation,
    resultSchema: definitions[0].resultSchema,
  };
}

export function parseCapabilityTasks(tasksMarkdown) {
  if (typeof tasksMarkdown !== 'string') {
    throw new Error('Tasks content must be a string');
  }

  const matches = [...tasksMarkdown.matchAll(/^\s*- \[([ xX])\]\s+(\d+(?:\.\d+)*)\s+(.+)$/gm)];
  const tasks = matches.map((match, index) => {
    const start = match.index;
    const candidateEnd = matches[index + 1]?.index ?? tasksMarkdown.length;
    const candidateBlock = tasksMarkdown.slice(start, candidateEnd);
    const heading = candidateBlock.slice(match[0].length).match(/^#{1,6}\s+/m);
    const end = heading?.index === undefined
      ? candidateEnd
      : start + match[0].length + heading.index;
    const block = tasksMarkdown.slice(start, end).trimEnd();
    const declarations = [...block.matchAll(
      /\bCapabilities:\s*([a-z][a-z0-9]*(?:\s*,\s*[a-z][a-z0-9]*)*)\b/gi,
    )];
    if (declarations.length !== 1) {
      throw new Error(`Task ${match[2]} must declare exactly one Capabilities set`);
    }
    const capabilities = declarations[0][1]
      .split(',')
      .map((capability) => capability.trim().toLowerCase())
      .sort();
    assertCapabilityIds(capabilities, `Task ${match[2]} capabilities`);

    return {
      id: match[2],
      completed: match[1].toLowerCase() === 'x',
      title: match[3].trim(),
      capabilities,
      block,
    };
  });
  const taskIds = new Set();
  for (const task of tasks) {
    if (taskIds.has(task.id)) {
      throw new Error(`Task ${task.id} is duplicated`);
    }
    taskIds.add(task.id);
  }
  return tasks;
}

export function selectNextTask(tasksMarkdown) {
  return parseCapabilityTasks(tasksMarkdown).find((task) => !task.completed) ?? null;
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

function normalizeManagedEdge(edge, path) {
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
  return {
    changeRef: edge.changeRef,
    dependsOn: edge.dependsOn,
    confidence: edge.confidence,
    evidence: [...new Set(edge.evidence)].sort(),
  };
}

function normalizeManagedEdges(edges, path) {
  if (!Array.isArray(edges)) throw new Error(`${path} must be an array`);
  const seen = new Set();
  return edges.map((edge, index) => {
    const normalized = normalizeManagedEdge(edge, `${path}[${index}]`);
    const key = edgeKey(normalized);
    if (seen.has(key)) throw new Error(`${path}[${index}] duplicates a managed edge`);
    seen.add(key);
    return normalized;
  }).sort((left, right) => edgeKey(left).localeCompare(edgeKey(right)));
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

export function validateDependencyGraphPatch(
  value,
  knownRefs,
  existingEdges = [],
  minimumConfidence = 0.85,
) {
  assertObject(value, 'Dependency graph patch');
  assertKnownKeys(
    value,
    new Set(['version', 'evaluationMode', 'evaluatedRefs', 'summaries', 'upsert', 'remove']),
    'Dependency graph patch',
  );
  if (value.version !== DEPENDENCY_PATCH_VERSION) {
    throw new Error(`Dependency graph patch version must be ${DEPENDENCY_PATCH_VERSION}`);
  }
  if (!['incremental', 'full'].includes(value.evaluationMode)) {
    throw new Error('Dependency graph patch.evaluationMode must be incremental or full');
  }

  const known = new Set(knownRefs);
  if (!Array.isArray(value.evaluatedRefs)) {
    throw new Error('Dependency graph patch.evaluatedRefs must be an array');
  }
  const evaluatedRefs = [...new Set(value.evaluatedRefs)];
  if (evaluatedRefs.length !== value.evaluatedRefs.length) {
    throw new Error('Dependency graph patch.evaluatedRefs must not contain duplicates');
  }
  evaluatedRefs.forEach((ref, index) => {
    assertKebabCase(ref, `Dependency graph patch.evaluatedRefs[${index}]`);
    if (!known.has(ref)) {
      throw new Error(`Dependency graph patch.evaluatedRefs[${index}] references an unknown change`);
    }
  });
  evaluatedRefs.sort();
  if (value.evaluationMode === 'full'
    && (evaluatedRefs.length !== known.size || evaluatedRefs.some((ref) => !known.has(ref)))) {
    throw new Error('Full dependency graph patches must evaluate every active change');
  }

  if (!Array.isArray(value.summaries)) {
    throw new Error('Dependency graph patch.summaries must be an array');
  }
  const summaryRefs = new Set();
  const summaries = value.summaries.map((summary, index) => {
    const path = `Dependency graph patch.summaries[${index}]`;
    assertObject(summary, path);
    assertKnownKeys(summary, new Set(['ref', 'summary']), path);
    assertKebabCase(summary.ref, `${path}.ref`);
    assertString(summary.summary, `${path}.summary`);
    if (summaryRefs.has(summary.ref)) throw new Error(`${path} duplicates ref ${summary.ref}`);
    summaryRefs.add(summary.ref);
    return { ref: summary.ref, summary: summary.summary };
  }).sort((left, right) => left.ref.localeCompare(right.ref));
  if (summaryRefs.size !== evaluatedRefs.length
    || evaluatedRefs.some((ref) => !summaryRefs.has(ref))) {
    throw new Error('Dependency graph patch.summaries must cover exactly the evaluated refs');
  }

  const upsertCandidates = normalizeManagedEdges(value.upsert, 'Dependency graph patch.upsert');
  if (!Array.isArray(value.remove)) {
    throw new Error('Dependency graph patch.remove must be an array');
  }
  const remove = value.remove.map((edge, index) => {
    const path = `Dependency graph patch.remove[${index}]`;
    assertObject(edge, path);
    assertKnownKeys(edge, new Set(['changeRef', 'dependsOn', 'evidence']), path);
    assertKebabCase(edge.changeRef, `${path}.changeRef`);
    assertKebabCase(edge.dependsOn, `${path}.dependsOn`);
    if (edge.changeRef === edge.dependsOn) throw new Error(`${path} cannot be a self dependency`);
    if (!Array.isArray(edge.evidence) || edge.evidence.length === 0) {
      throw new Error(`${path}.evidence must be a non-empty array`);
    }
    edge.evidence.forEach((entry, evidenceIndex) => {
      assertString(entry, `${path}.evidence[${evidenceIndex}]`);
    });
    return {
      changeRef: edge.changeRef,
      dependsOn: edge.dependsOn,
      evidence: [...new Set(edge.evidence)].sort(),
    };
  }).sort((left, right) => edgeKey(left).localeCompare(edgeKey(right)));

  const evaluated = new Set(evaluatedRefs);
  const operationKeys = new Set();
  for (const [operation, edges] of [['upsert', upsertCandidates], ['remove', remove]]) {
    for (const edge of edges) {
      if (!known.has(edge.changeRef) || !known.has(edge.dependsOn)) {
        throw new Error(`Dependency graph patch.${operation} references an unknown change`);
      }
      if (!evaluated.has(edge.changeRef) && !evaluated.has(edge.dependsOn)) {
        throw new Error(
          `Dependency graph patch.${operation} does not touch an evaluated change`,
        );
      }
      const key = edgeKey(edge);
      if (operationKeys.has(key)) {
        throw new Error(`Dependency graph patch repeats operation for ${edge.changeRef} -> ${edge.dependsOn}`);
      }
      operationKeys.add(key);
    }
  }

  const validated = validateDependencyOutput(
    { version: 1, candidates: upsertCandidates },
    knownRefs,
    existingEdges,
    minimumConfidence,
  );
  return {
    version: DEPENDENCY_PATCH_VERSION,
    evaluationMode: value.evaluationMode,
    evaluatedRefs,
    summaries,
    upsert: validated.accepted,
    review: validated.review,
    remove,
  };
}

export function mergeManagedDependencyGraph(previousManagedEdges, patch) {
  const merged = new Map(
    normalizeManagedEdges(previousManagedEdges, 'Previous managed edges')
      .map((edge) => [edgeKey(edge), edge]),
  );
  for (const edge of patch.remove) merged.delete(edgeKey(edge));
  for (const edge of patch.upsert) merged.set(edgeKey(edge), edge);
  return [...merged.values()]
    .sort((left, right) => edgeKey(left).localeCompare(edgeKey(right)));
}

export function mergeManagedEdgeProvenance({
  checkpointEdges = [],
  legacyCommentEdges = [],
  nativeEdges = [],
}) {
  const nativeKeys = new Set(nativeEdges.map(edgeKey));
  const merged = new Map(
    normalizeManagedEdges(checkpointEdges, 'Checkpoint managed edges')
      .map((edge) => [edgeKey(edge), edge]),
  );
  for (const edge of normalizeManagedEdges(
    legacyCommentEdges,
    'Legacy comment managed edges',
  )) {
    const key = edgeKey(edge);
    if (nativeKeys.has(key) && !merged.has(key)) merged.set(key, edge);
  }
  return [...merged.values()]
    .sort((left, right) => edgeKey(left).localeCompare(edgeKey(right)));
}

export function validateMergedDependencyGraph({
  knownRefs,
  previousManagedEdges,
  managedEdges,
  nativeEdges,
}) {
  const previousKeys = new Set(previousManagedEdges.map(edgeKey));
  const unrelatedNative = nativeEdges.filter((edge) => !previousKeys.has(edgeKey(edge)));
  const graph = new Map();
  for (const edge of [...unrelatedNative, ...managedEdges]) {
    if (knownRefs.includes(edge.changeRef) && knownRefs.includes(edge.dependsOn)) {
      graph.set(edgeKey(edge), edge);
    }
  }
  assertAcyclicGraph(knownRefs, [...graph.values()]);
  return [...graph.values()].sort((left, right) => edgeKey(left).localeCompare(edgeKey(right)));
}

export function validateDependencySummary(value) {
  assertObject(value, 'Dependency summary');
  assertKnownKeys(value, new Set(['version', 'managedEdges']), 'Dependency summary');
  if (value.version !== 1) throw new Error('Dependency summary version must be 1');
  if (!Array.isArray(value.managedEdges)) {
    throw new Error('Dependency summary.managedEdges must be an array');
  }

  return {
    version: 1,
    managedEdges: normalizeManagedEdges(value.managedEdges, 'Dependency summary.managedEdges'),
  };
}

export function validateDependencyCheckpoint(value) {
  assertObject(value, 'Dependency checkpoint');
  assertKnownKeys(
    value,
    new Set(['version', 'commit', 'changes', 'managedEdges', 'inference']),
    'Dependency checkpoint',
  );
  if (value.version !== DEPENDENCY_CHECKPOINT_VERSION) {
    throw new Error(`Dependency checkpoint version must be ${DEPENDENCY_CHECKPOINT_VERSION}`);
  }
  if (typeof value.commit !== 'string' || !GIT_SHA.test(value.commit)) {
    throw new Error('Dependency checkpoint.commit must be a full Git SHA');
  }
  if (!Array.isArray(value.changes)) throw new Error('Dependency checkpoint.changes must be an array');
  const changeRefs = new Set();
  const changes = value.changes.map((change, index) => {
    const path = `Dependency checkpoint.changes[${index}]`;
    assertObject(change, path);
    assertKnownKeys(change, new Set(['ref', 'digest', 'summary']), path);
    assertKebabCase(change.ref, `${path}.ref`);
    if (changeRefs.has(change.ref)) throw new Error(`${path} duplicates change ${change.ref}`);
    changeRefs.add(change.ref);
    if (typeof change.digest !== 'string' || !SHA256.test(change.digest)) {
      throw new Error(`${path}.digest must be a SHA-256 digest`);
    }
    assertString(change.summary, `${path}.summary`);
    return { ref: change.ref, digest: change.digest, summary: change.summary };
  }).sort((left, right) => left.ref.localeCompare(right.ref));

  assertObject(value.inference, 'Dependency checkpoint.inference');
  assertKnownKeys(
    value.inference,
    new Set(['mode', 'evaluatedRefs', 'baseCommit', 'minimumConfidence']),
    'Dependency checkpoint.inference',
  );
  if (!['incremental', 'full'].includes(value.inference.mode)) {
    throw new Error('Dependency checkpoint.inference.mode must be incremental or full');
  }
  if (!Array.isArray(value.inference.evaluatedRefs)) {
    throw new Error('Dependency checkpoint.inference.evaluatedRefs must be an array');
  }
  const evaluatedRefs = [...new Set(value.inference.evaluatedRefs)];
  if (evaluatedRefs.length !== value.inference.evaluatedRefs.length) {
    throw new Error('Dependency checkpoint.inference.evaluatedRefs must not contain duplicates');
  }
  evaluatedRefs.forEach((ref, index) => {
    assertKebabCase(ref, `Dependency checkpoint.inference.evaluatedRefs[${index}]`);
    if (!changeRefs.has(ref)) {
      throw new Error(`Dependency checkpoint.inference.evaluatedRefs[${index}] is not summarized`);
    }
  });
  evaluatedRefs.sort();
  if (value.inference.mode === 'full'
    && (evaluatedRefs.length !== changeRefs.size
      || evaluatedRefs.some((ref) => !changeRefs.has(ref)))) {
    throw new Error('Full dependency checkpoints must evaluate every summarized change');
  }
  if (value.inference.baseCommit !== null
    && (typeof value.inference.baseCommit !== 'string'
      || !GIT_SHA.test(value.inference.baseCommit))) {
    throw new Error('Dependency checkpoint.inference.baseCommit must be null or a full Git SHA');
  }
  if (typeof value.inference.minimumConfidence !== 'number'
    || value.inference.minimumConfidence < 0
    || value.inference.minimumConfidence > 1) {
    throw new Error('Dependency checkpoint.inference.minimumConfidence must be between 0 and 1');
  }

  const managedEdges = normalizeManagedEdges(
    value.managedEdges,
    'Dependency checkpoint.managedEdges',
  );
  for (const edge of managedEdges) {
    if (!changeRefs.has(edge.changeRef) || !changeRefs.has(edge.dependsOn)) {
      throw new Error(
        `Dependency checkpoint managed edge references an unsummarized change: ${edge.changeRef} -> ${edge.dependsOn}`,
      );
    }
  }
  assertAcyclicGraph([...changeRefs], managedEdges);

  return {
    version: DEPENDENCY_CHECKPOINT_VERSION,
    commit: value.commit,
    changes,
    managedEdges,
    inference: {
      mode: value.inference.mode,
      evaluatedRefs,
      baseCommit: value.inference.baseCommit,
      minimumConfidence: value.inference.minimumConfidence,
    },
  };
}

export function serializeDependencyCheckpoint(value) {
  return `${JSON.stringify(validateDependencyCheckpoint(value))}\n`;
}

export function parseDependencyCheckpoint(text) {
  if (typeof text !== 'string' || text.trim() === '') {
    throw new Error('Dependency checkpoint note must be a non-empty JSON string');
  }
  try {
    return validateDependencyCheckpoint(JSON.parse(text));
  } catch (error) {
    if (error instanceof SyntaxError) {
      throw new Error(`Dependency checkpoint note contains invalid JSON: ${error.message}`);
    }
    throw error;
  }
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
      'applyTaskId',
      'applyTaskOwner',
      'applyTaskCapabilities',
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
  assertApplyTask(value, 'Ledger entry');
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
      'applyTaskId',
      'applyTaskOwner',
      'applyTaskCapabilities',
      'completedApplyTaskIds',
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
  assertApplyTask(value, 'Queue state');
  assertCompletedApplyTasks(value, 'Queue state');
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

function validateOperationResult(value) {
  assertObject(value, 'Operation result');
  assertKnownKeys(
    value,
    new Set(['schema', 'changeRef', 'operation', 'applyTaskId', 'verdict', 'validation']),
    'Operation result',
  );
  if (value.schema !== 'operation-result-v1') {
    throw new Error('Operation result.schema must be operation-result-v1');
  }
  assertKebabCase(value.changeRef, 'Operation result.changeRef');
  if (!['apply', 'verify', 'sync', 'archive'].includes(value.operation)) {
    throw new Error('Operation result.operation is invalid');
  }
  if (value.applyTaskId !== undefined) {
    if (value.operation !== 'apply' || !/^\d+(?:\.\d+)*$/.test(value.applyTaskId)) {
      throw new Error('Operation result.applyTaskId is invalid');
    }
  }
  if (!['pass', 'blocked', 'fail'].includes(value.verdict)) {
    throw new Error('Operation result.verdict is invalid');
  }
  assertMarkerSafeString(value.validation, 'Operation result.validation');
  return { ...value };
}

function assertGitPath(value, path) {
  assertMarkerSafeString(value, path);
  if (value.includes('\\')
    || value.startsWith('/')
    || value.split('/').some((segment) => segment === '' || segment === '..')) {
    throw new Error(`${path} must be a normalized repository-relative path`);
  }
}

function validateCapabilityResult(value) {
  assertObject(value, 'Capability result');
  assertKnownKeys(
    value,
    new Set([
      'schema',
      'changeRef',
      'operation',
      'taskId',
      'capabilities',
      'verdict',
      'startingSha',
      'resultingSha',
      'artifactsChanged',
      'validation',
      'summary',
      'blockingFindings',
    ]),
    'Capability result',
  );
  if (value.schema !== 'capability-result-v1') {
    throw new Error('Capability result.schema must be capability-result-v1');
  }
  assertKebabCase(value.changeRef, 'Capability result.changeRef');
  if (value.operation !== 'apply') throw new Error('Capability result.operation must be apply');
  if (typeof value.taskId !== 'string' || !/^\d+(?:\.\d+)*$/.test(value.taskId)) {
    throw new Error('Capability result.taskId is invalid');
  }
  assertCapabilityIds(value.capabilities, 'Capability result.capabilities');
  if (!['pass', 'blocked', 'fail'].includes(value.verdict)) {
    throw new Error('Capability result.verdict is invalid');
  }
  if (!GIT_SHA.test(value.startingSha) || !GIT_SHA.test(value.resultingSha)) {
    throw new Error('Capability result SHAs are invalid');
  }
  if (!Array.isArray(value.artifactsChanged)
    || new Set(value.artifactsChanged).size !== value.artifactsChanged.length) {
    throw new Error('Capability result.artifactsChanged is invalid');
  }
  value.artifactsChanged.forEach((artifact, index) => {
    assertGitPath(artifact, `Capability result.artifactsChanged[${index}]`);
  });
  if (!Array.isArray(value.validation)) {
    throw new Error('Capability result.validation must be an array');
  }
  value.validation.forEach((entry, index) => {
    assertObject(entry, `Capability result.validation[${index}]`);
    assertKnownKeys(
      entry,
      new Set(['command', 'outcome']),
      `Capability result.validation[${index}]`,
    );
    assertMarkerSafeString(entry.command, `Capability result.validation[${index}].command`);
    if (!['passed', 'failed', 'not-run'].includes(entry.outcome)) {
      throw new Error(`Capability result.validation[${index}].outcome is invalid`);
    }
  });
  assertMarkerSafeString(value.summary, 'Capability result.summary');
  if (!Array.isArray(value.blockingFindings)) {
    throw new Error('Capability result.blockingFindings must be an array');
  }
  value.blockingFindings.forEach((finding, index) => {
    assertMarkerSafeString(finding, `Capability result.blockingFindings[${index}]`);
  });
  return { ...value };
}

const RESULT_SCHEMA_VALIDATORS = Object.freeze({
  'capability-result-v1': validateCapabilityResult,
  'operation-result-v1': validateOperationResult,
});

export function parseQueueOperationResult(response) {
  if (typeof response !== 'string') {
    throw new Error('Queue operation final response must be a string');
  }
  const content = response.trim();
  if (content === '') {
    throw new Error('Queue operation final response must not be empty');
  }
  let value;
  try {
    value = JSON.parse(content);
  } catch (error) {
    throw new Error(`Queue operation final response must be exactly one JSON object: ${error.message}`);
  }
  assertObject(value, 'Queue operation result');
  if (typeof value.schema !== 'string' || value.schema.trim() === '') {
    throw new Error('Queue operation result.schema must be a non-empty string');
  }
  const validate = RESULT_SCHEMA_VALIDATORS[value.schema];
  if (!validate) {
    throw new Error(`Queue operation result.schema is unsupported: ${value.schema}`);
  }
  return validate(value);
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
