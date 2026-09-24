import assert from 'node:assert/strict';
import test from 'node:test';

import { GitHubChangeClient } from './openspec-change-github.mjs';

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function clientWith(fetchImpl) {
  return new GitHubChangeClient({
    owner: 'markusheiliger',
    repo: 'socalytics',
    repositoryToken: 'repo-secret',
    agentToken: 'agent-secret',
    fetchImpl,
  });
}

test('paginates issue twins with repository authentication and version headers', async () => {
  const calls = [];
  const client = clientWith(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse(new URL(url).searchParams.get('page') === '1'
      ? Array.from({ length: 100 }, (_, index) => ({ id: index }))
      : [{ id: 100 }]);
  });

  const issues = await client.listIssueTwins();
  assert.equal(issues.length, 101);
  assert.equal(calls.length, 2);
  assert.match(calls[0].url, /labels=openspec%3Achange/);
  assert.equal(calls[0].options.headers.Authorization, 'Bearer repo-secret');
  assert.equal(calls[0].options.headers['X-GitHub-Api-Version'], '2026-03-10');
});

test('uses the user agent token only for Agent Tasks endpoints', async () => {
  const calls = [];
  const client = clientWith(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({ id: 'task-1' }, 201);
  });

  await client.startAgentTask({
    prompt: 'Apply change add-platform.',
    customAgent: 'openspec-cloud',
    baseRef: 'main',
    createPullRequest: true,
  });
  assert.equal(calls[0].options.headers.Authorization, 'Bearer agent-secret');
  assert.deepEqual(JSON.parse(calls[0].options.body), {
    prompt: 'Apply change add-platform.',
    custom_agent: 'openspec-cloud',
    base_ref: 'main',
    create_pull_request: true,
  });
});

test('continues an Agent Task on an existing pull request branch', async () => {
  let request;
  const client = clientWith(async (url, options) => {
    request = { url, options };
    return jsonResponse({ id: 'task-2' }, 201);
  });

  await client.startAgentTask({
    prompt: 'Verify change add-platform.',
    customAgent: 'openspec-cloud',
    baseRef: 'main',
    headRef: 'copilot/add-platform',
  });
  assert.equal(JSON.parse(request.options.body).head_ref, 'copilot/add-platform');
});

test('uses native issue dependency request shapes', async () => {
  const calls = [];
  const client = clientWith(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({ id: 42 }, options.method === 'DELETE' ? 200 : 201);
  });

  await client.addBlockedBy(12, 42);
  await client.removeBlockedBy(12, 42);
  assert.equal(
    calls[0].url,
    'https://api.github.com/repos/markusheiliger/socalytics/issues/12/dependencies/blocked_by',
  );
  assert.deepEqual(JSON.parse(calls[0].options.body), { issue_id: 42 });
  assert.match(calls[1].url, /blocked_by\/42$/);
});

test('redacts both tokens from GitHub API errors', async () => {
  const client = clientWith(async () => new Response(
    'repo-secret and agent-secret must not escape',
    { status: 403, statusText: 'Forbidden' },
  ));

  await assert.rejects(
    client.listIssueTwins(),
    (error) => {
      assert.doesNotMatch(error.message, /repo-secret|agent-secret/);
      assert.match(error.message, /\*\*\*/);
      return true;
    },
  );
});

test('rejects malformed pagination responses', async () => {
  const client = clientWith(async () => jsonResponse({ unexpected: [] }));
  await assert.rejects(client.listIssueTwins(), /did not contain an array/);
});

test('decodes repository text content', async () => {
  const client = clientWith(async () => jsonResponse({
    type: 'file',
    encoding: 'base64',
    content: Buffer.from('hello').toString('base64'),
  }));
  assert.equal(await client.getTextContent('README.md', 'main'), 'hello');
});
