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
  assert.deepEqual(parseCommand('  /speckit SYNC'), { name: 'sync', given: 'SYNC', argument: '' });
  assert.deepEqual(parseCommand('/speckit'), { name: 'help', given: '', argument: '' });
  assert.equal(parseCommand('/speckit deploy now').name, 'unknown');
  assert.equal(parseCommand('please /speckit resume'), null);
  assert.equal(parseCommand('/speckit-gha-diagnose'), null, 'a skill name is not a command');
  assert.match(renderHelp(), /\| `\/speckit sync` \|[\s\S]*An amendment pull request[\s\S]*skill `\/speckit-gha-diagnose`/);
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
  github.repo.runs.push({ id: 900, workflow: 'speckit-diagnose.lock.yml', display_title: 'Spec Kit diagnose #5', status: 'completed', conclusion: 'failure', created_at: github.tick() });
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
    assert.match(github.comments.at(-1).body, new RegExp(`^Unknown command \`/speckit ${name}\`[\\s\\S]*merge it to apply the amendment, comment or submit a review`));
  }
  assert.equal(github.repo.merges.length, 0);
});

test('resume passes guidance; sync merges the default branch', async () => {
  const github = diagnosisRepo();
  await run(github, '/speckit resume Use one statement on every refusal path');
  assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-implement.yml', { twin: '5', pull: '9' }], 'the implementation chain continues');
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
