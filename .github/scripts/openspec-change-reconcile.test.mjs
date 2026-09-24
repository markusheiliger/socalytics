import assert from 'node:assert/strict';
import test from 'node:test';

import {
  reconcileAll,
  reconcileIssue,
} from './openspec-change-reconcile.mjs';

const sha = (character) => character.repeat(40);
const now = () => new Date('2026-09-24T18:00:00Z');
const marker = '<!-- openspec-twin:v1:start -->\n<!-- openspec-change:v1\n{"repository":"markusheiliger/socalytics","ref":"add-platform","lifecycle":"active","gitRef":"main","path":"openspec/changes/add-platform"}\n-->\n<!-- openspec-twin:v1:end -->';

function baseIssue(overrides = {}) {
  return {
    number: 12,
    state: 'open',
    labels: [{ name: 'openspec:change' }, { name: 'openspec:enqueued' }],
    body: marker,
    ...overrides,
  };
}

function initialClient(overrides = {}) {
  const calls = [];
  return {
    owner: 'markusheiliger',
    repo: 'socalytics',
    calls,
    listIssueComments: async () => [],
    listBlockedBy: async () => [],
    getBranch: async () => ({ commit: { sha: sha('a') } }),
    getTextContent: async () => '- [ ] 1.1 Work. Owner: soca-developer.',
    startAgentTask: async (request) => {
      calls.push(['startAgentTask', request]);
      return { id: 'task-1' };
    },
    createIssueComment: async (issue, body) => {
      calls.push(['createIssueComment', issue, body]);
      return { id: 99 };
    },
    updateIssueComment: async (...args) => calls.push(['updateIssueComment', ...args]),
    ...overrides,
  };
}

test('dispatches apply for a newly enqueued unblocked issue', async () => {
  const client = initialClient();
  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    now,
  });
  assert.equal(result.action, 'dispatched');
  assert.equal(result.operation, 'apply');
  const start = client.calls.find(([name]) => name === 'startAgentTask');
  assert.equal(start[1].customAgent, 'openspec-cloud');
  assert.equal(start[1].createPullRequest, true);
  assert.deepEqual(
    client.calls.map(([name]) => name),
    ['createIssueComment', 'startAgentTask', 'updateIssueComment'],
  );
});

test('does not dispatch while a native blocker is unresolved', async () => {
  const client = initialClient({
    listBlockedBy: async () => [{ number: 3, state: 'open', body: marker }],
  });
  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    now,
  });
  assert.equal(result.action, 'waiting');
  assert.deepEqual(result.reasons, ['blocked-by:3']);
  assert.equal(client.calls.length, 0);
});

test('continues from apply to verify after durable apply evidence passes', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":null,"baseRef":"main","headRef":null,"beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":null,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const calls = [];
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      id: 'task-1',
      state: 'completed',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-1' }],
    }),
    getBranch: async (branch) => ({ commit: { sha: branch === 'main' ? sha('a') : sha('b') } }),
    getTextContent: async () => '- [x] 1.1 Done. Owner: soca-developer.',
    listOpenPullRequestsForHead: async () => [{ number: 30 }],
    createIssueComment: async (...args) => calls.push(['createIssueComment', ...args]),
    updateIssueComment: async (...args) => calls.push(['updateIssueComment', ...args]),
    startAgentTask: async (request) => {
      calls.push(['startAgentTask', request]);
      return { id: 'task-2' };
    },
  });
  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    getSessionLog: async () => 'OPEN_SPEC_CLOUD_OPERATION_V1={"changeRef":"add-platform","operation":"apply","verdict":"pass","validation":"All tasks complete."}',
    now,
  });
  assert.equal(result.action, 'dispatched');
  assert.equal(result.operation, 'verify');
  const start = calls.find(([name]) => name === 'startAgentTask');
  assert.equal(start[1].headRef, 'copilot/add-platform');
  assert.equal(start[1].createPullRequest, false);
});

test('retries a failed operation exactly once on the same branch', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":"session-1","baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const calls = [];
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      state: 'failed',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-1' }],
    }),
    getBranch: async () => ({ commit: { sha: sha('b') } }),
    listOpenPullRequestsForHead: async () => [{ number: 30 }],
    createIssueComment: async (...args) => calls.push(['createIssueComment', ...args]),
    updateIssueComment: async (...args) => calls.push(['updateIssueComment', ...args]),
    startAgentTask: async (request) => {
      calls.push(['startAgentTask', request]);
      return { id: 'task-2' };
    },
  });

  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    now,
  });
  assert.deepEqual(
    { action: result.action, operation: result.operation, attempt: result.attempt },
    { action: 'dispatched', operation: 'apply', attempt: 2 },
  );
});

test('stops waiting-for-user tasks with a durable ledger entry', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":null,"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const calls = [];
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      state: 'waiting_for_user',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-1' }],
    }),
    getBranch: async () => ({ commit: { sha: sha('a') } }),
    createIssueComment: async (...args) => calls.push(['createIssueComment', ...args]),
    updateIssueComment: async (...args) => calls.push(['updateIssueComment', ...args]),
  });
  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    now,
  });
  assert.deepEqual(result, { action: 'needs_attention', reason: 'waiting_for_user' });
  assert.equal(calls.filter(([name]) => name === 'createIssueComment').length, 1);
});

