import assert from 'node:assert/strict';
import test from 'node:test';

import { CHECK_DIAGNOSED, CHECK_DIAGNOSING, CHECK_RUN_NAME, DIAGNOSIS_COMMENT_MARKER, parseGuidance } from './speckit-implement-core.mjs';
import { main, parseCommand, renderHelp, runCommand } from './speckit-commands.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const run = (github, body, { pull = 9, actor = 'dev' } = {}) => runCommand({
  client: github,
  env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_PULL: String(pull), SPECKIT_COMMENT_ID: '501', SPECKIT_ACTOR: actor, SPECKIT_COMMENT_BODY: body },
  log: silent,
  now: () => github.clock,
});

test('parses commands: the first word, the name, and the rest as argument', () => {
  assert.deepEqual(parseCommand('/speckit resume Use the helper\nand keep the test'), { name: 'resume', given: 'resume', argument: 'Use the helper\nand keep the test' });
  assert.deepEqual(parseCommand('  /speckit APPLY'), { name: 'apply', given: 'APPLY', argument: '' });
  assert.deepEqual(parseCommand('/speckit'), { name: 'help', given: '', argument: '' });
  assert.equal(parseCommand('/speckit deploy now').name, 'unknown');
  assert.equal(parseCommand('please /speckit apply'), null);
  assert.equal(parseCommand('/speckit-gha-diagnose'), null, 'a skill name is not a command');
  assert.match(renderHelp(), /\| `\/speckit sync` \|[\s\S]*skill `\/speckit-gha-diagnose`/);
});

