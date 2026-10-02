import assert from 'node:assert/strict';
import test from 'node:test';

import {
  MAX_ATTEMPTS,
  applyCommand,
  createRunState,
  creditSession,
  decideNext,
  describeActivity,
  finalizeClosedUnmerged,
  finalizeMerged,
  issueLabels,
  lifecycleCheck,
  markDispatching,
  markGateNotified,
  markRunning,
  noteHeadMoved,
  recoverDispatch,
  shortTaskTitle,
  stageLabel,
} from './openspec-change-state.mjs';

const sha = (c) => c.repeat(40);
const now = new Date('2026-10-02T10:00:00Z');
const later = (minutes) => new Date(now.getTime() + minutes * 60_000);
const tasks = [
  { id: '1.1', title: 'Confirm the prerequisite. Capabilities: verification.', completed: true, capabilities: ['verification'] },
  { id: '2.1', title: 'Implement Club commands and queries. Verify with tests.', completed: false, capabilities: ['implementation'] },
  { id: '2.2', title: 'Implement the resolver', completed: false, capabilities: ['implementation'] },
];

function fresh() {
  return createRunState({
    change: 'add-club-identity-foundation',
    issue: 4,
    pr: 21,
    branch: 'openspec/add-club-identity-foundation',
    baseSha: sha('9'),
    headSha: sha('0'),
    requestedBy: 'markusheiliger',
    now,
  });
}

function running(state = fresh(), decision = decideNext(state, { tasks })) {
  const dispatching = markDispatching(state, {
    operation: decision.operation,
    task: decision.task,
    attempt: decision.attempt,
    startSha: state.headSha,
    now,
  });
  return markRunning(dispatching, { session: { runtime: 'actions', id: 'task-1', state: 'queued', url: 'https://x/1' }, now });
}

function checkpoint(overrides = {}) {
  return {
    change: 'add-club-identity-foundation',
    operation: 'apply',
    task: '2.1',
    verdict: 'complete',
    summary: 'Done.',
    validation: 'tests passed',
    ...overrides,
  };
}

function findings(critical, warning, suggestion) {
  return { critical, warning, suggestion, items: [] };
}

test('shortens long task titles to their first sentence without capabilities', () => {
  assert.equal(shortTaskTitle('Implement Club commands and queries. Verify with tests.'), 'Implement Club commands and queries');
  assert.equal(shortTaskTitle('Do it Capabilities: implementation.'), 'Do it');
  assert.equal(
    shortTaskTitle('**Capabilities: architecture, implementation.** Resolve the shared `persistence` boundary: add an ADR.'),
    'Resolve the shared persistence boundary',
  );
  assert.equal(shortTaskTitle('x'.repeat(200)).length, 72);
});

test('plans the next unchecked task, then verify, sync, and archive', () => {
  const state = fresh();
  assert.deepEqual(decideNext(state, { tasks }), {
    action: 'dispatch',
    operation: 'apply',
    task: { id: '2.1', title: 'Implement Club commands and queries', capabilities: ['implementation'] },
    attempt: 1,
  });
  const allDone = tasks.map((task) => ({ ...task, completed: true }));
  assert.equal(decideNext(state, { tasks: allDone }).operation, 'verify');
  for (const phase of ['verify', 'sync', 'archive']) {
    assert.equal(decideNext({ ...state, phase }).operation, phase);
  }
  assert.throws(() => decideNext(state), /Parsed tasks are required/);
  assert.equal(decideNext(running()).action, 'none');
});

test('credits a complete apply checkpoint and moves to the next task', () => {
  const result = creditSession(running(), {
    agentState: 'completed',
    headSha: sha('a'),
    checkpoint: checkpoint(),
    checkpointSha: sha('a'),
    evidence: { ok: true },
    sessionLog: 7,
  }, later(5));
  assert.equal(result.result.kind, 'credited');
  assert.equal(result.state.status, 'ready');
  assert.equal(result.state.current, null);
  assert.equal(result.state.headSha, sha('a'));
  assert.deepEqual(result.state.credited, [{ operation: 'apply', task: '2.1', sha: sha('a'), attempt: 1, log: 7 }]);
});

test('credits a checkpoint even while the agent task still reports in progress', () => {
  const result = creditSession(running(), {
    agentState: 'in_progress',
    headSha: sha('a'),
    checkpoint: checkpoint(),
    checkpointSha: sha('a'),
    evidence: { ok: true },
  }, later(5));
  assert.equal(result.result.kind, 'credited');
});

test('waits while the agent session is active without a checkpoint', () => {
  const state = running();
  assert.deepEqual(creditSession(state, { agentState: 'in_progress' }, later(1)), {
    state,
    result: { kind: 'wait' },
  });
});

