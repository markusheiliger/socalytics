import assert from 'node:assert/strict';
import test from 'node:test';

import {
  JSON_CONTRACTS,
  calculateManagedEdgeChanges,
  mergeManagedDependencyGraph,
  mergeManagedEdgeProvenance,
  parseChangeMarker,
  parseCapabilityDefinition,
  parseCapabilityTasks,
  parseCheckpointTrailer,
  parseDependencyCheckpoint,
  parseDependencySummary,
  parseRunStateText,
  renderChangeMarker,
  renderCheckpointTrailer,
  renderDependencySummary,
  renderRunStateText,
  selectNextTask,
  serializeDependencyCheckpoint,
  validateDependencyOutput,
  validateDependencyGraphPatch,
  validateMergedDependencyGraph,
  validateCapabilitySet,
  validateDispatch,
  validateRunState,
  validateSynchronizedDeltas,
} from './openspec-change-core.mjs';

const activeMarker = {
  $schema: JSON_CONTRACTS.changeMarker,
  repository: 'markusheiliger/socalytics',
  ref: 'add-platform-persistence-foundation',
  lifecycle: 'active',
  gitRef: 'main',
  path: 'openspec/changes/add-platform-persistence-foundation',
};

test('round-trips active and archived change markers', () => {
  assert.deepEqual(parseChangeMarker(renderChangeMarker(activeMarker)), activeMarker);
  const archived = {
    ...activeMarker,
    lifecycle: 'archived',
    path: 'openspec/changes/archive/2026-09-24-add-platform-persistence-foundation',
  };
  assert.deepEqual(parseChangeMarker(renderChangeMarker(archived)), archived);
});

test('rejects malformed, duplicate, and inconsistent change markers', () => {
  assert.throws(() => parseChangeMarker('missing'), /exactly one/);
  assert.throws(
    () => parseChangeMarker(`${renderChangeMarker(activeMarker)}\n${renderChangeMarker(activeMarker)}`),
    /exactly one/,
  );
  assert.throws(
    () => renderChangeMarker({ ...activeMarker, path: 'openspec/changes/wrong' }),
    /Active change path/,
  );
});

test('parses capability-backed multiline tasks and selects the first unchecked task', () => {
  const markdown = [
    '## Tasks',
    '',
    '- [x] 1.1 Implement the parser. Capabilities: implementation, architecture.',
    '  Validation: run the focused tests.',
    '- [ ] 1.2 **Capabilities: verification.** Verify the parser independently.',
    '  Do not edit implementation files.',
    '',
  ].join('\n');
  const tasks = parseCapabilityTasks(markdown);
  assert.equal(tasks.length, 2);
  assert.deepEqual(
    tasks.map(({ id, completed, capabilities }) => ({ id, completed, capabilities })),
    [
      { id: '1.1', completed: true, capabilities: ['architecture', 'implementation'] },
      { id: '1.2', completed: false, capabilities: ['verification'] },
    ],
  );
  assert.equal(selectNextTask(markdown).id, '1.2');
});

test('rejects tasks with missing, duplicate, or malformed capabilities', () => {
  assert.throws(() => parseCapabilityTasks('- [ ] 1.1 Missing capabilities'), /exactly one/);
  assert.throws(
    () => parseCapabilityTasks(
      '- [ ] 1.1 Capabilities: implementation. Capabilities: verification.',
    ),
    /exactly one/,
  );
  assert.throws(
    () => parseCapabilityTasks('- [ ] 1.1 Capabilities: implementation, implementation.'),
    /unique sorted/,
  );
});

