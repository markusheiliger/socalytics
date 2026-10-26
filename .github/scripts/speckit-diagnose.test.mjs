import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { pendingFeedback, renderFeedback } from './speckit-amend.mjs';
import { ATTENTION_LABEL, AUTO_CONTINUE_MARKER, IMPLEMENT_WORKFLOW_FILE, RETRY_COMMENT_MARKER, SYNC_COMMENT_MARKER, parseGuidance, renderRetryComment, syncBarrier, unchangedRestarts, CHECK_DIAGNOSED, CHECK_DIAGNOSING, CHECK_RUN_NAME, COMMAND_REFERENCE_TITLE, DIAGNOSIS_COMMENT_MARKER, MAX_WRITER_GUIDANCE, PR_COMMANDS, RECOMMENDABLE_COMMANDS, renderCommandReference, renderRepairMarker, renderSyncComment, repeatedFailure, isTwinRunOf } from './speckit-implement-core.mjs';
import { copilotRequestOf } from './speckit-implement-core.mjs';
import { CATEGORIES, CATEGORY_COMMANDS, MAX_RECOMMENDED_COMMAND, MAX_STOP_SUBJECT, categoryCommands, checkReportFile, commandPatterns, main, recommendNextSteps, removePlaceholderArgument, renderDiagnosisComment, runEvidence, runRecord, startDiagnosis, validateReport } from './speckit-diagnose.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const env = (extra = {}) => ({ GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_FOLDER: 'f', ...extra });

const REPORT = {
  category: 'artifacts',
  confidence: 'high',
  summary: 'The design leaves open how the counter write is issued.',
  cause: 'UserManager.UpdateAsync adds a lookup on one path only.',
  evidence: ['Attempt 1: D=0.55', 'Attempt 3: D=0.9'],
  artifacts: [{ file: 'specs/f/research.md', reason: 'refine R6' }],
  options: [
    { title: 'Retry with guidance', why: 'The agent took the slow path on one refusal path.', command: '/speckit retry Use one statement', recommended: false },
    { title: 'Review and merge the amendment', why: 'R6 leaves the counter write open.', command: null, recommended: true },
    { title: 'Ask the platform team', why: 'They own the counter.', command: null },
  ],
  questions: [{ question: 'Keep the counter write?', choices: ['Yes', 'No'] }],
};

test('starts a diagnosis with a linked check and dispatch, and leaves a failed dispatch retryable', async () => {
  const github = diagnosisRepo();
  const started = await startDiagnosis(github, env(), { twin: 5, pull: 9, folder: 'f', head: 'sha-impl', guidance: 'look at R6', actor: 'dev' });
  assert.equal(started.started, true);
  assert.equal(github.repo.pulls.find((pull) => pull.number === 11).state, 'closed', 'the previous amendment is superseded');
  const check = github.repo.checkRuns.at(-1);
  assert.deepEqual([check.external_id, check.status, check.output.title], [CHECK_DIAGNOSING, 'in_progress', 'Diagnosis in progress']);
  assert.equal(check.details_url, 'https://github.com/octo/repo/actions/workflows/speckit-diagnose.lock.yml');
  assert.match(check.output.summary, /Started by @dev\.\n\nGuidance: look at R6/);
  assert.deepEqual(github.repo.runs.at(-1).inputs, { issue: '5', pull: '9', folder: 'f', guidance: 'look at R6', previous: '', check_run: String(check.id), mode: 'diagnose', amendment: '', round: '0', stop: '' });
  assert.equal(github.repo.runs.at(-1).workflow, 'speckit-diagnose.lock.yml');
  const attention = () => (github.repo.pulls.find((pull) => pull.number === 9).labels ?? []).some((label) => label.name === ATTENTION_LABEL);
  assert.equal(attention(), false, 'a running diagnosis does not need attention');

  github.dispatchWorkflow = async () => { throw new Error('workflow not found'); };
  const failed = await startDiagnosis(github, env(), { twin: 5, pull: 9, folder: 'f', head: 'sha-impl', auto: true });
  assert.deepEqual([failed.started, failed.error], [false, 'workflow not found']);
  const failedCheck = github.repo.checkRuns.at(-1);
  assert.deepEqual([failedCheck.external_id, failedCheck.status, failedCheck.conclusion, failedCheck.output.title], [CHECK_DIAGNOSING, 'completed', 'failure', 'Diagnosis could not start']);
  assert.equal(attention(), false, 'a diagnosis that could not start remains automation-owned and retryable');
});

test('names the diagnose run after the latest stop on the head', async () => {
  const github = diagnosisRepo();
  const stopCheck = (title) => github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'completed', conclusion: 'failure', external_id: 'speckit:limit', output: { title, summary: 'stopped' } });
  await stopCheck('T002 reached the attempt limit');
  await stopCheck('The phase\nverification failed');
  await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'completed', conclusion: 'success', external_id: 'speckit:attempt:task:T003:1', output: { title: 'T003 attempt 1 succeeded' } });
  await startDiagnosis(github, env(), { twin: 5, pull: 9, folder: 'f', head: 'sha-impl', auto: true });
  const run = github.repo.runs.at(-1);
  assert.equal(run.inputs.stop, 'The phase verification failed', 'the latest stop, on one line; other check runs do not count');
  assert.equal(run.display_title, 'Spec Kit diagnose spec #5 · The phase verification failed');
  assert.ok(isTwinRunOf('speckit-diagnose.lock.yml', run.display_title, 5), 'the run is still found for its twin');
  await stopCheck(`T004 ${'x'.repeat(300)}`);
  await startDiagnosis(github, env(), { twin: 5, pull: 9, folder: 'f', head: 'sha-impl', auto: true });
  assert.equal(github.repo.runs.at(-1).inputs.stop.length, MAX_STOP_SUBJECT + 1, 'a long stop is clipped');
});