test('retries once, then opens a failure gate', () => {
  const first = creditSession(running(), { agentState: 'timed_out', headSha: sha('b') }, later(60));
  assert.equal(first.result.kind, 'retry');
  assert.equal(first.state.status, 'ready');
  assert.equal(first.state.current.attempt, 2);
  assert.equal(first.state.current.startSha, sha('b'));
  assert.equal(first.state.current.baselineSha, sha('0'));
  assert.equal(decideNext(first.state, { tasks }).attempt, 2);

  const second = creditSession(running(first.state), { agentState: 'failed', headSha: sha('b') }, later(120));
  assert.equal(MAX_ATTEMPTS, 2);
  assert.equal(second.result.kind, 'gate');
  assert.equal(second.state.gate.kind, 'failure');
  assert.deepEqual(second.state.gate.commands, ['retry', 'abort']);
  assert.match(second.state.gate.reason, /without a checkpoint/);
});

test('treats partial progress, failed verdicts, invalid trailers, and failed evidence as retries', () => {
  for (const observation of [
    { checkpoint: checkpoint({ verdict: 'partial' }), checkpointSha: sha('b') },
    { checkpoint: checkpoint({ verdict: 'failed', summary: 'Build broke.' }), checkpointSha: sha('b') },
    { checkpointError: 'bad json', agentState: 'completed' },
    { checkpoint: checkpoint(), checkpointSha: sha('b'), evidence: { ok: false, reason: 'task 2.1 is still unchecked' } },
  ]) {
    const result = creditSession(running(), { headSha: sha('b'), ...observation }, later(10));
    assert.equal(result.result.kind, 'retry', JSON.stringify(observation));
  }
});

test('opens a decision gate and resumes the same task with the answer', () => {
  const gated = creditSession(running(), {
    agentState: 'completed',
    headSha: sha('b'),
    checkpoint: checkpoint({ verdict: 'needs_decision', question: 'Deny or resolve?' }),
    checkpointSha: sha('b'),
  }, later(10)).state;
  assert.equal(gated.status, 'gated');
  assert.equal(decideNext(gated).action, 'notify-gate');
  const notified = markGateNotified(gated, { log: 99, now: later(11) });
  assert.equal(decideNext(notified).action, 'none');

  const answered = applyCommand(notified, { name: 'answer', text: 'Fail closed.', by: 'alice', commentId: 500, now: later(20) });
  assert.equal(answered.accepted, true);
  assert.equal(answered.state.status, 'ready');
  assert.equal(answered.state.commandCursor, 500);
  assert.deepEqual(answered.state.answers, [{ question: 'Deny or resolve?', text: 'Fail closed.', by: 'alice', comment: 500 }]);
  assert.deepEqual(decideNext(answered.state, { tasks }), {
    action: 'dispatch',
    operation: 'apply',
    task: notified.current.task,
    attempt: 1,
  });
});

test('maps waiting_for_user and cancelled sessions to gates', () => {
  assert.equal(creditSession(running(), { agentState: 'waiting_for_user' }, later(5)).state.gate.kind, 'decision');
  assert.equal(creditSession(running(), { agentState: 'cancelled' }, later(5)).state.gate.kind, 'failure');
});

function verifyRunning() {
  const allDone = tasks.map((task) => ({ ...task, completed: true }));
  const state = fresh();
  return running(state, decideNext(state, { tasks: allDone }));
}

function creditVerify(found) {
  return creditSession(verifyRunning(), {
    agentState: 'completed',
    headSha: sha('d'),
    checkpoint: checkpoint({ operation: 'verify', task: undefined, findings: found }),
    checkpointSha: sha('d'),
    evidence: { ok: true },
  }, later(30));
}

test('continues automatically after a clean verify', () => {
  const { state, result } = creditVerify(findings(0, 0, 0));
  assert.equal(result.kind, 'credited');
  assert.equal(state.phase, 'sync');
  assert.equal(decideNext(state).operation, 'sync');
});

test('opens a review gate for warnings or suggestions', () => {
  for (const found of [findings(0, 0, 1), findings(0, 2, 0)]) {
    const { state } = creditVerify(found);
    assert.equal(state.gate.kind, 'review');
    assert.deepEqual(state.gate.commands, ['approve', 'retry', 'abort']);
  }
  const { state } = creditVerify(findings(0, 1, 1));
  const approved = applyCommand(state, { name: 'approve', by: 'alice', commentId: 1, now: later(40) }).state;
  assert.equal(approved.phase, 'sync');
  const retried = applyCommand(state, { name: 'retry', by: 'alice', commentId: 1, now: later(40) }).state;
  assert.equal(decideNext(retried).operation, 'verify');
});

test('opens a failure gate for critical findings that cannot be approved', () => {
  const { state } = creditVerify(findings(1, 3, 0));
  assert.equal(state.gate.kind, 'failure');
  assert.match(state.gate.reason, /1 critical issue/);
  const approve = applyCommand(state, { name: 'approve', by: 'alice', commentId: 3, now: later(40) });
  assert.equal(approve.accepted, false);
  assert.match(approve.message, /not available/);
  assert.equal(approve.state.commandCursor, 3);
  const retry = applyCommand(state, { name: 'retry', by: 'alice', commentId: 4, now: later(40) }).state;
  assert.equal(retry.phase, 'verify');
  assert.equal(decideNext(retry).operation, 'verify');
});