test('validates capability definitions and composition', () => {
  const definition = (id, overrides = {}) => parseCapabilityDefinition([
    '---',
    `id: ${id}`,
    'version: 1',
    `operations: [${overrides.operations ?? 'apply'}]`,
    `composition: ${overrides.composition ?? 'composable'}`,
    `mutation: ${overrides.mutation ?? 'scoped'}`,
    `isolation: ${overrides.isolation ?? 'shared'}`,
    `resultSchema: ${JSON_CONTRACTS.capabilityResult}`,
    '---',
    '',
    `# ${id}`,
  ].join('\n'), id);
  const architecture = definition('architecture');
  const implementation = definition('implementation');
  assert.deepEqual(
    validateCapabilitySet([implementation, architecture]),
    {
      ids: ['architecture', 'implementation'],
      mutation: 'scoped',
      isolation: 'shared',
      resultSchema: JSON_CONTRACTS.capabilityResult,
    },
  );
  assert.throws(
    () => validateCapabilitySet([
      architecture,
      definition('verification', {
        composition: 'exclusive',
        mutation: 'checkbox-only',
        isolation: 'required',
      }),
    ]),
    /exclusive/,
  );
  assert.throws(
    () => validateCapabilitySet([
      architecture,
      definition('design', { isolation: 'required' }),
    ]),
    /incompatible isolation/,
  );
  assert.throws(
    () => parseCapabilityDefinition([
      '---',
      'id: wrong',
      'version: 1',
      'operations: [apply]',
      'composition: composable',
      'mutation: scoped',
      'isolation: shared',
      `resultSchema: ${JSON_CONTRACTS.capabilityResult}`,
      '---',
    ].join('\n'), 'architecture'),
    /does not match/,
  );
});

test('normalizes the previous capability-relative result schema reference', () => {
  const definition = parseCapabilityDefinition([
    '---',
    'id: implementation',
    'version: 1',
    'operations: [apply]',
    'composition: composable',
    'mutation: scoped',
    'isolation: shared',
    'resultSchema: schemas/capability-result-v1.schema.json',
    '---',
  ].join('\n'), 'implementation');

  assert.equal(definition.resultSchema, JSON_CONTRACTS.capabilityResult);
});

test('accepts high-confidence acyclic dependencies and flags low confidence', () => {
  const result = validateDependencyOutput({
    $schema: JSON_CONTRACTS.dependencyCandidates,
    candidates: [
      {
        changeRef: 'add-club',
        dependsOn: 'add-platform',
        confidence: 0.95,
        evidence: ['add-club/design.md:12'],
      },
      {
        changeRef: 'add-analysis',
        dependsOn: 'add-club',
        confidence: 0.6,
        evidence: ['add-analysis/proposal.md:8'],
      },
    ],
  }, ['add-platform', 'add-club', 'add-analysis']);
  assert.equal(result.accepted.length, 1);
  assert.equal(result.review.length, 1);
});

test('rejects dependency duplicates, unknown refs, and whole-graph cycles', () => {
  const duplicate = {
    $schema: JSON_CONTRACTS.dependencyCandidates,
    candidates: [
      { changeRef: 'two', dependsOn: 'one', confidence: 1, evidence: ['a'] },
      { changeRef: 'two', dependsOn: 'one', confidence: 1, evidence: ['b'] },
    ],
  };
  assert.throws(() => validateDependencyOutput(duplicate, ['one', 'two']), /duplicates dependency/);
  assert.throws(
    () => validateDependencyOutput({
      $schema: JSON_CONTRACTS.dependencyCandidates,
      candidates: [{ changeRef: 'two', dependsOn: 'missing', confidence: 1, evidence: ['a'] }],
    }, ['one', 'two']),
    /unknown change/,
  );
  assert.throws(
    () => validateDependencyOutput({
      $schema: JSON_CONTRACTS.dependencyCandidates,
      candidates: [{ changeRef: 'one', dependsOn: 'two', confidence: 1, evidence: ['a'] }],
    }, ['one', 'two'], [{ changeRef: 'two', dependsOn: 'one' }]),
    /Dependency cycle/,
  );
});

test('reconciles only previously managed native dependencies', () => {
  const previous = [
    { changeRef: 'two', dependsOn: 'one' },
    { changeRef: 'three', dependsOn: 'one' },
  ];
  const desired = [
    { changeRef: 'three', dependsOn: 'one' },
    { changeRef: 'three', dependsOn: 'two' },
  ];
  const native = [
    ...previous,
    { changeRef: 'two', dependsOn: 'manual' },
  ];
  assert.deepEqual(calculateManagedEdgeChanges(previous, desired, native), {
    add: [{ changeRef: 'three', dependsOn: 'two' }],
    remove: [{ changeRef: 'two', dependsOn: 'one' }],
    update: [],
  });
});

