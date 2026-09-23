import assert from 'node:assert/strict';
import test from 'node:test';

import { GitHubClient } from './openspec-changeset-github.mjs';

function response(payload) {
  return {
    ok: true,
    text: async () => JSON.stringify(payload),
  };
}

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
