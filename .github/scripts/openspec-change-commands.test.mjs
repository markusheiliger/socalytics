import assert from 'node:assert/strict';
import test from 'node:test';

import {
  authorizeCommand,
  isBotUser,
  parseCommand,
  classifyEvent,
  pendingCommandComments,
} from './openspec-change-commands.mjs';

test('parses the four slash commands', () => {
  assert.deepEqual(parseCommand('/openspec approve'), { name: 'approve', text: null });
  assert.deepEqual(parseCommand('  /openspec Retry\n'), { name: 'retry', text: null });
  assert.deepEqual(parseCommand('/openspec retry\nPoll /health with a 5 s request timeout.'), { name: 'retry', text: 'Poll /health with a 5 s request timeout.' });
  assert.deepEqual(parseCommand('/openspec abort please'), { name: 'abort', text: null });
  assert.deepEqual(
    parseCommand('/openspec answer Fail closed.\r\nDeny ambiguous Season scope.'),
    { name: 'answer', text: 'Fail closed.\nDeny ambiguous Season scope.' },
  );
  assert.deepEqual(parseCommand('/openspec answer\nMultiline answer'), { name: 'answer', text: 'Multiline answer' });
});

test('rejects unknown, empty, and oversized commands and ignores ordinary comments', () => {
  assert.equal(parseCommand('Looks good'), null);
  assert.equal(parseCommand('please /openspec approve'), null);
  assert.match(parseCommand('/openspec').error, /Unknown command/);
  assert.match(parseCommand('/openspec merge').error, /Unknown command/);
  assert.match(parseCommand('/openspecapprove').error, /Unrecognized command/);
  assert.match(parseCommand('/openspec answer   ').error, /needs your answer/);
  assert.match(parseCommand(`/openspec answer ${'x'.repeat(2001)}`).error, /at most 2000/);
  assert.match(parseCommand(`/openspec retry ${'x'.repeat(2001)}`).error, /Retry guidance must be at most 2000/);
});

test('selects unprocessed human command comments in order', () => {
  const comments = [
    { id: 30, body: '/openspec approve', user: { login: 'bob', type: 'User' } },
    { id: 10, body: '/openspec retry', user: { login: 'alice', type: 'User' } },
    { id: 20, body: 'chat', user: { login: 'alice', type: 'User' } },
    { id: 40, body: '/openspec abort', user: { login: 'github-actions[bot]', type: 'Bot' } },
    { id: 5, body: '/openspec abort', user: { login: 'alice', type: 'User' } },
  ];
  assert.deepEqual(pendingCommandComments(comments, 5).map(({ id }) => id), [10, 30]);
  assert.equal(isBotUser({ login: 'Copilot', type: 'Bot' }), true);
});

test('authorizes only unedited commands from users with write access', () => {
  const comment = {
    user: { login: 'alice' },
    created_at: '2026-10-02T10:00:00Z',
    updated_at: '2026-10-02T10:00:00Z',
  };
  assert.deepEqual(authorizeCommand(comment, 'write'), { ok: true });
  assert.deepEqual(authorizeCommand(comment, 'maintain'), { ok: true });
  assert.match(authorizeCommand(comment, 'triage').reason, /needs write access/);
  assert.match(authorizeCommand({ ...comment, updated_at: '2026-10-02T10:01:00Z' }, 'admin').reason, /edited/);
});

test('classifies only relevant events', () => {
  const twin = { number: 4, labels: [{ name: 'openspec:change' }] };
  assert.equal(classifyEvent('issues', { action: 'labeled', issue: twin }).relevant, true);
  assert.equal(classifyEvent('issues', { action: 'labeled', issue: { number: 1, labels: [] } }).relevant, false);
  assert.equal(classifyEvent('pull_request_target', {
    action: 'synchronize',
    pull_request: { number: 21, head: { ref: 'openspec/add-x' } },
  }).relevant, true);
  assert.equal(classifyEvent('pull_request_target', {
    action: 'synchronize',
    pull_request: { number: 22, head: { ref: 'feature/x' } },
  }).relevant, false);
  assert.equal(classifyEvent('issue_comment', {
    issue: { number: 21, pull_request: {} },
    comment: { body: '/openspec approve' },
  }).relevant, true);
  assert.equal(classifyEvent('issue_comment', {
    issue: { number: 4 },
    comment: { body: '/openspec approve' },
  }).relevant, false);
  assert.equal(classifyEvent('issue_comment', {
    issue: { number: 21, pull_request: {} },
    comment: { body: 'nice' },
  }).relevant, false);
  for (const name of ['push', 'schedule', 'workflow_dispatch']) {
    assert.equal(classifyEvent(name, {}).relevant, true);
  }
  assert.equal(classifyEvent('workflow_run', {
    action: 'completed',
    workflow_run: { id: 42, name: 'OpenSpec agent', conclusion: 'failure' },
  }).relevant, true);
  assert.equal(classifyEvent('workflow_run', {
    action: 'completed',
    workflow_run: { id: 43, name: 'Other workflow', conclusion: 'success' },
  }).relevant, false);
  assert.equal(classifyEvent('workflow_run', {
    action: 'requested',
    workflow_run: { id: 44, name: 'OpenSpec agent', conclusion: null },
  }).relevant, false);
  assert.equal(classifyEvent('release', {}).relevant, false);
});
