import assert from 'node:assert/strict';
import test from 'node:test';

import {
  QUEUE_LABEL_DEFINITIONS,
  reconcileAll,
  reconcileIssue,
  reconcileQueueLabels,
  reconcileUntilSettled,
} from './openspec-change-reconcile.mjs';

const sha = (character) => character.repeat(40);
const now = () => new Date('2026-09-24T18:00:00Z');
const marker = '<!-- openspec-twin:v1:start -->\n<!-- openspec-change:v1\n{"repository":"markusheiliger/socalytics","ref":"add-platform","lifecycle":"active","gitRef":"main","path":"openspec/changes/add-platform"}\n-->\n<!-- openspec-twin:v1:end -->';
const capability = (id, overrides = {}) => `---
id: ${id}
version: 1
operations: [apply]
composition: ${overrides.composition ?? 'composable'}
mutation: ${overrides.mutation ?? 'scoped'}
isolation: ${overrides.isolation ?? 'shared'}
resultSchema: schemas/capability-result-v1.schema.json
---

# ${id}`;
const repositoryContent = (path, tasks) => {
  if (!path.startsWith('openspec/capabilities/')) return tasks;
  const id = path.split('/').at(-1).replace('.md', '');
  return ['audit', 'verification'].includes(id)
    ? capability(id, {
      composition: 'exclusive',
      mutation: 'checkbox-only',
      isolation: 'required',
    })
    : capability(id);
};
const capabilityResult = ({
  taskId = '1.1',
  capabilities = ['implementation'],
  startingSha = sha('a'),
  resultingSha = sha('b'),
  artifactsChanged = ['openspec/changes/add-platform/tasks.md'],
} = {}) => `OPEN_SPEC_CAPABILITY_RESULT_V1=${JSON.stringify({
  version: 1,
  changeRef: 'add-platform',
  operation: 'apply',
  taskId,
  capabilities,
  verdict: 'pass',
  startingSha,
  resultingSha,
  artifactsChanged,
  validation: [{ command: 'node --test', outcome: 'passed' }],
  summary: `Task ${taskId} complete.`,
  blockingFindings: [],
})}`;

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
    compareCommits: async () => ({
      files: [{ filename: 'openspec/changes/add-platform/tasks.md' }],
    }),
    getTextContent: async (path) => repositoryContent(
      path,
      '- [ ] 1.1 Work. Capabilities: implementation.',
    ),
    startAgentTask: async (request) => {
      calls.push(['startAgentTask', request]);
      return { id: 'task-1' };
    },
    createIssueComment: async (issue, body) => {
      calls.push(['createIssueComment', issue, body]);
      return { id: 99 };
    },
    updateIssueComment: async (...args) => calls.push(['updateIssueComment', ...args]),
    addIssueLabel: async (...args) => calls.push(['addIssueLabel', ...args]),
    removeIssueLabel: async (...args) => calls.push(['removeIssueLabel', ...args]),
    ensureLabel: async (...args) => calls.push(['ensureLabel', ...args]),
    ...overrides,
  };
}

test('defines every visible processing, stage, attention, and review label', () => {
  assert.deepEqual(
    QUEUE_LABEL_DEFINITIONS.map(({ name }) => name),
    [
      'openspec:processing',
      'openspec:stage:apply',
      'openspec:stage:verify',
      'openspec:stage:sync',
      'openspec:stage:archive',
      'openspec:needs-attention',
      'openspec:awaiting-review',
    ],
  );
});

test('watches active Agent Tasks until the queue settles', async () => {
  const responses = [
    [{ action: 'dispatched', operation: 'apply' }],
    [{ action: 'waiting', reasons: ['agent-task-in_progress'] }],
    [{ action: 'awaiting_human_review', pullRequestNumber: 30 }],
  ];
  const waits = [];
  let reconcileCalls = 0;
  const result = await reconcileUntilSettled({
    reconcile: async () => {
      const response = responses[reconcileCalls];
      reconcileCalls += 1;
      return response;
    },
    wait: async (milliseconds) => waits.push(milliseconds),
    intervalMilliseconds: 25,
  });

  assert.deepEqual(result, responses.at(-1));
  assert.equal(reconcileCalls, 3);
  assert.deepEqual(waits, [25, 25]);
});

