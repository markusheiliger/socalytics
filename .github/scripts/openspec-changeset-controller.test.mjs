import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';

import { hashChangeset, renderChangesetIssue } from './openspec-changeset-core.mjs';
import {
  BLOCKER_END,
  BLOCKER_START,
  formatLedger,
  parseBlockerIssue,
  reconcileChangeset,
} from './openspec-changeset-controller.mjs';

const changeset = {
  version: 1,
  name: 'pilot',
  changes: [
    { ref: 'add-first', dependsOn: [] },
    { ref: 'add-second', dependsOn: ['add-first'] },
  ],
};

function repositoryRoot({ archived = [] } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'changeset-'));
  for (const ref of changeset.changes.map((change) => change.ref)) {
    const directory = join(root, 'openspec', 'changes', ref);
    mkdirSync(directory, { recursive: true });
    writeFileSync(join(directory, '.openspec.yaml'), 'schema: spec-driven\n');
  }
  for (const ref of archived) {
    mkdirSync(join(root, 'openspec', 'changes', 'archive', `2026-09-23-${ref}`), { recursive: true });
  }
  return root;
}

class FakeClient {
  constructor({ comments = [], pulls = [], subIssues = [], comparison = { status: 'ahead' }, agentTasks = {}, dispatchError } = {}) {
    this.issue = {
      number: 42,
      body: renderChangesetIssue(changeset),
      labels: [{ name: 'openspec:changeset' }, { name: 'changeset:ready' }],
    };
    this.comments = comments;
    this.pulls = pulls;
    this.subIssues = subIssues;
    this.comparison = comparison;
    this.agentTasks = agentTasks;
    this.dispatchError = dispatchError;
    this.calls = [];
  }

  async getIssue() { return this.issue; }
  async listIssueComments() { return this.comments; }
  async listPullRequests() { return this.pulls; }
  async listSubIssues() { return this.subIssues; }
  async getAgentTask(taskId) {
    this.calls.push(['get-task', taskId]);
    return this.agentTasks[taskId] ?? { id: taskId, state: 'in_progress' };
  }
  async compareCommits(base, head) {
    this.calls.push(['compare', base, head]);
    return this.comparison;
  }
  async setIssueLabels(number, labels) { this.calls.push(['labels', number, labels]); }
  async updateIssue(number, body) { this.calls.push(['issue', number, body]); }
  async createIssueComment(number, body) {
    const comment = { id: this.comments.length + 1, body };
    this.comments.push(comment);
    this.calls.push(['create-comment', number, body]);
    return comment;
  }
  async updateIssueComment(id, body) {
    const comment = this.comments.find((candidate) => candidate.id === id);
    if (comment) comment.body = body;
    this.calls.push(['update-comment', id, body]);
  }
  async createAgentTask(task) {
    this.calls.push(['dispatch', task]);
    if (this.dispatchError) throw this.dispatchError;
    return { id: `task-${this.calls.filter(([name]) => name === 'dispatch').length}` };
  }
  async enablePullRequestAutoMerge(nodeId) { this.calls.push(['auto-merge', nodeId]); }
}

const taskTemplate = 'Handle {{CHANGE_REF}} for #{{CHANGESET_ISSUE}} from {{DEFAULT_BRANCH}}.\n{{PR_MARKER}}';
const resumeTemplate = 'Resume {{CHANGE_REF}} on {{HEAD_REF}} from {{CHECKPOINT_SHA}}.\n{{PR_MARKER}}';

function blockerIssue(overrides = {}) {
  const marker = {
    version: 1,
    parentIssue: 42,
    change: 'add-first',
    baseRef: 'main',
    headRef: 'copilot/add-first',
    checkpointSha: 'abc123',
    taskId: 'task-1',
    ...overrides,
  };
  return {
    number: 84,
    state: 'open',
    labels: [{ name: 'openspec:change-blocker' }],
    body: `${BLOCKER_START}\n${JSON.stringify(marker)}\n${BLOCKER_END}`,
  };
}

test('parses blocker issue markers', () => {
  const parsed = parseBlockerIssue(blockerIssue());
  assert.equal(parsed.change, 'add-first');
  assert.equal(parsed.issue.number, 84);
});

test('resume dispatches on the preserved branch after checkpoint validation', async () => {
  const client = new FakeClient({ subIssues: [blockerIssue()] });
  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot(),
    mode: 'resume',
    resumeTemplate,
  });

  assert.equal(result.resumed, 'add-first');
  assert.deepEqual(client.calls.find(([name]) => name === 'compare'), ['compare', 'abc123', 'copilot/add-first']);
  assert.deepEqual(client.calls.find(([name]) => name === 'dispatch')[1], {
    prompt: 'Resume add-first on copilot/add-first from abc123.\n<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-first"} -->',
    baseRef: 'main',
    headRef: 'copilot/add-first',
  });
  assert.match(client.comments.at(-1).body, /"blockerIssue": 84/);
});

