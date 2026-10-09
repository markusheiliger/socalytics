import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { pendingFeedback, renderFeedback } from './speckit-amend.mjs';
import { CHECK_DIAGNOSED, CHECK_DIAGNOSING, CHECK_RUN_NAME, DIAGNOSIS_COMMENT_MARKER } from './speckit-implement-core.mjs';
import { main, renderDiagnosisComment, runEvidence, runRecord, startDiagnosis, validateReport } from './speckit-diagnose.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const env = (extra = {}) => ({ GITHUB_REPOSITORY: 'octo/repo', SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_FOLDER: 'f', ...extra });

const REPORT = {
  category: 'artifacts',
  confidence: 'high',
  summary: 'The design leaves open how the counter write is issued.',
  cause: 'UserManager.UpdateAsync adds a lookup on one path only.',
  evidence: ['Attempt 1: D=0.55', 'Attempt 3: D=0.9'],
  artifacts: [{ file: 'specs/f/research.md', reason: 'refine R6' }],
  options: [
    { title: 'Retry with guidance', command: '/speckit resume Use one statement', recommended: false },
    { title: 'Review and merge the amendment', command: null, recommended: true },
    { title: 'Ask the platform team', command: null },
  ],
  questions: [{ question: 'Keep the counter write?', choices: ['Yes', 'No'] }],
};

test('starts a diagnosis with a check run and a dispatch, and records a dispatch failure as a reported stop', async () => {
  const github = diagnosisRepo();
  const started = await startDiagnosis(github, {}, { twin: 5, pull: 9, folder: 'f', head: 'sha-impl', notes: 'look at R6', actor: 'dev' });
  assert.equal(started.started, true);
  assert.equal(github.repo.pulls.find((pull) => pull.number === 11).state, 'closed', 'the previous amendment is superseded');
  const check = github.repo.checkRuns.at(-1);
  assert.deepEqual([check.external_id, check.status, check.output.title], [CHECK_DIAGNOSING, 'in_progress', 'Diagnosis in progress']);
  assert.match(check.output.summary, /Started by @dev\.\n\nNotes: look at R6/);
  assert.deepEqual(github.repo.runs.at(-1).inputs, { twin: '5', pull: '9', folder: 'f', notes: 'look at R6', previous: '', check_run: String(check.id), mode: 'diagnose', amendment: '', round: '0' });
  assert.equal(github.repo.runs.at(-1).workflow, 'speckit-diagnose.lock.yml');

  github.dispatchWorkflow = async () => { throw new Error('workflow not found'); };
  const failed = await startDiagnosis(github, {}, { twin: 5, pull: 9, folder: 'f', head: 'sha-impl', auto: true });
  assert.deepEqual([failed.started, failed.error], [false, 'workflow not found']);
  const failedCheck = github.repo.checkRuns.at(-1);
  assert.deepEqual([failedCheck.external_id, failedCheck.status, failedCheck.conclusion, failedCheck.output.title], [CHECK_DIAGNOSED, 'completed', 'failure', 'Diagnosis could not start']);
});