test('opens the merge gate after a credited archive and finalizes on merge', () => {
  const state = running({ ...fresh(), phase: 'archive' }, { action: 'dispatch', operation: 'archive', task: null, attempt: 1 });
  const archived = creditSession(state, {
    agentState: 'completed',
    headSha: sha('e'),
    checkpoint: checkpoint({ operation: 'archive', task: undefined }),
    checkpointSha: sha('e'),
    evidence: { ok: true },
  }, later(50)).state;
  assert.equal(archived.phase, 'merge');
  assert.equal(archived.gate.kind, 'merge');
  assert.deepEqual(lifecycleCheck(archived), { status: 'completed', conclusion: 'success', title: 'Archived · ready for human merge' });
  assert.deepEqual(issueLabels(archived), ['openspec:awaiting-review']);
  assert.equal(stageLabel(archived), null);

  const merged = finalizeMerged(archived, { now: later(60) });
  assert.equal(merged.outcome, 'merged');
  assert.deepEqual(lifecycleCheck(merged), { status: 'completed', conclusion: 'success', title: 'Merged' });
  assert.deepEqual(issueLabels(merged), []);
});

test('rejects commands that do not fit the current gate', () => {
  const state = running();
  const rejected = applyCommand(state, { name: 'approve', by: 'alice', commentId: 9, now });
  assert.equal(rejected.accepted, false);
  assert.match(rejected.message, /only available while the workflow is waiting/);
  assert.equal(rejected.state.status, 'running');
  assert.equal(rejected.state.commandCursor, 9);
});

test('abort closes the run from any open state and closing keeps the abort outcome', () => {
  const aborted = applyCommand(running(), { name: 'abort', by: 'alice', commentId: 2, now }).state;
  assert.equal(aborted.outcome, 'aborted');
  assert.equal(finalizeClosedUnmerged(aborted, { now }).outcome, 'aborted');
  assert.equal(applyCommand(aborted, { name: 'retry', by: 'alice', commentId: 3, now }).accepted, false);
  assert.equal(finalizeClosedUnmerged(fresh(), { now }).outcome, 'closed-unmerged');
});

test('recovers interrupted dispatches by adopting, waiting, re-dispatching, or gating', () => {
  const state = fresh();
  const dispatching = markDispatching(state, { ...decideNext(state, { tasks }), startSha: state.headSha, now });
  assert.equal(recoverDispatch(dispatching, { matches: [{ runtime: 'actions', id: '7', state: 'queued' }], now }).action, 'adopted');
  assert.equal(recoverDispatch(dispatching, { matches: [], now: later(1) }).action, 'wait');
  const redispatch = recoverDispatch(dispatching, { matches: [], now: later(11) });
  assert.equal(redispatch.action, 'redispatch');
  assert.equal(decideNext(redispatch.state, { tasks }).action, 'dispatch');
  assert.equal(recoverDispatch(dispatching, { matches: [{ runtime: 'actions', id: 'a' }, { runtime: 'actions', id: 'b' }], now }).state.gate.kind, 'failure');
});

test('records human pushes only while no session is in flight', () => {
  assert.equal(noteHeadMoved(running(), { headSha: sha('f'), now }).moved, false);
  const moved = noteHeadMoved(fresh(), { headSha: sha('f'), now });
  assert.equal(moved.moved, true);
  assert.equal(moved.state.headSha, sha('f'));
  const retry = creditSession(running(), { agentState: 'failed', headSha: sha('b') }, now).state;
  assert.equal(noteHeadMoved(retry, { headSha: sha('f'), now }).state.current.baselineSha, sha('f'));
});

test('describes activity for the lifecycle check and labels', () => {
  const state = running();
  assert.equal(describeActivity(state), 'Apply 2.1 running · Implement Club commands and queries');
  assert.deepEqual(lifecycleCheck(state).status, 'in_progress');
  assert.deepEqual(issueLabels(state), ['openspec:processing']);
  assert.equal(stageLabel(state), 'openspec:stage:apply');
  const gated = creditSession(state, { agentState: 'cancelled' }, later(1)).state;
  assert.equal(lifecycleCheck(gated).conclusion, 'action_required');
  assert.deepEqual(issueLabels(gated), ['openspec:processing', 'openspec:needs-attention']);
});

test('keeps the evidence baseline and records bounded feedback across retries', () => {
  const failed = creditSession(running(), {
    headSha: sha('b'),
    checkpoint: checkpoint(),
    checkpointSha: sha('b'),
    evidence: { ok: false, reason: 'the platform tests failed at the checkpoint', feedback: `${'x'.repeat(5000)}\nAssert failed` },
  }, later(10));
  assert.equal(failed.result.kind, 'retry');
  assert.equal(failed.state.current.feedback.length, 4000);
  assert.match(failed.state.current.feedback, /Assert failed$/);
  const redispatched = markDispatching(failed.state, { ...decideNext(failed.state, { tasks }), startSha: sha('b'), dispatchId: 'd2', now });
  assert.equal(redispatched.current.feedback, failed.state.current.feedback);
  assert.equal(redispatched.current.dispatchId, 'd2');
  assert.equal(redispatched.current.baselineSha, sha('0'));
});