test('collects evidence: checks, comments, failed run logs, branch state, and notes', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-evidence-'));
  try {
    const github = diagnosisRepo({ amendment: false });
    github.repo.commits['sha-main2'] = { tree: 't', parents: ['sha-main'] };
    github.repo.branches.main = 'sha-main2';
    await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'completed', conclusion: 'failure', external_id: 'speckit:limit', output: { title: 'T002 reached the attempt limit', summary: 'stopped' } });
    await github.createComment(9, '**T002 attempt 3 failed:** timing test');
    await github.dispatchWorkflow(IMPLEMENT_WORKFLOW_FILE, 'main', { issue: '5', pull: '9', title: 'F' });
    github.completeRun(github.repo.runs.at(-1).id, 'failure');
    github.repo.jobs = { [github.repo.runs.at(-1).id]: [{ id: 77, name: 'Implement and verify', conclusion: 'failure' }, { id: 78, name: 'begin', conclusion: 'success' }] };
    github.repo.logs = { 77: '2026-10-08T19:11:13.1234567Z line one\n2026-10-08T19:11:14.1234567Z Failed! D=0.9' };
    await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_GUIDANCE: 'see research R6' }), log: silent });
    const evidence = readFileSync(path.join(dir, 'evidence.md'), 'utf8');
    assert.match(evidence, /Next unchecked task: T002 Next/);
    assert.match(evidence, /Base branch `main` \(the pull request's base, which the implementation merges into\): 1 commit\(s\) the implementation branch lacks; the branch is 1 commit\(s\) ahead/);
    assert.match(evidence, /see research R6/);
    assert.match(evidence, /speckit:limit completed\/failure: T002 reached the attempt limit — stopped/);
    assert.match(evidence, /T002 attempt 3 failed/);
    assert.match(evidence, /#### Job "Implement and verify"\n\n```text\nline one\nFailed! D=0\.9\n```/);
    assert.doesNotMatch(evidence, /Job "begin"/);
    const context = JSON.parse(readFileSync(path.join(dir, 'context.json'), 'utf8'));
    assert.deepEqual([context.implementationBranch, context.amendmentBranch, context.nextTask], ['speckit/f', 'speckit-amend/f', 'T002']);
    // The live command reference: every recommendable command with what it does on this pull request.
    assert.match(evidence, new RegExp(`## ${COMMAND_REFERENCE_TITLE}\\n`));
    for (const command of PR_COMMANDS.filter((item) => item.recommend)) assert.ok(evidence.includes(`### \`${command.usage}\``), command.name);
    assert.doesNotMatch(evidence, /### `\/speckit help/);
    assert.match(evidence, /No repair is pending on this pull request\./);
    assert.match(evidence, /Does not merge `main`: the implementation continues with what stopped/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('evidence: with a pending repair, the reference says what retry does with and without guidance', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-evidence-repair-'));
  try {
    const github = diagnosisRepo({ amendment: false });
    await github.createComment(9, `${renderRepairMarker({ id: 'phase:1:sha-impl', state: 'pending', scope: 'phase', tasks: ['T001', 'T002'], head: 'sha-impl', at: '2026-10-18T10:00:00Z' })}\npending`);
    await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir }), log: silent });
    const evidence = readFileSync(path.join(dir, 'evidence.md'), 'utf8');
    assert.match(evidence, /A \*\*phase repair is pending\*\* on this pull request \(T001, T002\)\./);
    assert.match(evidence, /- What it does with guidance: Does not merge `main`; runs a repair agent with the guidance, then reverifies the pending phase verification/);
    assert.match(evidence, /- What it does without guidance: Does not merge `main`; reverifies without running a repair agent the pending phase verification/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// Everything diagnose knows about the `/speckit` commands comes from PR_COMMANDS; these tests fail when the diagnose
// skill, its workflow prompt, or the derived lists fall behind a new, changed, or removed command.
const SKILL_SOURCE = readFileSync(new URL('../../.specify/extension-src/gha/commands/speckit.gha.diagnose.md', import.meta.url), 'utf8');
const DIAGNOSE_PROMPT = readFileSync(new URL('../workflows/speckit-diagnose.md', import.meta.url), 'utf8');
const mentionedVerbs = (text) => [...new Set([...text.matchAll(/\/speckit ([a-z][a-z-]*)/g)].map((match) => match[1]))];

test('drift: the diagnose skill names every recommendable command and flag, and no unknown command', () => {
  const known = PR_COMMANDS.map((command) => command.name);
  for (const command of PR_COMMANDS.filter((item) => item.recommend)) {
    assert.ok(SKILL_SOURCE.includes(`/speckit ${command.name}`), `the skill names /speckit ${command.name}`);
    for (const flag of Object.keys(command.flags ?? {})) assert.ok(SKILL_SOURCE.includes(flag), `the skill names ${flag}`);
  }
  for (const [label, text] of [['skill', SKILL_SOURCE], ['workflow prompt', DIAGNOSE_PROMPT]]) {
    assert.deepEqual(mentionedVerbs(text).filter((verb) => !known.includes(verb)), [], `${label} names only existing commands`);
  }
  assert.ok(SKILL_SOURCE.includes(COMMAND_REFERENCE_TITLE), 'the skill points to the generated command reference');
  assert.match(SKILL_SOURCE, /speckit-diagnose\.mjs commands/, 'the interactive skill prints the reference');
  // The installed skill is generated from the source; it must not be stale.
  const installed = readFileSync(new URL('../skills/speckit-gha-diagnose/SKILL.md', import.meta.url), 'utf8');
  assert.ok(installed.replace(/\r\n/g, '\n').includes(SKILL_SOURCE.replace(/\r\n/g, '\n').replace(/^---[\s\S]*?---\s*/, '').trim().slice(0, 2000)), 'reinstall the gha extension after changing the diagnose skill');
});

test('drift: the diagnosis command lists are derived from PR_COMMANDS', () => {
  assert.deepEqual(CATEGORY_COMMANDS, categoryCommands(PR_COMMANDS));
  assert.deepEqual(Object.keys(CATEGORY_COMMANDS), CATEGORIES);
  for (const verbs of Object.values(CATEGORY_COMMANDS)) for (const verb of verbs) assert.ok(RECOMMENDABLE_COMMANDS.includes(verb), verb);
  assert.ok(!RECOMMENDABLE_COMMANDS.includes('help'));
  for (const removed of ['/speckit diagnose x', '/speckit resume x', '/speckit help']) {
    assert.equal(validateReport({ ...REPORT, options: [{ title: 'Old', why: 'x', command: removed }] }).report.options[0].command, null, removed);
  }
  // A new command needs no edit in diagnose: it becomes recommendable, fits its categories, and is in the reference.
  const added = [...PR_COMMANDS, { name: 'rebase', usage: '/speckit rebase [guidance]', flags: {}, help: 'rebase the branch', recommend: true, categories: ['outside'], whenToRecommend: 'when rebasing helps', guidanceScope: 'the rebase' }];
  assert.ok(commandPatterns(added.filter((item) => item.recommend).map((item) => item.name)).command.test('/speckit rebase onto main'));
  assert.ok(categoryCommands(added).outside.includes('rebase'));
  assert.match(renderCommandReference({ commands: added }), /### `\/speckit rebase \[guidance\]`[\s\S]*- What it does: rebase the branch/);
});

test('a recommended command may carry the guidance the commands accept', () => {
  const guidance = (length) => 'x'.repeat(length);
  const command = (length) => validateReport({ ...REPORT, options: [{ title: 'Retry', why: 'x', command: `/speckit retry --force ${guidance(length)}`, recommended: true }] }).report.options[0].command;
  assert.ok(MAX_RECOMMENDED_COMMAND >= '/speckit retry --force '.length + MAX_WRITER_GUIDANCE);
  assert.notEqual(command(MAX_WRITER_GUIDANCE), null, 'guidance at the command limit is accepted');
  assert.equal(command(MAX_RECOMMENDED_COMMAND), null, 'longer guidance than the commands accept is rejected');
});

test('commands prints the command reference', async () => {
  const lines = [];
  assert.equal(await main(['commands'], { log: (line) => lines.push(line) }), 0);
  assert.match(lines.join('\n'), new RegExp(`^## ${COMMAND_REFERENCE_TITLE}`));
  for (const verb of RECOMMENDABLE_COMMANDS) assert.match(lines.join('\n'), new RegExp(`### \`/speckit ${verb}`));
});

test('validates the agent report and drops invalid commands', () => {
  const ok = validateReport(JSON.stringify(REPORT));
  assert.deepEqual(ok.reasons, []);
  assert.equal(ok.report.options[0].title, 'Review and merge the amendment', 'the recommended option goes first');
  const bad = validateReport({ ...REPORT, category: 'magic', options: [{ title: 'Delete', command: '/speckit nuke' }, { title: 'Two lines', command: '/speckit retry a\nb' }] });
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
    assert.match(comment.body, /\*\*Proposed amendment\*\*: Amend: F \(#11\) \(`speckit-amend\/f` → `speckit\/f`\)[\s\S]*checks it for consistency first, and correction rounds fix what it finds while they make progress[\s\S]*`specs\/f\/research\.md`: refine R6/);
    assert.match(comment.body, /\*\*Questions\*\* \(the recommended choice is listed first\)[\s\S]*Keep the counter write\?\n {3}- Yes/);
    assert.doesNotMatch(comment.body, /Actions outside these commands/);
    assert.equal(comment.body.match(/\*\*Recommended next step\*\*/g).length, 1, 'exactly one recommendation');
    assert.match(comment.body, /_`artifacts` means:_ The spec, plan, or tasks are wrong or incomplete/);
    assert.match(comment.body, /\*\*Recommended next step\*\*: Review and merge the amendment #11: Amend: F \(being checked for consistency first\)\.\n\n- \*\*Why\*\*: [^\n]+\n- \*\*What it does\*\*: Merging applies the amendment[\s\S]*<summary>Other options<\/summary>[\s\S]*- \*\*Retry with guidance\*\*\n {2}- Why: The agent took the slow path on one refusal path\.\n {2}- What it does: Does not merge `main`[\s\S]*\/speckit retry Use one statement[\s\S]*- \*\*Ask the platform team\*\*\n {2}- Why: They own the counter\./);
    assert.doesNotMatch(comment.body, /- \*\*Review and merge the amendment\*\*/, 'the open amendment is the recommendation');
    assert.doesNotMatch(comment.body, /\/speckit (apply|revise|discard)/);
    assert.match(comment.body, /\[Diagnosis run\]\(https:\/\/github\.com\/octo\/repo\/actions\/runs\/42\)/);
    const done = github.repo.checkRuns.find((run) => run.id === check.id);
    assert.deepEqual([done.status, done.conclusion, done.external_id, done.output.title], ['completed', 'neutral', CHECK_DIAGNOSED, 'Waiting for a decision: artifacts']);

    // The amendment goes into the consistency check before anyone is asked to review it.
    const amendment = github.repo.pulls.find((pull) => pull.number === 11);
    assert.match(amendment.body, /<!-- speckit-amend:status -->\n⏳ \*\*Being checked for consistency\.\*\*[\s\S]*\*\*Recommended next step\*\*: Wait for the consistency check[\s\S]*<!-- \/speckit-amend:status -->\n\namend$/);
    assert.match(github.comments.find((item) => item.number === 11).body, /^<!-- speckit-amend:rework -->\n<!-- speckit-amend:cutoff \S+ -->\nThis amendment comes from the diagnosis on #9[\s\S]*handed to @dev for review/);
    const analysis = github.repo.checkRuns.at(-1);
    assert.deepEqual([analysis.name, analysis.head_sha, analysis.status, analysis.external_id], ['Spec Kit amendment', 'sha-amend', 'in_progress', 'speckit:amend-checking']);
    assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-analyze.lock.yml', { issue: '5', pull: '9', folder: 'f', amendment: '11', round: '0', stalls: '0', previous: '', mode: 'loop', check_run: String(analysis.id), kind: 'amend' }]);
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
    const withheld = flagged.comments.find((comment) => comment.number === 9);
    assert.match(withheld.body, /did not produce a usable report\*\*: it was withheld because threat detection did not pass \(result: failure\)/);
    assert.doesNotMatch(withheld.body, /Proposed amendment/);

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
  assert.match(github.comments.find((comment) => comment.number === 9).body, /did not produce a usable report\*\*: the agent did not report/);
  assert.equal(github.repo.checkRuns.find((run) => run.id === check.id).conclusion, 'failure');
  assert.equal(github.repo.runs.at(-1).workflow, 'speckit-diagnose.lock.yml', 'an unusable report starts another diagnosis automatically');

  const comment = renderDiagnosisComment({ report: validateReport(REPORT).report, round: 2 });
  assert.match(comment, /\*\*Diagnosis \(round 2\)\*\*/);
  assert.match(comment, /_No amendment pull request was created\._/);
  assert.match(comment, /\*\*Recommended next step\*\*: Wait for another diagnosis to create the amendment\./, 'without an amendment pull request, another diagnosis must create it');
  assert.doesNotMatch(comment, /\/speckit diagnose/);
  assert.match(comment, /_Parts of the report were adjusted: the recommended option "Review and merge the amendment" does not fit the category `artifacts`/);
  const outside = renderDiagnosisComment({
    report: validateReport({
      ...REPORT,
      category: 'outside',
      options: [
        { title: 'Retry on a less loaded runner. Unlikely to succeed.', why: 'Timing only.', command: '/speckit retry Keep going', recommended: false },
        { title: 'Fix the flaky test outside this spec.', why: 'The test belongs to another feature.', command: '/speckit author Make the flaky test robust', recommended: true },
      ],
    }).report,
    baseBranch: 'main',
  });
  assert.match(outside, /\*\*Recommended next step\*\*: Fix the flaky test outside this spec\.\n\n- \*\*Why\*\*: The test belongs to another feature\.\n- \*\*What it does\*\*: The Spec Kit workflow `author` writes a new spec[\s\S]*merged automatically[\s\S]*```text\n\/speckit author Make the flaky test robust\n```/);
  assert.deepEqual(validateReport({ ...REPORT, category: 'outside', options: [{ title: 'Sync', why: 'x', command: '/speckit sync' }] }).reasons, []);
  assert.match(outside, /<summary>Other options<\/summary>[\s\S]*- \*\*Retry on a less loaded runner\. Unlikely to succeed\*\*\n/, 'alternatives are collapsed, without doubled punctuation');
  assert.match(validateReport({ ...REPORT, options: [{ title: 'Apply', why: 'x', command: '/speckit apply' }] }).reasons.join(), /option "Apply" has an invalid command/);
  assert.match(validateReport({ ...REPORT, options: [{ title: 'Sync hard', why: 'x', command: '/speckit sync --force' }] }).reasons.join(), /option "Sync hard" has an invalid command/, 'a flag the command does not support');
  assert.deepEqual(validateReport({ ...REPORT, options: [{ title: 'Insist', why: 'x', command: '/speckit retry --force Name both navigation specs' }] }).reasons, []);
});

// The report of diagnosis round 5 on PR #75 (run 38448806133): `retry` with high confidence, but its options are plain
// text, and the one marked as recommended asks for a resume. It used to render a placeholder command.
const PR75_REPORT = {
  category: 'retry',
  confidence: 'high',
  summary: 'The phase verification failed on one timing-sensitive test from another feature. The Registry work is not the cause, and the spec artifacts are fine.',
  cause: 'UploadSessionExpiryTests.ExpiredSessionIsRefusedImmediatelyAndReleasedBySweep sets SessionLifetime to 2 s. On the loaded runner the presigned URL had already expired.',
  evidence: ['Only one test failed, 1 of 851 in Integration.Tests, at UploadSessionExpiryTests.cs:63.'],
  artifacts: [],
  options: [
    "Run '/speckit retry' so the phase verification runs again. A single rerun will very likely pass, because the failure is timing-dependent. This is the recommended option.",
    "If it fails again, fix the flaky test on the base branch, for example by using a longer SessionLifetime or by uploading before the clock is advanced, then run '/speckit sync'.",
  ],
  questions: [],
};

test('a retry diagnosis recommends a retry with guidance, also when the agent wrote its options as text', () => {
  const { reasons, report } = validateReport(JSON.stringify(PR75_REPORT));
  assert.equal(report.options.length, 2, 'plain-text options are read, not dropped');
  assert.deepEqual(report.options.map((option) => [option.command, option.recommended]), [['/speckit retry', true], ['/speckit sync', false]]);
  assert.match(reasons.join('\n'), /option 1 was plain text instead of an object; it was read as "Run '\/speckit retry' so the phase verification runs again\."/);
  const comment = renderDiagnosisComment({ report, reasons, round: 5, baseBranch: 'main', continuation: { text: 'continues with the convergence (the failed verification is not repeated by itself; the next verification, at the latest the full verification before the merge, covers it)', mergesBase: true } });
  assert.match(comment, /^<!-- speckit-implement:diagnosis -->\n\*\*Diagnosis \(round 5\)\*\*: a retry is likely to succeed \(`retry`, confidence high\)\.\n\n_`retry` means:_ The failure was transient/);
  assert.match(comment, /\*\*Recommended next step\*\*: Run '\/speckit retry' so the phase verification runs again\.\n\n- \*\*Why\*\*: A single rerun will very likely pass, because the failure is timing-dependent\.\n- \*\*What it does\*\*: The implementation continues with the convergence[\s\S]*normal synchronization boundary[\s\S]*```text\n\/speckit retry\n```/);
  assert.doesNotMatch(comment, /\/speckit diagnose \[notes\]/);
  assert.doesNotMatch(comment, /\/speckit resume/);
  for (const fence of comment.matchAll(/```text\n([\s\S]*?)```/g)) assert.doesNotMatch(fence[1], /<[A-Za-z][^>]*>|\[[A-Za-z][^\]]*\]/, `no placeholder in a command: ${fence[1]}`);
});

test('the recommended command always fits the category', () => {
  const base = { ...REPORT, options: [], questions: [] };
  const recommendation = (report) => recommendNextSteps(validateReport(report).report).options.find((option) => option.recommended);
  assert.match(recommendation({ ...base, category: 'retry' }).command, /^\/speckit retry The diagnosis found: The design leaves open/);
  assert.match(recommendation({ ...base, category: 'outside' }).command, /^\/speckit author The design leaves open how the counter write is issued\. UserManager/);
  assert.equal(recommendation({ ...base, category: 'unknown' }).kind, 'wait');
  assert.equal(recommendation({ ...base, category: 'decision', questions: [{ question: 'Keep the counter write?', choices: ['Yes, keep it.', 'No'] }, { question: 'Split?', choices: ['No'] }] }).command, '/speckit retry 1. Yes, keep it. 2. No.');
  const agentRetry = { title: 'Retry and keep the helper', why: 'The agent dropped the helper.', command: '/speckit retry Keep the helper in UserManager', recommended: true };
  assert.equal(recommendation({ ...base, category: 'retry', options: [agentRetry] }).command, '/speckit retry Keep the helper in UserManager', 'a fitting agent recommendation keeps its guidance');
  const promoted = recommendation({ ...base, category: 'outside', options: [{ ...agentRetry, recommended: true }, { title: 'Fix the test on main', why: 'It is flaky.', command: '/speckit author Use a longer SessionLifetime', recommended: false }] });
  assert.equal(promoted.command, '/speckit author Use a longer SessionLifetime', 'a fitting option of the agent is preferred over the default');

  const decision = renderDiagnosisComment({ report: validateReport({ ...base, category: 'decision', questions: [{ question: 'Keep the counter write?', choices: ['Yes', 'No'] }] }).report });
  assert.match(decision, /\*\*Questions\*\* \(the recommended next step answers them with the first, recommended choice of each; change the answers before posting if you decide otherwise\)/);
  assert.match(decision, /```text\n\/speckit retry 1\. Yes\.\n```/);
});

test('validates options: placeholders, a missing why, and a single recommendation', () => {
  assert.equal(removePlaceholderArgument('/speckit retry <guidance>'), '/speckit retry');
  assert.equal(removePlaceholderArgument('/speckit diagnose [guidance]'), '/speckit diagnose');
  assert.equal(removePlaceholderArgument('/speckit retry Use <guidance> here'), null);
  assert.equal(removePlaceholderArgument('/speckit retry Read items[0] first'), '/speckit retry Read items[0] first');
  assert.equal(removePlaceholderArgument('/speckit retry Return IReadOnlyList<Recording> from the query handler'), '/speckit retry Return IReadOnlyList<Recording> from the query handler', 'code is no placeholder');
  assert.equal(removePlaceholderArgument('/speckit retry Run the [P] tasks of [US1] one after another'), '/speckit retry Run the [P] tasks of [US1] one after another', 'task tags are no placeholders');
  assert.equal(removePlaceholderArgument('/speckit author <what to fix>'), '/speckit author');
  const { reasons, report } = validateReport({
    ...REPORT,
    options: [
      { title: 'Retry', command: '/speckit retry <guidance>', recommended: true },
      { title: 'Retry again', why: 'x', command: '/speckit retry Use <guidance> now', recommended: true },
    ],
  });
  assert.deepEqual(report.options.map((option) => [option.command, option.recommended]), [['/speckit retry', true], [null, false]]);
  assert.match(reasons.join('\n'), /option "Retry" does not say why it helps/);
  assert.match(reasons.join('\n'), /option "Retry again" has an invalid command/);
  assert.match(reasons.join('\n'), /option "Retry again" was marked as recommended too/);
});

test('check-report prints the problems and the comment, and fails until the report is clean', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-check-'));
  try {
    const lines = [];
    const file = path.join(dir, 'report.json');
    writeFileSync(file, JSON.stringify(PR75_REPORT));
    assert.equal(checkReportFile(file, (line) => lines.push(line)), 1);
    assert.match(lines.join('\n'), /^Fix these problems before you report:\n- option 1 was plain text[\s\S]*The comment would read:[\s\S]*\/speckit retry/);
    writeFileSync(file, JSON.stringify({ ...PR75_REPORT, options: [{ title: 'Retry the convergence', why: 'Timing only.', command: '/speckit retry Rerun unchanged.', recommended: true }] }));
    lines.length = 0;
    assert.equal(checkReportFile(file, (line) => lines.push(line)), 0);
    assert.match(lines[0], /^The report is valid\./);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('PR #123: publication rejects retry classification with a sync recommendation instead of generating a retry', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-sync-report-'));
  try {
    const output = path.join(dir, 'agent_output.json');
    const report = {
      ...REPORT,
      category: 'retry',
      artifacts: [],
      summary: 'Restore fails with NU1902; the dependency fix is already on the base branch.',
      options: [{ title: 'Merge the fixed base', why: 'The implementation lacks the dependency pin.', command: '/speckit sync', recommended: true }],
    };
    const github = diagnosisRepo({ amendment: false });
    const check = await github.createCheckRun({ name: CHECK_RUN_NAME, head_sha: 'sha-impl', status: 'in_progress', external_id: CHECK_DIAGNOSING });
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report }] }));
    const rejected = await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_CHECK_RUN: String(check.id) }), log: silent });
    assert.equal(rejected.report, null);
    assert.match(rejected.reasons.join(), /does not fit the category `retry`/);
    assert.match(github.comments.at(-1).body, /did not produce a usable report/);
    assert.doesNotMatch(github.comments.at(-1).body, /\/speckit retry The diagnosis found/);
    assert.equal(github.repo.checkRuns.find((item) => item.id === check.id).conclusion, 'failure');

    report.category = 'outside';
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report }] }));
    const accepted = await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output }), log: silent });
    assert.equal(accepted.report.category, 'outside');
    const diagnosis = github.comments.filter((comment) => comment.body.startsWith(DIAGNOSIS_COMMENT_MARKER)).at(-1);
    assert.match(diagnosis.body, /```text\n\/speckit sync\n```/);
    assert.doesNotMatch(diagnosis.body, /\/speckit retry The diagnosis found/);
    assert.equal(accepted.continued, 'sync');

    report.options = [];
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report }] }));
    const missing = await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output }), log: silent });
    assert.equal(missing.report, null);
    assert.match(missing.reasons.join(), /must recommend one actionable command/);
    const reportFile = path.join(dir, 'report.json');
    writeFileSync(reportFile, JSON.stringify(report));
    assert.equal(checkReportFile(reportFile, silent), 1, 'self-check also rejects a missing actionable recommendation');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('collects the findings to fix and the feedback to address for a rework', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-evidence-'));
  try {
    const github = diagnosisRepo();
    await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_MODE: 'fix', SPECKIT_AMENDMENT: '11', SPECKIT_ROUND: '2', SPECKIT_STALLS: '1', SPECKIT_GUIDANCE: '- **HIGH** `plan.md` CI-R10: stale' }), log: silent });
    const fix = readFileSync(path.join(dir, 'evidence.md'), 'utf8');
    assert.ok(!fix.includes(COMMAND_REFERENCE_TITLE), 'a rework recommends no command');
    assert.match(fix, /Mode: `fix` \(correction round 2; rounds without progress so far: 1 of 3\); amendment pull request #11 on `speckit-amend\/f`/);
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
    await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_MODE: 'revise', SPECKIT_AMENDMENT: '11', SPECKIT_GUIDANCE: renderFeedback(items) }), log: silent });
    const revise = readFileSync(path.join(dir, 'evidence.md'), 'utf8');
    assert.ok(!revise.includes(COMMAND_REFERENCE_TITLE), 'a rework recommends no command');
    assert.match(revise, /## Feedback on the amendment to address\n\n### comment by @reviewer[\s\S]*Please also update CI-R10\.[\s\S]*### review \(changes requested\) by @reviewer[\s\S]*See the lines\.[\s\S]*- `specs\/f\/plan\.md` line 477: Stale sentence\./);
    assert.match(revise, /## Earlier comments on the amendment \(oldest first\)[\s\S]*Please also update CI-R10\./);
    await assert.rejects(() => runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_MODE: 'fix' }), log: silent }), /needs SPECKIT_AMENDMENT/);
    await assert.rejects(() => main(['nope'], { env: {}, client: github }), /Usage/);
    await assert.rejects(() => runEvidence({ client: github, env: { SPECKIT_TWIN: 'x' }, log: silent }), /SPECKIT_TWIN/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('a correction round hands its findings to the next analysis, and a refused push retries until the budget is spent', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-fix-'));
  try {
    const github = diagnosisRepo();
    const findings = JSON.stringify([{ id: 'F1', severity: 'HIGH', location: 'plan.md', summary: 'stale' }]);
    const output = path.join(dir, 'agent_output.json');
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report: JSON.stringify({ summary: 'Fixed CI-R10.' }) }] }));
    const fixEnv = (extra) => env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'fix', SPECKIT_AMENDMENT: '11', SPECKIT_ROUND: '2', SPECKIT_STALLS: '1', SPECKIT_FINDINGS: findings, SPECKIT_GUIDANCE: '- [F1] **HIGH** stale', ...extra });

    const pushed = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    assert.equal((await runRecord({ client: github, env: fixEnv({ SPECKIT_CHECK_RUN: String(pushed.id), SPECKIT_PUSH_SHA: 'sha-amend' }), log: silent })).outcome, 'checking');
    assert.equal(github.repo.checkRuns.find((run) => run.id === pushed.id).output.title, 'Correction round 2 pushed');
    const analysis = github.repo.runs.at(-1);
    assert.deepEqual([analysis.workflow, analysis.inputs.mode, analysis.inputs.round, analysis.inputs.stalls, analysis.inputs.previous], ['speckit-analyze.lock.yml', 'loop', '2', '1', findings]);

    writeFileSync(output, JSON.stringify({ items: [
      { type: 'speckit_diagnosis', report: JSON.stringify({ summary: 'Fixed CI-R10.' }) },
      { type: 'report_incomplete', reason: 'push refused' },
    ] }));
    const refused = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    assert.equal((await runRecord({ client: github, env: fixEnv({ SPECKIT_CHECK_RUN: String(refused.id) }), log: silent })).outcome, 'push failed');
    const retry = github.repo.runs.at(-1);
    assert.deepEqual([retry.workflow, retry.inputs.mode, retry.inputs.round, retry.inputs.stalls, retry.inputs.findings, retry.inputs.guidance], ['speckit-diagnose.lock.yml', 'fix', '3', '2', findings, '- [F1] **HIGH** stale']);
    assert.match(github.comments.at(-1).body, /^\*\*Correction round 3\*\*: [\s\S]*Round 2 could not push its changes\. Rounds without progress so far: 2 of 3\./);
    assert.deepEqual(github.repo.reviewRequests, []);

    const spent = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    const runs = github.repo.runs.length;
    assert.equal((await runRecord({ client: github, env: fixEnv({ SPECKIT_CHECK_RUN: String(spent.id), SPECKIT_ROUND: '3', SPECKIT_STALLS: '2' }), log: silent })).outcome, 'push failed');
    assert.equal(github.repo.runs.length, runs, 'the third round without progress ends the loop');
    assert.match(github.comments.at(-1).body, /^\*\*Amendment #11 is not consistent\*\* \(correction round 3 could not push its changes\)/);
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
    assert.match(github.comments.at(-1).body, /\*\*Recommended next step\*\*: Post the following comment on amendment #11[\s\S]*```text\nPlease rework this amendment/);
    assert.match(github.repo.pulls.find((pull) => pull.number === 11).body, /\*\*Recommended next step\*\*: Post the following comment/);
    assert.equal(github.repo.runs.length, runs, 'no check of unchanged files');

    // The safe-output tool refused the push (the agent reports it as incomplete), or a correction round pushed nothing.
    const refusedOutput = path.join(dir, 'refused.json');
    writeFileSync(refusedOutput, JSON.stringify({ items: [
      { type: 'speckit_diagnosis', report: JSON.stringify({ summary: 'Reworded CI-R10.' }) },
      { type: 'report_incomplete', reason: 'push_to_pull_request_branch rejected: files outside allowed-files' },
    ] }));
    const tool = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    assert.equal((await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: refusedOutput, SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'revise', SPECKIT_AMENDMENT: '11', SPECKIT_CHECK_RUN: String(tool.id) }), log: silent })).outcome, 'push failed');
    assert.match(github.comments.at(-1).body, /could not push its changes[\s\S]*> The push was refused: push_to_pull_request_branch rejected: files outside allowed-files/);
    const empty = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    assert.equal((await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'fix', SPECKIT_AMENDMENT: '11', SPECKIT_ROUND: '1', SPECKIT_CHECK_RUN: String(empty.id) }), log: silent })).outcome, 'unchanged');
    assert.equal(github.repo.checkRuns.find((run) => run.id === empty.id).output.title, 'Correction round 1 changed nothing');
    assert.match(github.comments.at(-1).body, /^\*\*Amendment #11 is not consistent\*\* \(correction round 1 changed nothing\)/, 'an empty correction round ends the loop');
    assert.equal(github.repo.runs.length, runs, 'no further analysis');
    github.repo.reviewRequests = [];

    const failing = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    const fixed = await runRecord({ client: github, env: env({ SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'fix', SPECKIT_AMENDMENT: '11', SPECKIT_ROUND: '2', SPECKIT_CHECK_RUN: String(failing.id) }), log: silent });
    assert.equal(fixed.outcome, 'failed');
    assert.deepEqual([github.repo.checkRuns.find((run) => run.id === failing.id).conclusion, github.repo.checkRuns.find((run) => run.id === failing.id).output.title], ['failure', 'Correction round 2 did not finish']);
    assert.ok(github.comments.some((comment) => comment.number === 11 && /correction round 2 did not produce a result: the agent did not report\./.test(comment.body)));
    assert.match(github.comments.at(-1).body, /^\*\*Amendment #11 is not consistent\*\* \(correction round 2 did not finish\)/, 'a failed correction round hands the amendment to the requester');
    assert.match(github.comments.at(-1).body, /\*\*Recommended next step\*\*: Post the following comment on amendment #11[\s\S]*```text\nPlease rework this amendment/);
    assert.deepEqual(github.repo.reviewRequests, [{ number: 11, reviewers: ['dev'] }]);

    const failedRework = await runRecord({ client: github, env: env({ SPECKIT_DETECTION: 'success', SPECKIT_MODE: 'revise', SPECKIT_AMENDMENT: '11' }), log: silent });
    assert.equal(failedRework.outcome, 'failed');
    assert.match(github.comments.at(-1).body, /The rework did not produce a result[\s\S]*\*\*Recommended next step\*\*: Post the following comment on amendment #11/);
    assert.match(github.repo.pulls.find((pull) => pull.number === 11).body, /The rework did not produce a result[\s\S]*\*\*Recommended next step\*\*: Post the following comment/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('a command that already failed the same way on the unchanged branch is not recommended again', () => {
  const at = (minute) => `2026-10-18T10:${String(minute).padStart(2, '0')}:00Z`;
  const bot = (minute, body) => ({ user: { login: 'github-actions[bot]' }, created_at: at(minute), body });
  const stop = (minute, reason) => bot(minute, `<!-- speckit-implement:attempt {"outcome":"attention"} -->\n**Implementation needs attention** @dev: ${reason}\n\nSee the [run](https://x/runs/${minute}).`);
  const protectedStop = 'conflicts in protected paths need a person: .specify/memory/constitution.md';
  const comments = [stop(1, protectedStop), bot(2, renderSyncComment('Required sync', 'Verify', { head: 'h1', base: 'b1' })), stop(3, protectedStop)];
  assert.deepEqual(repeatedFailure(comments, 'h1'), { verb: 'sync', reason: protectedStop });
  assert.equal(repeatedFailure(comments, 'h2'), null, 'a push changed the branch');
  assert.equal(repeatedFailure([stop(1, 'the build failed'), comments[1], stop(3, protectedStop)], 'h1'), null, 'a different stop');
  assert.equal(repeatedFailure(comments.slice(0, 2), 'h1'), null, 'no stop after the command yet');

  const report = validateReport({ ...REPORT, category: 'outside', artifacts: [], questions: [], options: [{ title: 'Sync again', why: 'x', command: '/speckit sync Verify the merge', recommended: true }] }).report;
  const repeated = repeatedFailure(comments, 'h1');
  const steps = recommendNextSteps(report, { repeated });
  assert.deepEqual(steps.notes, [], 'the report stays usable');
  assert.match(steps.adjusted[0], /`\/speckit sync` already stopped the same way on the unchanged branch/);
  const pick = steps.options.find((option) => option.recommended);
  assert.match(pick.command, /^\/speckit author /);
  assert.ok(steps.options.some((option) => option.command === '/speckit sync Verify the merge' && !option.recommended), 'still listed under the other options');
  const retryReport = validateReport({ ...REPORT, category: 'retry', artifacts: [], questions: [], options: [{ title: 'Retry', why: 'x', command: '/speckit retry', recommended: true }] }).report;
  assert.equal(recommendNextSteps(retryReport, { repeated }).options.find((option) => option.recommended).command, '/speckit retry', 'another command is not affected');
  const comment = renderDiagnosisComment({ report, repeated });
  assert.match(comment, /\*\*Recommended next step\*\*[\s\S]*\/speckit author/);
  assert.match(comment, /_Parts of the report were adjusted: the recommended `\/speckit sync` already stopped the same way/);
});
const RETRY_REPORT = {
  ...REPORT,
  category: 'retry',
  confidence: 'medium',
  summary: 'The package feed timed out while restoring.',
  artifacts: [],
  questions: [],
  options: [{ title: 'Retry the task', why: 'The feed outage has ended.', command: '/speckit retry Restore again; the feed timed out', recommended: true }],
};
const SYNC_REPORT = {
  ...RETRY_REPORT,
  category: 'outside',
  summary: 'The dependency pin is already fixed on main.',
  options: [{ title: 'Merge the fixed base', why: 'The fix is on main.', command: '/speckit sync', recommended: true }],
};

// Records one diagnosis report on #9 and returns the result.
async function recordReport(github, report, extraEnv = {}) {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-auto-'));
  try {
    const output = path.join(dir, 'agent_output.json');
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report: JSON.stringify(report) }] }));
    return await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_DETECTION: 'success', ...extraEnv }), log: silent });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}
