import assert from 'node:assert/strict';
import test from 'node:test';

import { GitHubChangeClient } from './openspec-change-github.mjs';

const sha = (character) => character.repeat(40);

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
    customAgent: 'openspec',
    baseRef: 'main',
    createPullRequest: true,
  });
  assert.equal(calls[0].options.headers.Authorization, 'Bearer agent-secret');
  assert.deepEqual(JSON.parse(calls[0].options.body), {
    prompt: 'Apply change add-platform.',
    custom_agent: 'openspec',
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
    customAgent: 'openspec',
    baseRef: 'main',
    headRef: 'openspec/add-platform',
  });
  assert.equal(JSON.parse(request.options.body).head_ref, 'openspec/add-platform');
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

test('adds and removes only the requested issue label', async () => {
  const calls = [];
  const client = clientWith(async (url, options) => {
    calls.push({ url, options });
    return options.method === 'DELETE'
      ? new Response(null, { status: 204 })
      : jsonResponse([{ name: 'openspec:processing' }]);
  });

  await client.addIssueLabel(12, 'openspec:processing');
  await client.removeIssueLabel(12, 'openspec:stage:apply');

  assert.equal(
    calls[0].url,
    'https://api.github.com/repos/markusheiliger/socalytics/issues/12/labels',
  );
  assert.deepEqual(JSON.parse(calls[0].options.body), {
    labels: ['openspec:processing'],
  });
  assert.equal(
    calls[1].url,
    'https://api.github.com/repos/markusheiliger/socalytics/issues/12/labels/openspec%3Astage%3Aapply',
  );
  assert.equal(calls[1].options.body, undefined);
});

test('reads an issue', async () => {
  const calls = [];
  const client = clientWith(async (url) => {
    calls.push(url);
    return jsonResponse({ number: 12 });
  });

  assert.equal((await client.getIssue(12)).number, 12);
  assert.match(calls[0], /\/issues\/12$/);
});

test('treats removing an absent issue label as idempotent', async () => {
  const client = clientWith(async () => new Response(
    JSON.stringify({ message: 'Label does not exist' }),
    { status: 404, headers: { 'Content-Type': 'application/json' } },
  ));

  await assert.doesNotReject(
    client.removeIssueLabel(12, 'openspec:awaiting-review'),
  );
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

test('appends a Git commit', async () => {
  const calls = [];
  const client = clientWith(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({ sha: sha('c') }, 201);
  });
  await client.createGitCommit({ message: 'checkpoint', tree: sha('a'), parents: [sha('b')] });
  assert.match(calls[0].url, /\/git\/commits$/);
  assert.deepEqual(JSON.parse(calls[0].options.body), { message: 'checkpoint', tree: sha('a'), parents: [sha('b')] });
});

test('compares commits and requires changed-file evidence', async () => {
  const calls = [];
  const client = clientWith(async (url) => {
    calls.push(url);
    return jsonResponse({ files: [{ filename: 'src/example.cs' }] });
  });

  const comparison = await client.compareCommits(sha('a'), sha('b'));
  assert.deepEqual(comparison.files, [{ filename: 'src/example.cs' }]);
  assert.match(calls[0], new RegExp(`/compare/${sha('a')}\\.\\.\\.${sha('b')}$`));

  const malformed = clientWith(async () => jsonResponse({ files: null }));
  await assert.rejects(
    malformed.compareCommits(sha('a'), sha('b')),
    /did not contain changed files/,
  );
});

test('stores state in check runs and reads named check runs for a commit', async () => {
  const calls = [];
  const client = clientWith(async (url, options) => {
    calls.push({ url, options });
    if (options.method === 'GET') return jsonResponse({ total_count: 1, check_runs: [{ id: 5 }] });
    return jsonResponse({ id: 6 }, options.method === 'POST' ? 201 : 200);
  });
  assert.deepEqual(await client.listCheckRuns(sha('a'), 'OpenSpec lifecycle'), [{ id: 5 }]);
  assert.match(calls[0].url, /commits\/a{40}\/check-runs\?check_name=OpenSpec%20lifecycle&filter=all/);
  await client.createCheckRun({ name: 'OpenSpec lifecycle', head_sha: sha('a') });
  await client.updateCheckRun(6, { status: 'completed' });
  assert.equal(calls[1].options.method, 'POST');
  assert.match(calls[2].url, /check-runs\/6$/);
  assert.equal(calls[2].options.method, 'PATCH');
});

test('creates the run branch and draft pull request with repository authentication', async () => {
  const calls = [];
  const client = clientWith(async (url, options) => {
    calls.push({ url, options });
    return jsonResponse({ number: 21 }, 201);
  });
  await client.createGitRef('openspec/add-platform', sha('b'));
  await client.createPullRequest({ title: 'T', body: 'B', head: 'openspec/add-platform', base: 'main' });
  assert.deepEqual(JSON.parse(calls[0].options.body), { ref: 'refs/heads/openspec/add-platform', sha: sha('b') });
  assert.deepEqual(JSON.parse(calls[1].options.body), {
    title: 'T', body: 'B', head: 'openspec/add-platform', base: 'main', draft: true,
  });
  assert.equal(calls[1].options.headers.Authorization, calls[0].options.headers.Authorization);
});

test('reads collaborator roles and treats unknown users as having no access', async () => {
  const client = clientWith(async (url) => (url.includes('/alice/')
    ? jsonResponse({ permission: 'write', role_name: 'maintain' })
    : jsonResponse({ message: 'Not Found' }, 404)));
  assert.equal(await client.getCollaboratorPermission('alice'), 'maintain');
  assert.equal(await client.getCollaboratorPermission('mallory'), 'none');
});

test('returns null or empty results for missing optional content', async () => {
  const client = clientWith(async () => jsonResponse({ message: 'Not Found' }, 404));
  assert.equal(await client.getOptionalTextContent('openspec/changes/x/tasks.md', 'main'), null);
  assert.deepEqual(await client.listDirectory('openspec/changes/archive', 'main'), []);
  assert.equal(await client.branchExists('openspec/x'), false);
});
