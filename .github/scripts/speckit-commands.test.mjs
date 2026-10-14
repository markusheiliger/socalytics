import assert from 'node:assert/strict';
import test from 'node:test';

import { CHECK_DIAGNOSED, CHECK_DIAGNOSING, CHECK_RUN_NAME, DIAGNOSIS_COMMENT_MARKER, parseGuidance } from './speckit-implement-core.mjs';
import { main, parseCommand, renderHelp, runCommand } from './speckit-commands.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const run = (github, body, { pull = 9, actor = 'dev' } = {}) => runCommand({
  client: github,
  env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_PULL: String(pull), SPECKIT_COMMENT_ID: '501', SPECKIT_ACTOR: actor, SPECKIT_COMMENT_BODY: body },
  log: silent,
  now: () => github.clock,
});

test('parses commands: the first word, the name, and the rest as argument', () => {
  assert.deepEqual(parseCommand('/speckit resume Use the helper\nand keep the test'), { name: 'resume', given: 'resume', argument: 'Use the helper\nand keep the test' });
  assert.deepEqual(parseCommand('  /speckit RESUME'), { name: 'resume', given: 'RESUME', argument: '' });
  assert.deepEqual(parseCommand('/speckit'), { name: 'help', given: '', argument: '' });
  assert.equal(parseCommand('/speckit deploy now').name, 'unknown');
  assert.equal(parseCommand('/speckit sync').name, 'unknown', 'resume replaced sync');
  assert.equal(parseCommand('please /speckit resume'), null);
  assert.equal(parseCommand('/speckit-gha-diagnose'), null, 'a skill name is not a command');
  assert.match(renderHelp(), /\| `\/speckit retry \[guidance\]` \| restart what stopped with a fresh attempt count, without merging the base branch for it[\s\S]*\| `\/speckit resume \[guidance\]` \| merge the base branch into the implementation branch first \(Copilot resolves conflicts\), then restart[\s\S]*\| `\/speckit fix \[guidance\]` \|[\s\S]*An amendment pull request[\s\S]*fix spec pull request[\s\S]*skill `\/speckit-gha-diagnose`/);
  assert.equal(parseCommand('/speckit retry Rerun as is').name, 'retry');
  assert.equal(parseCommand('/speckit fix Make the test robust').name, 'fix');
  assert.doesNotMatch(renderHelp(), /\/speckit sync/);
});

test('ignores people without write access and bots, reacts, and answers help and unknown commands', async () => {
  const github = diagnosisRepo();
  assert.equal((await run(github, '/speckit resume', { actor: 'stranger' })).handled, false);
  assert.equal((await run(github, '/speckit resume', { actor: 'github-actions[bot]' })).handled, false);
  assert.equal((await run(github, 'just a comment')).handled, false);
  assert.equal(github.comments.length, 0);

  await run(github, '/speckit deploy');
  assert.deepEqual(github.reactions, [{ commentId: 501, content: 'eyes' }]);
  assert.match(github.comments.at(-1).body, /^Unknown command `\/speckit deploy`\.[\s\S]*\*\*Spec Kit commands\*\*/);
  github.repo.pulls[0].head.ref = 'feature/x';
  github.repo.pulls[0].head.repo.full_name = 'octo/repo';
  const elsewhere = await run(github, '/speckit resume');
  assert.match(elsewhere.rejected, /not a Spec Kit implementation or amendment pull request/);
});

