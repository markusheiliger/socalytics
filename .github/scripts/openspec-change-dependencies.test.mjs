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