test('reconciles managed labels idempotently without replacing unrelated labels', async () => {
  const issue = baseIssue({
    labels: [
      { name: 'openspec:change' },
      { name: 'openspec:enqueued' },
      { name: 'team:platform' },
    ],
  });
  const client = initialClient();
  const desired = new Set(['openspec:processing', 'openspec:stage:apply']);

  await reconcileQueueLabels(client, issue, desired);
  await reconcileQueueLabels(client, issue, desired);

  assert.deepEqual(client.calls, [
    ['removeIssueLabel', 12, 'openspec:enqueued'],
    ['addIssueLabel', 12, 'openspec:processing'],
    ['addIssueLabel', 12, 'openspec:stage:apply'],
  ]);
  assert.deepEqual(labels(issue), [
    'openspec:change',
    'team:platform',
    'openspec:processing',
    'openspec:stage:apply',
  ]);
});

function labels(issue) {
  return issue.labels.map((label) => typeof label === 'string' ? label : label.name);
}

function dispatchEnvelope(prompt) {
  const prefix = 'OPEN_SPEC_CLOUD_DISPATCH_V1=';
  const lines = prompt.split(/\r?\n/).filter((line) => line.startsWith(prefix));
  assert.equal(lines.length, 1);
  return JSON.parse(lines[0].slice(prefix.length));
}

test('dispatches apply for a newly enqueued unblocked issue', async () => {
  const client = initialClient();
  const issue = baseIssue();
  const result = await reconcileIssue({
    client,
    issue,
    agentToken: 'agent-token',
    now,
  });
  assert.equal(result.action, 'dispatched');
  assert.equal(result.operation, 'apply');
  const start = client.calls.find(([name]) => name === 'startAgentTask');
  assert.equal(start[1].customAgent, 'openspec');
  assert.equal(start[1].createPullRequest, true);
  assert.equal(start[1].prompt.includes('<'), false);
  assert.deepEqual(dispatchEnvelope(start[1].prompt), {
    version: 1,
    changeRef: 'add-platform',
    operation: 'apply',
    applyTask: {
      id: '1.1',
      capabilities: ['implementation'],
      capabilityPaths: ['openspec/capabilities/implementation.md'],
      block: '- [ ] 1.1 Work. Capabilities: implementation.',
      policy: {
        ids: ['implementation'],
        isolation: 'shared',
        mutation: 'scoped',
        resultSchema: 'schemas/capability-result-v1.schema.json',
      },
    },
    issueNumber: 12,
    attempt: 1,
    checkpoint: {
      mode: 'create',
      baseRef: 'main',
      baseSha: sha('a'),
    },
  });
  assert.deepEqual(
    client.calls.map(([name]) => name),
    [
      'createIssueComment',
      'startAgentTask',
      'updateIssueComment',
      'removeIssueLabel',
      'addIssueLabel',
      'addIssueLabel',
    ],
  );
  assert.deepEqual(labels(issue), [
    'openspec:change',
    'openspec:processing',
    'openspec:stage:apply',
  ]);
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
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":null,"applyTaskId":"1.1","applyTaskCapabilities":["implementation"],"baseRef":"main","headRef":null,"beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":null,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
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
    getTextContent: async (path) => repositoryContent(
      path,
      '- [x] 1.1 Done. Capabilities: implementation.',
    ),
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
    getSessionLog: async () => capabilityResult(),
    now,
  });
  assert.equal(result.action, 'dispatched');
  assert.equal(result.operation, 'verify');
  const start = calls.find(([name]) => name === 'startAgentTask');
  assert.equal(start[1].headRef, 'copilot/add-platform');
  assert.equal(start[1].createPullRequest, false);
  assert.deepEqual(dispatchEnvelope(start[1].prompt), {
    version: 1,
    changeRef: 'add-platform',
    operation: 'verify',
    issueNumber: 12,
    attempt: 1,
    checkpoint: {
      mode: 'continue',
      baseRef: 'main',
      headRef: 'copilot/add-platform',
      headSha: sha('b'),
    },
  });
});

