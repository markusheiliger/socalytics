import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';

import { ATTENTION_LABEL, BLOCKED_LABEL, CHECK_DIAGNOSED, CHECK_RUN_NAME, COPILOT_HANDOFF_MARKER, PR_COMMANDS, parseGuidance, renderRepairMarker } from './speckit-implement-core.mjs';
import {
  COMMAND_DOCS_END,
  COMMAND_DOCS_FILE,
  COMMAND_DOCS_START,
  CommandUsageError,
  main,
  parseCommand,
  renderCommandDocs,
  renderCommandTable,
  renderHelp,
  runCommand,
  runCommandTarget,
  runCopilotHandoff,
  syncCommandDocs,
} from './speckit-commands.mjs';
import { diagnosisRepo, makeRepo, silent } from './speckit-test-helpers.mjs';
import { renderTwinBody } from './speckit-prepare-core.mjs';
import { resolveImplementRequester } from './speckit-prepare.mjs';
import { maintainAmendmentRequest, readAmendmentAuthorization } from './speckit-amend.mjs';

const run = (github, body, { pull = 9, actor = 'dev', env = {} } = {}) => runCommand({
  client: github,
  env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_PULL: String(pull), SPECKIT_COMMENT_ID: '501', SPECKIT_ACTOR: actor, SPECKIT_COMMENT_BODY: body, ...env },
  log: silent,
  now: () => github.clock,
});