test('ignores manual native dependency endpoints outside OpenSpec while checking cycles', () => {
  assert.deepEqual(validateDependencyOutput(
    { $schema: JSON_CONTRACTS.dependencyCandidates, candidates: [] },
    ['one', 'two'],
    [{ changeRef: 'two', dependsOn: 'manual-issue' }],
  ), { accepted: [], review: [] });
});

test('round-trips managed dependency provenance and detects updates', () => {
  const previous = [{
    changeRef: 'two',
    dependsOn: 'one',
    confidence: 0.9,
    evidence: ['old'],
  }];
  const desired = [{
    changeRef: 'two',
    dependsOn: 'one',
    confidence: 0.95,
    evidence: ['new'],
  }];
  const summary = { $schema: JSON_CONTRACTS.dependencySummary, managedEdges: desired };
  assert.deepEqual(parseDependencySummary(renderDependencySummary(summary)), summary);
  assert.deepEqual(calculateManagedEdgeChanges(previous, desired, previous).update, desired);
});

test('validates explicit incremental graph patches and merges deterministically', () => {
  const patch = validateDependencyGraphPatch({
    $schema: JSON_CONTRACTS.dependencyGraphPatch,
    evaluationMode: 'incremental',
    evaluatedRefs: ['three'],
    summaries: [{ ref: 'three', summary: 'Third change' }],
    upsert: [{
      changeRef: 'three',
      dependsOn: 'two',
      confidence: 0.95,
      evidence: ['new', 'new'],
    }],
    remove: [{
      changeRef: 'three',
      dependsOn: 'one',
      evidence: ['obsolete', 'obsolete'],
    }],
  }, ['one', 'two', 'three']);
  assert.deepEqual(patch, {
    $schema: JSON_CONTRACTS.dependencyGraphPatch,
    evaluationMode: 'incremental',
    evaluatedRefs: ['three'],
    summaries: [{ ref: 'three', summary: 'Third change' }],
    upsert: [{
      changeRef: 'three',
      dependsOn: 'two',
      confidence: 0.95,
      evidence: ['new'],
    }],
    review: [],
    remove: [{
      changeRef: 'three',
      dependsOn: 'one',
      evidence: ['obsolete'],
    }],
  });
  assert.deepEqual(mergeManagedDependencyGraph([
    {
      changeRef: 'two',
      dependsOn: 'one',
      confidence: 0.9,
      evidence: ['unrelated'],
    },
    {
      changeRef: 'three',
      dependsOn: 'one',
      confidence: 0.9,
      evidence: ['old'],
    },
  ], patch), [
    {
      changeRef: 'three',
      dependsOn: 'two',
      confidence: 0.95,
      evidence: ['new'],
    },
    {
      changeRef: 'two',
      dependsOn: 'one',
      confidence: 0.9,
      evidence: ['unrelated'],
    },
  ]);
});

test('requires complete full evaluations and scopes mutations to evaluated endpoints', () => {
  const base = {
    $schema: JSON_CONTRACTS.dependencyGraphPatch,
    evaluationMode: 'incremental',
    evaluatedRefs: ['one'],
    summaries: [{ ref: 'one', summary: 'First change' }],
    upsert: [],
    remove: [{
      changeRef: 'two',
      dependsOn: 'one',
      evidence: ['The evaluated prerequisite changed.'],
    }],
  };
  assert.deepEqual(
    validateDependencyGraphPatch(base, ['one', 'two']).remove,
    base.remove,
  );
  assert.throws(() => validateDependencyGraphPatch({
    ...base,
    evaluatedRefs: ['three'],
    summaries: [{ ref: 'three', summary: 'Third change' }],
  }, ['one', 'two', 'three']), /does not touch an evaluated change/);
  assert.throws(
    () => validateDependencyGraphPatch({
      ...base,
      evaluationMode: 'full',
      remove: [],
    }, ['one', 'two']),
    /evaluate every active change/,
  );
});

