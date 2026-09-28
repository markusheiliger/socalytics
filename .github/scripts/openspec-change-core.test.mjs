import assert from 'node:assert/strict';
import test from 'node:test';

import {
  calculateManagedEdgeChanges,
  deriveReadiness,
  mergeManagedDependencyGraph,
  mergeManagedEdgeProvenance,
  parseChangeMarker,
  parseCapabilityDefinition,
  parseCapabilityTasks,
  parseDependencyCheckpoint,
  parseDependencySummary,
  parseLedgerEntry,
  parseQueueCheckpoint,
  parseQueueOperationResult,
  parseQueueState,
  renderChangeMarker,
  renderDependencySummary,
  renderLedgerEntry,
  renderQueueState,
  retryDecision,
  selectNextOperation,
  selectNextTask,
  serializeDependencyCheckpoint,
  validateDependencyOutput,
  validateDependencyGraphPatch,
  validateMergedDependencyGraph,
  validateCapabilitySet,
  validateOperationEvidence,
  validateSynchronizedDeltas,
} from './openspec-change-core.mjs';

const activeMarker = {
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
    'resultSchema: schemas/capability-result-v1.schema.json',
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
      resultSchema: 'schemas/capability-result-v1.schema.json',
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
      'resultSchema: schemas/capability-result-v1.schema.json',
      '---',
    ].join('\n'), 'architecture'),
    /does not match/,
  );
});