test('ignores people without write access and bots, reacts, and answers help and unknown commands', async () => {
  const github = diagnosisRepo();
  assert.equal((await run(github, '/speckit apply', { actor: 'stranger' })).handled, false);
  assert.equal((await run(github, '/speckit apply', { actor: 'github-actions[bot]' })).handled, false);
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

test('diagnose and revise start a diagnosis, unless one is running', async () => {
  const github = diagnosisRepo();
  const started = await run(github, '/speckit diagnose look at research R6');
  assert.equal(started.outcome, 'started');
  assert.equal(github.repo.checkRuns.at(-1).external_id, CHECK_DIAGNOSING);
  assert.deepEqual(github.repo.runs.at(-1).inputs, { twin: '5', pull: '9', folder: 'f', notes: 'look at research R6', previous: '', check_run: String(github.repo.checkRuns.at(-1).id) });
  assert.match(github.comments.at(-1).body, /A diagnosis was started by @dev with your notes/);
  assert.equal(github.repo.pulls.find((pull) => pull.number === 11).state, 'closed', 'a new diagnosis supersedes the open amendment');
  assert.equal(github.repo.branches['speckit-amend/f'], undefined, 'so the new run can create the amendment branch afresh');
  assert.match(github.comments.find((comment) => comment.number === 11).body, /Superseded by a new diagnosis/);

  assert.equal((await run(github, '/speckit diagnose')).outcome, 'already running');

  Object.assign(github.repo.checkRuns.at(-1), { status: 'completed', external_id: CHECK_DIAGNOSED });
  await github.createComment(9, `${DIAGNOSIS_COMMENT_MARKER}\n**Diagnosis**: …`);
  const previous = github.comments.at(-1).id;
  const revised = await run(github, '/speckit revise Keep the counter write', { pull: 11 });
  assert.equal(revised.outcome, 'started');
  assert.equal(github.repo.runs.at(-1).inputs.previous, String(previous), 'a revise passes the previous diagnosis');
  assert.match(github.comments.at(-1).body, /A revised diagnosis was started/);
  assert.equal(github.comments.at(-1).number, 11, 'the reply goes where the command was given');
});

test('apply validates, merges the amendment, deletes its branch, and resumes', async () => {
  const github = diagnosisRepo();
  const applied = await run(github, '/speckit apply');
  assert.equal(applied.outcome, 'applied');
  const merge = github.repo.merges.at(-1);
  assert.deepEqual([merge.number, merge.sha, merge.merge_method, merge.commit_title], [11, 'sha-amend', 'squash', 'docs(f): amend spec artifacts (#11)']);
  assert.equal(github.repo.pulls.find((pull) => pull.number === 11).draft, false);
  assert.equal(github.repo.branches['speckit-amend/f'], undefined);
  assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-orchestrate.yml', {}]);
  const appliedComment = github.comments.at(-1);
  assert.equal(appliedComment.number, 9);
  assert.ok(appliedComment.body.startsWith('<!-- speckit-implement:resume -->'), 'the resume persists as a comment, not only as a dispatch');
  assert.match(appliedComment.body, /\*\*Amendment applied\*\* by @dev: #11 was merged into `speckit\/f`[\s\S]*it adds T003[\s\S]*fresh attempt count/);

  assert.equal((await run(github, '/speckit apply')).outcome, 'no amendment');
});

test('apply refuses an amendment that breaks the rules or cannot be merged', async () => {
  const invalid = diagnosisRepo({ amendmentFiles: ['src/a.cs'] });
  assert.equal((await run(invalid, '/speckit apply')).outcome, 'invalid');
  assert.match(invalid.comments.at(-1).body, /cannot be applied:\n\n- an amendment may only change files in `specs\/f\/`/);
  assert.equal(invalid.repo.merges.length, 0);

  const refused = diagnosisRepo();
  refused.mergeRefusal = 'Merge conflict';
  assert.equal((await run(refused, '/speckit apply')).outcome, 'merge refused');
  assert.match(refused.comments.at(-1).body, /could not be merged: Merge conflict/);
});

test('discard closes the amendment; resume passes guidance; sync merges the default branch', async () => {
  const github = diagnosisRepo();
  assert.equal((await run(github, '/speckit discard', { pull: 11 })).outcome, 'discarded');
  assert.equal(github.repo.pulls.find((pull) => pull.number === 11).state, 'closed');
  assert.match(github.comments.find((comment) => comment.number === 9).body, /Amendment #11 was discarded by @dev/);

  await run(github, '/speckit resume Use one statement on every refusal path');
  assert.deepEqual(github.repo.runs.at(-1).inputs, {});
  const resumed = github.comments.at(-1);
  assert.equal(parseGuidance(resumed.body), 'Use one statement on every refusal path');
  assert.match(resumed.body, /^<!-- speckit-implement:resume -->[\s\S]*Implementation resumed by @dev[\s\S]*> Use one statement on every refusal path/);

  github.repo.commits['sha-main2'] = { tree: 't', parents: ['sha-main'] };
  github.repo.branches.main = 'sha-main2';
  assert.equal((await run(github, '/speckit sync')).outcome, 'merged');
  assert.deepEqual(github.repo.commits[github.repo.branches['speckit/f']].parents, ['sha-impl', 'sha-main2']);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:resume -->\n@dev merged `main` into `speckit\/f`/);
  assert.equal((await run(github, '/speckit sync')).outcome, 'up-to-date');
  github.mergeBranchOutcome = 'conflict';
  assert.equal((await run(github, '/speckit sync')).outcome, 'conflict');
  assert.match(github.comments.at(-1).body, /conflicts with `speckit\/f`[\s\S]*\/speckit-gha-diagnose/);
  github.mergeBranch = async () => { throw new Error('POST /merges failed with HTTP 403: refusing to allow a GitHub App to create or update workflow'); };
  assert.equal((await run(github, '/speckit sync')).outcome, 'failed');
  assert.match(github.comments.at(-1).body, /could not be merged into `speckit\/f`: [\s\S]*HTTP 403[\s\S]*Merge it locally and push/);
  await assert.rejects(() => main(['other'], { env: {}, client: github }), /Usage/);
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
