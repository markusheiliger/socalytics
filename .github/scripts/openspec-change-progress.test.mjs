import assert from 'node:assert/strict';
import test from 'node:test';

import {
  admittedEntry,
  closedEntry,
  commandEntry,
  describeNext,
  findLogComment,
  gateEntry,
  nextLogNumber,
  operationCheckRun,
  parseLogMarker,
  renderLogEntry,
  renderOverview,
  sanitizeAgentText,
  sessionFinishedEntry,
  sessionStartedEntry,
} from './openspec-change-progress.mjs';
import {
  applyCommand,
  createRunState,
  creditSession,
  decideNext,
  finalizeMerged,
  markDispatching,
  markGateNotified,
  markRunning,
} from './openspec-change-state.mjs';

const context = { owner: 'markusheiliger', repo: 'socalytics' };
const sha = (c) => c.repeat(40);
const now = new Date('2026-10-02T10:00:00Z');
const tasks = [
  { id: '1.1', title: 'Confirm the prerequisite. Capabilities: verification.', completed: true, capabilities: ['verification'] },
  { id: '2.1', title: 'Implement Club commands. Capabilities: implementation.', completed: false, capabilities: ['implementation'] },
];

function running() {
  const state = createRunState({
    change: 'add-club',
    issue: 4,
    pr: 21,
    branch: 'openspec/add-club',
    baseSha: sha('9'),
    headSha: sha('0'),
    requestedBy: 'markusheiliger',
    now,
  });
  const decision = decideNext(state, { tasks });
  const dispatching = markDispatching(state, { ...decision, startSha: state.headSha, now });
  return markRunning(dispatching, {
    agentTask: { id: 'task-7', state: 'queued', url: 'https://github.com/markusheiliger/socalytics/copilot/tasks/7' },
    now,
  });
}