test('validates cycles across the complete merged managed and manual graph', () => {
  assert.throws(() => validateMergedDependencyGraph({
    knownRefs: ['one', 'two', 'three'],
    previousManagedEdges: [{ changeRef: 'two', dependsOn: 'one' }],
    managedEdges: [
      {
        changeRef: 'two',
        dependsOn: 'one',
        confidence: 0.9,
        evidence: ['retained'],
      },
      {
        changeRef: 'three',
        dependsOn: 'two',
        confidence: 0.95,
        evidence: ['inferred'],
      },
    ],
    nativeEdges: [
      { changeRef: 'two', dependsOn: 'one' },
      { changeRef: 'one', dependsOn: 'three' },
    ],
  }), /Dependency cycle/);
});

test('migrates only legacy comment edges that still exist natively', () => {
  const checkpointEdge = {
    changeRef: 'two',
    dependsOn: 'one',
    confidence: 0.99,
    evidence: ['checkpoint'],
  };
  const commentEdge = {
    changeRef: 'three',
    dependsOn: 'one',
    confidence: 0.9,
    evidence: ['comment'],
  };
  assert.deepEqual(mergeManagedEdgeProvenance({
    checkpointEdges: [checkpointEdge],
    legacyCommentEdges: [
      { ...checkpointEdge, confidence: 0.5, evidence: ['stale'] },
      commentEdge,
      {
        changeRef: 'three',
        dependsOn: 'two',
        confidence: 0.9,
        evidence: ['orphan'],
      },
    ],
    nativeEdges: [
      { changeRef: 'two', dependsOn: 'one' },
      { changeRef: 'three', dependsOn: 'one' },
    ],
  }), [commentEdge, checkpointEdge]);
});

test('round-trips strict deterministic dependency checkpoints', () => {
  const checkpoint = {
    $schema: JSON_CONTRACTS.dependencyCheckpoint,
    commit: 'a'.repeat(40),
    changes: [
      { ref: 'two', digest: '2'.repeat(64), summary: 'Second change' },
      { ref: 'one', digest: '1'.repeat(64), summary: 'First change' },
    ],
    managedEdges: [{
      changeRef: 'two',
      dependsOn: 'one',
      confidence: 0.95,
      evidence: ['design.md'],
    }],
    inference: {
      mode: 'full',
      evaluatedRefs: ['two', 'one'],
      baseCommit: null,
      minimumConfidence: 0.85,
    },
  };
  const parsed = parseDependencyCheckpoint(serializeDependencyCheckpoint(checkpoint));
  assert.deepEqual(parsed.changes.map(({ ref }) => ref), ['one', 'two']);
  assert.deepEqual(parsed.inference.evaluatedRefs, ['one', 'two']);
  assert.throws(
    () => parseDependencyCheckpoint(JSON.stringify({ ...checkpoint, extra: true })),
    /unknown field/,
  );
  assert.throws(
    () => serializeDependencyCheckpoint({
      ...checkpoint,
      inference: { ...checkpoint.inference, evaluatedRefs: ['one'] },
    }),
    /evaluate every summarized change/,
  );
});

const sha = (c) => c.repeat(40);

function runState(overrides = {}) {
  return {
    $schema: JSON_CONTRACTS.changeRunState,
    change: 'add-club-identity-foundation',
    issue: 4,
    pr: 21,
    branch: 'openspec/add-club-identity-foundation',
    base: { ref: 'main', sha: sha('9') },
    requestedBy: 'markusheiliger',
    revision: 3,
    headSha: sha('c'),
    phase: 'apply',
    status: 'running',
    outcome: null,
    current: {
      operation: 'apply',
      task: { id: '2.1', title: 'Club Dapper commands', capabilities: ['implementation'] },
      attempt: 1,
      startSha: sha('b'),
      baselineSha: sha('b'),
      agentTask: { id: 'task-1', state: 'in_progress', url: 'https://github.com/x/y/tasks/1' },
      dispatchedAt: '2026-10-02T10:41:07Z',
    },
    credited: [{ operation: 'apply', task: '1.1', sha: sha('a'), attempt: 1, log: 5 }],
    gate: null,
    answers: [],
    commandCursor: 0,
    updatedAt: '2026-10-02T10:41:09Z',
    ...overrides,
  };
}

