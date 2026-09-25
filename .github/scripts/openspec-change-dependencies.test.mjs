import assert from 'node:assert/strict';
import test from 'node:test';

import {
  extractDependencySafeOutput,
  reconcileDependencies,
} from './openspec-change-dependencies.mjs';
import { renderChangeMarker, renderDependencySummary } from './openspec-change-core.mjs';

function issue(number, id, ref) {
  return {
    number,
    id,
    body: renderChangeMarker({
      repository: 'markusheiliger/socalytics',
      ref,
      lifecycle: 'active',
      gitRef: 'main',
      path: `openspec/changes/${ref}`,
    }),
  };
}

test('applies accepted edges and records provenance', async () => {
  const calls = [];
  const issues = [issue(1, 101, 'one'), issue(2, 102, 'two')];
  const client = {
    owner: 'markusheiliger',
    repo: 'socalytics',
    listIssueTwins: async () => issues,
    listIssueComments: async () => [],
    listBlockedBy: async () => [],
    addBlockedBy: async (...args) => calls.push(['add', ...args]),
    removeBlockedBy: async (...args) => calls.push(['remove', ...args]),
    createIssueComment: async (...args) => calls.push(['comment', ...args]),
  };
  const result = await reconcileDependencies({
    client,
    output: {
      version: 1,
      candidates: [{
        changeRef: 'two',
        dependsOn: 'one',
        confidence: 0.95,
        evidence: ['openspec/changes/two/design.md:10'],
      }],
    },
  });
  assert.equal(result.accepted.length, 1);
  assert.deepEqual(calls[0], ['add', 2, 101]);
  assert.equal(calls[1][0], 'comment');
});

test('removes only a previously managed edge and preserves manual blockers', async () => {
  const calls = [];
  const issues = [issue(1, 101, 'one'), issue(2, 102, 'two')];
  const managed = renderDependencySummary({
    version: 1,
    managedEdges: [{
      changeRef: 'two',
      dependsOn: 'one',
      confidence: 0.95,
      evidence: ['old'],
    }],
  });
  const client = {
    owner: 'markusheiliger',
    repo: 'socalytics',
    listIssueTwins: async () => issues,
    listIssueComments: async (number) => number === 2
      ? [{ id: 20, body: managed, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }]
      : [],
    listBlockedBy: async (number) => number === 2
      ? [
        { ...issues[0], state: 'open' },
        { id: 999, number: 99, state: 'open', body: 'manual blocker' },
      ]
      : [],
    addBlockedBy: async (...args) => calls.push(['add', ...args]),
    removeBlockedBy: async (...args) => calls.push(['remove', ...args]),
    updateIssueComment: async (...args) => calls.push(['update', ...args]),
  };
  await reconcileDependencies({
    client,
    output: { version: 1, candidates: [] },
  });
  assert.deepEqual(calls.find(([name]) => name === 'remove'), ['remove', 2, 101]);
  assert.equal(calls.some((call) => call.includes(999)), false);
});

test('does not mutate low-confidence candidates', async () => {
  const calls = [];
  const issues = [issue(1, 101, 'one'), issue(2, 102, 'two')];
  const result = await reconcileDependencies({
    client: {
      owner: 'markusheiliger',
      repo: 'socalytics',
      listIssueTwins: async () => issues,
      listIssueComments: async () => [],
      listBlockedBy: async () => [],
      addBlockedBy: async (...args) => calls.push(args),
      createIssueComment: async (...args) => calls.push(args),
    },
    output: {
      version: 1,
      candidates: [{
        changeRef: 'two',
        dependsOn: 'one',
        confidence: 0.5,
        evidence: ['ambiguous'],
      }],
    },
  });
  assert.equal(result.review.length, 1);
  assert.equal(calls.length, 0);
});

test('extracts exactly one typed dependency safe output', () => {
  const output = extractDependencySafeOutput({
    items: [{
      type: 'reconcile_openspec_dependencies',
      payload: '{"version":1,"candidates":[]}',
    }],
  });
  assert.deepEqual(output, { version: 1, candidates: [] });
  assert.throws(
    () => extractDependencySafeOutput({ items: [] }),
    /Exactly one/,
  );
  assert.throws(
    () => extractDependencySafeOutput({
      items: [{
        type: 'reconcile_openspec_dependencies',
        payload: '{}',
        extra: true,
      }],
    }),
    /unknown fields/,
  );
});

