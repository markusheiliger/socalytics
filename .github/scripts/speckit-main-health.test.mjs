import assert from 'node:assert/strict';
import test from 'node:test';

import { ATTENTION_LABEL } from './speckit-implement-core.mjs';
import { MAIN_HEALTH_LABEL, MAIN_HEALTH_WORKFLOW_FILE, attributeCause, dispatchAfterHealth, failedShards, parseCause, recordHealth, renderCauseLines } from './speckit-main-health.mjs';
import { FakeGitHub } from './speckit-test-helpers.mjs';

const env = { SPECKIT_DEFAULT_BRANCH: 'trunk' };

const LOG = [
  '=== Shard 1/4 (success): Markdown check ===\nok\n',
  '=== Shard 2/4 (failure): platform build and tests ===\nfailed\n',
  '=== Shard 3/4 (cancelled): no check ===\n',
].join('\n');

test('reads the failed shards of a combined verification log', () => {
  assert.deepEqual(failedShards(LOG), [
    { shard: '2/4', outcome: 'failure', checks: 'platform build and tests' },
    { shard: '3/4', outcome: 'cancelled', checks: 'no check' },
  ]);
});

test('a red run opens one tracking issue that says the healer works, adds later runs once, and a green run closes it', async () => {
  const github = new FakeGitHub();
  const red = { result: 'failure', log: LOG, sha: 'a'.repeat(40), runUrl: 'https://github.com/octo/repo/actions/runs/11' };
  assert.deepEqual(await recordHealth(github, env, red), { outcome: 'opened', issue: github.issues.at(-1).number, cause: null });
  const issue = github.issues.at(-1);
  assert.deepEqual(issue.labels.map((label) => label.name), [MAIN_HEALTH_LABEL], 'nothing is needed from a person while the healer works');
  assert.match(issue.body, /^<!-- speckit:main-health -->\n\*\*The default branch is unhealthy\*\*: the full verification of `a{40}` failed in \[this run\]\(https:\/\/github\.com\/octo\/repo\/actions\/runs\/11\)\./);
  assert.match(issue.body, /- Shard 2\/4 \(failure\): platform build and tests\n- Shard 3\/4 \(cancelled\): no check/);
  assert.match(issue.body, /\*\*Every Spec Kit implementation is blocked\*\*/);
  assert.match(issue.body, /\*\*Nothing is needed yet\*\*: the healer \(workflow Main heal\)/);
  assert.doesNotMatch(issue.body, /speckit-gha-author|Recommended next step/);

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

test('a red run starts the healer and a green run the scheduler; other results start nothing', async () => {
  const github = new FakeGitHub();
  assert.equal(await dispatchAfterHealth(github, env, 'failure'), 'main-heal.yml');
  assert.equal(await dispatchAfterHealth(github, env, 'timed_out'), 'main-heal.yml');
  assert.equal(await dispatchAfterHealth(github, env, 'success'), 'speckit-orchestrate.yml');
  assert.equal(await dispatchAfterHealth(github, env, 'cancelled'), null);
  assert.deepEqual(github.repo.runs.map((run) => [run.workflow, run.ref]), [['main-heal.yml', 'trunk'], ['main-heal.yml', 'trunk'], ['speckit-orchestrate.yml', 'trunk']]);
  assert.equal(await dispatchAfterHealth({ dispatchWorkflow: async () => { throw new Error('forbidden'); } }, env, 'failure'), null, 'a failed dispatch never fails the record');
});
const GREEN = 'g'.repeat(40);
const RED = 'r'.repeat(40);
const NOW = Date.parse('2026-10-27T12:00:00Z');

function greenRun(github, sha = GREEN) {
  github.repo.runs.push({ id: github.repo.nextRun++, workflow: MAIN_HEALTH_WORKFLOW_FILE, status: 'completed', conclusion: 'success', head_branch: 'trunk', head_sha: sha, created_at: '2026-10-27T10:00:00Z' });
}

// A git that knows one range: commits (sha, subject) and the changed files.
const fakeGit = (commits, files) => (args) => {
  if (args[0] === 'log') return { status: 0, stdout: commits.map(([sha, subject]) => `${sha}\t${subject}\n`).join('') };
  if (args[0] === 'diff') return { status: 0, stdout: files.join('\n') };
  return { status: 1, stdout: '' };
};

function mergedPull(github, number, head, sha) {
  github.repo.pulls.push({ number, head: { ref: head }, base: { ref: 'trunk' }, state: 'closed', merged_at: '2026-10-27T11:00:00Z', merge_commit_sha: sha, labels: [] });
}

test('attributes a red default branch to what changed since the last green verification', async () => {
  const github = new FakeGitHub();
  assert.equal((await attributeCause(github, env, { git: fakeGit([], []), sha: RED, now: NOW })).kind, 'unknown', 'no green run to compare with');
  greenRun(github);
  assert.deepEqual(await attributeCause(github, env, { git: fakeGit([], []), sha: GREEN, now: NOW }), { kind: 'drift', green: GREEN, red: GREEN, commits: [], healable: true });
  assert.equal((await attributeCause(github, env, { git: fakeGit([], []), sha: RED, now: NOW })).kind, 'drift', 'only commits that change nothing');

  mergedPull(github, 40, 'speckit/20260101-000000-x', 'a1');
  const spec = await attributeCause(github, env, { git: fakeGit([['a1', 'Implement: X (#40)']], ['src/platform/a.cs']), sha: RED, now: NOW });
  assert.deepEqual([spec.kind, spec.commits[0].pull, spec.healable], ['spec', 40, true]);
  assert.match(renderCauseLines(spec).join('\n'), /merge of the Spec Kit implementation #40 \(`a1`\): Implement: X \(#40\)\. The healer fixes it forward; the implementation is not reverted\./);

  const direct = await attributeCause(github, env, { git: fakeGit([['b2', 'Tweak the build']], ['src/platform/b.cs', '.github/scripts/x.mjs']), sha: RED, now: NOW });
  assert.deepEqual([direct.kind, direct.healable, direct.protected], ['direct', true, ['.github/scripts/x.mjs']]);
  assert.match(renderCauseLines(direct).join('\n'), /the direct push `b2`: Tweak the build\. The healer may revert it/);

  const tooling = await attributeCause(github, env, { git: fakeGit([['c3', 'Tooling']], ['.github/workflows/x.yml', 'specs/a/spec.md']), sha: RED, now: NOW });
  assert.equal(tooling.healable, false, 'only paths the healer may not change');
  assert.equal((await attributeCause(github, env, { git: fakeGit([['c4', 'Env']], ['.github/actions/environment-verify/action.yml']), sha: RED, now: NOW })).healable, true, 'the environment actions are the healer\'s');

  const combined = await attributeCause(github, env, { git: fakeGit([['b2', 'Tweak'], ['a1', 'Implement: X (#40)']], ['src/a.cs']), sha: RED, now: NOW });
  assert.equal(combined.kind, 'combined');
  assert.deepEqual(combined.commits.map((commit) => commit.kind), ['direct', 'spec']);
  assert.equal((await attributeCause(github, env, { git: () => ({ status: 128, stdout: '' }), sha: RED, now: NOW })).kind, 'unknown', 'missing history');
});

test('the opening red run records the cause, tells the culprit pull request, and asks a person when the healer cannot fix it', async () => {
  const github = new FakeGitHub();
  const red = { result: 'failure', log: LOG, sha: RED, runUrl: 'https://github.com/octo/repo/actions/runs/11' };
  const spec = { kind: 'spec', green: GREEN, red: RED, commits: [{ sha: 'a1', subject: 'Implement: X', kind: 'spec', pull: 40 }], healable: true };
  const opened = await recordHealth(github, env, { ...red, attribute: async () => spec });
  const issue = github.find(opened.issue);
  assert.deepEqual(parseCause(issue.body), spec);
  assert.match(issue.body, /\*\*Cause\*\*:\nIntroduced by the merge of the Spec Kit implementation #40/);
  assert.match(github.comments.find((comment) => comment.number === 40).body, new RegExp(`^\\*\\*Merging this pull request made the default branch unhealthy\\*\\* \\(#${issue.number}\\)[^\\n]*The healer fixes it forward`));
  let attributed = 0;
  await recordHealth(github, env, { ...red, runUrl: 'https://github.com/octo/repo/actions/runs/12', attribute: async () => { attributed += 1; return null; } });
  assert.equal(attributed, 0, 'later red runs keep the first cause');

  const other = new FakeGitHub();
  const tooling = { kind: 'direct', green: GREEN, red: RED, commits: [{ sha: 'c3', subject: 'Tooling', kind: 'direct' }], healable: false, protected: ['.github/workflows/x.yml'] };
  const stuck = other.find((await recordHealth(other, env, { ...red, attribute: async () => tooling })).issue);
  assert.deepEqual(stuck.labels.map((label) => label.name), [MAIN_HEALTH_LABEL, ATTENTION_LABEL]);
  assert.equal(stuck.body.match(/\*\*Recommended next step\*\*/g).length, 1);
  assert.match(stuck.body, /paths the healer may not change \(`\.github\/workflows\/x\.yml`\)/);
  assert.doesNotMatch(stuck.body, /Nothing is needed yet/);
  const failing = new FakeGitHub();
  assert.equal((await recordHealth(failing, env, { ...red, attribute: async () => { throw new Error('boom'); } })).outcome, 'opened', 'a failed attribution still opens the issue');
});