test('dispatches the next apply task on the same branch before verify', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":null,"applyTaskId":"1.1","applyTaskCapabilities":["architecture"],"baseRef":"main","headRef":null,"beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":null,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
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
    getTextContent: async (path) => repositoryContent(path, [
      '- [x] 1.1 Define persistence decisions. Capabilities: architecture.',
      '- [ ] 1.2 Implement persistence. Capabilities: implementation.',
    ].join('\n')),
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
    getSessionLog: async () => capabilityResult({ capabilities: ['architecture'] }),
    now,
  });

  assert.deepEqual(result, {
    action: 'dispatched',
    operation: 'apply',
    attempt: 1,
    taskId: 'task-2',
  });
  const start = calls.find(([name]) => name === 'startAgentTask');
  assert.equal(start[1].headRef, 'copilot/add-platform');
  assert.equal(start[1].createPullRequest, false);
  assert.deepEqual(dispatchEnvelope(start[1].prompt).applyTask, {
    id: '1.2',
    capabilities: ['implementation'],
    capabilityPaths: ['openspec/capabilities/implementation.md'],
    block: '- [ ] 1.2 Implement persistence. Capabilities: implementation.',
    policy: {
      ids: ['implementation'],
      isolation: 'shared',
      mutation: 'scoped',
      resultSchema: 'schemas/capability-result-v1.schema.json',
    },
  });
});

test('rejects regressed or out-of-scope apply task completion', async () => {
  const scenarios = [
    {
      name: 'regressed prior task',
      tasks: [
        '- [ ] 1.0 Prepare context. Capabilities: architecture.',
        '- [x] 1.1 Implement persistence. Capabilities: implementation.',
      ],
    },
    {
      name: 'completed unrelated task',
      tasks: [
        '- [x] 1.0 Prepare context. Capabilities: architecture.',
        '- [x] 1.1 Implement persistence. Capabilities: implementation.',
        '- [x] 1.2 Add tests. Capabilities: verification.',
      ],
    },
  ];

  for (const scenario of scenarios) {
    const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":null,"applyTaskId":"1.1","applyTaskCapabilities":["implementation"],"completedApplyTaskIds":["1.0"],"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
    const client = initialClient({
      listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
      getAgentTask: async () => ({
        id: 'task-1',
        state: 'completed',
        artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
        sessions: [{ id: 'session-1' }],
      }),
      getBranch: async () => ({ commit: { sha: sha('b') } }),
      getTextContent: async (path) => repositoryContent(path, scenario.tasks.join('\n')),
    });

    const result = await reconcileIssue({
      client,
      issue: baseIssue(),
      agentToken: 'agent-token',
      getSessionLog: async () => capabilityResult(),
      now,
    });

    assert.deepEqual(
      result,
      { action: 'needs_attention', reason: 'invalid-result' },
      scenario.name,
    );
  }
});

test('rejects mutations outside an isolated capability task checkbox', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":null,"applyTaskId":"1.1","applyTaskCapabilities":["verification"],"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      id: 'task-1',
      state: 'completed',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-1' }],
    }),
    getBranch: async () => ({ commit: { sha: sha('b') } }),
    compareCommits: async () => ({ files: [{ filename: 'docs/report.md' }] }),
    getTextContent: async (path) => repositoryContent(
      path,
      '- [x] 1.1 Verify evidence. Capabilities: verification.',
    ),
  });

  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    getSessionLog: async () => capabilityResult({
      capabilities: ['verification'],
      artifactsChanged: ['docs/report.md'],
    }),
    now,
  });

  assert.deepEqual(result, { action: 'needs_attention', reason: 'invalid-result' });
});

test('accepts only the selected checkbox mutation for an isolated capability', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":null,"applyTaskId":"1.1","applyTaskCapabilities":["verification"],"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      id: 'task-1',
      state: 'completed',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-1' }],
    }),
    getBranch: async () => ({ commit: { sha: sha('b') } }),
    getTextContent: async (path, ref) => repositoryContent(
      path,
      `- [${ref === sha('a') ? ' ' : 'x'}] 1.1 Verify evidence. Capabilities: verification.`,
    ),
    listOpenPullRequestsForHead: async () => [{ number: 30 }],
  });

  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    getSessionLog: async () => capabilityResult({ capabilities: ['verification'] }),
    now,
  });

  assert.equal(result.action, 'dispatched');
  assert.equal(result.operation, 'verify');
});

