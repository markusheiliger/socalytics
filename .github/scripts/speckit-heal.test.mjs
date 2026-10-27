import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { ATTENTION_LABEL } from './speckit-implement-core.mjs';
import { MAIN_HEALTH_LABEL, renderCause } from './speckit-main-health.mjs';
import {
  HEAL_CHECK_NAME, HEAL_LABEL, HEAL_STOP_MARKER, MAX_HEAL_ATTEMPTS, decideHeal, healAttempts, healthBlock, healthState, parseNameStatus, renderCauseGuidance,
  renderHealAttempt, renderHealPrompt, runDecide, runLand, runReport, validateHealChange,
} from './speckit-heal.mjs';
import { FakeGitHub } from './speckit-test-helpers.mjs';

const env = { SPECKIT_DEFAULT_BRANCH: 'trunk', GITHUB_REPOSITORY: 'octo/repo', GITHUB_RUN_ID: '5' };
const BASE = 'b'.repeat(40);
const openIssue = (github, body = '<!-- speckit:main-health -->') => github.createIssue({ title: 'The default branch is unhealthy', body, labels: [MAIN_HEALTH_LABEL] });
const attempt = (github, issue, data) => github.createComment(issue, `${renderHealAttempt(data)}\n**Healing attempt ${data.attempt}**`);
const dispatched = (github) => github.repo.runs.filter((run) => run.workflow === 'main-heal.yml');

function outputsOf(file) {
  return Object.fromEntries(readFileSync(file, 'utf8').trim().split('\n').filter(Boolean).map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]));
}

