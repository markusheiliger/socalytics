import assert from 'node:assert/strict';
import test from 'node:test';

import { GitHubClient, normalizeAgentTask, taskBranchArtifact } from './openspec-changeset-github.mjs';

function response(payload) {
  return {
    ok: true,
    text: async () => JSON.stringify(payload),
  };
}

test('starts branch-only agent tasks by default', async () => {
  let request;
  const client = new GitHubClient({
    repository: 'owner/repository',
    token: 'github-token',
    agentToken: 'agent-token',
    fetchImpl: async (url, options) => {
      request = { url, options };
      return response({ id: 'task-1' });
    },
  });

  await client.createAgentTask({ prompt: 'Apply the change', baseRef: 'main' });

  assert.equal(request.url, 'https://api.github.com/agents/repos/owner/repository/tasks');
  assert.equal(request.options.headers.Authorization, 'Bearer agent-token');
  assert.deepEqual(JSON.parse(request.options.body), {
    prompt: 'Apply the change',
    base_ref: 'main',
    create_pull_request: false,
  });
});

test('starts agent tasks on an existing branch', async () => {
  let request;
  const client = new GitHubClient({
    repository: 'owner/repository',
    token: 'github-token',
    agentToken: 'agent-token',
    fetchImpl: async (url, options) => {
      request = { url, options };
      return response({ id: 'task-2' });
    },
  });

  await client.createAgentTask({
    prompt: 'Recover the change',
    baseRef: 'main',
    headRef: 'copilot/add-first',
    createPullRequest: true,
  });

  assert.deepEqual(JSON.parse(request.options.body), {
    prompt: 'Recover the change',
    base_ref: 'main',
    head_ref: 'copilot/add-first',
    create_pull_request: true,
  });
});

test('gets agent tasks with the agent token', async () => {
  let request;
  const client = new GitHubClient({
    repository: 'owner/repository',
    token: 'github-token',
    agentToken: 'agent-token',
    fetchImpl: async (url, options) => {
      request = { url, options };
      return response({ id: 'task/1' });
    },
  });

  await client.getAgentTask('task/1');

  assert.equal(request.url, 'https://api.github.com/agents/repos/owner/repository/tasks/task%2F1');
  assert.equal(request.options.headers.Authorization, 'Bearer agent-token');
});

test('extracts GitHub branch artifacts from agent tasks', () => {
  const branch = taskBranchArtifact({
    artifacts: [
      { provider: 'github', type: 'pull', data: { id: 7 } },
      { provider: 'github', type: 'branch', data: { head_ref: 'copilot/add-first', base_ref: 'main' } },
    ],
  });

  assert.deepEqual(branch, { headRef: 'copilot/add-first', baseRef: 'main' });
  assert.equal(taskBranchArtifact({ artifacts: [] }), null);
});

test('normalizes nested agent task identity and state', () => {
  assert.deepEqual(normalizeAgentTask({
    task: { id: 'task-1', state: 'in_progress' },
    sessions: [{
      id: 'session-1',
      state: 'waiting_for_user',
      artifacts: [{
        provider: 'github',
        type: 'branch',
        data: { head_ref: 'copilot/add-first', base_ref: 'main' },
      }],
    }],
  }), {
    id: 'task-1',
    state: 'waiting_for_user',
    taskState: 'in_progress',
    sessionId: 'session-1',
    sessionState: 'waiting_for_user',
    branch: { headRef: 'copilot/add-first', baseRef: 'main' },
  });
});

test('uses the latest agent session as the effective state', () => {
  assert.deepEqual(normalizeAgentTask({
    id: 'task-1',
    state: 'in_progress',
    sessions: [
      { id: 'session-1', state: 'waiting_for_user' },
      { id: 'session-2', state: 'completed' },
    ],
  }), {
    id: 'task-1',
    state: 'completed',
    taskState: 'in_progress',
    sessionId: 'session-2',
    sessionState: 'completed',
    branch: null,
  });
});

test('preserves a terminal top-level task state over a stale session', () => {
  assert.deepEqual(normalizeAgentTask({
    id: 'task-1',
    state: 'failed',
    sessions: [{ id: 'session-1', state: 'in_progress' }],
  }), {
    id: 'task-1',
    state: 'failed',
    taskState: 'failed',
    sessionId: 'session-1',
    sessionState: 'in_progress',
    branch: null,
  });
});

test('preserves top-level idle over a stale active session', () => {
  const normalized = normalizeAgentTask({
    id: 'task-1',
    state: 'idle',
    sessions: [{ id: 'session-1', state: 'in_progress' }],
  });

  assert.equal(normalized.state, 'idle');
});

test('normalizes the one-element task envelope returned by the API', () => {
  const normalized = normalizeAgentTask([{
    id: 'task-1',
    state: 'in_progress',
    sessions: [{ id: 'session-1', state: 'waiting_for_user' }],
  }]);

  assert.equal(normalized.id, 'task-1');
  assert.equal(normalized.state, 'waiting_for_user');
  assert.equal(normalized.sessionId, 'session-1');
});

test('lists child blocker issues', async () => {
  let request;
  const client = new GitHubClient({
    repository: 'owner/repository',
    token: 'github-token',
    fetchImpl: async (url, options) => {
      request = { url, options };
      return response([]);
    },
  });

  await client.listSubIssues(42);

  assert.equal(request.url, 'https://api.github.com/repos/owner/repository/issues/42/sub_issues?per_page=100');
  assert.equal(request.options.headers.Authorization, 'Bearer github-token');
});

test('reads branches and compares checkpoint ancestry', async () => {
  const requests = [];
  const client = new GitHubClient({
    repository: 'owner/repository',
    token: 'github-token',
    fetchImpl: async (url, options) => {
      requests.push({ url, options });
      return response({});
    },
  });

  await client.getBranch('copilot/add-first');
  await client.compareCommits('abc123', 'copilot/add-first');

  assert.equal(requests[0].url, 'https://api.github.com/repos/owner/repository/branches/copilot%2Fadd-first');
  assert.equal(requests[1].url, 'https://api.github.com/repos/owner/repository/compare/abc123...copilot%2Fadd-first');
});

test('enables squash auto-merge through the GitHub GraphQL API', async () => {
  let request;
  const client = new GitHubClient({
    repository: 'owner/repository',
    token: 'github-token',
    fetchImpl: async (url, options) => {
      request = { url, options };
      return response({
        data: { enablePullRequestAutoMerge: { pullRequest: { number: 7 } } },
      });
    },
  });

  const pullRequest = await client.enablePullRequestAutoMerge('PR_node_7');
  const body = JSON.parse(request.options.body);
  assert.equal(request.url, 'https://api.github.com/graphql');
  assert.equal(request.options.headers.Authorization, 'Bearer github-token');
  assert.equal(body.variables.pullRequestId, 'PR_node_7');
  assert.match(body.query, /mergeMethod: SQUASH/);
  assert.deepEqual(pullRequest, { number: 7 });
});

test('reports GraphQL auto-merge errors', async () => {
  const client = new GitHubClient({
    repository: 'owner/repository',
    token: 'github-token',
    fetchImpl: async () => response({ errors: [{ message: 'Auto-merge is disabled' }] }),
  });

  await assert.rejects(
    client.enablePullRequestAutoMerge('PR_node_7'),
    /Auto-merge is disabled/,
  );
});