test('rejects lifecycle verify when the branch SHA changes', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"verify","attempt":1,"taskId":"task-1","sessionId":null,"applyTaskId":null,"applyTaskCapabilities":null,"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      id: 'task-1',
      state: 'completed',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform', base_ref: 'main' } }],
      sessions: [{ id: 'session-verify' }],
    }),
    getBranch: async () => ({ commit: { sha: sha('b') } }),
  });

  const result = await reconcileIssue({
    client,
    issue: baseIssue(),
    agentToken: 'agent-token',
    getSessionLog: async () => 'OPEN_SPEC_CLOUD_OPERATION_V1={"changeRef":"add-platform","operation":"verify","verdict":"pass","validation":"Verification passed."}',
    validateBranch: async () => {},
    now,
  });

  assert.deepEqual(result, { action: 'needs_attention', reason: 'invalid-result' });
});

test('advances apply through archive on one durable pull request', async () => {
  const issue = baseIssue();
  const comments = [];
  const tasks = new Map();
  const requests = [];
  const operationsBySession = new Map();
  let nextCommentId = 20;
  let nextTaskId = 1;
  let branchSha = sha('a');
  let tasksMarkdown = '- [ ] 1.1 Done. Capabilities: implementation.';

  const client = initialClient({
    listIssueComments: async () => comments,
    getAgentTask: async (taskId) => tasks.get(taskId),
    getBranch: async (branch) => ({
      commit: { sha: branch === 'main' ? sha('a') : branchSha },
    }),
    getTextContent: async (path) => repositoryContent(path, tasksMarkdown),
    getRepositoryContent: async (path) => path === 'openspec/changes/archive'
      ? [{
        type: 'dir',
        name: '2026-09-24-add-platform',
        path: 'openspec/changes/archive/2026-09-24-add-platform',
      }]
      : [
        { name: 'proposal.md' },
        { name: 'design.md' },
        { name: 'tasks.md' },
        { name: 'specs' },
      ],
    listOpenPullRequestsForHead: async () => [{ number: 30 }],
    createIssueComment: async (issueNumber, body) => {
      const comment = {
        id: nextCommentId,
        body,
        updated_at: now().toISOString(),
        user: { login: 'github-actions[bot]' },
      };
      nextCommentId += 1;
      comments.push(comment);
      return comment;
    },
    updateIssueComment: async (commentId, body) => {
      const comment = comments.find(({ id }) => id === commentId);
      comment.body = body;
      comment.updated_at = now().toISOString();
    },
    updateIssue: async (issueNumber, update) => {
      assert.equal(issueNumber, issue.number);
      issue.body = update.body;
    },
    startAgentTask: async (request) => {
      const taskId = `task-${nextTaskId}`;
      nextTaskId += 1;
      requests.push(request);
      tasks.set(taskId, { id: taskId, state: 'queued' });
      return { id: taskId };
    },
  });

  const completeTask = (taskId, operation, shaCharacter) => {
    branchSha = sha(shaCharacter);
    if (operation === 'apply') {
      tasksMarkdown = '- [x] 1.1 Done. Capabilities: implementation.';
    }
    const sessionId = `session-${operation}`;
    operationsBySession.set(sessionId, operation);
    tasks.set(taskId, {
      id: taskId,
      state: 'completed',
      artifacts: [{
        type: 'branch',
        data: { head_ref: 'copilot/add-platform', base_ref: 'main' },
      }],
      sessions: [{ id: sessionId }],
    });
  };
  const getSessionLog = async (sessionId) => {
    const operation = operationsBySession.get(sessionId);
    if (operation === 'apply') return capabilityResult();
    return `OPEN_SPEC_CLOUD_OPERATION_V1={"changeRef":"add-platform","operation":"${operation}","verdict":"pass","validation":"${operation} passed."}`;
  };
  const reconcile = () => reconcileIssue({
    client,
    issue,
    agentToken: 'agent-token',
    getSessionLog,
    validateBranch: async () => {},
    now,
  });

  assert.deepEqual(
    await reconcile(),
    { action: 'dispatched', operation: 'apply', attempt: 1, taskId: 'task-1' },
  );
  completeTask('task-1', 'apply', 'b');
  assert.deepEqual(
    await reconcile(),
    { action: 'dispatched', operation: 'verify', attempt: 1, taskId: 'task-2' },
  );
  completeTask('task-2', 'verify', 'b');
  assert.deepEqual(
    await reconcile(),
    { action: 'dispatched', operation: 'sync', attempt: 1, taskId: 'task-3' },
  );
  completeTask('task-3', 'sync', 'c');
  assert.deepEqual(
    await reconcile(),
    { action: 'dispatched', operation: 'archive', attempt: 1, taskId: 'task-4' },
  );
  completeTask('task-4', 'archive', 'd');
  assert.deepEqual(
    await reconcile(),
    {
      action: 'awaiting_human_review',
      pullRequestNumber: 30,
      archivePath: 'openspec/changes/archive/2026-09-24-add-platform',
    },
  );

  assert.deepEqual(
    requests.map((request) => ({
      operation: dispatchEnvelope(request.prompt).operation,
      mode: dispatchEnvelope(request.prompt).checkpoint.mode,
      headRef: request.headRef ?? null,
      createPullRequest: request.createPullRequest,
      attempt: dispatchEnvelope(request.prompt).attempt,
    })),
    [
      {
        operation: 'apply',
        mode: 'create',
        headRef: null,
        createPullRequest: true,
        attempt: 1,
      },
      {
        operation: 'verify',
        mode: 'continue',
        headRef: 'copilot/add-platform',
        createPullRequest: false,
        attempt: 1,
      },
      {
        operation: 'sync',
        mode: 'continue',
        headRef: 'copilot/add-platform',
        createPullRequest: false,
        attempt: 1,
      },
      {
        operation: 'archive',
        mode: 'continue',
        headRef: 'copilot/add-platform',
        createPullRequest: false,
        attempt: 1,
      },
    ],
  );
  assert.deepEqual(labels(issue), [
    'openspec:change',
    'openspec:awaiting-review',
  ]);
});