const attention = (github) => (github.repo.pulls.find((pull) => pull.number === 9).labels ?? []).some((label) => label.name === ATTENTION_LABEL);
const implementRuns = (github) => github.repo.runs.filter((run) => run.workflow === IMPLEMENT_WORKFLOW_FILE).length;

test('a transient retry diagnosis continues automatically with the recommended retry', async () => {
  for (const confidence of ['medium', 'high']) {
    const github = diagnosisRepo({ amendment: false });
    const result = await recordReport(github, { ...RETRY_REPORT, confidence });
    assert.equal(result.continued, 'retry', confidence);
    const diagnosis = github.comments.find((comment) => comment.body.startsWith(DIAGNOSIS_COMMENT_MARKER));
    assert.match(diagnosis.body, /\*\*Recommended next step\*\*: Retry the task[\s\S]*\/speckit retry Restore again; the feed timed out/, 'the recommendation is unchanged');
    const continued = github.comments.at(-1);
    assert.ok(continued.body.startsWith(`${AUTO_CONTINUE_MARKER}\n${RETRY_COMMENT_MARKER}\n<!-- speckit-implement:restart-state {"head":"sha-impl","base":"sha-main"} -->`), continued.body);
    assert.match(continued.body, /Continued automatically with `\/speckit retry` as the diagnosis recommends; what stopped restarts with a fresh attempt count[\s\S]* Post another command or push to steer it\./);
    assert.match(continued.body, /> Restore again; the feed timed out/);
    assert.equal(parseGuidance(continued.body), 'Restore again; the feed timed out');
    assert.equal(unchangedRestarts(github.comments, 'sha-impl').length, 1, 'it counts as a restart');
    assert.equal(implementRuns(github), 1);
    assert.equal(attention(github), false);
  }
});