test('collects evidence: checks, comments, failed run logs, branch state, and notes', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-evidence-'));
  try {
    const github = diagnosisRepo({ amendment: false });
    github.repo.commits['sha-main2'] = { tree: 't', parents: ['sha-main'] };
    github.repo.branches.main = 'sha-main2';
    await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'completed', conclusion: 'failure', external_id: 'speckit:limit', output: { title: 'T002 reached the attempt limit', summary: 'stopped' } });
    await github.createComment(9, '**T002 attempt 3 failed:** timing test');
    await github.dispatchWorkflow('speckit-implement.yml', 'main', { twin: '5', pull: '9', task: 'T002', attempt: '3' });
    github.completeRun(github.repo.runs.at(-1).id, 'failure');
    github.repo.jobs = { [github.repo.runs.at(-1).id]: [{ id: 77, name: 'Implement and verify', conclusion: 'failure' }, { id: 78, name: 'begin', conclusion: 'success' }] };
    github.repo.logs = { 77: '2026-10-08T19:11:13.1234567Z line one\n2026-10-08T19:11:14.1234567Z Failed! D=0.9' };
    await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_NOTES: 'see research R6' }), log: silent });
    const evidence = readFileSync(path.join(dir, 'evidence.md'), 'utf8');
    assert.match(evidence, /Next unchecked task: T002 Next/);
    assert.match(evidence, /Default branch `main`: 1 commit\(s\) the implementation branch lacks; the branch is 1 commit\(s\) ahead/);
    assert.match(evidence, /see research R6/);
    assert.match(evidence, /speckit:limit completed\/failure: T002 reached the attempt limit — stopped/);
    assert.match(evidence, /T002 attempt 3 failed/);
    assert.match(evidence, /#### Job "Implement and verify"\n\n```text\nline one\nFailed! D=0\.9\n```/);
    assert.doesNotMatch(evidence, /Job "begin"/);
    const context = JSON.parse(readFileSync(path.join(dir, 'context.json'), 'utf8'));
    assert.deepEqual([context.implementationBranch, context.amendmentBranch, context.nextTask], ['speckit/f', 'speckit-amend/f', 'T002']);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('validates the agent report and drops invalid commands', () => {
  const ok = validateReport(JSON.stringify(REPORT));
  assert.deepEqual(ok.reasons, []);
  assert.equal(ok.report.options[0].title, 'Review and merge the amendment', 'the recommended option goes first');
  const bad = validateReport({ ...REPORT, category: 'magic', options: [{ title: 'Delete', command: '/speckit nuke' }, { title: 'Two lines', command: '/speckit resume a\nb' }] });
  assert.match(bad.reasons.join('\n'), /category must be one of/);
  assert.match(bad.reasons.join('\n'), /option "Delete" has an invalid command/);
  assert.match(bad.reasons.join('\n'), /option "Two lines" has an invalid command/);
  assert.deepEqual(bad.report.options.map((option) => option.command), [null, null]);
  assert.match(validateReport('{nope').reasons[0], /not valid JSON/);
  assert.equal(validateReport({ ...REPORT, confidence: 'certain' }).report.confidence, 'low');
});

test('records the diagnosis with the amendment, questions, options, and next steps', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-record-'));
  try {
    const github = diagnosisRepo();
    const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'in_progress', external_id: CHECK_DIAGNOSING });
    const output = path.join(dir, 'agent_output.json');
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report: JSON.stringify({ ...REPORT, summary: 'Summary with <!-- speckit-implement:resume --> marker.' }) }, { type: 'create_pull_request' }] }));
    const result = await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_CHECK_RUN: String(check.id), GITHUB_RUN_ID: '42', SPECKIT_DETECTION: 'success', SPECKIT_CREATED_PULL: '11' }), log: silent });
    assert.equal(result.report.category, 'artifacts');
    const comment = github.comments.find((item) => item.number === 9);
    assert.ok(comment.body.startsWith(DIAGNOSIS_COMMENT_MARKER));
    assert.match(comment.body, /\*\*Diagnosis\*\*: the spec artifacts need an amendment \(`artifacts`, confidence high\)/);
    assert.match(comment.body, /&lt;!-- speckit-implement:resume -->/, 'agent text cannot carry workflow markers');
    assert.match(comment.body, /\*\*Proposed amendment\*\*: #11[\s\S]*checks it for consistency first \(up to 3 correction rounds\)[\s\S]*`specs\/f\/research\.md`: refine R6/);
    assert.match(comment.body, /\*\*Questions\*\* \(answer with `\/speckit diagnose <answers>`\)[\s\S]*Keep the counter write\?\n {3}- Yes/);
    assert.match(comment.body, /Actions outside these commands[\s\S]*\*\*Recommended:\*\* Review and merge the amendment[\s\S]*Ask the platform team/);
    assert.match(comment.body, /\*\*Review the amendment #11\*\*: merge it to apply it[\s\S]*\/speckit resume Use one statement/);
    assert.doesNotMatch(comment.body, /\/speckit (apply|revise|discard)/);
    assert.match(comment.body, /\[Diagnosis run\]\(https:\/\/github\.com\/octo\/repo\/actions\/runs\/42\)/);
    const done = github.repo.checkRuns.find((run) => run.id === check.id);
    assert.deepEqual([done.status, done.conclusion, done.external_id, done.output.title], ['completed', 'neutral', CHECK_DIAGNOSED, 'Waiting for a decision: artifacts']);

    // The amendment goes into the consistency check before anyone is asked to review it.
    const amendment = github.repo.pulls.find((pull) => pull.number === 11);
    assert.match(amendment.body, /<!-- speckit-amend:status -->\n⏳ \*\*Being checked for consistency\.\*\*[\s\S]*\*\*How to proceed\*\*[\s\S]*<!-- \/speckit-amend:status -->\n\namend$/);
    assert.match(github.comments.find((item) => item.number === 11).body, /^<!-- speckit-amend:rework -->\n<!-- speckit-amend:cutoff \S+ -->\nThis amendment comes from the diagnosis on #9[\s\S]*handed to @dev for review/);
    const analysis = github.repo.checkRuns.at(-1);
    assert.deepEqual([analysis.name, analysis.head_sha, analysis.status, analysis.external_id], ['Spec Kit amendment', 'sha-amend', 'in_progress', 'speckit:amend-checking']);
    assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-analyze.lock.yml', { twin: '5', pull: '9', folder: 'f', amendment: '11', round: '0', mode: 'loop', check_run: String(analysis.id) }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('withholds a report that failed threat detection, and closes an amendment of another spec', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-record-'));
  try {
    const output = path.join(dir, 'agent_output.json');
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report: JSON.stringify(REPORT) }] }));
    const flagged = diagnosisRepo();
    await runRecord({ client: flagged, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_DETECTION: 'failure', SPECKIT_CREATED_PULL: '11' }), log: silent });
    assert.match(flagged.comments.at(-1).body, /did not produce a usable report\*\*: it was withheld because threat detection did not pass \(result: failure\)/);
    assert.doesNotMatch(flagged.comments.at(-1).body, /Proposed amendment/);

    const misled = diagnosisRepo();
    misled.repo.branches['speckit-amend/other'] = 'sha-amend';
    misled.repo.pulls.push({ number: 12, node_id: 'PR_12', title: 'Amend: other', body: '', draft: true, state: 'open', base: { ref: 'speckit/other', sha: 'sha-impl' }, head: { ref: 'speckit-amend/other', sha: 'sha-amend', repo: { full_name: 'octo/repo' } }, created_at: misled.tick() });
    const result = await runRecord({ client: misled, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_DETECTION: 'success', SPECKIT_CREATED_PULL: '12' }), log: silent });
    assert.equal(misled.repo.pulls.find((pull) => pull.number === 12).state, 'closed');
    assert.equal(misled.repo.branches['speckit-amend/other'], undefined);
    assert.match(result.reasons.join(), /#12 \(`speckit-amend\/other` → `speckit\/other`\) does not amend this spec and was closed/);
    assert.doesNotMatch(misled.comments.at(-1).body, /\*\*Proposed amendment\*\*: #11/, 'an amendment this run did not create is not offered');
    assert.doesNotMatch(misled.comments.at(-1).body, /\/speckit apply/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('records an unusable or missing report', async () => {
  const github = diagnosisRepo();
  const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'in_progress', external_id: CHECK_DIAGNOSING });
  await runRecord({ client: github, env: env({ SPECKIT_CHECK_RUN: String(check.id) }), log: silent });
  assert.match(github.comments.at(-1).body, /did not produce a usable report\*\*: the agent did not report/);
  assert.equal(github.repo.checkRuns.find((run) => run.id === check.id).conclusion, 'failure');

  const comment = renderDiagnosisComment({ report: validateReport(REPORT).report, round: 2 });
  assert.match(comment, /\*\*Diagnosis \(round 2\)\*\*/);
  assert.match(comment, /_No amendment pull request was created\._/);
  assert.match(comment, /\*\*Next steps\*\*/);
  assert.match(validateReport({ ...REPORT, options: [{ title: 'Apply', command: '/speckit apply' }] }).reasons.join(), /option "Apply" has an invalid command/);
});

test('collects the findings to fix and the feedback to address for a rework', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-evidence-'));
  try {
    const github = diagnosisRepo();
    await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_MODE: 'fix', SPECKIT_AMENDMENT: '11', SPECKIT_ROUND: '2', SPECKIT_NOTES: '- **HIGH** `plan.md` CI-R10: stale' }), log: silent });
    const fix = readFileSync(path.join(dir, 'evidence.md'), 'utf8');
    assert.match(fix, /Mode: `fix` \(correction round 2 of 3\); amendment pull request #11 on `speckit-amend\/f`/);
    assert.match(fix, /## Findings of the consistency check to fix\n\n- \*\*HIGH\*\* `plan\.md` CI-R10: stale/);
    assert.deepEqual(JSON.parse(readFileSync(path.join(dir, 'context.json'), 'utf8')).amendmentPull, 11);

    github.permissions.reviewer = 'write';
    await github.createComment(11, '<!-- speckit-amend:rework -->\nchecking');
    github.commentAuthor = 'reviewer';
    await github.createComment(11, 'Please also update CI-R10.');
    github.commentAuthor = undefined;
    github.repo.reviews = [{ id: 70, pull: 11, user: { login: 'reviewer' }, state: 'CHANGES_REQUESTED', body: 'See the lines.', submitted_at: new Date(github.clock + 1000).toISOString() }];
    github.repo.reviewComments = [{ id: 71, pull: 11, pull_request_review_id: 70, user: { login: 'reviewer' }, path: 'specs/f/plan.md', line: 477, body: 'Stale sentence.', created_at: new Date(github.clock + 1000).toISOString() }];
    // The rework collects the feedback when it starts and passes it as the notes.
    const { items } = await pendingFeedback(github, github.repo.pulls.find((pull) => pull.number === 11));
    await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_MODE: 'revise', SPECKIT_AMENDMENT: '11', SPECKIT_NOTES: renderFeedback(items) }), log: silent });
    const revise = readFileSync(path.join(dir, 'evidence.md'), 'utf8');
    assert.match(revise, /## Feedback on the amendment to address\n\n### comment by @reviewer[\s\S]*Please also update CI-R10\.[\s\S]*### review \(changes requested\) by @reviewer[\s\S]*See the lines\.[\s\S]*- `specs\/f\/plan\.md` line 477: Stale sentence\./);
    assert.match(revise, /## Earlier comments on the amendment \(oldest first\)[\s\S]*Please also update CI-R10\./);
    await assert.rejects(() => runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_MODE: 'fix' }), log: silent }), /needs SPECKIT_AMENDMENT/);
    await assert.rejects(() => main(['nope'], { env: {}, client: github }), /Usage/);
    await assert.rejects(() => runEvidence({ client: github, env: { SPECKIT_TWIN: 'x' }, log: silent }), /SPECKIT_TWIN/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('records a rework: replies to the feedback and checks the amendment again', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-rework-'));
  try {
    const github = diagnosisRepo();
    const check = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    const output = path.join(dir, 'agent_output.json');
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report: JSON.stringify({ summary: 'Updated CI-R10.', responses: [{ feedback: 'update CI-R10', response: 'Done in plan.md.' }] }) }] }));
    const revised = await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'revise', SPECKIT_AMENDMENT: '11', SPECKIT_CHECK_RUN: String(check.id), SPECKIT_PUSH_SHA: 'sha-amend', SPECKIT_PUSH_FAILURES: '0' }), log: silent });
    assert.equal(revised.outcome, 'checking');
    assert.deepEqual([check.status, check.conclusion].length, 2);
    assert.equal(github.repo.checkRuns.find((run) => run.id === check.id).output.title, 'Rework pushed');
    assert.match(github.comments.at(-1).body, /^\*\*Reworked\*\*: Updated CI-R10\.\n\n- _update CI-R10_: Done in plan\.md\.[\s\S]*consistency check runs again/);
    assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs.round, github.repo.runs.at(-1).inputs.mode], ['speckit-analyze.lock.yml', '0', 'loop']);

    const answered = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    assert.equal((await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'revise', SPECKIT_AMENDMENT: '11', SPECKIT_CHECK_RUN: String(answered.id) }), log: silent })).outcome, 'checking');
    assert.equal(github.repo.checkRuns.find((run) => run.id === answered.id).output.title, 'Rework finished without changes');
    assert.match(github.comments.at(-1).body, /^\*\*Answered without changes\*\*: Updated CI-R10\./);

    const refused = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    const runs = github.repo.runs.length;
    assert.equal((await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'revise', SPECKIT_AMENDMENT: '11', SPECKIT_CHECK_RUN: String(refused.id), SPECKIT_PUSH_FAILURES: '1' }), log: silent })).outcome, 'push failed');
    assert.deepEqual([github.repo.checkRuns.find((run) => run.id === refused.id).conclusion, github.repo.checkRuns.find((run) => run.id === refused.id).output.title], ['failure', 'The rework could not push its changes']);
    assert.match(github.comments.at(-1).body, /^The rework could not push its changes to this pull request, so nothing changed\./);
    assert.equal(github.repo.runs.length, runs, 'no check of unchanged files');

    const failing = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    const fixed = await runRecord({ client: github, env: env({ SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'fix', SPECKIT_AMENDMENT: '11', SPECKIT_ROUND: '2', SPECKIT_CHECK_RUN: String(failing.id) }), log: silent });
    assert.equal(fixed.outcome, 'failed');
    assert.deepEqual([github.repo.checkRuns.find((run) => run.id === failing.id).conclusion, github.repo.checkRuns.find((run) => run.id === failing.id).output.title], ['failure', 'Correction round 2 did not finish']);
    assert.ok(github.comments.some((comment) => comment.number === 11 && /correction round 2 did not produce a result: the agent did not report\. Comment on this pull request to try again\./.test(comment.body)));
    assert.match(github.comments.at(-1).body, /^\*\*Amendment #11 is not consistent\*\* \(correction round 2 did not finish\)/, 'a failed correction round hands the amendment to the requester');
    assert.deepEqual(github.repo.reviewRequests, [{ number: 11, reviewers: ['dev'] }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
