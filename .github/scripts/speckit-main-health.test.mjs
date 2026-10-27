import assert from 'node:assert/strict';
import test from 'node:test';

import { ATTENTION_LABEL } from './speckit-implement-core.mjs';
import { MAIN_HEALTH_LABEL, MAIN_HEALTH_WORKFLOW_FILE, failedShards, mainHealth, recordHealth } from './speckit-main-health.mjs';
import { FakeGitHub } from './speckit-test-helpers.mjs';

const env = { SPECKIT_DEFAULT_BRANCH: 'trunk' };
const NOW = Date.parse('2026-10-27T12:00:00Z');

function run(github, { id, conclusion, status = 'completed', branch = 'trunk', at = '2026-10-27T10:00:00Z', workflow = MAIN_HEALTH_WORKFLOW_FILE }) {
  github.repo.runs.push({ id, workflow, status, conclusion, head_branch: branch, created_at: at, html_url: `https://github.com/octo/repo/actions/runs/${id}` });
}

const LOG = [
  '=== Shard 1/4 (success): Markdown check ===\nok\n',
  '=== Shard 2/4 (failure): platform build and tests ===\nfailed\n',
  '=== Shard 3/4 (cancelled): no check ===\n',
].join('\n');

test('the health is the latest finished run of the default branch', async () => {
  const github = new FakeGitHub();
  assert.equal((await mainHealth(github, env, { now: NOW })).state, 'unknown');
  run(github, { id: 1, conclusion: 'failure', at: '2026-10-27T08:00:00Z' });
  run(github, { id: 2, conclusion: 'success', at: '2026-10-27T09:00:00Z' });
  run(github, { id: 3, conclusion: 'cancelled', at: '2026-10-27T10:00:00Z' });
  run(github, { id: 4, conclusion: null, status: 'in_progress', at: '2026-10-27T11:00:00Z' });
  run(github, { id: 5, conclusion: 'failure', branch: 'feature', at: '2026-10-27T11:30:00Z' });
  run(github, { id: 6, conclusion: 'failure', workflow: 'other.yml', at: '2026-10-27T11:40:00Z' });
  assert.deepEqual(await mainHealth(github, env, { now: NOW }).then((health) => [health.state, health.run.id]), ['green', 2]);
  run(github, { id: 7, conclusion: 'timed_out', at: '2026-10-27T11:50:00Z' });
  assert.deepEqual(await mainHealth(github, env, { now: NOW }).then((health) => [health.state, health.run.id]), ['red', 7]);
  const failing = { listWorkflowRuns: async () => { throw new Error('rate limited'); } };
  assert.equal((await mainHealth(failing, env, { now: NOW })).state, 'unknown', 'a failed lookup never holds anything');
});

test('reads the failed shards of a combined verification log', () => {
  assert.deepEqual(failedShards(LOG), [
    { shard: '2/4', outcome: 'failure', checks: 'platform build and tests' },
    { shard: '3/4', outcome: 'cancelled', checks: 'no check' },
  ]);
});

test('a red run opens one tracking issue with a recommendation, adds later runs once, and a green run closes it', async () => {
  const github = new FakeGitHub();
  const red = { result: 'failure', log: LOG, sha: 'a'.repeat(40), runUrl: 'https://github.com/octo/repo/actions/runs/11' };
  assert.deepEqual(await recordHealth(github, env, red), { outcome: 'opened', issue: github.issues.at(-1).number });
  const issue = github.issues.at(-1);
  assert.deepEqual(issue.labels.map((label) => label.name), [MAIN_HEALTH_LABEL, ATTENTION_LABEL]);
  assert.match(issue.body, /^<!-- speckit:main-health -->\n\*\*The default branch is unhealthy\*\*: the full verification of `a{40}` failed in \[this run\]\(https:\/\/github\.com\/octo\/repo\/actions\/runs\/11\)\./);
  assert.match(issue.body, /- Shard 2\/4 \(failure\): platform build and tests\n- Shard 3\/4 \(cancelled\): no check/);
  assert.equal(issue.body.match(/\*\*Recommended next step\*\*/g).length, 1);
  assert.match(issue.body, /```text\n\/speckit-gha-author Make the default branch healthy again: [^\n]*platform build and tests; no check \(Main health run https:\/\/github\.com\/octo\/repo\/actions\/runs\/11\)/);

  assert.equal((await recordHealth(github, env, red)).outcome, 'updated');
  assert.equal(github.comments.filter((comment) => comment.number === issue.number).length, 0, 'the run that opened the issue is not added again');
  await recordHealth(github, env, { ...red, runUrl: 'https://github.com/octo/repo/actions/runs/12' });
  await recordHealth(github, env, { ...red, runUrl: 'https://github.com/octo/repo/actions/runs/12' });
  assert.equal(github.comments.filter((comment) => comment.number === issue.number).length, 1, 'each later red run is added once');
  assert.equal(github.issues.filter((item) => item.labels.some((label) => label.name === MAIN_HEALTH_LABEL)).length, 1, 'one tracking issue');

  assert.equal((await recordHealth(github, env, { ...red, result: 'cancelled' })).outcome, 'ignored');
  assert.equal((await recordHealth(github, env, { ...red, result: 'success', runUrl: 'https://github.com/octo/repo/actions/runs/13' })).outcome, 'closed');
  assert.equal(github.find(issue.number).state, 'closed');
  assert.ok(!github.find(issue.number).labels.some((label) => label.name === ATTENTION_LABEL));
  assert.match(github.comments.at(-1).body, /\*\*The default branch is healthy again\*\*/);
  assert.equal((await recordHealth(github, env, { ...red, result: 'success' })).outcome, 'healthy');
});