test('sanitizes agent text for safe rendering in comments', () => {
  const text = sanitizeAgentText(
    '# Title\n@alice see <script>x</script> <!-- hidden --> [doc](https://evil.example/a) '
      + '[ok](https://github.com/markusheiliger/socalytics/pull/1) https://evil.example/b ```code```',
    context,
  );
  assert.doesNotMatch(text, /^#/m);
  assert.doesNotMatch(text, /@alice/);
  assert.match(text, /@\u200balice/);
  assert.doesNotMatch(text, /<script>|<!--/);
  assert.doesNotMatch(text, /\(https:\/\/evil\.example\/a\)/);
  assert.match(text, /\[ok\]\(https:\/\/github\.com\/markusheiliger\/socalytics\/pull\/1\)/);
  assert.match(text, /`https:\/\/evil\.example\/b`/);
  assert.doesNotMatch(text, /```/);
  assert.equal(sanitizeAgentText('x'.repeat(900), context).length, 500);
});

test('renders numbered entries with an idempotency marker', () => {
  const state = running();
  const body = renderLogEntry({ change: 'add-club', n: 2, ...sessionStartedEntry({ state, context }) });
  assert.match(body, /^### ▶️ #2 · Apply 2\.1 · Implement Club commands: running/);
  assert.match(body, /\[Agent session\]\(https:\/\/github\.com\/markusheiliger\/socalytics\/copilot\/tasks\/7\)/);
  assert.deepEqual(parseLogMarker(body), { change: 'add-club', n: 2, event: 'session:task-7' });
  const comments = [{ body }, { body: renderLogEntry({ change: 'add-club', n: 5, event: 'admit', emoji: 'x', title: 't' }) }, { body: 'chat' }];
  assert.equal(nextLogNumber(comments, 'add-club'), 6);
  assert.equal(nextLogNumber(comments, 'other'), 1);
  assert.equal(findLogComment(comments, 'add-club', 'session:task-7'), comments[0]);
  assert.equal(findLogComment(comments, 'add-club', 'gate:x'), null);
  assert.doesNotMatch(body, /^\/openspec/m);
});

test('renders the started, credited, retried, and gated stories', () => {
  const admitted = renderLogEntry({ change: 'add-club', n: 1, ...admittedEntry({ state: running(), context, blockers: [6] }) });
  assert.match(admitted, /📥 #1 · Started/);
  assert.match(admitted, /archived on `main` \(#6\)/);

  const before = running();
  const checkpoint = {
    change: 'add-club', operation: 'apply', task: '2.1', verdict: 'complete', summary: 'Added @team commands.', validation: 'dotnet test: 14 passed',
  };
  const credited = creditSession(before, {
    agentState: 'completed', headSha: sha('a'), checkpoint, checkpointSha: sha('a'), evidence: { ok: true },
  }, now);
  const done = renderLogEntry({
    change: 'add-club', n: 3, ...sessionFinishedEntry({ before, after: credited.state, result: credited.result, checkpoint, checkpointSha: sha('a'), context }),
  });
  assert.match(done, /✅ #3 · Apply 2\.1 · Implement Club commands: done/);
  assert.match(done, /> \*\*Agent:\*\* Added @\u200bteam commands\./);
  assert.match(done, /compare\/0{40}\.\.\.a{40}/);
  assert.match(done, /Validation: dotnet test: 14 passed/);

  const retried = creditSession(before, { agentState: 'timed_out', headSha: sha('b') }, now);
  const retry = renderLogEntry({
    change: 'add-club', n: 4, ...sessionFinishedEntry({ before, after: retried.state, result: retried.result, context }),
  });
  assert.match(retry, /⚠️ #4 · .*retrying/);
  assert.match(retry, /\*\*Next:\*\* Retry \(attempt 2\)\./);

  const asked = creditSession(before, {
    agentState: 'completed',
    headSha: sha('c'),
    checkpoint: { ...checkpoint, verdict: 'needs_decision', question: 'Deny or resolve?' },
    checkpointSha: sha('c'),
  }, now);
  const gate = renderLogEntry({ change: 'add-club', n: 5, ...gateEntry({ state: asked.state, context }) });
  assert.match(gate, /❓ #5 · Apply 2\.1 · Implement Club commands: decision needed · @markusheiliger/);
  assert.match(gate, /\*\*Agent asks:\*\* Deny or resolve\?/);
  assert.match(gate, /`\/openspec answer &lt;text&gt;`|`\/openspec answer <text>`/);
});

test('renders review findings grouped by severity', () => {
  const state = {
    ...running(),
    status: 'gated',
    current: null,
    phase: 'verify',
    gate: {
      kind: 'review',
      operation: 'verify',
      commands: ['approve', 'retry', 'abort'],
      openedAt: '2026-10-02T11:00:00Z',
      notified: false,
      log: null,
      findings: {
        critical: 0,
        warning: 1,
        suggestion: 1,
        items: [
          { severity: 'suggestion', text: 'Rename helper.' },
          { severity: 'warning', text: 'Scenario not covered.' },
        ],
      },
    },
  };
  const body = renderLogEntry({ change: 'add-club', n: 9, ...gateEntry({ state, context }) });
  assert.match(body, /🔎 #9 · Review needed · @markusheiliger/);
  assert.match(body, /1 warning, 1 suggestion/);
  assert.ok(body.indexOf('**Warning**') < body.indexOf('**Suggestion**'));
});

test('records accepted and rejected commands in plain words', () => {
  const state = running();
  const comment = { id: 77 };
  const accepted = renderLogEntry({
    change: 'add-club', n: 6, ...commandEntry({ comment, accepted: true, message: '@alice answered: Fail closed.', after: state, context }),
  });
  assert.match(accepted, /💬 #6 · Decision recorded\n\n@alice answered: Fail closed\./);
  const rejected = renderLogEntry({
    change: 'add-club', n: 7, ...commandEntry({ comment, accepted: false, message: 'Needs write access.', after: state, context }),
  });
  assert.match(rejected, /🚫 #7 · Command not accepted/);
  assert.match(rejected, /issuecomment-77/);
});

test('renders the overview with a call to action for open gates', () => {
  const asked = creditSession(running(), {
    agentState: 'waiting_for_user', headSha: sha('c'),
  }, now).state;
  const notified = markGateNotified(asked, { log: 1234, now });
  const overview = renderOverview({ state: notified, tasks, context });
  assert.match(overview, /^## 🧭 OpenSpec · `add-club`/);
  assert.match(overview, /> \[!IMPORTANT\]\n> \*\*Decision needed\.\*\* \[See details\]\(.*issuecomment-1234\)/);
  assert.match(overview, /\| ⏸️ \| Apply \| 1 \/ 2 tasks · Decision needed · Apply 2\.1 \|/);
  assert.match(overview, /- \[ \] 2\.1 Implement Club commands · `implementation` · ❓ decision needed/);
  assert.match(overview, /<!-- openspec:overview change=add-club -->/);
  assert.doesNotMatch(renderOverview({ state: notified, tasks, context, includeMarker: false }), /openspec:overview/);
});

test('describes next steps for every state', () => {
  const state = running();
  assert.match(describeNext(state), /Wait for the agent session/);
  const merged = finalizeMerged(state, { now });
  assert.match(describeNext(merged), /Processing has ended/);
  const aborted = applyCommand(state, { name: 'abort', by: 'a', commentId: 1, now }).state;
  assert.match(renderLogEntry({ change: 'add-club', n: 8, ...closedEntry({ state: aborted, context }) }), /🛑 #8 · Aborted/);
  assert.match(renderLogEntry({ change: 'add-club', n: 9, ...closedEntry({ state: merged, context, archivePath: 'openspec/changes/archive/2026-10-04-add-club' }) }), /🎉 #9 · Merged/);
});

test('builds per-operation check runs from session results', () => {
  const before = running();
  const credited = operationCheckRun({ before, result: { kind: 'credited' }, context });
  assert.equal(credited.name, 'OpenSpec apply 2.1');
  assert.equal(credited.conclusion, 'success');
  const retry = operationCheckRun({ before, result: { kind: 'retry', reason: 'Timed out.', nextAttempt: 2 }, context });
  assert.equal(retry.conclusion, 'failure');
  assert.match(retry.summary, /Timed out/);
  const decision = operationCheckRun({ before, result: { kind: 'gate', gate: 'decision' }, context });
  assert.equal(decision.conclusion, 'neutral');
});