test('retries a failed operation exactly once on the same branch', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"apply","attempt":1,"taskId":"task-1","sessionId":"session-1","applyTaskId":"1.1","applyTaskCapabilities":["implementation"],"baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
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
  const start = calls.find(([name]) => name === 'startAgentTask');
  assert.deepEqual(dispatchEnvelope(start[1].prompt).applyTask, {
    id: '1.1',
    capabilities: ['implementation'],
    capabilityPaths: ['openspec/capabilities/implementation.md'],
    block: '- [ ] 1.1 Work. Capabilities: implementation.',
    policy: {
      ids: ['implementation'],
      isolation: 'shared',
      mutation: 'scoped',
      resultSchema: 'schemas/capability-result-v1.schema.json',
    },
  });
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
  const issue = baseIssue();
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
    issue,
    agentToken: 'agent-token',
    getSessionLog: async () => 'OPEN_SPEC_CLOUD_OPERATION_V1={"changeRef":"add-platform","operation":"archive","verdict":"pass","validation":"Archived."}',
    now,
  });
  assert.equal(result.action, 'awaiting_human_review');
  assert.equal(result.pullRequestNumber, 30);
  assert.equal(calls.some(([name]) => name === 'startAgentTask'), false);
  assert.equal(calls.some(([name]) => name === 'updateIssue'), true);
  assert.equal(client.calls.some(([name]) => name === 'addIssueLabel'), true);
  assert.deepEqual(labels(issue), [
    'openspec:change',
    'openspec:awaiting-review',
  ]);
});