test('an outside diagnosis that recommends sync continues automatically with the sync', async () => {
  const github = diagnosisRepo({ amendment: false });
  const result = await recordReport(github, SYNC_REPORT);
  assert.equal(result.continued, 'sync');
  const continued = github.comments.at(-1);
  assert.ok(continued.body.startsWith(`${AUTO_CONTINUE_MARKER}\n${SYNC_COMMENT_MARKER}\n`));
  assert.match(continued.body, /Continued automatically with `\/speckit sync` as the diagnosis recommends; merge `main` and pass full verification before continuing\.[\s\S]*Post another command or push to steer it\./);
  assert.ok(syncBarrier(github.comments), 'the barrier sees the request');
  assert.equal(implementRuns(github), 1);
  assert.equal(attention(github), false);
});

test('other diagnoses wait for a person', async () => {
  const cases = {
    low: { ...RETRY_REPORT, confidence: 'low' },
    decision: { ...RETRY_REPORT, category: 'decision', questions: [{ question: 'Keep it?', choices: ['Yes', 'No'] }], options: [{ title: 'Answer', why: 'A trade-off.', command: '/speckit retry 1. Yes.', recommended: true }] },
    artifacts: REPORT,
    unknown: { ...RETRY_REPORT, category: 'unknown', options: [] },
    'outside specify': { ...SYNC_REPORT, options: [{ title: 'New spec', why: 'Not fixed yet.', command: '/speckit author Fix the pin in the platform', recommended: true }] },
  };
  for (const [name, report] of Object.entries(cases)) {
    const github = diagnosisRepo({ amendment: false });
    const result = await recordReport(github, report);
    assert.equal(result.continued ?? null, null, name);
    assert.equal(implementRuns(github), 0, name);
    assert.ok(!github.comments.some((comment) => comment.body.startsWith(AUTO_CONTINUE_MARKER)), name);
  }
  const off = diagnosisRepo({ amendment: false });
  assert.equal((await recordReport(off, RETRY_REPORT, { SPECKIT_AUTO_CONTINUE: 'false' })).continued, null);
  assert.equal(attention(off), true);
});