test('resume rejects a checkpoint that is not in the preserved branch history', async () => {
  const client = new FakeClient({
    subIssues: [blockerIssue()],
    comparison: { status: 'diverged' },
  });

  await assert.rejects(
    reconcileChangeset({
      client,
      issueNumber: 42,
      root: repositoryRoot(),
      mode: 'resume',
      resumeTemplate,
    }),
    /not reachable/,
  );
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});

test('resume rejects a concurrent active task', async () => {
  const client = new FakeClient({
    subIssues: [blockerIssue()],
    comments: [{
      id: 1,
      body: formatLedger({
        version: 1,
        change: 'add-first',
        status: 'dispatched',
        graphHash: hashChangeset(changeset),
        attempt: 1,
        taskId: 'task-1',
        createdAt: new Date().toISOString(),
      }),
    }],
  });

  await assert.rejects(
    reconcileChangeset({
      client,
      issueNumber: 42,
      root: repositoryRoot(),
      mode: 'resume',
      resumeTemplate,
    }),
    /active task/,
  );
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});

test('duplicate blockers request attention without selecting one', async () => {
  const client = new FakeClient({
    subIssues: [blockerIssue(), { ...blockerIssue(), number: 85 }],
  });

  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot(),
    taskTemplate,
  });

  assert.equal(result.state, 'attention');
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});

test('open blocker requests attention without waiting for dispatch expiry', async () => {
  const client = new FakeClient({ subIssues: [blockerIssue()] });
  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot(),
    taskTemplate,
  });

  assert.equal(result.state, 'attention');
  assert.deepEqual(result.attention, ['add-first']);
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});

test('completed final pull request closes its blocker issue', async () => {
  const client = new FakeClient({
    subIssues: [blockerIssue()],
    pulls: [{
      number: 7,
      node_id: 'PR_node_7',
      state: 'open',
      merged_at: null,
      auto_merge: null,
      updated_at: '2026-09-23T10:00:00Z',
      body: '<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-first"} -->\n<!-- openspec-changeset-auto-merge:v1 -->',
    }],
  });

  await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), taskTemplate });

  assert.deepEqual(
    client.calls.find(([name, number]) => name === 'issue' && number === 84),
    ['issue', 84, { state: 'closed', state_reason: 'completed' }],
  );
});

test('legacy partial pull request does not close its blocker issue', async () => {
  const client = new FakeClient({
    subIssues: [blockerIssue()],
    pulls: [{
      number: 7,
      node_id: 'PR_node_7',
      state: 'open',
      merged_at: null,
      auto_merge: null,
      updated_at: '2026-09-23T10:00:00Z',
      body: '<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-first"} -->',
    }],
  });

  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), taskTemplate });

  assert.equal(result.state, 'attention');
  assert.equal(client.calls.some(([name, number]) => name === 'issue' && number === 84), false);
});

test('dry-run reports the initial frontier without mutating GitHub', async () => {
  const client = new FakeClient();
  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), mode: 'dry-run' });
  assert.deepEqual(result.frontier, ['add-first']);
  assert.deepEqual(client.calls, []);
});

test('reserves before dispatch and records the task', async () => {
  const client = new FakeClient();
  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), taskTemplate });
  assert.equal(result.state, 'running');
  const names = client.calls.map(([name]) => name);
  assert.ok(names.indexOf('create-comment') < names.indexOf('dispatch'));
  assert.match(client.comments[0].body, /"status": "dispatched"/);
  assert.match(client.comments[0].body, /"taskId": "task-1"/);
});

test('does not duplicate an active dispatch', async () => {
  const now = new Date('2026-09-23T12:00:00Z');
  const client = new FakeClient({
    comments: [{
      id: 1,
      body: formatLedger({
        version: 1,
        change: 'add-first',
        status: 'dispatched',
        graphHash: hashChangeset(changeset),
        attempt: 1,
        createdAt: '2026-09-23T11:30:00Z',
      }),
    }],
  });
  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), now, taskTemplate });
  assert.equal(result.state, 'running');
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});

test('retry dispatches after a failed attempt with an incremented attempt', async () => {
  const client = new FakeClient({
    comments: [{
      id: 1,
      body: formatLedger({
        version: 1,
        change: 'add-first',
        status: 'failed',
        graphHash: hashChangeset(changeset),
        attempt: 1,
        createdAt: '2026-09-23T11:30:00Z',
      }),
    }],
  });
  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot(),
    mode: 'retry',
    taskTemplate,
  });
  assert.equal(result.state, 'running');
  assert.equal(client.calls.filter(([name]) => name === 'dispatch').length, 1);
  const dispatched = client.comments.find((comment) => comment.body.includes('"status": "dispatched"'));
  assert.match(dispatched.body, /"attempt": 2/);
});