test('advances after one-shot enqueue intent is removed by durable dispatch', async () => {
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
  assert.equal(result.action, 'dispatched');
  assert.equal(result.operation, 'sync');
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

test('migrates a failed legacy apply dispatch to the first granular task', async () => {
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
  assert.equal(result.attempt, 1);
  const start = client.calls.find(([name]) => name === 'startAgentTask');
  assert.equal(start[1].headRef, null);
  assert.equal(start[1].createPullRequest, true);
  assert.deepEqual(dispatchEnvelope(start[1].prompt).applyTask, {
    id: '1.1',
    capabilities: ['implementation'],
    capabilityPaths: ['openspec/capabilities/implementation.md'],
    block: '- [ ] 1.1 Work. Capabilities: implementation.',
    policy: {
      ids: ['implementation'],
      isolation: 'shared',
      mutation: 'scoped',
      resultSchema: 'schemas/capability-result-v1.schema.json',
    },
  });
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
    labels: [
      { name: 'openspec:change' },
      { name: 'openspec:awaiting-review' },
      { name: 'team:platform' },
    ],
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
  assert.deepEqual(labels(archivedIssue), [
    'openspec:change',
    'team:platform',
    'openspec:needs-attention',
    'openspec:stage:archive',
  ]);
});

test('cleans visible queue labels after the archive pull request merges to main', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"awaiting_human_review","operation":"archive","attempt":1,"taskId":"task-4","sessionId":"session-4","baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const issue = baseIssue({
    labels: [
      { name: 'openspec:change' },
      { name: 'openspec:awaiting-review' },
      { name: 'team:platform' },
    ],
    body: marker
      .replace('"lifecycle":"active"', '"lifecycle":"archived"')
      .replace('"gitRef":"main"', '"gitRef":"copilot/add-platform"')
      .replace('"path":"openspec/changes/add-platform"', '"path":"openspec/changes/archive/2026-09-24-add-platform"'),
  });
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getPullRequest: async () => ({
      state: 'closed',
      merged_at: '2026-09-24T18:00:00Z',
    }),
    getRepositoryContent: async (path, ref) => {
      assert.equal(path, 'openspec/changes/archive/2026-09-24-add-platform');
      assert.equal(ref, 'main');
      return [{ name: 'tasks.md' }];
    },
  });

  const result = await reconcileIssue({
    client,
    issue,
    agentToken: 'agent-token',
    now,
  });

  assert.deepEqual(result, { action: 'complete', reason: 'archive-merged' });
  assert.deepEqual(labels(issue), ['openspec:change', 'team:platform']);
  assert.equal(client.calls.some(([name]) => name === 'startAgentTask'), false);
});

test('event re-entry preserves an active stage without duplicate dispatch or label calls', async () => {
  const stateBody = '<!-- openspec-queue-state:v1\n{"version":1,"changeRef":"add-platform","issueNumber":12,"status":"dispatched","operation":"verify","attempt":1,"taskId":"task-2","sessionId":"session-2","baseRef":"main","headRef":"copilot/add-platform","beforeSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pullRequestNumber":30,"updatedAt":"2026-09-24T17:00:00Z"}\n-->';
  const client = initialClient({
    listIssueComments: async () => [{ id: 20, body: stateBody, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }],
    getAgentTask: async () => ({
      state: 'in_progress',
      artifacts: [{ type: 'branch', data: { head_ref: 'copilot/add-platform' } }],
      sessions: [{ id: 'session-2' }],
    }),
  });
  const issue = baseIssue({
    labels: [
      { name: 'openspec:change' },
      { name: 'openspec:processing' },
      { name: 'openspec:stage:verify' },
    ],
  });

  const result = await reconcileIssue({
    client,
    issue,
    agentToken: 'agent-token',
    now,
  });

  assert.deepEqual(result, {
    action: 'waiting',
    reasons: ['agent-task-in_progress'],
  });
  assert.deepEqual(client.calls, []);
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

test('ensures visible queue label definitions before reconciliation', async () => {
  const client = initialClient({
    listIssueTwins: async () => [],
  });

  assert.deepEqual(
    await reconcileAll({ client, agentToken: 'agent-token', now }),
    [],
  );
  assert.deepEqual(
    client.calls
      .filter(([name]) => name === 'ensureLabel')
      .map(([, definition]) => definition.name),
    QUEUE_LABEL_DEFINITIONS.map(({ name }) => name),
  );
});
