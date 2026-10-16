import assert from 'node:assert/strict';
import test from 'node:test';

import { ATTENTION_LABEL, CHECK_DIAGNOSED, CHECK_RUN_NAME, parseGuidance, renderRepairMarker } from './speckit-implement-core.mjs';
import { main, parseCommand, renderHelp, runCommand } from './speckit-commands.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const run = (github, body, { pull = 9, actor = 'dev' } = {}) => runCommand({
  client: github,
  env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_PULL: String(pull), SPECKIT_COMMENT_ID: '501', SPECKIT_ACTOR: actor, SPECKIT_COMMENT_BODY: body },
  log: silent,
  now: () => github.clock,
});

test('parses commands: the first word, the name, and the rest as argument', () => {
  assert.deepEqual(parseCommand('/speckit retry Use the helper\nand keep the test'), { name: 'retry', given: 'retry', argument: 'Use the helper\nand keep the test' });
  assert.deepEqual(parseCommand('/speckit'), { name: 'help', given: '', argument: '' });
  assert.equal(parseCommand('/speckit deploy now').name, 'unknown');
  assert.equal(parseCommand('/speckit resume').name, 'unknown');
  assert.equal(parseCommand('/speckit sync').name, 'unknown');
  assert.equal(parseCommand('/speckit diagnose').name, 'unknown');
  assert.equal(parseCommand('please /speckit retry'), null);
  assert.equal(parseCommand('/speckit-gha-diagnose'), null, 'a skill name is not a command');
  assert.match(renderHelp(), /\| `\/speckit retry \[guidance\]` \| restart what stopped with a fresh attempt count while preserving the current phase base[\s\S]*\| `\/speckit fix \[guidance\]` \|[\s\S]*An amendment pull request[\s\S]*fix spec pull request[\s\S]*skill `\/speckit-gha-diagnose`/);
  assert.doesNotMatch(renderHelp(), /\/speckit resume/);
  assert.doesNotMatch(renderHelp(), /\/speckit diagnose/);
  assert.equal(parseCommand('/speckit retry Rerun as is').name, 'retry');
  assert.equal(parseCommand('/speckit fix Make the test robust').name, 'fix');
  assert.doesNotMatch(renderHelp(), /\/speckit sync/);
});

test('ignores people without write access and bots, reacts, and answers help and unknown commands', async () => {
  const github = diagnosisRepo();
  assert.equal((await run(github, '/speckit retry', { actor: 'stranger' })).handled, false);
  assert.equal((await run(github, '/speckit retry', { actor: 'github-actions[bot]' })).handled, false);
  assert.equal((await run(github, 'just a comment')).handled, false);
  assert.equal(github.comments.length, 0);

  await run(github, '/speckit deploy');
  assert.deepEqual(github.reactions, [{ commentId: 501, content: 'eyes' }]);
  assert.match(github.comments.at(-1).body, /^Unknown command `\/speckit deploy`\.[\s\S]*\*\*Spec Kit commands\*\*/);
  github.repo.pulls[0].head.ref = 'feature/x';
  github.repo.pulls[0].head.repo.full_name = 'octo/repo';
  const elsewhere = await run(github, '/speckit retry');
  assert.match(elsewhere.rejected, /not a Spec Kit implementation or amendment pull request/);
});

test('apply, revise, and discard are no commands: the amendment pull request is merged, commented, or closed', async () => {
  const github = diagnosisRepo();
  for (const name of ['apply', 'revise', 'discard']) {
    assert.equal(parseCommand(`/speckit ${name}`).name, 'unknown');
    await run(github, `/speckit ${name}`);
    assert.match(github.comments.at(-1).body, new RegExp(`^Unknown command \`/speckit ${name}\`[\\s\\S]*merge it to apply it, comment or submit a review`));
  }
  assert.equal(github.repo.merges.length, 0);
});

test('removed resume and sync commands show help without starting work', async () => {
  const github = diagnosisRepo();
  for (const name of ['resume', 'sync']) {
    const result = await run(github, `/speckit ${name}`);
    assert.equal(result.command, 'unknown');
    assert.match(github.comments.at(-1).body, new RegExp(`^Unknown command \`/speckit ${name}\`\\.[\\s\\S]*\\*\\*Spec Kit commands\\*\\*`));
  }
  assert.equal(github.repo.runs.length, 0);
  await assert.rejects(() => main(['other'], { env: {}, client: github }), /Usage/);
});

test('retry restarts what stopped with guidance, on the branch as it is', async () => {
  const github = diagnosisRepo();
  const result = await run(github, '/speckit retry Rerun the verification unchanged');
  assert.equal(result.outcome, 'retried');
  assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-implement-spec.yml', { twin: '5', pull: '9', title: 'F' }]);
  const retried = github.comments.at(-1);
  assert.match(retried.body, /^<!-- speckit-implement:retry -->[\s\S]*Implementation retried by @dev; what stopped restarts with a fresh attempt count while preserving the current phase base\.[\s\S]*> Rerun the verification unchanged/);
  assert.equal(parseGuidance(retried.body), 'Rerun the verification unchanged');
});