test('records dispatch failures and requests attention', async () => {
  const client = new FakeClient({ dispatchError: new Error('agent unavailable') });
  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), taskTemplate });
  assert.equal(result.state, 'attention');
  assert.match(client.comments[0].body, /"status": "failed"/);
  assert.match(client.comments[0].body, /agent unavailable/);
});

test('enables auto-merge for a completed open pull request', async () => {
  const client = new FakeClient({
    pulls: [{
      number: 7,
      node_id: 'PR_node_7',
      state: 'open',
      merged_at: null,
      auto_merge: null,
      updated_at: '2026-09-23T10:00:00Z',
      body: '<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-first"} -->\n<!-- openspec-changeset-auto-merge:v1 -->',
    }],
  });
  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), taskTemplate });
  assert.equal(result.state, 'running');
  assert.deepEqual(client.calls.find(([name]) => name === 'auto-merge'), ['auto-merge', 'PR_node_7']);
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});

test('does not re-enable auto-merge when GitHub already has it pending', async () => {
  const client = new FakeClient({
    pulls: [{
      number: 7,
      node_id: 'PR_node_7',
      state: 'open',
      merged_at: null,
      auto_merge: { merge_method: 'squash' },
      updated_at: '2026-09-23T10:00:00Z',
      body: '<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-first"} -->\n<!-- openspec-changeset-auto-merge:v1 -->',
    }],
  });
  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), taskTemplate });
  assert.equal(result.state, 'running');
  assert.equal(client.calls.some(([name]) => name === 'auto-merge'), false);
});

test('requests attention for an open partial pull request', async () => {
  const client = new FakeClient({
    pulls: [{
      number: 7,
      node_id: 'PR_node_7',
      state: 'open',
      merged_at: null,
      auto_merge: null,
      updated_at: '2026-09-23T10:00:00Z',
      body: '<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-first"} -->',
    }],
  });
  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), taskTemplate });
  assert.equal(result.state, 'attention');
  assert.equal(client.calls.some(([name]) => name === 'auto-merge'), false);
});

test('retry does not duplicate work for an open partial pull request', async () => {
  const client = new FakeClient({
    pulls: [{
      number: 7,
      node_id: 'PR_node_7',
      state: 'open',
      merged_at: null,
      auto_merge: null,
      updated_at: '2026-09-23T10:00:00Z',
      body: '<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-first"} -->',
    }],
  });
  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot(),
    mode: 'retry',
    taskTemplate,
  });
  assert.equal(result.state, 'attention');
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});

test('merged PR without archive does not release dependents', async () => {
  const client = new FakeClient({
    pulls: [{
      number: 7,
      state: 'closed',
      merged_at: '2026-09-23T10:00:00Z',
      updated_at: '2026-09-23T10:00:00Z',
      body: '<!-- openspec-changeset-pr:v1 {"changeset":42,"change":"add-first"} -->',
    }],
  });
  const result = await reconcileChangeset({ client, issueNumber: 42, root: repositoryRoot(), taskTemplate });
  assert.equal(result.state, 'attention');
  assert.deepEqual(result.frontier, []);
});

test('archived prerequisite releases its dependent', async () => {
  const client = new FakeClient();
  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot({ archived: ['add-first'] }),
    mode: 'dry-run',
  });
  assert.deepEqual(result.frontier, ['add-second']);
});

test('all archived changes close the issue', async () => {
  const client = new FakeClient();
  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot({ archived: ['add-first', 'add-second'] }),
    taskTemplate,
  });
  assert.equal(result.state, 'complete');
  assert.ok(client.calls.some(([name, , body]) => name === 'issue' && body.state === 'closed'));
});

test('requests attention when a task completes without a blocker or final pull request', async () => {
  const client = new FakeClient({
    comments: [{
      id: 1,
      body: formatLedger({
        version: 1,
        change: 'add-first',
        status: 'dispatched',
        graphHash: hashChangeset(changeset),
        attempt: 1,
        taskId: 'task-1',
        createdAt: '2026-09-23T11:30:00Z',
      }),
    }],
    agentTasks: { 'task-1': { id: 'task-1', state: 'completed' } },
  });
  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot(),
    now: new Date('2026-09-23T12:00:00Z'),
    taskTemplate,
  });

  assert.equal(result.state, 'attention');
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});

test('retry does not duplicate an in-progress task', async () => {
  const client = new FakeClient({
    comments: [{
      id: 1,
      body: formatLedger({
        version: 1,
        change: 'add-first',
        status: 'dispatched',
        graphHash: hashChangeset(changeset),
        attempt: 1,
        taskId: 'task-1',
        createdAt: '2026-09-23T11:30:00Z',
      }),
    }],
  });
  const result = await reconcileChangeset({
    client,
    issueNumber: 42,
    root: repositoryRoot(),
    mode: 'retry',
    now: new Date('2026-09-23T12:00:00Z'),
    taskTemplate,
  });

  assert.equal(result.state, 'running');
  assert.equal(client.calls.some(([name]) => name === 'dispatch'), false);
});