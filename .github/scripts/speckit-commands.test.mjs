import assert from 'node:assert/strict';
import test from 'node:test';

import { ATTENTION_LABEL, CHECK_DIAGNOSED, CHECK_RUN_NAME, parseGuidance, renderRepairMarker } from './speckit-implement-core.mjs';
import { main, parseCommand, renderHelp, runCommand, runCommandTarget } from './speckit-commands.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const run = (github, body, { pull = 9, actor = 'dev', env = {} } = {}) => runCommand({
  client: github,
  env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_PULL: String(pull), SPECKIT_COMMENT_ID: '501', SPECKIT_ACTOR: actor, SPECKIT_COMMENT_BODY: body, ...env },
  log: silent,
  now: () => github.clock,
});

test('parses commands: the first word, the name, and the rest as flags and guidance', () => {
  assert.deepEqual(parseCommand('/speckit retry Use the helper\nand keep the test'), { name: 'retry', given: 'retry', argument: 'Use the helper\nand keep the test', flags: [], unknownFlags: [], guidance: 'Use the helper\nand keep the test' });
  assert.deepEqual(parseCommand('/speckit'), { name: 'help', given: '', argument: '', flags: [], unknownFlags: [], guidance: '' });
  assert.equal(parseCommand('/speckit deploy now').name, 'unknown');
  assert.equal(parseCommand('/speckit sync').name, 'sync');
  assert.equal(parseCommand('/speckit resume').name, 'unknown');
  assert.equal(parseCommand('/speckit diagnose').name, 'unknown');
  assert.equal(parseCommand('please /speckit retry'), null);
  assert.equal(parseCommand('/speckit-gha-diagnose'), null, 'a skill name is not a command');
  assert.match(renderHelp(), /`\/speckit <command> \[flags\] \[guidance\]`/);
  assert.match(renderHelp(), /\| `\/speckit retry \[--force\] \[guidance\]` \| restart what stopped with a fresh attempt count while preserving the current phase base[\s\S]*\| `\/speckit fix \[guidance\]` \|[\s\S]*An amendment pull request[\s\S]*fix spec pull request[\s\S]*skill `\/speckit-gha-diagnose`/);
  assert.doesNotMatch(renderHelp(), /\/speckit resume/);
  assert.doesNotMatch(renderHelp(), /\/speckit diagnose/);
  assert.equal(parseCommand('/speckit retry Rerun as is').name, 'retry');
  assert.equal(parseCommand('/speckit fix Make the test robust').name, 'fix');
  assert.match(renderHelp(), /\/speckit sync \[guidance\]/);
});

test('flags come right after the command, tolerate case and trailing punctuation, and end at the guidance or `--`', () => {
  for (const body of ['/speckit retry --force', '/speckit retry --force;', '/speckit retry --FORCE,', '/speckit retry\n--force.', '/speckit retry --force --force']) {
    assert.deepEqual([parseCommand(body).flags, parseCommand(body).unknownFlags, parseCommand(body).guidance], [['--force'], [], ''], body);
  }
  const guided = parseCommand('/speckit retry --force; Update both navigation specs --force');
  assert.deepEqual([guided.flags, guided.guidance], [['--force'], 'Update both navigation specs --force']);
  assert.deepEqual([parseCommand('/speckit retry Do it --force').flags, parseCommand('/speckit retry Do it --force').guidance], [[], 'Do it --force'], 'flags only lead');
  assert.deepEqual([parseCommand('/speckit retry -- --force is guidance').flags, parseCommand('/speckit retry -- --force is guidance').guidance], [[], '--force is guidance']);
  assert.deepEqual(parseCommand('/speckit retry --forse Rerun').unknownFlags, ['--forse']);
  assert.deepEqual([parseCommand('/speckit sync --force').flags, parseCommand('/speckit sync --force').unknownFlags], [[], ['--force']], 'flags depend on the command');
  assert.deepEqual(parseCommand('/speckit fix ---- not a flag').guidance, '---- not a flag');
});

