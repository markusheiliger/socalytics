const KEBAB_CASE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const CAPABILITY_ID = /^[a-z][a-z0-9]*$/;
const GIT_SHA = /^[a-f0-9]{40}$/;
const SHA256 = /^[a-f0-9]{64}$/;
const CAPABILITY_OPERATIONS = new Set(['propose', 'update', 'apply', 'verify', 'archive']);

export const JSON_CONTRACTS = Object.freeze({
  changeMarker: '.github/scripts/schemas/change-marker-v1.schema.json',
  changeCheckpoint: '.github/scripts/schemas/change-checkpoint-v2.schema.json',
  changeDispatch: '.github/scripts/schemas/change-dispatch-v2.schema.json',
  changeRunState: '.github/scripts/schemas/change-run-state-v1.schema.json',
  dependencySummary: '.github/scripts/schemas/dependency-summary-v1.schema.json',
  dependencyCheckpoint: '.github/scripts/schemas/dependency-checkpoint-v1.schema.json',
  dependencyReconciliationContext:
    '.github/scripts/schemas/dependency-reconciliation-context-v1.schema.json',
  dependencyCandidates: '.github/scripts/schemas/dependency-candidates-v1.schema.json',
  dependencyGraphPatch: '.github/scripts/schemas/dependency-graph-patch-v2.schema.json',
  capabilityResult: 'openspec/capabilities/schemas/capability-result-v1.schema.json',
});

const LEGACY_CAPABILITY_RESULT_SCHEMA = 'schemas/capability-result-v1.schema.json';

export const JSON_MARKER_START = '<!-- openspec-json';
export const COMMENT_MARKER_END = '-->';
export const CHECKPOINT_TRAILER = 'OpenSpec-JSON:';

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

function assertSchema(value, expectedSchema, path) {
  assertObject(value, path);
  if (value.$schema !== expectedSchema) {
    throw new Error(`${path}.$schema must be ${expectedSchema}`);
  }
}

function extractSingleJsonMarker(body, expectedSchema, label) {
  if (typeof body !== 'string') {
    throw new Error(`${label} body must be a string`);
  }

  const starts = body.split(JSON_MARKER_START).length - 1;
  if (starts !== 1) {
    throw new Error(`${label} body must contain exactly one ${JSON_MARKER_START} marker`);
  }

  const start = body.indexOf(JSON_MARKER_START) + JSON_MARKER_START.length;
  const end = body.indexOf(COMMENT_MARKER_END, start);
  if (end < start) {
    throw new Error(`${label} marker is not closed`);
  }

  const content = body.slice(start, end).trim();
  if (!content) {
    throw new Error(`${label} marker is empty`);
  }

  try {
    const value = JSON.parse(content);
    assertSchema(value, expectedSchema, label);
    return value;
  } catch (error) {
    throw new Error(`${label} marker contains invalid JSON: ${error.message}`);
  }
}