test('the default branch is unhealthy while a health issue or a healing pull request is open', async () => {
  const github = new FakeGitHub();
  assert.equal((await healthState(github, env)).unhealthy, false);
  const issue = await openIssue(github);
  assert.deepEqual(await healthState(github, env), { unhealthy: true, issue: issue.number, heal: null, gate: 'on' });
  assert.equal((await healthState(github, { ...env, SPECKIT_HEALTH_GATE: 'false' })).unhealthy, false, 'the gate can be turned off');
  github.find(issue.number).state = 'closed';
  const pull = await github.createPullRequest({ title: 'Heal', head: 'main-heal/1-1', base: 'trunk', body: '', draft: true });
  assert.deepEqual(await healthState(github, env), { unhealthy: true, issue: null, heal: pull.number, gate: 'on' }, 'until the healing pull request is merged or closed');
  assert.deepEqual(healthBlock({ issue: 3, heal: null }).key, { health: 3 });
  assert.match(healthBlock({ issue: 3 }).lines[0], /^\*\*Blocked\*\* while the default branch is unhealthy: its full verification fails \(#3\)\. [^\n]*Nothing is needed on this pull request\.$/);
});

test('decides the next healing attempt, waits for an open healing pull request, and stops after the last attempt', async () => {
  const github = new FakeGitHub();
  assert.equal((await decideHeal(github, env)).action, 'none', 'healthy');
  const issue = await openIssue(github);
  const first = await decideHeal(github, env);
  assert.deepEqual([first.action, first.attempt, first.branch], ['heal', 1, `main-heal/${issue.number}-1`]);

  await attempt(github, issue.number, { attempt: 1, outcome: 'failed to land', reasons: 'no change' });
  const pull = await github.createPullRequest({ title: 'Heal', head: `main-heal/${issue.number}-2`, base: 'trunk', body: '', draft: true });
  assert.equal((await decideHeal(github, env)).action, 'none', 'a healing pull request is open');
  github.repo.pulls.find((item) => item.number === pull.number).state = 'closed';
  await attempt(github, issue.number, { attempt: 2, outcome: 'verifying', pull: pull.number });
  await attempt(github, issue.number, { attempt: 2, outcome: 'failed verification', pull: pull.number });
  assert.deepEqual(healAttempts(github.comments).map((item) => [item.attempt, item.outcome]), [[1, 'failed to land'], [2, 'failed verification']]);
  const third = await decideHeal(github, env);
  assert.deepEqual([third.action, third.attempt, third.previous.length], ['heal', 3, 2]);

  await attempt(github, issue.number, { attempt: 3, outcome: 'failed verification' });
  assert.equal((await decideHeal(github, env)).action, 'stop');
  assert.equal((await decideHeal(github, env)).action, 'stop');
  assert.equal(github.comments.filter((comment) => comment.body.includes(HEAL_STOP_MARKER)).length, 1, 'asks a person once');
  assert.match(github.comments.find((comment) => comment.body.includes(HEAL_STOP_MARKER)).body, new RegExp(`could not fix it in ${MAX_HEAL_ATTEMPTS} attempts[\\s\\S]*\\*\\*Recommended next step\\*\\*`));
  assert.ok(github.find(issue.number).labels.some((label) => label.name === ATTENTION_LABEL));

  github.commentAuthor = 'mallory';
  const other = new FakeGitHub();
  const forged = await openIssue(other);
  other.commentAuthor = 'mallory';
  await attempt(other, forged.number, { attempt: 1, outcome: 'failed' });
  assert.equal(healAttempts(other.comments).length, 0, 'only the bot records attempts');
});

test('a cause the healer may not change is left to a person', async () => {
  const github = new FakeGitHub();
  await openIssue(github, `<!-- speckit:main-health -->\n${renderCause({ kind: 'direct', healable: false, protected: ['.github/workflows/x.yml'], commits: [] })}`);
  const decision = await decideHeal(github, env);
  assert.equal(decision.action, 'none');
  assert.match(decision.reason, /paths the healer may not change/);
});

test('decide reports the attempt, its cause, and the evidence run', async () => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'heal-'));
  try {
    const github = new FakeGitHub();
    const cause = { kind: 'spec', green: 'g', red: 'r', commits: [{ sha: 'a1', subject: 'X', kind: 'spec', pull: 4 }], healable: true };
    await openIssue(github, `<!-- speckit:main-health -->\n${renderCause(cause)}`);
    github.repo.runs.push({ id: 70, workflow: 'main-health.yml', status: 'completed', conclusion: 'failure', head_branch: 'trunk', created_at: new Date().toISOString() });
    const output = path.join(dir, 'out.txt');
    writeFileSync(output, '');
    await runDecide({ client: github, env: { ...env, GITHUB_OUTPUT: output }, log: () => {} });
    const outputs = outputsOf(output);
    assert.deepEqual([outputs.action, outputs.attempt, outputs.run, JSON.parse(outputs.cause).kind, outputs.previous], ['heal', '1', '70', 'spec', '[]']);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('the prompt tells the agent the cause and how to treat it', () => {
  const commit = { sha: 'a'.repeat(40), subject: 'Implement: X', kind: 'spec', pull: 4 };
  assert.match(renderCauseGuidance({ kind: 'spec', green: 'g', red: 'r', commits: [commit] }, 'ev').join('\n'), /Never revert the implementation/);
  assert.match(renderCauseGuidance({ kind: 'direct', green: 'g', red: 'r', commits: [{ ...commit, kind: 'direct', pull: undefined }] }, 'ev').join('\n'), /\(direct push\)[\s\S]*Reverting it [^\n]*is acceptable/);
  assert.match(renderCauseGuidance({ kind: 'drift', green: 'g' }, 'ev').join('\n'), /outside it[^\n]*do not look for a culprit commit/);
  assert.match(renderCauseGuidance({ kind: 'combined', green: 'g', red: 'r', commits: [commit, commit] }, 'ev').join('\n'), /2 changes[\s\S]*how they interact/);
  assert.match(renderCauseGuidance(null, 'ev')[0], /unknown/);
  const prompt = renderHealPrompt({ issue: 9, attempt: 2, evidence: 'ev', failed: [{ shard: '2/4', outcome: 'failure', checks: 'platform' }], previous: [{ attempt: 1, outcome: 'failed verification', reasons: 'x' }], cause: { kind: 'spec', green: 'g', red: 'r', commits: [commit] } });
  assert.match(prompt, /health issue #9/);
  assert.match(prompt, /ev\/cause\.diff holds the diff from the last green verification \(g\) to the failing commit \(r\)/);
  assert.match(prompt, /Failing parts: shard 2\/4 \(failure\): platform\./);
  assert.match(prompt, /attempt 1 failed verification \(x\)\. Do not repeat them\./);
  assert.match(prompt, /This is attempt 2 of 3\./);
});

test('a healing change may not touch the tooling or specs, delete tests, or disable them', () => {
  assert.deepEqual(validateHealChange({ changes: [], patch: '' }), ['the agent made no change']);
  assert.deepEqual(validateHealChange({ changes: parseNameStatus('M\tsrc/platform/a.cs\nM\t.github/actions/environment-verify/action.yml\n'), patch: '+var x = 1;\n' }), []);
  const reasons = validateHealChange({
    changes: parseNameStatus('M\t.github/workflows/x.yml\nM\t.specify/memory/constitution.md\nM\tspecs/a/tasks.md\nD\tsrc/platform/Tests/X.Tests/ATests.cs\nR100\tsrc/a.ts\tsrc/b.ts\n'),
    patch: '+++ b/src/x.cs\n+    [Fact(Skip = "flaky")]\n+  it.skip(\'works\', () => {});\n+@pytest.mark.skip\n',
  });
  assert.ok(reasons.includes('.github/workflows/x.yml may not be changed by the healer'));
  assert.ok(reasons.includes('.specify/memory/constitution.md may not be changed by the healer'));
  assert.ok(reasons.includes('specs/a/tasks.md may not be changed by the healer'));
  assert.ok(reasons.includes('src/platform/Tests/X.Tests/ATests.cs is a test file and may not be deleted'));
  assert.match(reasons.find((reason) => reason.startsWith('the change disables tests')), /Fact\(Skip/);
  assert.deepEqual(parseNameStatus('R100\tsrc/a.ts\tsrc/b.ts'), [{ status: 'R100', path: 'src/b.ts' }]);
});

function landDir({ patch = 'diff --git a/src/a.cs b/src/a.cs\n+fixed\n', changes = 'M\tsrc/a.cs\n', base = BASE, exit = '0' } = {}) {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'heal-land-'));
  writeFileSync(path.join(dir, 'patch.diff'), patch);
  writeFileSync(path.join(dir, 'changes.txt'), changes);
  writeFileSync(path.join(dir, 'base.txt'), `${base}\n`);
  writeFileSync(path.join(dir, 'summary.md'), 'Root cause: a typo. Fixed it.');
  writeFileSync(path.join(dir, 'agent-exit.txt'), exit);
  return dir;
}

const recordingGit = (calls, { fail = null } = {}) => (args) => {
  calls.push(args.join(' '));
  if (fail && args[0] === fail) return { status: 1, stdout: '', stderr: `${fail} refused` };
  return { status: 0, stdout: args[0] === 'rev-parse' ? `${'h'.repeat(40)}\n` : '', stderr: '' };
};

test('land commits a valid change to the healing branch and opens a draft healing pull request', async () => {
  const dir = landDir();
  try {
    const github = new FakeGitHub();
    const issue = await openIssue(github);
    const calls = [];
    const output = path.join(dir, 'out.txt');
    writeFileSync(output, '');
    const result = await runLand({ client: github, env: { ...env, GITHUB_OUTPUT: output, SPECKIT_HEAL_ISSUE: String(issue.number), SPECKIT_HEAL_ATTEMPT: '1', SPECKIT_HEAL_BRANCH: `main-heal/${issue.number}-1` }, git: recordingGit(calls), resultDir: dir, log: () => {} });
    assert.equal(result.landed, true);
    assert.deepEqual(calls.map((call) => call.split(' ')[0] === '-c' ? 'commit' : call.split(' ')[0]), ['checkout', 'apply', 'commit', 'push', 'rev-parse']);
    assert.equal(calls[0], `checkout -B main-heal/${issue.number}-1 ${BASE}`);
    const pull = github.repo.pulls.at(-1);
    assert.deepEqual([pull.head.ref, pull.base.ref, pull.draft], [`main-heal/${issue.number}-1`, 'trunk', true]);
    assert.match(pull.body, /\[this Main heal run\]\(https:\/\/github\.com\/octo\/repo\/actions\/runs\/5\)[\s\S]*Root cause: a typo\.[\s\S]*never merged automatically/);
    const check = github.repo.checkRuns.find((run) => run.name === HEAL_CHECK_NAME);
    assert.deepEqual([check?.head_sha, check?.status, check?.details_url], ['h'.repeat(40), 'in_progress', 'https://github.com/octo/repo/actions/runs/5']);
    assert.ok(github.labels.has(HEAL_LABEL));
    assert.deepEqual(healAttempts(github.comments), [{ attempt: 1, outcome: 'verifying', pull: pull.number }]);
    assert.equal(outputsOf(output).landed, 'true');
    assert.equal(dispatched(github).length, 0);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('land records a change that cannot land as a failed attempt and starts the next one', async () => {
  for (const [label, options, gitOptions, expected] of [
    ['forbidden path', { changes: 'M\t.github/scripts/x.mjs\n' }, {}, /\.github\/scripts\/x\.mjs may not be changed by the healer/],
    ['agent failure', { exit: '1' }, {}, /the agent ended with exit code 1/],
    ['patch conflict', {}, { fail: 'apply' }, /git apply [^\n]*failed: apply refused/],
  ]) {
    const dir = landDir(options);
    try {
      const github = new FakeGitHub();
      const issue = await openIssue(github);
      const calls = [];
      const result = await runLand({ client: github, env: { ...env, SPECKIT_HEAL_ISSUE: String(issue.number), SPECKIT_HEAL_ATTEMPT: '1', SPECKIT_HEAL_BRANCH: 'main-heal/x-1' }, git: recordingGit(calls, gitOptions), resultDir: dir, log: () => {} });
      assert.equal(result.landed, false, label);
      assert.ok(!calls.some((call) => call.startsWith('push')), `${label}: nothing is pushed`);
      assert.equal(github.repo.pulls.length, 0, label);
      assert.match(github.comments.at(-1).body, expected, label);
      assert.equal(healAttempts(github.comments)[0].outcome, 'failed to land', label);
      assert.equal(dispatched(github).length, 1, `${label}: the next attempt starts`);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  }
});

test('report hands a passed healing pull request to a person and closes a failed one', async () => {
  const github = new FakeGitHub();
  const issue = await openIssue(github);
  github.repo.branches['main-heal/1-1'] = 'h1';
  const pull = await github.createPullRequest({ title: 'Heal', head: 'main-heal/1-1', base: 'trunk', body: '', draft: true });
  await github.createCheckRun({ name: HEAL_CHECK_NAME, head_sha: 'h1', status: 'in_progress' });
  const reportEnv = { ...env, SPECKIT_HEAL_ISSUE: String(issue.number), SPECKIT_HEAL_ATTEMPT: '1', SPECKIT_HEAL_PULL: String(pull.number), SPECKIT_HEAL_REVIEWER: 'owner' };
  assert.equal((await runReport({ client: github, env: { ...reportEnv, SPECKIT_HEAL_RESULT: 'success' }, log: () => {} })).outcome, 'ready');
  assert.deepEqual(github.repo.checkRuns.map((run) => [run.status, run.conclusion]), [['completed', 'success']], 'the check run is completed, not duplicated');
  const ready = github.repo.pulls.find((item) => item.number === pull.number);
  assert.equal(ready.draft, false);
  assert.deepEqual(github.repo.reviewRequests, [{ number: pull.number, reviewers: ['owner'] }]);
  assert.ok(ready.labels.some((label) => label.name === ATTENTION_LABEL));
  assert.match(github.comments.find((comment) => comment.number === pull.number).body, /passed the full verification[\s\S]*\*\*Recommended next step\*\*: Review and merge this pull request\./);
  assert.equal(healAttempts(github.comments)[0].outcome, 'ready for review');
  assert.equal(dispatched(github).length, 0, 'never merges, never starts another attempt');

  const other = new FakeGitHub();
  const otherIssue = await openIssue(other);
  other.repo.branches['main-heal/2-1'] = 'h2';
  const failing = await other.createPullRequest({ title: 'Heal', head: 'main-heal/2-1', base: 'trunk', body: '', draft: true });
  const dir = mkdtempSync(path.join(os.tmpdir(), 'heal-report-'));
  try {
    writeFileSync(path.join(dir, 'verification.log'), '=== Shard 3/4 (failure): platform end-to-end ===\n');
    const result = await runReport({ client: other, env: { ...reportEnv, SPECKIT_HEAL_ISSUE: String(otherIssue.number), SPECKIT_HEAL_PULL: String(failing.number), SPECKIT_HEAL_RESULT: 'failure', SPECKIT_HEAL_LOG: path.join(dir, 'verification.log') }, log: () => {} });
    assert.equal(result.outcome, 'failed');
    assert.deepEqual(other.repo.checkRuns.map((run) => [run.name, run.head_sha, run.conclusion]), [[HEAL_CHECK_NAME, 'h2', 'failure']]);
    assert.equal(other.repo.pulls.find((item) => item.number === failing.number).state, 'closed');
    assert.equal(other.repo.branches['main-heal/2-1'], undefined);
    assert.deepEqual(healAttempts(other.comments).map((item) => [item.outcome, item.reasons]), [['failed verification', 'shard 3/4 (failure): platform end-to-end']]);
    assert.equal(dispatched(other).length, 1, 'the next attempt starts');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