test('unsupported flags are refused with the command usage instead of becoming guidance', async () => {
  const github = diagnosisRepo();
  const result = await run(github, '/speckit sync --force Keep both migrations');
  assert.equal(result.rejected, 'unsupported flag');
  assert.match(github.comments.at(-1).body, /^\*\*`\/speckit sync` was not run\*\*: it does not support `--force`\. It supports no flags\.[\s\S]*`\/speckit sync \[guidance\]`/);
  await run(github, '/speckit retry --forse Rerun');
  assert.match(github.comments.at(-1).body, /does not support `--forse`\. Its flags: `--force`: restart even after/);
  assert.equal(github.repo.runs.length, 0, 'nothing was started');
  assert.equal(github.comments.some((comment) => /^<!-- speckit-implement:(retry|sync) -->/.test(comment.body)), false);
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

test('commands on amendments acquire their implementation publication lock and reject a changed lock target', async () => {
  const github = diagnosisRepo();
  const target = await runCommandTarget({
    client: github,
    env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_PULL: '11', SPECKIT_COMMENT_BODY: '/speckit sync' },
  });
  assert.equal(target, 9);
  assert.equal((await run(github, '/speckit sync', { pull: 11, env: { SPECKIT_PUBLICATION_PULL: '11' } })).rejected, 'publication target changed');
  assert.equal(github.comments.some((comment) => comment.body.startsWith('<!-- speckit-implement:sync -->')), false);
  assert.equal((await run(github, '/speckit sync', { pull: 11, env: { SPECKIT_PUBLICATION_PULL: String(target) } })).outcome, 'sync requested');
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

test('sync persists a required barrier before dispatching', async () => {
  const github = diagnosisRepo();
  const runs = github.repo.runs.length;
  const branch = github.repo.branches['speckit/f'];
  const sync = await run(github, '/speckit sync');
  assert.equal(sync.command, 'sync');
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:sync -->[\s\S]*Required synchronization requested by @dev/);
  assert.equal(github.repo.runs.length, runs + 1);
  assert.equal(github.repo.branches['speckit/f'], branch, 'the commands never merge themselves; the chain pulls the base branch');
  await assert.rejects(() => main(['other'], { env: {}, client: github }), /Usage/);
});

test('removed resume and diagnose commands show help without starting work', async () => {
  const github = diagnosisRepo();
  for (const name of ['resume', 'diagnose']) {
    const result = await run(github, `/speckit ${name}`);
    assert.equal(result.command, 'unknown');
    assert.match(github.comments.at(-1).body, new RegExp(`^Unknown command \`/speckit ${name}\`\\.[\\s\\S]*\\*\\*Spec Kit commands\\*\\*`));
  }
  assert.equal(github.repo.runs.length, 0);
  await assert.rejects(() => main(['other'], { env: {}, client: github }), /Usage/);
});

test('sync bypasses unchanged retry refusal, uses the actual base, and survives dispatch failure', async () => {
  const github = diagnosisRepo({ amendment: false });
  const pull = github.repo.pulls[0];
  pull.base.ref = 'release';
  github.repo.branches.release = 'base-release';
  await run(github, '/speckit retry');
  await run(github, '/speckit retry');
  pull.labels = [{ name: ATTENTION_LABEL }];
  assert.equal((await run(github, '/speckit retry')).outcome, 'refused');
  github.dispatchWorkflow = async () => { throw new Error('dispatch unavailable'); };
  assert.equal((await run(github, '/speckit sync Keep both migrations')).outcome, 'sync requested');
  const comment = github.comments.at(-1).body;
  assert.match(comment, /merge `release` and pass full verification/);
  assert.equal(parseGuidance(comment), 'Keep both migrations');
  assert.equal(pull.labels.length, 0);
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
  assert.match(github.comments.at(-1).body, /`\/speckit retry` was not run[\s\S]*\*\*Recommended next step\*\*: Review the latest diagnosis on #9[\s\S]*push it to `speckit\/f`/);
  assert.equal(github.comments.at(-1).body.match(/\*\*Recommended next step\*\*/g)?.length, 1);
  assert.doesNotMatch(github.comments.at(-1).body, /<guidance>|<what to fix>/);
  assert.match(github.comments.at(-1).body, /<summary>Other options<\/summary>[\s\S]*`\/speckit retry --force`/);
  assert.deepEqual([restarts(), github.repo.runs.length], before, 'nothing was restarted');

  // New commits on the base branch do not change the current phase's retry guard.
  github.repo.branches.main = 'sha-main-2';
  assert.equal((await run(github, '/speckit retry Again')).outcome, 'refused');

  // The person insists: --force restarts, and is not part of the guidance, even with trailing punctuation.
  assert.equal((await run(github, '/speckit retry --force The registry outage is over')).outcome, 'retried');
  assert.equal(parseGuidance(github.comments.at(-1).body), 'The registry outage is over');
  stop();
  assert.equal((await run(github, '/speckit retry --force; Still over')).outcome, 'retried');
  assert.equal(parseGuidance(github.comments.at(-1).body), 'Still over');

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
