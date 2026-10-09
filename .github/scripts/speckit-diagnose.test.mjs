import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { CHECK_DIAGNOSED, CHECK_DIAGNOSING, CHECK_RUN_NAME, DIAGNOSIS_COMMENT_MARKER } from './speckit-implement-core.mjs';
import { main, renderDiagnosisComment, runCheckAmendment, runEvidence, runRecord, startDiagnosis, validateReport } from './speckit-diagnose.mjs';
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
    { title: 'Apply the amendment', command: '/speckit apply', recommended: true },
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
  assert.deepEqual(github.repo.runs.at(-1).inputs, { twin: '5', pull: '9', folder: 'f', notes: 'look at R6', previous: '', check_run: String(check.id) });
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
  assert.equal(ok.report.options[0].title, 'Apply the amendment', 'the recommended option goes first');
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
    const comment = github.comments.at(-1);
    assert.equal(comment.number, 9);
    assert.ok(comment.body.startsWith(DIAGNOSIS_COMMENT_MARKER));
    assert.match(comment.body, /\*\*Diagnosis\*\*: the spec artifacts need an amendment \(`artifacts`, confidence high\)/);
    assert.match(comment.body, /&lt;!-- speckit-implement:resume -->/, 'agent text cannot carry workflow markers');
    assert.match(comment.body, /\*\*Proposed amendment\*\*: #11[\s\S]*`specs\/f\/research\.md`: refine R6[\s\S]*Note: it adds T003/);
    assert.match(comment.body, /\*\*Questions\*\*[\s\S]*Keep the counter write\?\n {3}- Yes/);
    assert.match(comment.body, /Actions outside these commands[\s\S]*Ask the platform team/);
    assert.ok(comment.body.indexOf('**Recommended:** Apply the amendment') < comment.body.indexOf('/speckit resume Use one statement'));
    assert.match(comment.body, /\/speckit revise <notes>[\s\S]*\/speckit discard/);
    assert.match(comment.body, /\[Diagnosis run\]\(https:\/\/github\.com\/octo\/repo\/actions\/runs\/42\)/);
    const done = github.repo.checkRuns.find((run) => run.id === check.id);
    assert.deepEqual([done.status, done.conclusion, done.external_id, done.output.title], ['completed', 'neutral', CHECK_DIAGNOSED, 'Waiting for a decision: artifacts']);
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

test('records an unusable or missing report and an amendment that breaks the rules', async () => {
  const github = diagnosisRepo({ amendmentFiles: ['src/Program.cs', 'specs/f/tasks.md'], after: '## P\n- [x] T001 Done\n- [x] T002 Next\n' });
  const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'in_progress', external_id: CHECK_DIAGNOSING });
  await runRecord({ client: github, env: env({ SPECKIT_CHECK_RUN: String(check.id) }), log: silent });
  assert.match(github.comments.at(-1).body, /did not produce a usable report\*\*: the agent did not report/);
  assert.equal(github.repo.checkRuns.find((run) => run.id === check.id).conclusion, 'failure');

  const comment = renderDiagnosisComment({ report: validateReport(REPORT).report, round: 2, amendment: { number: 11, head: { ref: 'speckit-amend/f' }, base: { ref: 'speckit/f' } }, amendmentCheck: { reasons: ['it changes `src/Program.cs`'], notes: [] } });
  assert.match(comment, /\*\*Diagnosis \(round 2\)\*\*/);
  assert.match(comment, /It cannot be applied: it changes `src\/Program\.cs`/);
  assert.doesNotMatch(comment, /\/speckit apply/, 'an invalid amendment is not offered for applying');
});

test('checks an edited amendment pull request', async () => {
  const github = diagnosisRepo();
  const valid = await runCheckAmendment({ client: github, env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_PULL: '11' }, log: silent });
  assert.deepEqual(valid.reasons, []);
  assert.deepEqual([github.repo.checkRuns.at(-1).name, github.repo.checkRuns.at(-1).conclusion], ['Spec Kit amendment', 'success']);
  github.repo.pullFiles[11] = ['specs/other/spec.md'];
  const invalid = await runCheckAmendment({ client: github, env: { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_PULL: '11' }, log: silent });
  assert.equal(invalid.reasons.length, 1);
  assert.equal(github.repo.checkRuns.at(-1).conclusion, 'failure');
  await assert.rejects(() => main(['nope'], { env: {}, client: github }), /Usage/);
  await assert.rejects(() => runEvidence({ client: github, env: { SPECKIT_TWIN: 'x' }, log: silent }), /SPECKIT_TWIN/);
});