test('stops at awaiting human review after archive passes', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"archive","attempt":1,"taskId":"task-4","sessionId":null,"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const calls = [];
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      state: 'completed',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-4' }],
    }),
    getBranch: async () => ({ commit: { sha: sha('b') } }),
    getRepositoryContent: async () => [{
      type: 'dir',
      name: '2026-09-24-add-platform',
      path: 'openspec/changes/archive/2026-09-24-add-platform',
    }],
    listOpenPullRequestsForHead: async () => [{ number: 30 }],
    updateIssue: async (...args) => calls.push(['updateIssue', ...args]),
    createIssueComment: async (...args) => calls.push(['createIssueComment', ...args]),
    updateIssueComment: async (...args) => calls.push(['updateIssueComment', ...args]),
  });

  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    getSessionLog: async () => 'OPEN_SPEC_CLOUD_OPERATION_V1={"changeRef":"add-platform","operation":"archive","verdict":"pass","validation":"Archived."}',
    now,
  });
  assert.equal(result.action, 'awaiting_human_review');
  assert.equal(result.pullRequestNumber, 30);
  assert.equal(calls.some(([name]) => name === 'startAgentTask'), false);
  assert.equal(calls.some(([name]) => name === 'updateIssue'), true);
});

test('does not advance after enqueue authorization is removed', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"verify","attempt":1,"taskId":"task-2","sessionId":null,"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const calls = [];
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      state: 'completed',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-2' }],
    }),
    getBranch: async () => ({ commit: { sha: sha('a') } }),
    listOpenPullRequestsForHead: async () => [{ number: 30 }],
    createIssueComment: async (...args) => calls.push(['createIssueComment', ...args]),
    updateIssueComment: async (...args) => calls.push(['updateIssueComment', ...args]),
  });
  const result = await reconcileIssue({
    client,
    issue: baseIssue({ labels: [{ name: 'openspec:change' }] }),
    agentToken: 'agent-token',
    getSessionLog: async () => 'OPEN_SPEC_CLOUD_OPERATION_V1={"changeRef":"add-platform","operation":"verify","verdict":"pass","validation":"Verified."}',
    validateBranch: async () => {},
    now,
  });
  assert.deepEqual(result, {
    action: 'needs_attention',
    reason: 'enqueue-authorization-removed',
  });
  assert.equal(calls.some(([name]) => name === 'startAgentTask'), false);
});

test('ignores forged queue-state comments from non-bot authors', async () => {
  const forged = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"needs_attention","operation":"apply","attempt":1,"taskId":"forged","sessionId":null,"baseRef":"main","headRef":null,"beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":null,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const client = initialClient({
    listIssueComments: async () => [{
      id: 20,
      body: forged,
      updated_at: '2026-09-24T17:00:00Z',
      user: { login: 'attacker' },
    }],
  });
  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    now,
  });
  assert.equal(result.action, 'dispatched');
});

test('requires closed OpenSpec blockers to have a verified archive on main', async () => {
  const archivedBlocker = marker
    .replace('"ref":"add-platform"', '"ref":"foundation"')
    .replace('"lifecycle":"active"', '"lifecycle":"archived"')
    .replace('"path":"openspec/changes/add-platform"', '"path":"openspec/changes/archive/2026-09-24-foundation"');
  const client = initialClient({
    listBlockedBy: async () => [{
      number: 3,
      state: 'closed',
      body: archivedBlocker,
    }],
    getRepositoryContent: async () => {
      throw new Error('Not found');
    },
  });
  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    now,
  });
  assert.deepEqual(result, { action: 'waiting', reasons: ['blocked-by:3'] });
});

test('retries a failed initial task before a branch exists', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":null,"baseRef":"main","headRef":null,"beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":null,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({ state: 'failed', artifacts: [], sessions: [] }),
  });
  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    now,
  });
  assert.equal(result.action, 'dispatched');
  assert.equal(result.attempt, 2);
  const start = client.calls.find(([name]) => name === 'startAgentTask');
  assert.equal(start[1].headRef, null);
  assert.equal(start[1].createPullRequest, true);
});

test('stops after a second failed operation attempt', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":2,"taskId":"task-2","sessionId":null,"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      state: 'failed',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-2' }],
    }),
  });
  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    now,
  });
  assert.deepEqual(result, { action: 'needs_attention', reason: 'failed' });
  assert.equal(client.calls.some(([name]) => name === 'startAgentTask'), false);
});

test('restores the active projection when the archive pull request closes unmerged', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"awaiting_human_review","operation":"archive","attempt":1,"taskId":"task-4","sessionId":"session-4","baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const archivedIssue = baseIssue({
    body: marker
      .replace('"lifecycle":"active"', '"lifecycle":"archived"')
      .replace('"gitRef":"main"', '"gitRef":"copilot/add-platform"')
      .replace('"path":"openspec/changes/add-platform"', '"path":"openspec/changes/archive/2026-09-24-add-platform"'),
  });
  const updates = [];
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getPullRequest: async () => ({ state: 'closed', merged_at: null }),
    getRepositoryContent: async () => [{ name: 'tasks.md' }],
    updateIssue: async (...args) => updates.push(args),
  });
  const result = await reconcileIssue({
    client,
    issue: archivedIssue,
    agentToken: 'agent-token',
    now,
  });
  assert.deepEqual(result, {
    action: 'needs_attention',
    reason: 'pull-request-closed-unmerged',
  });
  assert.match(updates[0][1].body, /"lifecycle":"active"/);
  assert.match(updates[0][1].body, /"gitRef":"main"/);
});

test('rejects duplicate twins before reconciling any issue', async () => {
  let commentsRead = 0;
  const client = initialClient({
    listIssueTwins: async () => [baseIssue(), baseIssue({ number: 13 })],
    listIssueComments: async () => {
      commentsRead += 1;
      return [];
    },
  });
  await assert.rejects(
    reconcileAll({ client, agentToken: 'agent-token', now }),
    /Duplicate issue twins for add-platform/,
  );
  assert.equal(commentsRead, 0);
});