test('accepts high-confidence acyclic dependencies and flags low confidence', () => {
  const result = validateDependencyOutput({
    version: 1,
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
    version: 1,
    candidates: [
      { changeRef: 'two', dependsOn: 'one', confidence: 1, evidence: ['a'] },
      { changeRef: 'two', dependsOn: 'one', confidence: 1, evidence: ['b'] },
    ],
  };
  assert.throws(() => validateDependencyOutput(duplicate, ['one', 'two']), /duplicates dependency/);
  assert.throws(
    () => validateDependencyOutput({
      version: 1,
      candidates: [{ changeRef: 'two', dependsOn: 'missing', confidence: 1, evidence: ['a'] }],
    }, ['one', 'two']),
    /unknown change/,
  );
  assert.throws(
    () => validateDependencyOutput({
      version: 1,
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
    { version: 1, candidates: [] },
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
  const summary = { version: 1, managedEdges: desired };
  assert.deepEqual(parseDependencySummary(renderDependencySummary(summary)), summary);
  assert.deepEqual(calculateManagedEdgeChanges(previous, desired, previous).update, desired);
});

test('validates explicit incremental graph patches and merges deterministically', () => {
  const patch = validateDependencyGraphPatch({
    version: 2,
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
    version: 2,
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
    version: 2,
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
    version: 1,
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

test('derives runnable state only for open unblocked archived-safe issues', () => {
  const ready = deriveReadiness({
    issueState: 'open',
    labels: ['openspec:change', 'openspec:enqueued'],
    lifecycle: 'active',
    blockers: [{ number: 10, state: 'closed', archivedOnMain: true }],
  });
  assert.deepEqual(ready, { runnable: true, reasons: [] });

  const blocked = deriveReadiness({
    issueState: 'open',
    labels: ['openspec:change'],
    lifecycle: 'active',
    blockers: [{ number: 10, state: 'closed', archivedOnMain: false }],
    activeTaskState: 'waiting_for_user',
    needsDecision: true,
  });
  assert.deepEqual(blocked.reasons, [
    'not-enqueued',
    'needs-human-decision',
    'agent-task-active',
    'blocked-by:10',
  ]);
  assert.deepEqual(deriveReadiness({
    issueState: 'open',
    labels: ['openspec:enqueued'],
    lifecycle: 'active',
    activeTaskState: 'failed',
    activeTaskAttempt: 2,
  }).reasons, ['retries-exhausted']);
});

test('selects OpenSpec operations from persisted lifecycle evidence', () => {
  assert.equal(selectNextOperation({
    lifecycle: 'active',
    tasksComplete: false,
    verificationPassed: false,
    specsSynchronized: false,
  }), 'apply');
  assert.equal(selectNextOperation({
    lifecycle: 'active',
    tasksComplete: true,
    verificationPassed: false,
    specsSynchronized: false,
  }), 'verify');
  assert.equal(selectNextOperation({
    lifecycle: 'active',
    tasksComplete: true,
    verificationPassed: true,
    specsSynchronized: false,
  }), 'sync');
  assert.equal(selectNextOperation({
    lifecycle: 'active',
    tasksComplete: true,
    verificationPassed: true,
    specsSynchronized: true,
  }), 'archive');
  assert.equal(selectNextOperation({ lifecycle: 'archived' }), 'complete');
});

test('applies the single retry policy', () => {
  assert.equal(retryDecision('failed', 1), 'retry');
  assert.equal(retryDecision('timed_out', 2), 'stop');
  assert.equal(retryDecision('cancelled', 1), 'stop');
  assert.equal(retryDecision('waiting_for_user', 1), 'stop');
  assert.equal(retryDecision('completed', 1), 'advance');
  assert.equal(retryDecision('in_progress', 1), 'wait');
});

test('round-trips strict operation ledger entries', () => {
  const entry = {
    version: 1,
    changeRef: 'add-platform',
    operation: 'apply',
    attempt: 1,
    taskId: 'task-123',
    sessionId: 'session-123',
    branch: 'copilot/add-platform',
    beforeSha: 'a'.repeat(40),
    afterSha: 'b'.repeat(40),
    outcome: 'succeeded',
    validation: 'Focused tests passed.',
    recovery: null,
    recordedAt: '2026-09-24T16:00:00Z',
  };
  assert.deepEqual(parseLedgerEntry(renderLedgerEntry(entry)), entry);
  assert.throws(
    () => renderLedgerEntry({ ...entry, outcome: 'failed', recovery: null }),
    /require recovery guidance/,
  );
  assert.throws(
    () => renderLedgerEntry({ ...entry, validation: 'bad --> delimiter' }),
    /HTML comment delimiter/,
  );
});

test('requires operation-specific persisted evidence and coherent checkpoints', () => {
  assert.deepEqual(validateOperationEvidence({
    operation: 'apply',
    outcome: 'succeeded',
    beforeSha: 'a'.repeat(40),
    afterSha: 'b'.repeat(40),
    filesChanged: true,
    tasksComplete: true,
  }), { valid: true, reason: null });
  assert.deepEqual(validateOperationEvidence({
    operation: 'verify',
    outcome: 'succeeded',
    beforeSha: 'a'.repeat(40),
    afterSha: 'b'.repeat(40),
    filesChanged: false,
    verificationPassed: false,
  }), { valid: false, reason: 'verify-evidence-missing' });
  assert.deepEqual(validateOperationEvidence({
    operation: 'sync',
    outcome: 'succeeded',
    beforeSha: 'a'.repeat(40),
    afterSha: 'a'.repeat(40),
    filesChanged: true,
    specsSynchronized: true,
  }), { valid: false, reason: 'checkpoint-not-advanced' });
  assert.deepEqual(validateOperationEvidence({
    operation: 'archive',
    outcome: 'succeeded',
    beforeSha: 'invalid',
    afterSha: 'invalid',
    filesChanged: false,
    lifecycle: 'archived',
  }), { valid: false, reason: 'invalid-checkpoint' });
});

test('parses one strict queue checkpoint trailer from a commit message', () => {
  const checkpoint = {
    version: 1,
    changeRef: 'add-platform',
    operation: 'apply',
    taskId: '1.2',
    verdict: 'pass',
    validation: 'dotnet build: passed',
  };
  assert.deepEqual(
    parseQueueCheckpoint(`Implement task\n\nOpenSpec-Queue-Checkpoint: ${JSON.stringify(checkpoint)}`),
    checkpoint,
  );
  assert.throws(
    () => parseQueueCheckpoint('No checkpoint'),
    /exactly one OpenSpec-Queue-Checkpoint:/,
  );
  assert.throws(
    () => parseQueueCheckpoint(`OpenSpec-Queue-Checkpoint: ${JSON.stringify({
      ...checkpoint,
      operation: 'verify',
    })}`),
    /taskId is valid only for apply/,
  );
});

test('round-trips queue state and parses an operation result envelope', () => {
  const state = {
    version: 1,
    changeRef: 'add-platform',
    issueNumber: 12,
    status: 'dispatched',
    operation: 'apply',
    attempt: 1,
    taskId: 'task-1',
    sessionId: null,
    applyTaskId: '1.1',
    applyTaskCapabilities: ['implementation'],
    completedApplyTaskIds: [],
    baseRef: 'main',
    headRef: null,
    beforeSha: 'a'.repeat(40),
    pullRequestNumber: null,
    updatedAt: '2026-09-24T18:00:00Z',
  };
  assert.deepEqual(parseQueueState(renderQueueState(state)), state);
  assert.deepEqual(parseQueueOperationResult(JSON.stringify({
    schema: 'operation-result-v1',
    changeRef: 'add-platform',
    operation: 'verify',
    verdict: 'pass',
    validation: 'Verification complete.',
  })), {
    schema: 'operation-result-v1',
    changeRef: 'add-platform',
    operation: 'verify',
    verdict: 'pass',
    validation: 'Verification complete.',
  });
  assert.throws(
    () => parseQueueOperationResult('no result'),
    /exactly one JSON object/,
  );
});

test('parses one structured capability result envelope', () => {
  const expected = {
    schema: 'capability-result-v1',
    changeRef: 'add-platform',
    operation: 'apply',
    taskId: '1.1',
    capabilities: ['architecture', 'implementation'],
    verdict: 'pass',
    artifactsChanged: ['src/platform/file.cs'],
    validation: [{ command: 'dotnet test', outcome: 'passed' }],
    summary: 'Task complete.',
    blockingFindings: [],
  };
  const result = JSON.stringify(expected);
  assert.deepEqual(parseQueueOperationResult(result), expected);
  for (const legacy of [
    `OPEN_SPEC_CAPABILITY_RESULT_V1=${result}`,
    `OPEN_SPEC_CAPABILITY_RESULT_V1 ${result}`,
    `OPEN_SPEC_CLOUD_OPERATION_V1=${JSON.stringify({
      schema: 'operation-result-v1',
      changeRef: 'add-platform',
      operation: 'verify',
      verdict: 'pass',
      validation: 'Complete.',
    })}`,
  ]) {
    assert.throws(
      () => parseQueueOperationResult(legacy),
      /exactly one JSON object/,
    );
  }
  assert.throws(
    () => parseQueueOperationResult(`Result:\n${result}`),
    /exactly one JSON object/,
  );
  assert.throws(
    () => parseQueueOperationResult('[]'),
    /Queue operation result must be an object/,
  );
  assert.throws(
    () => parseQueueOperationResult('{"changeRef":"add-platform"}'),
    /schema must be a non-empty string/,
  );
  assert.throws(
    () => parseQueueOperationResult(JSON.stringify({
      ...expected,
      resultingSha: 'b'.repeat(40),
    })),
    /unknown field\(s\): resultingSha/,
  );
  assert.throws(
    () => parseQueueOperationResult('{"schema":"unknown-result-v1"}'),
    /schema is unsupported/,
  );
  assert.throws(
    () => parseQueueOperationResult(JSON.stringify({ ...expected, extra: true })),
    /contains unknown field/,
  );
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
