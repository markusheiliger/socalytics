import assert from 'node:assert/strict';
import test from 'node:test';

import {
  GitHubChangeClient,
  decodeAgentSessionFinalResponse,
} from './openspec-change-github.mjs';

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

test('reads the final completed Agent Task response with the OAuth agent token', async () => {
  let request;
  const client = clientWith(async (url, options) => {
    request = { url, options };
    return new Response([
      'data: {"choices":[{"index":0,"delta":{"role":"assistant"}}]}',
      '',
      'data: {"choices":[{"index":0,"delta":{"content":"Task complete."},"finish_reason":"stop"}]}',
      '',
      'data: {"choices":[{"index":0,"delta":{"role":"assistant"}}]}',
      '',
      'data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}',
      '',
      'data: {"choices":[{"index":0,"delta":{"content":"{\\"$schema\\":\\"openspec/capabilities/schemas/"}}]}',
      '',
      'data: {"choices":[{"index":0,"delta":{"content":"capability-result-v1.schema.json\\"}"},"finish_reason":"stop"}]}',
      '',
      'data: [DONE]',
      '',
    ].join('\n'), { status: 200 });
  });

  const log = await client.getAgentSessionLog('session-1');

  assert.equal(
    log,
    '{"$schema":"openspec/capabilities/schemas/capability-result-v1.schema.json"}',
  );
  assert.equal(
    request.url,
    'https://api.githubcopilot.com/agents/sessions/session-1/logs',
  );
  assert.equal(request.options.headers.Authorization, 'Bearer agent-secret');
  assert.equal(request.options.headers['Copilot-Integration-Id'], 'copilot-4-cli');
  assert.equal(request.options.headers['X-GitHub-Api-Version'], '2026-01-09');
});

test('requires an unambiguous completed Agent Task response', () => {
  assert.equal(
    decodeAgentSessionFinalResponse([
      'data:{"choices":[{"delta":{"content":"{}"}}]}',
      'data: {"choices":[{"delta":{},"finish_reason":"stop"}]}',
    ].join('\n')),
    '{}',
  );
  assert.equal(
    decodeAgentSessionFinalResponse([
      'data: {"choices":[{"delta":{"content":"intermediate"}}]}',
      'data: {"choices":[{"delta":{"role":"assistant"}}]}',
      'data: {"choices":[{"delta":{"content":"final"}}]}',
      'data: {"choices":[{"delta":{},"finish_reason":"stop"}]}',
    ].join('\n')),
    'final',
  );
  assert.throws(
    () => decodeAgentSessionFinalResponse(
      'data: {"choices":[{"delta":{"content":"{}"}}]}\n',
    ),
    /incomplete/,
  );
  assert.throws(
    () => decodeAgentSessionFinalResponse([
      'data: {"choices":[{"index":0,"delta":{"content":"one"}},{"index":1,"delta":{"content":"two"}}]}',
      'data: [DONE]',
    ].join('\n')),
    /ambiguous/,
  );
  assert.throws(
    () => decodeAgentSessionFinalResponse([
      'data: {"choices":[{"delta":{"content":"{}"}}]}',
      'data: not-json',
      'data: [DONE]',
    ].join('\n')),
    /invalid event JSON/,
  );
  assert.throws(
    () => decodeAgentSessionFinalResponse([
      'data: {"choices":{}}',
      'data: [DONE]',
    ].join('\n')),
    /event\.choices must be an array/,
  );
  assert.throws(
    () => decodeAgentSessionFinalResponse('data: [DONE]\n'),
    /did not contain assistant content/,
  );
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

test('reads an issue and deletes a mutable issue comment', async () => {
  const calls = [];
  const client = clientWith(async (url, options) => {
    calls.push([url, options]);
    return options?.method === 'DELETE'
      ? new Response(null, { status: 204 })
      : jsonResponse({ number: 12 });
  });

  assert.equal((await client.getIssue(12)).number, 12);
  await client.deleteIssueComment(99);
  assert.match(calls[0][0], /\/issues\/12$/);
  assert.match(calls[1][0], /\/issues\/comments\/99$/);
  assert.equal(calls[1][1].method, 'DELETE');
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

test('reads commit metadata for a durable queue checkpoint', async () => {
  const calls = [];
  const client = clientWith(async (url) => {
    calls.push(url);
    return jsonResponse({ sha: sha('b'), commit: { message: 'checkpoint' } });
  });

  test('appends a Git commit and fast-forwards a branch ref', async () => {
    const calls = [];
    const client = clientWith(async (url, options) => {
      calls.push({ url, options });
      return jsonResponse({ sha: sha('c') });
    });
    await client.createGitCommit({
      message: 'checkpoint',
      tree: sha('a'),
      parents: [sha('b')],
    });
    await client.updateGitRef('copilot/change', sha('c'));

    assert.match(calls[0].url, /\/git\/commits$/);
    assert.deepEqual(JSON.parse(calls[0].options.body), {
      message: 'checkpoint',
      tree: sha('a'),
      parents: [sha('b')],
    });
    assert.match(calls[1].url, /\/git\/refs\/heads\/copilot\/change$/);
    assert.deepEqual(JSON.parse(calls[1].options.body), {
      sha: sha('c'),
      force: false,
    });
  });
  const commit = await client.getCommit(sha('b'));
  assert.equal(commit.commit.message, 'checkpoint');
  assert.match(calls[0], new RegExp(`/commits/${sha('b')}$`));
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
