import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { createGitHubClient, createReporter, setOutput } from './speckit-script-runtime.mjs';

test('writes step summaries and workflow outputs only when configured', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-runtime-'));
  try {
    const summary = path.join(root, 'summary.md');
    const output = path.join(root, 'output.txt');
    const logged = [];
    const reporter = createReporter({ GITHUB_STEP_SUMMARY: summary }, (line) => logged.push(line));
    reporter.line('first');
    reporter.line();
    reporter.line('last');
    reporter.flush();
    setOutput({ GITHUB_OUTPUT: output }, 'action', 'implement');
    assert.deepEqual(logged, ['first', '', 'last']);
    assert.equal(readFileSync(summary, 'utf8'), 'first\n\nlast\n');
    assert.equal(readFileSync(output, 'utf8'), 'action=implement\n');
    createReporter({}, () => {}).flush();
    setOutput({}, 'ignored', 'value');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('creates the Actions GitHub client from the standard environment', () => {
  const client = createGitHubClient({
    GITHUB_TOKEN: 'token',
    GH_TOKEN: 'fallback',
    GITHUB_REPOSITORY: 'octo/repo',
    GITHUB_API_URL: 'https://example.test/api',
    GITHUB_GRAPHQL_URL: 'https://example.test/graphql',
  });
  assert.equal(client.token, 'token');
  assert.equal(client.repository, 'octo/repo');
  assert.equal(client.apiUrl, 'https://example.test/api');
  assert.equal(client.graphqlUrl, 'https://example.test/graphql');
});