test('parses at most one strict checkpoint trailer from a commit message', () => {
  const checkpoint = {
    $schema: JSON_CONTRACTS.changeCheckpoint,
    change: 'add-platform',
    operation: 'apply',
    task: '1.2',
    verdict: 'complete',
    summary: 'Added migrations.',
    validation: 'dotnet test: passed',
  };
  assert.deepEqual(
    parseCheckpointTrailer(`Implement task\n\n${renderCheckpointTrailer(checkpoint)}`),
    checkpoint,
  );
  assert.equal(parseCheckpointTrailer('No checkpoint'), null);
  assert.throws(
    () => parseCheckpointTrailer(`${renderCheckpointTrailer(checkpoint)}\n${renderCheckpointTrailer(checkpoint)}`),
    /at most one OpenSpec-JSON:/,
  );
  assert.throws(
    () => parseCheckpointTrailer('OpenSpec-JSON: {nope'),
    /invalid JSON/,
  );
  assert.throws(
    () => renderCheckpointTrailer({ ...checkpoint, operation: 'verify' }),
    /task is valid only for apply/,
  );
  assert.throws(
    () => renderCheckpointTrailer({ ...checkpoint, verdict: 'needs_decision' }),
    /question must be a non-empty string/,
  );
  assert.throws(
    () => renderCheckpointTrailer({ ...checkpoint, validation: 'two\nlines' }),
    /single line/,
  );
});

test('requires bounded findings exactly for a complete verify', () => {
  const verify = {
    change: 'add-platform',
    operation: 'verify',
    verdict: 'complete',
    summary: 'Verified.',
    validation: 'openspec validate: passed',
    findings: {
      critical: 0,
      warning: 1,
      suggestion: 0,
      items: [{ severity: 'warning', text: 'Scenario not covered.' }],
    },
  };
  assert.equal(parseCheckpointTrailer(renderCheckpointTrailer(verify)).findings.warning, 1);
  const { findings, ...withoutFindings } = verify;
  assert.throws(() => renderCheckpointTrailer(withoutFindings), /findings must be an object/);
  assert.throws(
    () => renderCheckpointTrailer({ ...withoutFindings, verdict: 'failed', findings }),
    /valid only for a complete verify/,
  );
  assert.throws(
    () => renderCheckpointTrailer({
      ...verify,
      findings: { ...findings, items: Array.from({ length: 21 }, () => findings.items[0]) },
    }),
    /at most 20 findings/,
  );
  assert.throws(
    () => renderCheckpointTrailer({ ...verify, findings: { ...findings, items: [] } }),
    /must list every finding/,
  );
  assert.throws(
    () => renderCheckpointTrailer({
      ...verify,
      findings: { ...findings, warning: 0, suggestion: 1 },
    }),
    /more warning items than its warning count/,
  );
});

test('validates dispatch envelopes with direct capability paths', () => {
  const dispatch = {
    $schema: JSON_CONTRACTS.changeDispatch,
    change: 'add-platform',
    operation: 'apply',
    issue: 4,
    pr: 21,
    branch: 'openspec/add-platform',
    baseRef: 'main',
    expectedHeadSha: sha('a'),
    attempt: 1,
    task: {
      id: '1.1',
      title: 'Do it',
      capabilities: ['architecture', 'implementation'],
      capabilityPaths: ['openspec/capabilities/architecture.md', 'openspec/capabilities/implementation.md'],
      block: '- [ ] 1.1 Do it. Capabilities: architecture, implementation.',
    },
    answers: [{ question: 'Q?', text: 'A.', by: 'alice' }],
  };
  assert.deepEqual(validateDispatch(dispatch), dispatch);
  assert.throws(
    () => validateDispatch({ ...dispatch, task: { ...dispatch.task, capabilityPaths: ['x.md', 'y.md'] } }),
    /resolve each capability directly/,
  );
  assert.throws(
    () => validateDispatch({ ...dispatch, branch: 'copilot/add-platform' }),
    /branch is invalid/,
  );
  const { task, ...verify } = dispatch;
  assert.deepEqual(validateDispatch({ ...verify, operation: 'verify' }).operation, 'verify');
});