test('rejects duplicate twins before dependency mutations', async () => {
  const calls = [];
  const duplicate = issue(2, 102, 'one');
  const client = {
    owner: 'markusheiliger',
    repo: 'socalytics',
    listIssueTwins: async () => [issue(1, 101, 'one'), duplicate],
    listIssueComments: async () => calls.push('comments'),
  };
  await assert.rejects(
    reconcileDependencies({
      client,
      output: { version: 1, candidates: [] },
    }),
    /Duplicate issue twins for one/,
  );
  assert.deepEqual(calls, []);
});

test('applies an incremental patch while preserving unrelated managed and manual edges', async () => {
  const calls = [];
  const issues = [
    issue(1, 101, 'one'),
    issue(2, 102, 'two'),
    issue(3, 103, 'three'),
  ];
  const checkpoint = {
    managedEdges: [
      {
        changeRef: 'two',
        dependsOn: 'one',
        confidence: 0.95,
        evidence: ['checkpoint'],
      },
      {
        changeRef: 'three',
        dependsOn: 'one',
        confidence: 0.9,
        evidence: ['old'],
      },
    ],
  };
  const client = {
    owner: 'markusheiliger',
    repo: 'socalytics',
    listIssueTwins: async () => issues,
    listIssueComments: async () => [],
    listBlockedBy: async (number) => {
      if (number === 2) return [issues[0]];
      if (number === 3) return [issues[0], { id: 999, body: 'manual' }];
      return [];
    },
    addBlockedBy: async (...args) => calls.push(['add', ...args]),
    removeBlockedBy: async (...args) => calls.push(['remove', ...args]),
    createIssueComment: async (...args) => calls.push(['comment', ...args]),
  };
  const result = await reconcileDependencies({
    client,
    checkpoint,
    output: {
      version: 2,
      evaluationMode: 'incremental',
      evaluatedRefs: ['three'],
      summaries: [{ ref: 'three', summary: 'Third change' }],
      upsert: [{
        changeRef: 'three',
        dependsOn: 'two',
        confidence: 0.96,
        evidence: ['new'],
      }],
      remove: [{
        changeRef: 'three',
        dependsOn: 'one',
        evidence: ['The dependency is obsolete.'],
      }],
    },
  });
  assert.deepEqual(result.accepted.map(
    ({ changeRef, dependsOn }) => `${changeRef}->${dependsOn}`,
  ), ['three->two', 'two->one']);
  assert.deepEqual(calls.find(([name]) => name === 'remove'), ['remove', 3, 101]);
  assert.deepEqual(calls.find(([name]) => name === 'add'), ['add', 3, 102]);
  assert.equal(calls.some((call) => call.includes(999)), false);
  assert.deepEqual(result.evaluatedRefs, ['three']);
});

test('rejects a patch that creates a cycle through an unrelated manual edge', async () => {
  const issues = [issue(1, 101, 'one'), issue(2, 102, 'two')];
  await assert.rejects(reconcileDependencies({
    client: {
      owner: 'markusheiliger',
      repo: 'socalytics',
      listIssueTwins: async () => issues,
      listIssueComments: async () => [],
      listBlockedBy: async (number) => number === 1 ? [issues[1]] : [],
    },
    output: {
      version: 2,
      evaluationMode: 'incremental',
      evaluatedRefs: ['two'],
      summaries: [{ ref: 'two', summary: 'Second change' }],
      upsert: [{
        changeRef: 'two',
        dependsOn: 'one',
        confidence: 0.95,
        evidence: ['new'],
      }],
      remove: [],
    },
  }), /Dependency cycle/);
});

test('migrates existing comment provenance only for a matching native edge', async () => {
  const calls = [];
  const issues = [issue(1, 101, 'one'), issue(2, 102, 'two')];
  const managed = renderDependencySummary({
    version: 1,
    managedEdges: [{
      changeRef: 'two',
      dependsOn: 'one',
      confidence: 0.95,
      evidence: ['legacy'],
    }],
  });
  const result = await reconcileDependencies({
    client: {
      owner: 'markusheiliger',
      repo: 'socalytics',
      listIssueTwins: async () => issues,
      listIssueComments: async (number) => number === 2
        ? [{ id: 20, body: managed, updated_at: '2026-09-24T17:00:00Z', user: { login: 'github-actions[bot]' } }]
        : [],
      listBlockedBy: async (number) => number === 2 ? [issues[0]] : [],
      addBlockedBy: async (...args) => calls.push(['add', ...args]),
      removeBlockedBy: async (...args) => calls.push(['remove', ...args]),
      updateIssueComment: async (...args) => calls.push(['update', ...args]),
    },
    output: {
      version: 2,
      evaluationMode: 'incremental',
      evaluatedRefs: [],
      summaries: [],
      upsert: [],
      remove: [],
    },
  });
  assert.equal(result.accepted.length, 1);
  assert.deepEqual(calls, []);
});