function renderJsonMarker(value) {
  return `${JSON_MARKER_START}\n${JSON.stringify(value)}\n${COMMENT_MARKER_END}`;
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

export function validateChangeMarker(value) {
  assertSchema(value, JSON_CONTRACTS.changeMarker, 'Change marker');
  assertKnownKeys(
    value,
    new Set(['$schema', 'repository', 'ref', 'lifecycle', 'gitRef', 'path']),
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
    $schema: JSON_CONTRACTS.changeMarker,
    repository: value.repository,
    ref: value.ref,
    lifecycle: value.lifecycle,
    gitRef: value.gitRef,
    path: value.path,
  };
}

export function parseChangeMarker(issueBody) {
  return validateChangeMarker(
    extractSingleJsonMarker(issueBody, JSON_CONTRACTS.changeMarker, 'Issue'),
  );
}

export function renderChangeMarker(value) {
  return renderJsonMarker(validateChangeMarker({
    $schema: JSON_CONTRACTS.changeMarker,
    ...value,
  }));
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
  if (![JSON_CONTRACTS.capabilityResult, LEGACY_CAPABILITY_RESULT_SCHEMA]
    .includes(value.resultSchema)) {
    throw new Error('Capability definition.resultSchema is invalid');
  }
  return {
    ...value,
    resultSchema: JSON_CONTRACTS.capabilityResult,
  };
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
  assertSchema(value, JSON_CONTRACTS.dependencyCandidates, 'Dependency output');
  assertKnownKeys(value, new Set(['$schema', 'candidates']), 'Dependency output');
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
  assertSchema(value, JSON_CONTRACTS.dependencyGraphPatch, 'Dependency graph patch');
  assertKnownKeys(
    value,
    new Set(['$schema', 'evaluationMode', 'evaluatedRefs', 'summaries', 'upsert', 'remove']),
    'Dependency graph patch',
  );
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
    { $schema: JSON_CONTRACTS.dependencyCandidates, candidates: upsertCandidates },
    knownRefs,
    existingEdges,
    minimumConfidence,
  );
  return {
    $schema: JSON_CONTRACTS.dependencyGraphPatch,
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
  assertSchema(value, JSON_CONTRACTS.dependencySummary, 'Dependency summary');
  assertKnownKeys(value, new Set(['$schema', 'managedEdges']), 'Dependency summary');
  if (!Array.isArray(value.managedEdges)) {
    throw new Error('Dependency summary.managedEdges must be an array');
  }

  return {
    $schema: JSON_CONTRACTS.dependencySummary,
    managedEdges: normalizeManagedEdges(value.managedEdges, 'Dependency summary.managedEdges'),
  };
}

export function validateDependencyCheckpoint(value) {
  assertSchema(value, JSON_CONTRACTS.dependencyCheckpoint, 'Dependency checkpoint');
  assertKnownKeys(
    value,
    new Set(['$schema', 'commit', 'changes', 'managedEdges', 'inference']),
    'Dependency checkpoint',
  );
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
    $schema: JSON_CONTRACTS.dependencyCheckpoint,
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
  return renderJsonMarker(validateDependencySummary({
    $schema: JSON_CONTRACTS.dependencySummary,
    ...value,
  }));
}

export function parseDependencySummary(commentBody) {
  return validateDependencySummary(
    extractSingleJsonMarker(
      commentBody,
      JSON_CONTRACTS.dependencySummary,
      'Dependency summary',
    ),
  );
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

const OPERATIONS = Object.freeze(['apply', 'verify', 'sync', 'archive']);
const TASK_ID = /^\d+(?:\.\d+)*$/;
const ISO_TIMESTAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z$/;
const RUN_BRANCH = /^openspec\/[a-z0-9]+(?:-[a-z0-9]+)*$/;
const CHECKPOINT_VERDICTS = new Set(['complete', 'partial', 'needs_decision', 'failed']);
const FINDING_SEVERITIES = new Set(['critical', 'warning', 'suggestion']);
const RUN_PHASES = new Set(['apply', 'verify', 'sync', 'archive', 'merge', 'done', 'aborted']);
const RUN_STATUSES = new Set(['dispatching', 'running', 'ready', 'gated', 'closed']);
const RUN_OUTCOMES = new Set([null, 'merged', 'aborted', 'closed-unmerged']);
const GATE_KINDS = new Set(['decision', 'review', 'failure', 'merge']);
const GATE_COMMAND_NAMES = new Set(['approve', 'retry', 'answer', 'abort']);
const RUN_STATE_FENCE = /```json\n([\s\S]*?)\n```/;
export const MAX_RUN_STATE_TEXT = 65535;

function assertBoundedString(value, path, maxLength) {
  assertString(value, path);
  if (value.length > maxLength) {
    throw new Error(`${path} must be at most ${maxLength} characters`);
  }
}

function assertSingleLine(value, path) {
  if (value.includes('\r') || value.includes('\n')) {
    throw new Error(`${path} must be a single line`);
  }
}

function assertPositiveInteger(value, path) {
  if (!Number.isInteger(value) || value < 1) {
    throw new Error(`${path} must be a positive integer`);
  }
}

function assertSha(value, path) {
  if (typeof value !== 'string' || !GIT_SHA.test(value)) {
    throw new Error(`${path} must be a 40-character lowercase Git SHA`);
  }
}

function assertTimestamp(value, path) {
  if (typeof value !== 'string' || !ISO_TIMESTAMP.test(value) || Number.isNaN(Date.parse(value))) {
    throw new Error(`${path} must be an ISO-8601 UTC timestamp`);
  }
}

function assertOperation(value, path) {
  if (!OPERATIONS.includes(value)) throw new Error(`${path} is invalid`);
}

function assertTaskId(value, path) {
  if (typeof value !== 'string' || !TASK_ID.test(value)) throw new Error(`${path} is invalid`);
}

function validateFindings(value, path, { maxItems = Infinity, maxText = Infinity } = {}) {
  assertObject(value, path);
  assertKnownKeys(value, new Set(['critical', 'warning', 'suggestion', 'items']), path);
  for (const severity of FINDING_SEVERITIES) {
    if (!Number.isInteger(value[severity]) || value[severity] < 0) {
      throw new Error(`${path}.${severity} must be a non-negative integer`);
    }
  }
  if (!Array.isArray(value.items) || value.items.length > maxItems) {
    throw new Error(`${path}.items must be an array of at most ${maxItems} findings`);
  }
  value.items.forEach((item, index) => {
    const itemPath = `${path}.items[${index}]`;
    assertObject(item, itemPath);
    assertKnownKeys(item, new Set(['severity', 'text']), itemPath);
    if (!FINDING_SEVERITIES.has(item.severity)) throw new Error(`${itemPath}.severity is invalid`);
    assertBoundedString(item.text, `${itemPath}.text`, maxText);
  });
  return value;
}

function validateRunTask(value, path) {
  assertObject(value, path);
  assertKnownKeys(value, new Set(['id', 'title', 'capabilities']), path);
  assertTaskId(value.id, `${path}.id`);
  assertString(value.title, `${path}.title`);
  if (!Array.isArray(value.capabilities)
    || value.capabilities.length === 0
    || value.capabilities.some((id) => typeof id !== 'string' || !CAPABILITY_ID.test(id))) {
    throw new Error(`${path}.capabilities is invalid`);
  }
}

export function validateCheckpoint(value) {
  assertSchema(value, JSON_CONTRACTS.changeCheckpoint, 'Checkpoint');
  assertKnownKeys(
    value,
    new Set(['$schema', 'change', 'operation', 'task', 'verdict', 'summary', 'validation', 'question', 'findings']),
    'Checkpoint',
  );
  assertKebabCase(value.change, 'Checkpoint.change');
  assertOperation(value.operation, 'Checkpoint.operation');
  if (value.operation === 'apply') {
    assertTaskId(value.task, 'Checkpoint.task');
  } else if (value.task !== undefined) {
    throw new Error('Checkpoint.task is valid only for apply');
  }
  if (!CHECKPOINT_VERDICTS.has(value.verdict)) throw new Error('Checkpoint.verdict is invalid');
  assertBoundedString(value.summary, 'Checkpoint.summary', 500);
  assertBoundedString(value.validation, 'Checkpoint.validation', 300);
  assertSingleLine(value.validation, 'Checkpoint.validation');
  if (value.verdict === 'needs_decision') {
    assertBoundedString(value.question, 'Checkpoint.question', 1000);
  } else if (value.question !== undefined) {
    throw new Error('Checkpoint.question is valid only for needs_decision');
  }
  const findingsRequired = value.operation === 'verify' && value.verdict === 'complete';
  if (findingsRequired) {
    const findings = validateFindings(value.findings, 'Checkpoint.findings', { maxItems: 20, maxText: 300 });
    const total = findings.critical + findings.warning + findings.suggestion;
    if (findings.items.length !== Math.min(total, 20)) {
      throw new Error('Checkpoint.findings.items must list every finding, or the 20 most important when there are more');
    }
    for (const severity of FINDING_SEVERITIES) {
      if (findings.items.filter((item) => item.severity === severity).length > findings[severity]) {
        throw new Error(`Checkpoint.findings lists more ${severity} items than its ${severity} count`);
      }
    }
  } else if (value.findings !== undefined) {
    throw new Error('Checkpoint.findings is valid only for a complete verify');
  }
  return { ...value };
}

export function parseCheckpointTrailer(commitMessage) {
  if (typeof commitMessage !== 'string') {
    throw new Error('Checkpoint commit message must be a string');
  }
  const lines = commitMessage
    .split(/\r?\n/)
    .filter((line) => line.startsWith(CHECKPOINT_TRAILER));
  if (lines.length === 0) return null;
  if (lines.length > 1) {
    throw new Error(`Checkpoint commit must contain at most one ${CHECKPOINT_TRAILER} trailer`);
  }
  const content = lines[0].slice(CHECKPOINT_TRAILER.length).trim();
  let value;
  try {
    value = JSON.parse(content);
  } catch (error) {
    throw new Error(`Checkpoint trailer contains invalid JSON: ${error.message}`);
  }
  return validateCheckpoint(value);
}

export function renderCheckpointTrailer(value) {
  return `${CHECKPOINT_TRAILER} ${JSON.stringify(validateCheckpoint({
    $schema: JSON_CONTRACTS.changeCheckpoint,
    ...value,
  }))}`;
}

export function validateDispatch(value) {
  assertSchema(value, JSON_CONTRACTS.changeDispatch, 'Dispatch');
  assertKnownKeys(
    value,
    new Set(['$schema', 'change', 'operation', 'issue', 'pr', 'branch', 'baseRef', 'expectedHeadSha', 'attempt', 'task', 'answers']),
    'Dispatch',
  );
  assertKebabCase(value.change, 'Dispatch.change');
  assertOperation(value.operation, 'Dispatch.operation');
  assertPositiveInteger(value.issue, 'Dispatch.issue');
  assertPositiveInteger(value.pr, 'Dispatch.pr');
  if (typeof value.branch !== 'string' || !RUN_BRANCH.test(value.branch)) {
    throw new Error('Dispatch.branch is invalid');
  }
  assertString(value.baseRef, 'Dispatch.baseRef');
  assertSha(value.expectedHeadSha, 'Dispatch.expectedHeadSha');
  assertPositiveInteger(value.attempt, 'Dispatch.attempt');
  if (value.operation === 'apply') {
    assertObject(value.task, 'Dispatch.task');
    assertKnownKeys(value.task, new Set(['id', 'title', 'capabilities', 'capabilityPaths', 'block']), 'Dispatch.task');
    assertTaskId(value.task.id, 'Dispatch.task.id');
    assertString(value.task.title, 'Dispatch.task.title');
    assertCapabilityIds(value.task.capabilities, 'Dispatch.task.capabilities');
    const expectedPaths = value.task.capabilities.map((id) => `openspec/capabilities/${id}.md`);
    if (JSON.stringify(value.task.capabilityPaths) !== JSON.stringify(expectedPaths)) {
      throw new Error('Dispatch.task.capabilityPaths must resolve each capability directly');
    }
    assertString(value.task.block, 'Dispatch.task.block');
  } else if (value.task !== undefined) {
    throw new Error('Dispatch.task is valid only for apply');
  }
  if (!Array.isArray(value.answers)) throw new Error('Dispatch.answers must be an array');
  value.answers.forEach((answer, index) => {
    const path = `Dispatch.answers[${index}]`;
    assertObject(answer, path);
    assertKnownKeys(answer, new Set(['question', 'text', 'by']), path);
    assertString(answer.question, `${path}.question`);
    assertString(answer.text, `${path}.text`);
    assertString(answer.by, `${path}.by`);
  });
  return { ...value };
}

export function validateRunState(value) {
  assertSchema(value, JSON_CONTRACTS.changeRunState, 'Run state');
  assertKnownKeys(
    value,
    new Set([
      '$schema', 'change', 'issue', 'pr', 'branch', 'base', 'requestedBy', 'revision', 'headSha',
      'phase', 'status', 'outcome', 'current', 'credited', 'gate', 'answers', 'commandCursor', 'updatedAt',
    ]),
    'Run state',
  );
  assertKebabCase(value.change, 'Run state.change');
  assertPositiveInteger(value.issue, 'Run state.issue');
  assertPositiveInteger(value.pr, 'Run state.pr');
  if (typeof value.branch !== 'string' || !RUN_BRANCH.test(value.branch)) {
    throw new Error('Run state.branch is invalid');
  }
  assertObject(value.base, 'Run state.base');
  assertKnownKeys(value.base, new Set(['ref', 'sha']), 'Run state.base');
  assertString(value.base.ref, 'Run state.base.ref');
  assertSha(value.base.sha, 'Run state.base.sha');
  assertString(value.requestedBy, 'Run state.requestedBy');
  assertPositiveInteger(value.revision, 'Run state.revision');
  assertSha(value.headSha, 'Run state.headSha');
  if (!RUN_PHASES.has(value.phase)) throw new Error('Run state.phase is invalid');
  if (!RUN_STATUSES.has(value.status)) throw new Error('Run state.status is invalid');
  if (!RUN_OUTCOMES.has(value.outcome)) throw new Error('Run state.outcome is invalid');

  if (value.current !== null) {
    const path = 'Run state.current';
    assertObject(value.current, path);
    assertKnownKeys(value.current, new Set(['operation', 'task', 'attempt', 'startSha', 'baselineSha', 'agentTask', 'dispatchedAt']), path);
    assertOperation(value.current.operation, `${path}.operation`);
    if (value.current.operation === 'apply') {
      validateRunTask(value.current.task, `${path}.task`);
    } else if (value.current.task !== null) {
      throw new Error(`${path}.task is valid only for apply`);
    }
    assertPositiveInteger(value.current.attempt, `${path}.attempt`);
    assertSha(value.current.startSha, `${path}.startSha`);
    assertSha(value.current.baselineSha, `${path}.baselineSha`);
    if (value.current.agentTask !== null) {
      assertObject(value.current.agentTask, `${path}.agentTask`);
      assertKnownKeys(value.current.agentTask, new Set(['id', 'state', 'url']), `${path}.agentTask`);
      assertString(value.current.agentTask.id, `${path}.agentTask.id`);
      assertString(value.current.agentTask.state, `${path}.agentTask.state`);
      if (value.current.agentTask.url !== undefined) {
        assertString(value.current.agentTask.url, `${path}.agentTask.url`);
      }
    }
    if (value.current.dispatchedAt !== null) {
      assertTimestamp(value.current.dispatchedAt, `${path}.dispatchedAt`);
    }
  }

  if (!Array.isArray(value.credited)) throw new Error('Run state.credited must be an array');
  value.credited.forEach((entry, index) => {
    const path = `Run state.credited[${index}]`;
    assertObject(entry, path);
    assertKnownKeys(entry, new Set(['operation', 'task', 'sha', 'attempt', 'log']), path);
    assertOperation(entry.operation, `${path}.operation`);
    if (entry.operation === 'apply') assertTaskId(entry.task, `${path}.task`);
    else if (entry.task !== undefined) throw new Error(`${path}.task is valid only for apply`);
    assertSha(entry.sha, `${path}.sha`);
    if (entry.attempt !== undefined) assertPositiveInteger(entry.attempt, `${path}.attempt`);
    if (entry.log !== undefined && entry.log !== null) assertPositiveInteger(entry.log, `${path}.log`);
  });

  if (value.gate !== null) {
    const path = 'Run state.gate';
    assertObject(value.gate, path);
    assertKnownKeys(
      value.gate,
      new Set(['kind', 'operation', 'task', 'question', 'reason', 'findings', 'commands', 'openedAt', 'notified', 'log']),
      path,
    );
    if (!GATE_KINDS.has(value.gate.kind)) throw new Error(`${path}.kind is invalid`);
    if (value.gate.operation !== null) assertOperation(value.gate.operation, `${path}.operation`);
    if (value.gate.task !== undefined) validateRunTask(value.gate.task, `${path}.task`);
    if (value.gate.question !== undefined) assertString(value.gate.question, `${path}.question`);
    if (value.gate.reason !== undefined) assertString(value.gate.reason, `${path}.reason`);
    if (value.gate.findings !== undefined) validateFindings(value.gate.findings, `${path}.findings`);
    if (!Array.isArray(value.gate.commands)
      || value.gate.commands.some((command) => !GATE_COMMAND_NAMES.has(command))) {
      throw new Error(`${path}.commands is invalid`);
    }
    assertTimestamp(value.gate.openedAt, `${path}.openedAt`);
    if (typeof value.gate.notified !== 'boolean') throw new Error(`${path}.notified must be a boolean`);
    if (value.gate.log !== null) assertPositiveInteger(value.gate.log, `${path}.log`);
    if (value.gate.kind === 'decision' && value.gate.question === undefined) {
      throw new Error(`${path}.question is required for decision gates`);
    }
  }

  if (!Array.isArray(value.answers) || value.answers.length > 5) {
    throw new Error('Run state.answers must be an array of at most 5 answers');
  }
  value.answers.forEach((answer, index) => {
    const path = `Run state.answers[${index}]`;
    assertObject(answer, path);
    assertKnownKeys(answer, new Set(['question', 'text', 'by', 'comment']), path);
    assertString(answer.question, `${path}.question`);
    assertString(answer.text, `${path}.text`);
    assertString(answer.by, `${path}.by`);
    assertPositiveInteger(answer.comment, `${path}.comment`);
  });
  if (!Number.isInteger(value.commandCursor) || value.commandCursor < 0) {
    throw new Error('Run state.commandCursor must be a non-negative integer');
  }
  assertTimestamp(value.updatedAt, 'Run state.updatedAt');

  if ((value.status === 'gated') !== (value.gate !== null)) {
    throw new Error('Run state.gate must be present exactly when status is gated');
  }
  if (['dispatching', 'running'].includes(value.status) && value.current === null) {
    throw new Error(`Run state.current is required while ${value.status}`);
  }
  if (value.status === 'running' && value.current.agentTask === null) {
    throw new Error('Run state.current.agentTask is required while running');
  }
  if ((value.status === 'closed') !== ['done', 'aborted'].includes(value.phase)) {
    throw new Error('Run state.status closed requires a done or aborted phase');
  }
  if ((value.outcome === null) !== (value.status !== 'closed')) {
    throw new Error('Run state.outcome must be set exactly when the run is closed');
  }
  return value;
}

export function renderRunStateText(state) {
  validateRunState(state);
  const text = [
    'Processing state for the OpenSpec workflow. Do not edit; only the workflow can update this check run.',
    '',
    '```json',
    JSON.stringify(state),
    '```',
  ].join('\n');
  if (text.length > MAX_RUN_STATE_TEXT) {
    throw new Error(`Run state is ${text.length} characters, above the ${MAX_RUN_STATE_TEXT}-character check-run limit`);
  }
  return text;
}

export function parseRunStateText(text) {
  if (typeof text !== 'string') throw new Error('Run state text must be a string');
  const match = text.replace(/\r\n/g, '\n').match(RUN_STATE_FENCE);
  if (!match) throw new Error('Run state text does not contain a JSON block');
  let value;
  try {
    value = JSON.parse(match[1]);
  } catch (error) {
    throw new Error(`Run state JSON is invalid: ${error.message}`);
  }
  return validateRunState(value);
}