test('round-trips run state through the lifecycle check-run text', () => {
  const state = runState();
  assert.deepEqual(parseRunStateText(renderRunStateText(state)), state);
  assert.deepEqual(parseRunStateText(renderRunStateText(state).replace(/\n/g, '\r\n')), state);
  assert.throws(() => parseRunStateText('no state here'), /does not contain a JSON block/);
  assert.throws(
    () => renderRunStateText(runState({ credited: Array.from({ length: 700 }, () => ({ operation: 'apply', task: '1.1', sha: sha('a'), attempt: 1, log: 123456789 })) })),
    /above the 65535-character check-run limit/,
  );
  const answer = { question: 'Q?', text: 'A.', by: 'alice', comment: 1 };
  assert.throws(() => validateRunState(runState({ answers: Array.from({ length: 6 }, () => answer) })), /at most 5 answers/);
});

test('rejects inconsistent run states', () => {
  assert.throws(() => validateRunState(runState({ status: 'gated' })), /gate must be present exactly when status is gated/);
  assert.throws(
    () => validateRunState(runState({ current: { ...runState().current, agentTask: null } })),
    /agentTask is required while running/,
  );
  assert.throws(
    () => validateRunState(runState({ status: 'closed', current: null })),
    /requires a done or aborted phase/,
  );
  assert.throws(
    () => validateRunState(runState({ phase: 'done', status: 'closed', current: null })),
    /outcome must be set exactly when the run is closed/,
  );
  assert.throws(
    () => validateRunState(runState({
      status: 'gated',
      current: null,
      gate: {
        kind: 'decision', operation: 'apply', commands: ['answer'], openedAt: '2026-10-02T10:00:00Z', notified: false, log: null,
      },
    })),
    /question is required for decision gates/,
  );
  assert.throws(() => validateRunState({ ...runState(), extra: 1 }), /unknown field/);
});

test('does not absorb trailing sections or nested tasks into the preceding capability block', () => {
  const markdown = [
    '- [ ] 1.1 Parent. Capabilities: implementation.',
    '  - [ ] 1.1.1 Child. Capabilities: verification.',
    '',
    '## Notes',
    '',
    'Capabilities: audit is considered later.',
  ].join('\n');
  assert.deepEqual(
    parseCapabilityTasks(markdown).map(({ id, capabilities }) => ({ id, capabilities })),
    [
      { id: '1.1', capabilities: ['implementation'] },
      { id: '1.1.1', capabilities: ['verification'] },
    ],
  );
});

test('proves synchronized added, modified, and removed requirements', () => {
  const kept = {
    text: 'The system SHALL keep this.',
    scenarios: [{ rawText: '- **WHEN** used\n- **THEN** it works' }],
  };
  const removed = {
    text: 'The system SHALL remove this.',
    scenarios: [{ rawText: '- **WHEN** inspected\n- **THEN** it is absent' }],
  };
  assert.equal(validateSynchronizedDeltas({
    deltas: [
      { spec: 'one', operation: 'ADDED', requirement: kept },
      { spec: 'one', operation: 'REMOVED', requirement: removed },
    ],
  }, {
    one: { requirements: [kept] },
  }), true);
  assert.throws(
    () => validateSynchronizedDeltas({
      deltas: [{ spec: 'one', operation: 'MODIFIED', requirement: kept }],
    }, {
      one: { requirements: [{ ...kept, scenarios: [] }] },
    }),
    /not synchronized/,
  );
});