test('diagnose starts a diagnosis with the previous one as context, unless one is running', async () => {
  const github = diagnosisRepo();
  const started = await run(github, '/speckit diagnose look at research R6');
  assert.equal(started.outcome, 'started');
  assert.equal(github.repo.checkRuns.at(-1).external_id, CHECK_DIAGNOSING);
  assert.deepEqual(github.repo.runs.at(-1).inputs, { twin: '5', pull: '9', folder: 'f', notes: 'look at research R6', previous: '', check_run: String(github.repo.checkRuns.at(-1).id), mode: 'diagnose', amendment: '', round: '0' });
  assert.match(github.comments.at(-1).body, /A diagnosis was started by @dev with your notes/);
  assert.equal(github.repo.pulls.find((pull) => pull.number === 11).state, 'closed', 'a new diagnosis supersedes the open amendment');
  assert.equal(github.repo.branches['speckit-amend/f'], undefined, 'so the new run can create the amendment branch afresh');
  assert.match(github.comments.find((comment) => comment.number === 11).body, /Superseded by a new diagnosis/);

  assert.equal((await run(github, '/speckit diagnose')).outcome, 'already running');

  // The diagnose run ended without reporting (for example the agent failed): a new diagnosis may start.
  const unfinished = github.repo.checkRuns.at(-1);
  Object.assign(github.repo.runs.at(-1), { status: 'completed', conclusion: 'failure' });
  github.repo.runs.push({ id: 900, workflow: 'speckit-diagnose.lock.yml', display_title: 'Spec Kit diagnose spec #5 · Diagnose why the implementation stopped', status: 'completed', conclusion: 'failure', created_at: github.tick() });
  assert.equal((await run(github, '/speckit diagnose')).outcome, 'started');
  assert.deepEqual([unfinished.status, unfinished.conclusion, unfinished.output.title], ['completed', 'failure', 'Diagnosis did not finish']);

  Object.assign(github.repo.checkRuns.at(-1), { status: 'completed', external_id: CHECK_DIAGNOSED });
  await github.createComment(9, `${DIAGNOSIS_COMMENT_MARKER}\n**Diagnosis**: …`);
  const previous = github.comments.at(-1).id;
  const answered = await run(github, '/speckit diagnose Keep the counter write');
  assert.equal(answered.outcome, 'started');
  assert.equal(github.repo.runs.at(-1).inputs.previous, String(previous), 'the previous diagnosis is context for the answers');
  assert.match(github.comments.at(-1).body, /A new diagnosis was started/);
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

test('resume passes guidance and merges the base branch first; sync points to resume', async () => {
  const github = diagnosisRepo();
  await run(github, '/speckit resume Use one statement on every refusal path');
  assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-implement-spec.yml', { twin: '5', pull: '9', title: 'F' }], 'the implementation chain continues, named after the spec');
  const resumed = github.comments.at(-1);
  assert.equal(parseGuidance(resumed.body), 'Use one statement on every refusal path');
  assert.match(resumed.body, /^<!-- speckit-implement:resume -->[\s\S]*Implementation resumed by @dev; the base branch is merged first[\s\S]*> Use one statement on every refusal path/);

  const runs = github.repo.runs.length;
  const branch = github.repo.branches['speckit/f'];
  const sync = await run(github, '/speckit sync');
  assert.equal(sync.command, 'unknown');
  assert.match(github.comments.at(-1).body, /^`\/speckit sync` was replaced by `\/speckit resume`, which merges the base branch first[\s\S]*\*\*Spec Kit commands\*\*/);
  assert.equal(github.repo.runs.length, runs, 'sync starts nothing');
  assert.equal(github.repo.branches['speckit/f'], branch, 'the commands never merge themselves; the chain pulls the base branch');
  await assert.rejects(() => main(['other'], { env: {}, client: github }), /Usage/);
});

test('retry restarts what stopped with guidance, on the branch as it is', async () => {
  const github = diagnosisRepo();
  const result = await run(github, '/speckit retry Rerun the verification unchanged');
  assert.equal(result.outcome, 'retried');
  assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-implement-spec.yml', { twin: '5', pull: '9', title: 'F' }]);
  const retried = github.comments.at(-1);
  assert.match(retried.body, /^<!-- speckit-implement:retry -->[\s\S]*Implementation retried by @dev; what stopped restarts with a fresh attempt count, without merging the base branch for it\.[\s\S]*> Rerun the verification unchanged/);
  assert.doesNotMatch(retried.body, /speckit-implement:resume/, 'a retry is no resume, so the chain does not merge the base branch for it');
  assert.equal(parseGuidance(retried.body), 'Rerun the verification unchanged');
});

test('fix starts the authoring of a fix spec once, and refuses while one is pending', async () => {
  const github = diagnosisRepo();
  const started = await run(github, '/speckit fix Make UploadSessionExpiryTests independent of runner load');
  assert.equal(started.outcome, 'started');
  const check = github.repo.checkRuns.at(-1);
  assert.deepEqual([check.name, check.external_id, check.status], ['Spec Kit fix spec', 'speckit:fix-authoring', 'in_progress'], 'its own check run, apart from the implementation');
  const dispatched = github.repo.runs.at(-1);
  assert.equal(dispatched.workflow, 'speckit-fix.lock.yml');
  assert.deepEqual({ ...dispatched.inputs, stamp: 'x' }, { twin: '5', pull: '9', folder: 'f', notes: 'Make UploadSessionExpiryTests independent of runner load', check_run: String(check.id), mode: 'author', amendment: '', round: '0', stalls: '0', findings: '', stamp: 'x' });
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
  assert.match((await run(github, '/speckit resume', { pull: 11 })).rejected, /no open implementation pull request/);
  const noTwin = diagnosisRepo();
  noTwin.repo.pulls[0].body = 'no closing keyword';
  assert.match((await run(noTwin, '/speckit resume')).rejected, /does not name its twin/);
  await noTwin.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'completed', external_id: CHECK_DIAGNOSED });
});