test('amend authorizes artifact preparation without retrying, and duplicate delivery is idempotent', async () => {
  const github = diagnosisRepo({ amendment: false });
  await github.addLabels(9, ['help wanted']);
  github.commentAuthor = 'dev';
  const source = await github.createComment(9, '/speckit amend Make SC-002 manual evidence; preserve completed tasks.');
  github.commentAuthor = undefined;
  const options = { env: { SPECKIT_COMMENT_ID: String(source.id) } };
  assert.equal((await run(github, source.body, options)).outcome, 'preparing');
  const workflow = github.repo.runs.at(-1);
  assert.equal(workflow.workflow, 'speckit-diagnose.lock.yml');
  assert.equal(workflow.inputs.mode, 'amend');
  assert.equal(workflow.inputs.authorization, String(source.id));
  const preparing = github.comments.find((item) => item.body.startsWith('<!-- speckit-amend:request-state '));
  assert.match(preparing.body, /"state":"preparing".*"check":\d+[\s\S]*\*\*Amendment preparation started\*\* for #9/);
  assert.equal(github.comments.some((item) => item.body.startsWith('<!-- speckit-implement:retry -->')), false);
  assert.match(github.comments.find((item) => item.body.startsWith('<!-- speckit-amend:request ')).body, /No requirement change has landed/);
  assert.equal((await run(github, source.body, options)).outcome, 'already requested');
  assert.equal(github.repo.runs.length, 1);
  const request = await readAmendmentAuthorization(github, await github.getPullRequest(9), source.id);
  assert.equal(request.actor, 'dev');
  assert.equal((await maintainAmendmentRequest(github, { SPECKIT_DEFAULT_BRANCH: 'main' }, { implementation: await github.getPullRequest(9), twin: 5, folder: 'f', now: github.clock })).outcome, 'preparing');
  assert.equal(github.repo.runs.length, 1);
  github.repo.branches['speckit/f'] = 'another-head';
  await assert.rejects(readAmendmentAuthorization(github, await github.getPullRequest(9), source.id), /changed after authorization/);
});

test('amend refuses empty guidance, running implementations, and existing amendments without replacing them', async () => {
  const github = diagnosisRepo();
  await github.addLabels(9, ['help wanted']);
  assert.match((await run(github, '/speckit amend')).rejected, /nonempty guidance/);
  assert.match((await run(github, '/speckit amend Change SC-002')).rejected, /amendment #11 is already open/);
  assert.equal(github.repo.pulls.find((item) => item.number === 11).state, 'open');
  assert.equal(github.repo.runs.length, 0);
  const running = diagnosisRepo({ amendment: false });
  assert.match((await run(running, '/speckit amend Change SC-002')).rejected, /must be stopped/);
  assert.equal((await run(running, '/speckit amend Change SC-002', { actor: 'stranger' })).handled, false);
  assert.equal((await run(running, '/speckit amend Change SC-002', { actor: 'github-actions[bot]' })).handled, false);
});

test('amend dispatch failures remain durable and can be reconciled, without accepting edited authorization', async () => {
  const github = diagnosisRepo({ amendment: false });
  await github.addLabels(9, ['help wanted']);
  github.commentAuthor = 'dev';
  const source = await github.createComment(9, '/speckit amend Make the probes manual.');
  github.commentAuthor = undefined;
  const dispatch = github.dispatchWorkflow.bind(github);
  github.dispatchWorkflow = async () => { throw new Error('dispatch unavailable'); };
  assert.equal((await run(github, source.body, { env: { SPECKIT_COMMENT_ID: String(source.id) } })).outcome, 'queued');
  const queued = github.comments.find((item) => item.body.startsWith('<!-- speckit-amend:request-state '));
  assert.match(queued.body, /"state":"preparing"[\s\S]*could not start: dispatch unavailable/);
  github.dispatchWorkflow = dispatch;
  assert.equal((await maintainAmendmentRequest(github, { SPECKIT_DEFAULT_BRANCH: 'main' }, { implementation: await github.getPullRequest(9), twin: 5, folder: 'f' })).outcome, 'preparing');
  assert.equal(github.repo.runs.length, 1);
  await github.updateComment(source.id, '/speckit amend Remove every assertion.');
  await assert.rejects(readAmendmentAuthorization(github, await github.getPullRequest(9), source.id), /no longer authorizes/);
  assert.equal((await maintainAmendmentRequest(github, { SPECKIT_DEFAULT_BRANCH: 'main' }, { implementation: await github.getPullRequest(9), twin: 5, folder: 'f' })).outcome, 'stale');
  assert.match(github.comments.findLast((item) => item.body.startsWith('<!-- speckit-amend:request-state ')).body, /"state":"stale"[\s\S]*\*\*Amendment preparation stopped\*\*/);
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
  assert.match(renderHelp(), /\| `\/speckit retry \[--force\] \[guidance\]` \| implementation pull request \| restart what stopped with a fresh attempt count while preserving the current phase base[\s\S]*\| `\/speckit author \[guidance\]` \| implementation pull request \|[\s\S]*\| `\/speckit implement` \| spec twin issue \|[\s\S]*An amendment pull request[\s\S]*spec pull request authored by `\/speckit author` \(`speckit-spec\/<folder>`[\s\S]*skill `\/speckit-gha-diagnose`/);
  assert.doesNotMatch(renderHelp(), /\/speckit resume/);
  assert.doesNotMatch(renderHelp(), /\/speckit diagnose/);
  assert.doesNotMatch(renderHelp(), /\/speckit fix/);
  assert.equal(parseCommand('/speckit retry Rerun as is').name, 'retry');
  assert.equal(parseCommand('/speckit author Make the test robust').name, 'author');
  assert.equal(parseCommand('/speckit fix Make the test robust').name, 'unknown', 'renamed to specify, without an alias');
  assert.equal(parseCommand('/speckit implement').name, 'implement');
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
  assert.deepEqual(parseCommand('/speckit author ---- not a flag').guidance, '---- not a flag');
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

test('renamed commands name their new command without starting work', async () => {
  const github = diagnosisRepo();
  const result = await run(github, '/speckit specify Make the test robust');
  assert.equal(result.command, 'unknown');
  assert.match(github.comments.at(-1).body, /^\*\*`\/speckit specify` was renamed to `\/speckit author`\*\* and was not run\. Comment this instead \(people with write access\):\n\n```text\n\/speckit author Make the test robust\n```\n\n[\s\S]*\*\*Spec Kit commands\*\*/);
  await run(github, '/speckit fix');
  assert.match(github.comments.at(-1).body, /^\*\*`\/speckit fix` was renamed to `\/speckit author`\*\*[\s\S]*```text\n\/speckit author\n```/);
  assert.equal(github.repo.runs.length, 0);
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
  assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-implement-decide.yml', { issue: '5', pull: '9', title: 'F' }]);
  const retried = github.comments.at(-1);
  assert.match(retried.body, /^<!-- speckit-implement:retry -->[\s\S]*Implementation retried by @dev; what stopped restarts with a fresh attempt count while preserving the current phase base\.[\s\S]*> Rerun the verification unchanged/);
  assert.match(retried.body, /The guidance is added to the prompt of the first work that runs[^\n]* and ends after its first success or a person's push; it changes no check, limit, or verification\./);
  assert.equal(parseGuidance(retried.body), 'Rerun the verification unchanged');
  await run(github, '/speckit retry');
  assert.doesNotMatch(github.comments.at(-1).body, /The guidance/, 'no guidance, no note');
});

test('the sync reply says where its guidance goes, and when it cannot change the last stop', async () => {
  const github = diagnosisRepo();
  await run(github, '/speckit sync Keep both migrations');
  assert.match(github.comments.at(-1).body, /The guidance reaches Copilot only if conflicts remain after the merge[^\n]*after a clean merge it goes to the first work that continues\./);
  assert.doesNotMatch(github.comments.at(-1).body, /before any agent ran/);
  await github.createComment(9, '**Implementation needs attention** @dev: conflicts in protected paths need a person: .specify/memory/constitution.md');
  await run(github, '/speckit sync Keep both migrations');
  assert.match(github.comments.at(-1).body, /The last stop came before any agent ran \(conflicts in protected paths need a person: \.specify\/memory\/constitution\.md\), so the guidance alone cannot change that outcome\./);
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

  // When the latest diagnosis asks Copilot for the change, the refusal recommends posting that request.
  await github.createComment(pull.number, '<!-- speckit-implement:diagnosis -->\n**Diagnosis**: a person has to decide how to continue.\n\n**Recommended next step**: Ask Copilot to repoint the link.\n\nComment this on the pull request (people with write access):\n\n```text\n@copilot Change line 83 of specs/other/spec.md.\n\nMake only this change.\n```');
  assert.equal((await run(github, '/speckit retry')).outcome, 'refused');
  const refusal = github.comments.at(-1).body;
  assert.match(refusal, /\*\*Recommended next step\*\*: Ask Copilot to push the correction the \[latest diagnosis\]\([^)]+\) describes\.[\s\S]*```text\n@copilot Change line 83 of specs\/other\/spec\.md\.\n\nMake only this change\.\n```/);
  assert.match(refusal, /<summary>Other options<\/summary>[\s\S]*Push the correction yourself to `speckit\/f`/);
  assert.equal(refusal.match(/\*\*Recommended next step\*\*/g)?.length, 1);
  assert.deepEqual([restarts(), github.repo.runs.length], before, 'still nothing was restarted');

  // New commits on the base branch do not change the current phase's retry guard.
  github.repo.branches.main = 'sha-main-2';
  assert.equal((await run(github, '/speckit retry Again')).outcome, 'refused');

  // A blocked implementation whose attention label only asks for a blocker's /speckit implement is not stopped.
  pull.labels = [{ name: ATTENTION_LABEL }, { name: BLOCKED_LABEL }];
  assert.notEqual((await run(github, '/speckit retry While blocked')).outcome, 'refused');
  stop();

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

test('specify starts the authoring of a new spec once, and refuses while one is pending; fix is unknown', async () => {
  const github = diagnosisRepo();
  const started = await run(github, '/speckit author Make UploadSessionExpiryTests independent of runner load');
  assert.equal(started.outcome, 'started');
  const check = github.repo.checkRuns.at(-1);
  assert.deepEqual([check.name, check.external_id, check.status], ['Spec Kit author', 'speckit:fix-authoring', 'in_progress'], 'its own check run, apart from the implementation');
  const dispatched = github.repo.runs.at(-1);
  assert.equal(dispatched.workflow, 'speckit-author.yml');
  assert.deepEqual({ ...dispatched.inputs, stamp: 'x' }, { issue: '5', pull: '9', folder: 'f', guidance: 'Make UploadSessionExpiryTests independent of runner load', check_run: String(check.id), mode: 'author', amendment: '', round: '0', stalls: '0', findings: '', stamp: 'x' });
  assert.match(dispatched.inputs.stamp, /^\d{8}-\d{6}$/);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-fix:requested \{"by":"dev"[\s\S]*\*\*A new spec is being authored\*\*, requested by @dev[\s\S]*merged automatically[\s\S]*> Make UploadSessionExpiryTests/);
  assert.equal(parseGuidance(github.comments.at(-1).body), 'Make UploadSessionExpiryTests independent of runner load');

  const again = await run(github, '/speckit author');
  assert.equal(again.outcome, 'refused');
  assert.match(github.comments.at(-1).body, /^`\/speckit author` cannot start: a new spec is already being authored\./);

  const runs = github.repo.runs.length;
  const renamed = await run(github, '/speckit fix Make it robust');
  assert.equal(renamed.command, 'unknown');
  assert.match(github.comments.at(-1).body, /^\*\*`\/speckit fix` was renamed to `\/speckit author`\*\* and was not run[\s\S]*`\/speckit author \[guidance\]`/);
  assert.equal(github.repo.runs.length, runs, 'nothing started');
});

// A spec twin #20 of `specs/<folder>` in a temporary repository with the default branch's spec folders.
function twinRepo({ folder = 't', tasks = '## P\n- [ ] T001 Do it\n', checklists = { 'requirements.md': '- [x] Done\n' }, labels = [] } = {}) {
  const root = makeRepo([{ folder, plan: true, tasks, checklists }]);
  const github = diagnosisRepo({ amendment: false });
  github.issues.push({ number: 20, id: 20000, node_id: 'I_20', state: 'open', title: 'T', body: renderTwinBody({ folder, title: 'T', summary: '' }, ['spec.md', 'plan.md', 'tasks.md'], { serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' }), labels: [{ name: 'speckit:spec' }, ...labels.map((name) => ({ name }))] });
  const onTwin = (body, { actor = 'dev', commentId = '601' } = {}) => runCommand({
    client: github,
    env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_PULL: '20', SPECKIT_TARGET: 'twin', SPECKIT_COMMENT_ID: commentId, SPECKIT_ACTOR: actor, SPECKIT_COMMENT_BODY: body },
    log: silent,
    now: () => github.clock,
    rootDir: root,
  });
  return { root, github, onTwin, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

test('/speckit implement on a twin records the requester, sets the implement stage, and starts the scheduler', async () => {
  const { github, onTwin, cleanup } = twinRepo();
  try {
    github.edges.push([20, 5]);
    const result = await onTwin('/speckit implement');
    assert.equal(result.outcome, 'requested');
    assert.deepEqual(result.blockers, [5]);
    const record = github.comments.filter((comment) => comment.number === 20).at(-1);
    assert.match(record.body, /^<!-- speckit:implement-requested \{"by":"dev","comment":601\} -->\n\*\*Implementation requested\*\* by @dev: `specs\/t` is `tasked`[\s\S]*It waits for its open prerequisites #5/);
    assert.deepEqual(github.find(20).labels.map((label) => label.name), ['speckit:spec', 'speckit:stage:implement']);
    assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-orchestrate.yml', {}]);
    assert.deepEqual(github.reactions.at(-1), { commentId: 601, content: 'eyes' });
    assert.equal((await resolveImplementRequester(github, 20)).login, 'dev', 'the record names the requester');

    const runs = github.repo.runs.length;
    const again = await onTwin('/speckit implement');
    assert.equal(again.outcome, 'already requested');
    assert.match(github.comments.at(-1).body, /^\*\*Already requested\*\*: this spec is at `speckit:stage:implement`/);
    assert.equal(github.repo.runs.length, runs);
    assert.equal((await onTwin('/speckit implement', { actor: 'stranger' })).handled, false, 'people without write access are ignored');
  } finally {
    cleanup();
  }
});

test('/speckit implement is refused with the reasons and one next step unless the spec is tasked with checked checklists', async () => {
  const open = twinRepo({ checklists: { 'requirements.md': '- [ ] Open item\n' } });
  try {
    assert.equal((await open.onTwin('/speckit implement')).outcome, 'refused');
    const body = open.github.comments.at(-1).body;
    assert.match(body, /^\*\*`\/speckit implement` was not accepted\*\*, @dev:\n\n- 1 checklist item\(s\) in `specs\/t\/checklists\/` are not checked[\s\S]*\*\*Recommended next step\*\*: Resolve the open items/);
    assert.equal(body.match(/\*\*Recommended next step\*\*/g).length, 1);
    assert.deepEqual(open.github.find(20).labels.map((label) => label.name), ['speckit:spec']);
    assert.equal(open.github.repo.runs.length, 0);
  } finally {
    open.cleanup();
  }
  const started = twinRepo({ tasks: '## P\n- [x] T001 Done\n- [ ] T002 Next\n' });
  try {
    assert.equal((await started.onTwin('/speckit implement')).outcome, 'refused');
    assert.match(started.github.comments.at(-1).body, /stage `implementing`[\s\S]*\*\*Recommended next step\*\*: Continue the implementation that already started/);
  } finally {
    started.cleanup();
  }
});

test('commands outside their scope are refused with help: implement on a pull request, pull request commands on a twin', async () => {
  const { github, onTwin, cleanup } = twinRepo();
  try {
    const onPull = await run(github, '/speckit implement');
    assert.equal(onPull.rejected, 'wrong scope');
    assert.match(github.comments.at(-1).body, /^\*\*`\/speckit implement` was not run\*\*: it works on a spec twin issue \(label `speckit:spec`\), not on a pull request\.[\s\S]*\*\*Spec Kit commands\*\*/);
    for (const name of ['retry', 'sync', 'author']) {
      const result = await onTwin(`/speckit ${name}`);
      assert.equal(result.rejected, 'wrong scope', name);
      assert.match(github.comments.at(-1).body, new RegExp(`^\\*\\*\`/speckit ${name}\` was not run\\*\\*: it works on an implementation pull request`));
    }
    assert.equal((await onTwin('/speckit help')).command, 'help');
    assert.equal(github.repo.runs.length, 0);
    assert.equal(await runCommandTarget({ client: github, env: { SPECKIT_PULL: '20', SPECKIT_TARGET: 'twin', SPECKIT_COMMENT_BODY: '/speckit implement' } }), 20, 'a twin command locks the twin');
  } finally {
    cleanup();
  }
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

const repoFile = (path) => readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8').replace(/\r\n/g, '\n');

test('the command table in the workflows README is generated from PR_COMMANDS and current', () => {
  const docs = repoFile(COMMAND_DOCS_FILE);
  assert.equal(docs, syncCommandDocs(docs), `The command table in ${COMMAND_DOCS_FILE} is stale; run \`node .github/scripts/speckit-commands.mjs docs --write\`.`);
  assert.match(docs, /^## Comment commands$/m);
  assert.ok(renderHelp().includes(renderCommandTable()), '/speckit help shows the same table');
  for (const command of PR_COMMANDS) assert.ok(docs.includes(`\`${command.usage}\``), command.usage);
});

test('syncCommandDocs replaces only the marked block and requires the markers', () => {
  const text = `intro\n${COMMAND_DOCS_START}\nold table\n${COMMAND_DOCS_END}\noutro\n`;
  const synced = syncCommandDocs(text, [{ name: 'rebase', usage: '/speckit rebase', help: 'rebase the branch', flags: {} }]);
  assert.equal(synced, `intro\n${renderCommandDocs([{ name: 'rebase', usage: '/speckit rebase', help: 'rebase the branch', flags: {} }])}\noutro\n`);
  assert.match(synced, /\| `\/speckit rebase` \| implementation pull request \| rebase the branch \| none \|/);
  assert.doesNotMatch(synced, /old table/);
  assert.throws(() => syncCommandDocs('no markers'), CommandUsageError);
  assert.throws(() => syncCommandDocs(`${COMMAND_DOCS_END}\n${COMMAND_DOCS_START}`), CommandUsageError);
});

test('tooling docs name only known comment commands and the root README links to the canonical section', () => {
  const names = PR_COMMANDS.map((command) => command.name);
  for (const path of ['README.md', COMMAND_DOCS_FILE, 'AGENTS.md', '.specify/extension-src/gha/README.md']) {
    const text = repoFile(path).replace(/```mermaid[\s\S]*?```/g, '');
    const unknown = [...text.matchAll(/\/speckit[ \t]+([A-Za-z][A-Za-z-]*)/g)].map((match) => match[1]).filter((name) => !names.includes(name));
    assert.deepEqual([...new Set(unknown)], [], `${path} names commands that PR_COMMANDS does not define`);
  }
  const readme = repoFile('README.md');
  assert.ok(readme.includes('(.github/workflows/README.md#comment-commands)'), 'the root README links to the canonical command section');
  assert.doesNotMatch(readme, /^\s*\| `\/speckit /m, 'the root README has no command table of its own');
  assert.ok(repoFile('AGENTS.md').includes('(.github/workflows/README.md#comment-commands)'));
});

test('docs checks the command table and refreshes it with --write, keeping line endings', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'speckit-docs-'));
  try {
    const file = join(dir, 'README.md');
    const stale = `# Title\r\n\r\n${COMMAND_DOCS_START}\r\nold\r\n${COMMAND_DOCS_END}\r\n`;
    writeFileSync(file, stale);
    const lines = [];
    const log = (line) => lines.push(line);
    assert.equal(await main(['docs'], { docsFile: file, log }), 1);
    assert.equal(readFileSync(file, 'utf8'), stale, 'the check does not write');
    assert.match(lines.at(-1), /stale; run `node \.github\/scripts\/speckit-commands\.mjs docs --write`/);
    assert.equal(await main(['docs', '--write'], { docsFile: file, log }), 0);
    const written = readFileSync(file, 'utf8');
    assert.ok(written.includes(renderCommandTable().replace(/\n/g, '\r\n')));
    assert.doesNotMatch(written.replace(/\r\n/g, ''), /\n/, 'CRLF stays CRLF');
    assert.equal(await main(['docs'], { docsFile: file, log }), 0);
    await assert.rejects(main(['docs', '--force'], { docsFile: file, log }), CommandUsageError);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

const handoff = (github, body, { pull = 9, actor = 'dev' } = {}) => runCopilotHandoff({
  client: github,
  env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_PULL: String(pull), SPECKIT_ACTOR: actor, SPECKIT_COMMENT_BODY: body },
  log: silent,
});

test('a writer\'s @copilot request hands a stopped implementation to Copilot and withdraws the request for help', async () => {
  const github = diagnosisRepo();
  const pull = github.repo.pulls[0];
  const labels = () => (pull.labels ?? []).map((label) => label.name);
  pull.labels = [{ name: ATTENTION_LABEL }];
  assert.equal((await handoff(github, 'thanks @copilot, see above')).handled, false, 'only a comment that starts with the mention');
  assert.equal((await handoff(github, '@copilot fix it', { actor: 'stranger' })).handled, false, 'only people with write access');
  assert.equal((await handoff(github, '@copilot fix it', { pull: 11 })).handled, false, 'only implementation pull requests');
  assert.deepEqual(labels(), [ATTENTION_LABEL]);
  assert.equal(github.comments.length, 0);
  assert.equal((await handoff(github, '  @Copilot Merge main and resolve the README conflict.')).handled, true);
  assert.deepEqual(labels(), []);
  const note = github.comments.at(-1).body;
  assert.ok(note.startsWith(COPILOT_HANDOFF_MARKER));
  assert.match(note, /\*\*Copilot is working on it\*\*: @dev asked GitHub Copilot's cloud agent to change `speckit\/f`[\s\S]*Copilot's push continues the implementation by itself[\s\S]*has not pushed within 60 minutes, the scheduler asks for help here again\./);
  assert.equal((await handoff(github, '@copilot again')).handled, false, 'nothing to hand over while it does not wait for a person');
  pull.labels = [{ name: ATTENTION_LABEL }, { name: BLOCKED_LABEL }];
  assert.equal((await handoff(github, '@copilot fix it')).handled, false, 'a blocked implementation waits for its prerequisites');
  assert.deepEqual(labels(), [ATTENTION_LABEL, BLOCKED_LABEL]);
  pull.labels = [{ name: ATTENTION_LABEL }];
  pull.state = 'closed';
  assert.equal((await handoff(github, '@copilot fix it')).handled, false);
});

test('copilot-handoff is a command of the script and runs from its workflow job for @copilot comments', async () => {
  const github = diagnosisRepo({ amendment: false });
  github.repo.pulls[0].labels = [{ name: ATTENTION_LABEL }];
  assert.equal(await main(['copilot-handoff'], { client: github, env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_PULL: '9', SPECKIT_ACTOR: 'dev', SPECKIT_COMMENT_BODY: '@copilot go' }, log: silent }), 0);
  assert.equal(github.repo.pulls[0].labels.length, 0);
  const workflow = readFileSync(new URL('../workflows/speckit-commands.yml', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
  const job = workflow.match(/^ {2}copilot-handoff:\n[\s\S]*?(?=^ {2}[a-z-]+:\n|(?![\s\S]))/m)?.[0] ?? '';
  assert.match(job, /name: Hand a stopped implementation over to Copilot\n/);
  assert.match(job, /github\.event\.comment\.user\.type != 'Bot' &&\n\s+contains\(github\.event\.comment\.body, '@copilot'\)/);
  assert.match(job, /SPECKIT_COMMENT_BODY: \$\{\{ github\.event\.comment\.body \}\}\n\s+run: node \.github\/scripts\/speckit-commands\.mjs copilot-handoff\n/);
  assert.doesNotMatch(job, /contents: write/);
});