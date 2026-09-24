import assert from 'node:assert/strict';
import test from 'node:test';

import {
  calculateManagedEdgeChanges,
  deriveReadiness,
  parseChangeMarker,
  parseDependencySummary,
  parseLedgerEntry,
  parseCloudOperationResult,
  parseQueueState,
  parseOwnedTasks,
  renderChangeMarker,
  renderDependencySummary,
  renderLedgerEntry,
  renderQueueState,
  retryDecision,
  selectNextOperation,
  selectNextTask,
  validateDependencyOutput,
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

test('parses owned multiline tasks and selects the first unchecked task', () => {
  const markdown = [
    '## Tasks',
    '',
    '- [x] 1.1 Implement the parser. Owner: soca-developer.',
    '  Validation: run the focused tests.',
    '- [ ] 1.2 **Owner: soca-verifier.** Verify the parser independently.',
    '  Do not edit implementation files.',
    '',
  ].join('\n');
  const tasks = parseOwnedTasks(markdown);
  assert.equal(tasks.length, 2);
  assert.deepEqual(
    tasks.map(({ id, completed, owner }) => ({ id, completed, owner })),
    [
      { id: '1.1', completed: true, owner: 'soca-developer' },
      { id: '1.2', completed: false, owner: 'soca-verifier' },
    ],
  );
  assert.equal(selectNextTask(markdown).id, '1.2');
});

test('rejects tasks with missing, duplicate, or unsupported owners', () => {
  assert.throws(() => parseOwnedTasks('- [ ] 1.1 Missing owner'), /exactly one/);
  assert.throws(
    () => parseOwnedTasks('- [ ] 1.1 Owner: soca-developer. Owner: soca-verifier.'),
    /exactly one/,
  );
  assert.throws(
    () => parseOwnedTasks('- [ ] 1.1 Owner: soca-unknown.'),
    /unsupported owner/,
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
    afterSha: 'a'.repeat(40),
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
  }), { valid: false, reason: 'changed-files-without-checkpoint' });
  assert.deepEqual(validateOperationEvidence({
    operation: 'archive',
    outcome: 'succeeded',
    beforeSha: 'invalid',
    afterSha: 'invalid',
    filesChanged: false,
    lifecycle: 'archived',
  }), { valid: false, reason: 'invalid-checkpoint' });
});

test('round-trips queue state and parses one cloud result marker', () => {
  const state = {
    version: 1,
    changeRef: 'add-platform',
    issueNumber: 12,
    status: 'dispatched',
    operation: 'apply',
    attempt: 1,
    taskId: 'task-1',
    sessionId: null,
    baseRef: 'main',
    headRef: null,
    beforeSha: 'a'.repeat(40),
    pullRequestNumber: null,
    updatedAt: '2026-09-24T18:00:00Z',
  };
  assert.deepEqual(parseQueueState(renderQueueState(state)), state);
  assert.deepEqual(parseCloudOperationResult([
    'other log output',
    'OPEN_SPEC_CLOUD_OPERATION_V1={"changeRef":"add-platform","operation":"verify","verdict":"pass","validation":"No critical findings."}',
  ].join('\n')), {
    changeRef: 'add-platform',
    operation: 'verify',
    verdict: 'pass',
    validation: 'No critical findings.',
  });
  assert.throws(
    () => parseCloudOperationResult('no marker'),
    /exactly one result marker/,
  );
});

test('does not absorb trailing sections or nested tasks into the preceding owner block', () => {
  const markdown = [
    '- [ ] 1.1 Parent. Owner: soca-developer.',
    '  - [ ] 1.1.1 Child. Owner: soca-verifier.',
    '',
    '## Notes',
    '',
    'Owner: soca-auditor reviews later.',
  ].join('\n');
  assert.deepEqual(
    parseOwnedTasks(markdown).map(({ id, owner }) => ({ id, owner })),
    [
      { id: '1.1', owner: 'soca-developer' },
      { id: '1.1.1', owner: 'soca-verifier' },
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