test('pending repair retries distinguish verification-only and guided work without claiming fresh attempts', async () => {
  for (const guidance of ['', 'Inspect the evidence', '--force', '--force Inspect the evidence']) {
    const github = diagnosisRepo();
    await github.createComment(9, renderRepairMarker({ id: 'phase:failure', state: 'pending', scope: 'phase', at: github.tick() }));
    await run(github, `/speckit retry${guidance ? ` ${guidance}` : ''}`);
    const comment = github.comments.at(-1).body;
    assert.match(comment, /pending phase verification/);
    const actualGuidance = guidance.replace(/^--force(?:\s+|$)/, '');
    assert.match(comment, actualGuidance ? /runs a repair agent with the guidance/ : /reverifies without running a repair agent/);
    assert.match(comment, /Does not merge `main`/);
    assert.doesNotMatch(comment, /fresh attempt count|convergence|not repeated by itself/);
    assert.equal(parseGuidance(comment) ?? '', actualGuidance);
  }
});

test('restarts that leave a stopped implementation unchanged are refused, unless something changed or the person insists', async () => {
  const github = diagnosisRepo({ amendment: false });
  github.repo.branches.main = 'sha-main';
  const pull = github.repo.pulls[0];
  const stop = () => {
    pull.labels = [{ name: ATTENTION_LABEL }];
  };
  const restarts = () => github.comments.filter((comment) => /^<!-- speckit-implement:retry -->/.test(comment.body)).length;

  // Restarting while the implementation runs, or once after a stop, is never refused.
  assert.equal((await run(github, '/speckit retry Rename the migration')).outcome, 'retried');
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:retry -->\n<!-- speckit-implement:restart-state \{"head":"sha-impl","base":"sha-main"\} -->/);
  stop();
  assert.equal((await run(github, '/speckit retry Rename it to 0019')).outcome, 'retried');
  assert.equal(pull.labels.length, 0, 'the restart clears the attention label');

  // Stopped again on the same head after two retries: the third is refused.
  stop();
  const before = [restarts(), github.repo.runs.length];
  assert.equal((await run(github, '/speckit retry After pushing the rename')).outcome, 'refused');
  assert.match(github.comments.at(-1).body, /`\/speckit retry` was not run[\s\S]*`\/speckit retry --force <guidance>`/);
  assert.deepEqual([restarts(), github.repo.runs.length], before, 'nothing was restarted');

  // New commits on the base branch do not change the current phase's retry guard.
  github.repo.branches.main = 'sha-main-2';
  assert.equal((await run(github, '/speckit retry Again')).outcome, 'refused');

  // The person insists: --force restarts, and is not part of the guidance.
  assert.equal((await run(github, '/speckit retry --force The registry outage is over')).outcome, 'retried');
  assert.equal(parseGuidance(github.comments.at(-1).body), 'The registry outage is over');

  // A push moves the head: restarts count from there.
  stop();
  pull.head.sha = 'sha-pushed';
  github.repo.branches['speckit/f'] = 'sha-pushed';
  assert.equal((await run(github, '/speckit retry')).outcome, 'retried');
});

test('fix starts the authoring of a fix spec once, and refuses while one is pending', async () => {
  const github = diagnosisRepo();
  const started = await run(github, '/speckit fix Make UploadSessionExpiryTests independent of runner load');
  assert.equal(started.outcome, 'started');
  const check = github.repo.checkRuns.at(-1);
  assert.deepEqual([check.name, check.external_id, check.status], ['Spec Kit fix spec', 'speckit:fix-authoring', 'in_progress'], 'its own check run, apart from the implementation');
  const dispatched = github.repo.runs.at(-1);
  assert.equal(dispatched.workflow, 'speckit-fix.lock.yml');
  assert.deepEqual({ ...dispatched.inputs, stamp: 'x' }, { twin: '5', pull: '9', folder: 'f', guidance: 'Make UploadSessionExpiryTests independent of runner load', check_run: String(check.id), mode: 'author', amendment: '', round: '0', stalls: '0', findings: '', stamp: 'x' });
  assert.match(dispatched.inputs.stamp, /^\d{8}-\d{6}$/);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-fix:requested \{"by":"dev"[\s\S]*\*\*A fix spec is being authored\*\*, requested by @dev[\s\S]*> Make UploadSessionExpiryTests/);
  assert.equal(parseGuidance(github.comments.at(-1).body), 'Make UploadSessionExpiryTests independent of runner load');

  const again = await run(github, '/speckit fix');
  assert.equal(again.outcome, 'refused');
  assert.match(github.comments.at(-1).body, /^`\/speckit fix` cannot start: a fix spec is already being authored\./);
});
test('commands need an open implementation pull request with a twin', async () => {
  const github = diagnosisRepo();
  github.repo.pulls[0].state = 'closed';
  assert.match((await run(github, '/speckit retry', { pull: 11 })).rejected, /no open implementation pull request/);
  const noTwin = diagnosisRepo();
  noTwin.repo.pulls[0].body = 'no closing keyword';
  assert.match((await run(noTwin, '/speckit retry')).rejected, /does not name its twin/);
  await noTwin.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'completed', external_id: CHECK_DIAGNOSED });
});
