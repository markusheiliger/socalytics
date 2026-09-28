import assert from 'node:assert/strict';
import test from 'node:test';

import { JSON_CONTRACTS } from './openspec-change-core.mjs';
import {
  migrateGitHubJsonContracts,
  migrateLegacyChangeMarker,
  migrateLegacyCheckpointMessage,
  migrateLegacyDependencySummary,
  migrateLegacyQueueState,
} from './openspec-json-migration.mjs';

const legacyMarker = (name, value) => `<!-- ${name}:v1
${JSON.stringify({ version: 1, ...value })}
-->`;

test('migrates legacy markers and checkpoints to self-describing JSON', () => {
  const change = migrateLegacyChangeMarker(legacyMarker('openspec-change', {
    repository: 'markusheiliger/socalytics',
    ref: 'add-platform',
    lifecycle: 'active',
    gitRef: 'main',
    path: 'openspec/changes/add-platform',
  }));
  assert.match(change, /<!-- openspec-json/);
  assert.match(change, new RegExp(JSON_CONTRACTS.changeMarker.replaceAll('.', '\\.')));
  assert.doesNotMatch(change, /"version"/);

  const queue = migrateLegacyQueueState(legacyMarker('openspec-queue-state', {
    taskId: 'task-1',
    headRef: 'copilot/change',
  }));
  assert.equal(queue.value.taskId, 'task-1');
  assert.match(queue.body, new RegExp(JSON_CONTRACTS.queueState.replaceAll('.', '\\.')));

  const dependency = migrateLegacyDependencySummary(
    legacyMarker('openspec-dependencies', { managedEdges: [] }),
  );
  assert.match(dependency, new RegExp(JSON_CONTRACTS.dependencySummary.replaceAll('.', '\\.')));

  const checkpoint = migrateLegacyCheckpointMessage(
    'Complete\n\nOpenSpec-Queue-Checkpoint: {"version":1,"changeRef":"add-platform","operation":"verify","verdict":"pass","validation":"passed"}',
  );
  assert.match(checkpoint, /^OpenSpec-JSON: /m);
  assert.match(checkpoint, new RegExp(JSON_CONTRACTS.queueCheckpoint.replaceAll('.', '\\.')));
});

test('migration dry run is read-only and reports current authoritative records', async () => {
  const calls = [];
  const issue = {
    number: 6,
    body: legacyMarker('openspec-change', {
      repository: 'markusheiliger/socalytics',
      ref: 'add-platform',
      lifecycle: 'active',
      gitRef: 'main',
      path: 'openspec/changes/add-platform',
    }),
  };
  const state = legacyMarker('openspec-queue-state', {
    changeRef: 'add-platform',
    issueNumber: 6,
    status: 'needs_attention',
    operation: 'apply',
    attempt: 1,
    taskId: 'task-1',
    sessionId: 'session-1',
    applyTaskId: '1.1',
    applyTaskCapabilities: ['implementation'],
    completedApplyTaskIds: [],
    baseRef: 'main',
    headRef: null,
    beforeSha: 'a'.repeat(40),
    pullRequestNumber: null,
    updatedAt: '2026-09-28T18:00:00Z',
  });
  const result = await migrateGitHubJsonContracts({
    client: {
      listIssueTwins: async () => [issue],
      listIssueComments: async () => [{
        id: 10,
        body: state,
        updated_at: '2026-09-28T18:00:00Z',
        user: { login: 'github-actions[bot]' },
      }],
      getAgentTask: async () => ({ state: 'failed' }),
      updateIssue: async (...args) => calls.push(args),
      updateIssueComment: async (...args) => calls.push(args),
    },
    dryRun: true,
  });
  assert.deepEqual(result.issues, [6]);
  assert.deepEqual(result.comments, [10]);
  assert.deepEqual(calls, []);
});

test('migration stops before mutation when an Agent Task is active', async () => {
  await assert.rejects(
    migrateGitHubJsonContracts({
      client: {
        listIssueTwins: async () => [{ number: 6, body: '' }],
        listIssueComments: async () => [{
          id: 10,
          body: legacyMarker('openspec-queue-state', {
            taskId: 'task-1',
            headRef: null,
          }),
          updated_at: '2026-09-28T18:00:00Z',
          user: { login: 'github-actions[bot]' },
        }],
        getAgentTask: async () => ({ state: 'in_progress' }),
      },
      dryRun: false,
    }),
    /still in_progress/,
  );
});