test('continues once per action of a person, and never after the same failure repeated', async () => {
  const github = diagnosisRepo({ amendment: false });
  assert.equal((await recordReport(github, RETRY_REPORT)).continued, 'retry');
  const again = await recordReport(github, RETRY_REPORT);
  assert.equal(again.continued, null, 'no second automatic continuation without a person');
  assert.equal(attention(github), true);
  assert.equal(implementRuns(github), 1);

  github.commentAuthor = 'dev';
  await github.createComment(9, '/speckit retry try once more');
  github.commentAuthor = undefined;
  assert.equal((await recordReport(github, RETRY_REPORT)).continued, 'retry', 'a person\'s command allows another one');
  assert.equal((await recordReport(github, RETRY_REPORT)).continued, null);

  github.repo.pullCommits[9] = [{ author: { login: 'dev' }, committer: { login: 'dev' }, commit: { committer: { date: github.tick() } } }];
  assert.equal((await recordReport(github, RETRY_REPORT)).continued, 'retry', 'a person\'s push allows another one');

  const repeated = diagnosisRepo({ amendment: false });
  const stop = (reason) => `<!-- speckit-implement:attempt {"outcome":"attention"} -->\n**Implementation needs attention** @dev: ${reason}`;
  await repeated.createComment(9, stop('T002 timed out'));
  await repeated.createComment(9, renderRetryComment('Implementation retried by @dev', '', { head: 'sha-impl', base: 'sha-main' }));
  await repeated.createComment(9, stop('T002 timed out'));
  repeated.commentAuthor = 'dev';
  await repeated.createComment(9, '/speckit help');
  repeated.commentAuthor = undefined;
  assert.ok(repeatedFailure(await repeated.listIssueComments(9), 'sha-impl'));
  assert.equal((await recordReport(repeated, RETRY_REPORT)).continued, null, 'the same failure already repeated');
  assert.equal(implementRuns(repeated), 0);
});

test('a change only a push can make is recommended as a ready-to-post @copilot prompt', () => {
  const prompt = 'In specs/other/spec.md, change the link on line 83 from ../a/spec.md to ../archive/a/spec.md. Then run node .github/scripts/check-markdown.mjs --files specs/other/spec.md.';
  const copilotOption = (extra = {}) => ({ title: 'Ask Copilot to repoint the link', why: 'Only a push can change another spec folder.', copilot: prompt, recommended: true, ...extra });
  const decision = validateReport({ ...REPORT, category: 'decision', artifacts: [], questions: [], options: [copilotOption({ copilot: `@copilot ${prompt}` })] });
  assert.deepEqual(decision.reasons, []);
  assert.equal(decision.report.options[0].copilot, prompt, 'a leading @copilot is not kept twice');
  const steps = recommendNextSteps(decision.report);
  assert.deepEqual(steps.notes, [], 'a Copilot prompt fits a decision');
  assert.equal(steps.options.find((option) => option.recommended).copilot, prompt);
  const comment = renderDiagnosisComment({ report: decision.report, round: 1 });
  assert.match(comment, new RegExp(`\\*\\*Recommended next step\\*\\*: Ask Copilot to repoint the link\\.[\\s\\S]*Comment this on the pull request \\(people with write access\\):\\n\\n\`\`\`text\\n@copilot In specs/other/spec\\.md, change the link on line 83[^\\n]*\\n\\nMake only this change, [^\\n]*do not open another pull request\\.\\n\`\`\``));
  assert.match(comment, /GitHub Copilot's cloud agent \(it must be enabled for the repository\) makes the change and pushes it to the implementation branch; that push continues the implementation by itself/);
  assert.equal(copilotRequestOf(comment).split('\n')[0], `@copilot ${prompt}`, 'the refused retry can reuse it');

  // Invalid prompts are dropped with a reason, and a prompt next to a command loses to the command.
  for (const [copilot, problem] of [['```sh\nrm -rf\n```', /invalid Copilot prompt/], ['x'.repeat(3100), /invalid Copilot prompt/], ['Change <what to fix> in the spec', /invalid Copilot prompt/]]) {
    const checked = validateReport({ ...REPORT, category: 'decision', artifacts: [], questions: [], options: [copilotOption({ copilot })] });
    assert.match(checked.reasons.join('; '), problem);
    assert.equal(checked.report.options[0].copilot, null);
  }
  const both = validateReport({ ...REPORT, category: 'decision', artifacts: [], questions: [], options: [copilotOption({ command: '/speckit retry 1. Keep the scope.' })] });
  assert.match(both.reasons.join('; '), /has both a command and a Copilot prompt; the command is kept/);
  assert.equal(both.report.options[0].copilot, null);

  // Another category does not recommend a Copilot prompt; it stays under the other options.
  const retry = validateReport({ ...REPORT, category: 'retry', artifacts: [], questions: [], options: [copilotOption()] }).report;
  const moved = recommendNextSteps(retry);
  assert.match(moved.notes.join('; '), /does not fit the category `retry`/);
  assert.match(moved.options.find((option) => option.recommended).command, /^\/speckit retry/);

  // A command that already failed the same way is replaced by the Copilot prompt rather than a new spec.
  const repeated = { verb: 'retry', reason: 'T001 did not succeed in 2 attempts' };
  const stuck = validateReport({ ...REPORT, category: 'decision', artifacts: [], questions: [], options: [
    { title: 'Retry with the answers', why: 'x', command: '/speckit retry 1. Keep it.', recommended: true },
    copilotOption({ recommended: false }),
  ] }).report;
  const replaced = recommendNextSteps(stuck, { repeated });
  assert.equal(replaced.options.find((option) => option.recommended).copilot, prompt);
  assert.match(replaced.adjusted[0], /already stopped the same way/);
});
