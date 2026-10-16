import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  main,
  maintainAmendment,
  pendingFeedback,
  resolveAmendment,
  runClosed,
  runFeedback,
  runGuard,
  runPushed,
  runRecordAnalysis,
  startRework,
  validateAnalysis,
} from './speckit-amend.mjs';
import { ATTENTION_LABEL } from './speckit-implement-core.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const env = (extra = {}) => ({ GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', ...extra });
const amendmentPull = (github) => github.repo.pulls.find((pull) => pull.number === 11);
const later = (github, ms = 1000) => new Date(github.clock + ms).toISOString();

// Records an analysis of amendment #11 as the custom safe-output job of Spec Kit analyze would.
async function recordAnalysis(github, report, { round = 0, mode = 'loop', detection = 'success', stalls = 0, previous = '' } = {}) {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-analysis-'));
  try {
    const check = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    const output = path.join(dir, 'agent_output.json');
    writeFileSync(output, JSON.stringify({ items: report === null ? [] : [{ type: 'speckit_analysis', report: JSON.stringify(report) }] }));
    const result = await runRecordAnalysis({
      client: github,
      env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_AMENDMENT: '11', SPECKIT_ROUND: String(round), SPECKIT_STALLS: String(stalls), SPECKIT_PREVIOUS: previous, SPECKIT_MODE: mode, SPECKIT_CHECK_RUN: String(check.id), SPECKIT_DETECTION: detection }),
      log: silent,
    });
    return { ...result, check: github.repo.checkRuns.find((run) => run.id === check.id) };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const HIGH = { severity: 'HIGH', category: 'Inconsistency', location: 'plan.md CI-R10', summary: 'CI-R10 still expects a residual difference.', recommendation: 'Update it to the refined R6.' };
const trackedOf = (findings) => JSON.stringify(findings.map((item, index) => ({ id: `F${index + 1}`, severity: item.severity, location: item.location ?? '', summary: item.summary })));

test('a consistent amendment is handed to the requester for review', async () => {
  const github = diagnosisRepo();
  const result = await recordAnalysis(github, { summary: 'No issues.', findings: [] });
  assert.equal(result.outcome, 'consistent');
  assert.deepEqual([result.check.conclusion, result.check.external_id, result.check.output.title], ['success', 'speckit:amend-consistent', 'Consistent']);
  const pull = amendmentPull(github);
  assert.equal(pull.draft, false, 'ready for review');
  assert.deepEqual(pull.assignees, ['dev']);
  assert.deepEqual(github.repo.reviewRequests, [{ number: 11, reviewers: ['dev'] }]);
  assert.match(pull.body, /^<!-- speckit-amend:status -->\n✅ \*\*Consistent\*\*[\s\S]*\*\*How to proceed\*\*[\s\S]*<!-- \/speckit-amend:status -->/);
  const presented = github.comments.find((comment) => comment.number === 11);
  assert.match(presented.body, /^<!-- speckit-amend:presented -->\n<!-- speckit-amend:cutoff \S+ -->\n@dev ✅ \*\*Consistent\*\*/);
  assert.match(github.comments.find((comment) => comment.number === 9).body, /^\*\*Amendment #11 is ready for your review\*\*/);
  const attention = () => (amendmentPull(github).labels ?? []).some((label) => label.name === ATTENTION_LABEL);
  assert.equal(attention(), true, 'a presented amendment waits for its reviewer');

  await startRework(github, env(), await resolveAmendment(github, env(), 11), { mode: 'revise', feedback: { items: [], since: null } });
  assert.equal(attention(), false, 'a rework after feedback is processing');
});

test('LOW findings get correction rounds too, and are handed over once a round resolves none of them', async () => {
  const LOW = { severity: 'LOW', category: 'Coverage', location: 'tasks.md T001', summary: 'SC-003 is not verified by T001.', recommendation: 'Add the Host tests.' };
  const github = diagnosisRepo();
  const first = await recordAnalysis(github, { summary: 'Minor gap.', findings: [LOW] });
  assert.equal(first.outcome, 'correcting', 'a LOW finding is corrected instead of handed over');
  assert.deepEqual([first.check.conclusion, first.check.output.title], ['neutral', '1 LOW finding(s) found; correction round 1 follows']);
  const fix = github.repo.runs.at(-1);
  assert.equal(fix.inputs.mode, 'fix');
  assert.match(fix.inputs.guidance, /- \[F1\] \*\*LOW\*\* \(Coverage\) `tasks\.md T001`: SC-003 is not verified by T001\. — Add the Host tests\./);
  assert.deepEqual(github.repo.reviewRequests, [], 'nobody is asked while corrections run');

  const resolved = diagnosisRepo();
  assert.equal((await recordAnalysis(resolved, { findings: [], resolved: ['F1'] }, { round: 1, previous: fix.inputs.findings })).outcome, 'consistent');
  assert.match(amendmentPull(resolved).body, /✅ \*\*Consistent\*\*: [^\n]*found no CRITICAL, HIGH, or MEDIUM inconsistencies\./);

  // A blocking finding resolved while a LOW one appears still counts as progress, so the LOW finding gets its round.
  const mixed = diagnosisRepo();
  const blocking = trackedOf([HIGH]);
  assert.equal((await recordAnalysis(mixed, { findings: [LOW], resolved: ['F1'] }, { round: 2, stalls: 2, previous: blocking })).outcome, 'correcting');

  const declined = diagnosisRepo();
  const handed = await recordAnalysis(declined, { summary: 'Minor gap.', findings: [LOW] }, { round: 1, previous: fix.inputs.findings });
  assert.equal(handed.outcome, 'consistent', 'a round that resolves no LOW finding ends the correction');
  assert.deepEqual([handed.check.conclusion, handed.check.output.title], ['success', 'Consistent']);
  assert.equal(amendmentPull(declined).draft, false);
  assert.match(amendmentPull(declined).body, /✅ \*\*Consistent\*\*[^\n]*\(1 LOW finding\(s\) that the correction rounds left as they are, listed for information; nothing needs fixing by hand\)/);
  assert.match(declined.comments.find((comment) => comment.number === 11).body, /\*\*LOW\*\* \(Coverage\) `tasks\.md T001`: SC-003 is not verified by T001\./);
});

test('correction rounds continue while they make progress and stop after three rounds without it', async () => {
  const github = diagnosisRepo();
  const first = await recordAnalysis(github, { summary: 'One issue.', findings: [HIGH] });
  assert.equal(first.outcome, 'correcting');
  assert.deepEqual([first.check.conclusion, first.check.output.title], ['neutral', '1 inconsistency(ies) found; correction round 1 follows']);
  const fix = github.repo.runs.at(-1);
  assert.equal(fix.workflow, 'speckit-diagnose.lock.yml');
  assert.deepEqual([fix.inputs.mode, fix.inputs.amendment, fix.inputs.round, fix.inputs.stalls, fix.inputs.pull, fix.inputs.twin], ['fix', '11', '1', '0', '9', '5']);
  assert.match(fix.inputs.guidance, /- \[F1\] \*\*HIGH\*\* \(Inconsistency\) `plan\.md CI-R10`: CI-R10 still expects a residual difference\. — Update it to the refined R6\./);
  const tracked = JSON.parse(fix.inputs.findings);
  assert.deepEqual(tracked.map((item) => [item.id, item.severity, item.location]), [['F1', 'HIGH', 'plan.md CI-R10']]);
  assert.match(github.comments.at(-1).body, /^\*\*Correction round 1\*\*: [\s\S]*Rounds without progress so far: 0 of 3\./);
  assert.equal(amendmentPull(github).draft, true);
  assert.deepEqual(github.repo.reviewRequests, [], 'nobody is asked while corrections run');

  // Progress: a previous finding is resolved, even though a new one appears; the budget is untouched.
  const NEW = { severity: 'MEDIUM', location: 'data-model.md', summary: 'A new gap.' };
  const progressed = await recordAnalysis(github, { findings: [NEW], resolved: ['F1', 'F9', 'x'] }, { round: 4, stalls: 2, previous: fix.inputs.findings });
  assert.deepEqual([progressed.outcome, progressed.stalls], ['correcting', 2]);
  assert.match(progressed.check.output.summary, /^Round 4 resolved F1\./);
  assert.deepEqual([github.repo.runs.at(-1).inputs.round, github.repo.runs.at(-1).inputs.stalls], ['5', '2']);
  assert.match(github.comments.at(-1).body, /^\*\*Correction round 5\*\*: [\s\S]*Round 4 resolved F1\. Rounds without progress so far: 2 of 3\./);

  // No progress: the same finding persists; the third round without progress hands the amendment over.
  const stalled = await recordAnalysis(github, { findings: [HIGH] }, { round: 1, stalls: 0, previous: fix.inputs.findings });
  assert.deepEqual([stalled.outcome, stalled.stalls], ['correcting', 1]);
  const last = await recordAnalysis(github, { summary: 'Still one issue.', findings: [HIGH] }, { round: 6, stalls: 2, previous: fix.inputs.findings });
  assert.deepEqual([last.outcome, last.stalls], ['inconsistent', 3]);
  assert.deepEqual([last.check.conclusion, last.check.output.title], ['failure', '1 inconsistency(ies) remain']);
  assert.equal(amendmentPull(github).draft, true, 'an inconsistent amendment stays a draft');
  assert.deepEqual(github.repo.reviewRequests, [{ number: 11, reviewers: ['dev'] }]);
  assert.match(amendmentPull(github).body, /⚠️ \*\*Not consistent\*\*: 1 inconsistency\(ies\) remain; 3 of 6 correction round\(s\) made no progress/);
  assert.match(github.comments.find((comment) => comment.number === 9 && /not consistent/.test(comment.body)).body, /^\*\*Amendment #11 is not consistent\*\* \(1 inconsistency\(ies\) remain; 3 of 6/);

  const capped = diagnosisRepo();
  const limit = await recordAnalysis(capped, { findings: [HIGH], resolved: ['F1'] }, { round: 10, stalls: 0, previous: fix.inputs.findings });
  assert.equal(limit.outcome, 'inconsistent', 'progress or not, ten rounds are the limit');
  assert.match(amendmentPull(capped).body, /after the limit of 10 correction rounds/);

  // A broken amendment rule counts like a finding, and its repair is progress.
  const rules = diagnosisRepo({ after: '## P\n- [x] T001 Done\n- [x] T002 Next\n' });
  const ruled = await recordAnalysis(rules, { summary: 'Clean.', findings: [] }, { round: 2 });
  assert.equal(ruled.outcome, 'correcting');
  assert.match(rules.repo.runs.at(-1).inputs.guidance, /- \[R1\] \*\*RULE\*\*: an amendment may not check T002/);
  const repaired = diagnosisRepo();
  const fixedRule = await recordAnalysis(repaired, { findings: [HIGH] }, { round: 3, stalls: 1, previous: rules.repo.runs.at(-1).inputs.findings });
  assert.deepEqual([fixedRule.outcome, fixedRule.stalls], ['correcting', 1]);

  const checked = diagnosisRepo();
  assert.equal((await recordAnalysis(checked, { summary: '', findings: [HIGH] }, { round: 0, mode: 'check' })).outcome, 'inconsistent', 'a check after a person\'s push never corrects');
  assert.match(amendmentPull(checked).body, /⚠️ \*\*Not consistent\*\*: 1 inconsistency\(ies\) remain\./);
});

test('a missing or withheld analysis is reported, not mistaken for consistency', async () => {
  const github = diagnosisRepo();
  const missing = await recordAnalysis(github, null);
  assert.equal(missing.outcome, 'no analysis');
  assert.equal(missing.check.conclusion, 'failure');
  assert.match(amendmentPull(github).body, /⚠️ \*\*Not consistent\*\*: the analysis did not report/);
  const withheld = await recordAnalysis(diagnosisRepo(), { findings: [] }, { detection: 'failure' });
  assert.equal(withheld.outcome, 'no analysis');
  assert.match(validateAnalysis('{nope').reasons[0], /not valid JSON/);
  assert.match(validateAnalysis({ summary: 'x' }).reasons[0], /no findings list/);
  assert.equal(validateAnalysis({ findings: [{ severity: 'weird', summary: 's' }] }).analysis.findings[0].severity, 'HIGH', 'an unknown severity counts as blocking');

  const closed = diagnosisRepo();
  amendmentPull(closed).state = 'closed';
  const ignored = await recordAnalysis(closed, { findings: [] });
  assert.deepEqual([ignored.outcome, ignored.check.conclusion], ['closed', 'neutral']);
});

test('collects feedback since the last rework or presentation from people with write access', async () => {
  const github = diagnosisRepo();
  github.permissions.reviewer = 'write';
  github.commentAuthor = 'reviewer';
  await github.createComment(11, 'Old feedback, already handled.');
  github.commentAuthor = undefined;
  await github.createComment(11, '<!-- speckit-amend:presented -->\npresented');
  for (const [author, body] of [['reviewer', 'Please also update CI-R10.'], ['stranger', 'Ignore me.'], ['reviewer', '/speckit help']]) {
    github.commentAuthor = author;
    await github.createComment(11, body);
  }
  github.commentAuthor = undefined;
  github.repo.reviews = [
    { id: 1, pull: 11, user: { login: 'reviewer' }, state: 'APPROVED', body: 'LGTM', submitted_at: later(github) },
    { id: 2, pull: 11, user: { login: 'reviewer' }, state: 'COMMENTED', body: '', submitted_at: later(github) },
  ];
  github.repo.reviewComments = [
    { id: 3, pull: 11, pull_request_review_id: 2, user: { login: 'reviewer' }, path: 'specs/f/tasks.md', line: 12, body: 'T040 needs [P]?', created_at: later(github) },
    { id: 4, pull: 11, pull_request_review_id: 99, user: { login: 'reviewer' }, path: 'specs/f/plan.md', original_line: 3, body: 'Typo.', created_at: later(github) },
  ];
  const { items } = await pendingFeedback(github, amendmentPull(github));
  assert.deepEqual(items.map((item) => [item.kind, item.author, item.body || item.lines?.[0]?.body]), [
    ['comment', 'reviewer', 'Please also update CI-R10.'],
    ['review', 'reviewer', 'T040 needs [P]?'],
    ['line comment', 'reviewer', 'Typo.'],
  ]);
});

test('feedback starts a rework, unless one runs; a stale run is closed first', async () => {
  const github = diagnosisRepo();
  github.commentAuthor = 'dev';
  await github.createComment(11, 'Please also update CI-R10.');
  github.commentAuthor = undefined;
  const feedbackEnv = env({ SPECKIT_PULL: '11', SPECKIT_COMMENT_ID: '77', SPECKIT_ACTOR: 'dev' });
  const started = await runFeedback({ client: github, env: feedbackEnv, log: silent, now: () => github.clock });
  assert.equal(started.outcome, 'rework');
  assert.deepEqual(github.reactions, [{ commentId: 77, content: 'eyes' }]);
  assert.equal(amendmentPull(github).draft, true);
  assert.match(github.comments.at(-1).body, /^<!-- speckit-amend:rework -->\n<!-- speckit-amend:cutoff \S+ -->\n\*\*Reworking this amendment\*\* with the feedback from @dev/);
  const revise = github.repo.runs.at(-1);
  assert.deepEqual([revise.workflow, revise.inputs.mode, revise.inputs.amendment, revise.inputs.round], ['speckit-diagnose.lock.yml', 'revise', '11', '0']);
  const rework = github.repo.checkRuns.at(-1);
  assert.deepEqual([rework.name, rework.status, rework.output.title], ['Spec Kit amendment', 'in_progress', 'Rework in progress']);

  assert.equal((await runFeedback({ client: github, env: feedbackEnv, log: silent, now: () => github.clock })).outcome, 'none', 'the rework marker ends the feedback window');
  github.commentAuthor = 'dev';
  await github.createComment(11, 'One more thing.');
  github.commentAuthor = undefined;
  const wait = { client: github, env: feedbackEnv, log: silent, now: () => github.clock, sleep: async (ms) => { github.clock += ms; } };
  assert.equal((await runFeedback(wait)).outcome, 'queued', 'a rework that runs past the wait leaves the feedback to the orchestrator');

  // The rework ends while the feedback waits: its feedback is handled right away.
  let polls = 0;
  const finished = await runFeedback({
    ...wait,
    sleep: async (ms) => {
      github.clock += ms;
      polls += 1;
      for (const run of github.repo.runs) run.status = 'completed';
      Object.assign(rework, { status: 'completed', conclusion: 'neutral' });
    },
  });
  assert.deepEqual([finished.outcome, polls], ['rework', 1]);
  assert.match(github.repo.runs.at(-1).inputs.guidance, /One more thing\./);

  const stale = diagnosisRepo();
  stale.commentAuthor = 'dev';
  await stale.createComment(11, 'Please also update CI-R10.');
  stale.commentAuthor = undefined;
  await runFeedback({ client: stale, env: feedbackEnv, log: silent, now: () => stale.clock });
  const stuck = stale.repo.checkRuns.at(-1);
  stale.commentAuthor = 'dev';
  await stale.createComment(11, 'Again.');
  stale.commentAuthor = undefined;
  stale.clock += 2 * 60 * 60 * 1000;
  assert.equal((await runFeedback({ client: stale, env: feedbackEnv, log: silent, now: () => stale.clock })).outcome, 'rework');
  assert.deepEqual([stuck.status, stuck.conclusion, stuck.output.title], ['completed', 'failure', 'The check or rework did not finish']);

  assert.equal((await runFeedback({ client: github, env: env({ SPECKIT_PULL: '9' }), log: silent })).outcome, 'ignored', 'the implementation pull request is no amendment');
});

test('feedback that arrives during the check is handled before the amendment is presented', async () => {
  const github = diagnosisRepo();
  await github.createComment(11, '<!-- speckit-amend:rework -->\nchecking');
  github.commentAuthor = 'dev';
  await github.createComment(11, 'Also mention the data model.');
  github.commentAuthor = undefined;
  const result = await recordAnalysis(github, { findings: [] });
  assert.equal(result.outcome, 'rework');
  assert.equal(result.check.conclusion, 'success');
  assert.equal(github.repo.runs.at(-1).inputs.mode, 'revise');
  assert.deepEqual(github.repo.reviewRequests, []);
});

test('merging continues the implementation; closing discards; a person\'s push is checked again', async () => {
  const merged = diagnosisRepo();
  merged.closePull(11, { merged: true });
  assert.equal((await runClosed({ client: merged, env: env({ SPECKIT_PULL: '11', SPECKIT_ACTOR: 'dev' }), log: silent })).outcome, 'merged');
  assert.equal(merged.repo.branches['speckit-amend/f'], undefined);
  assert.match(merged.comments.at(-1).body, /^<!-- speckit-implement:resume -->\n<!-- speckit-amend:closed 11 -->\n\*\*Amendment #11 was merged\*\* by @dev\. The implementation continues/);
  assert.equal(merged.comments.at(-1).number, 9);
  assert.deepEqual([merged.repo.runs.at(-1).workflow, merged.repo.runs.at(-1).inputs], ['speckit-implement-spec.yml', { twin: '5', pull: '9', title: 'F' }], 'the implementation chain continues, named after the spec');
  assert.equal((await runClosed({ client: merged, env: env({ SPECKIT_PULL: '11' }), log: silent })).outcome, 'handled', 'a merge is settled once');

  const discarded = diagnosisRepo();
  discarded.closePull(11);
  assert.equal((await runClosed({ client: discarded, env: env({ SPECKIT_PULL: '11', SPECKIT_ACTOR: 'dev' }), log: silent })).outcome, 'discarded');
  assert.match(discarded.comments.at(-1).body, /^<!-- speckit-amend:closed 11 -->\nAmendment #11 was closed without merging by @dev\.[\s\S]*\*\*Recommended next step\*\*: Diagnose again[\s\S]*\/speckit diagnose/);
  assert.equal((await runClosed({ client: discarded, env: env({ SPECKIT_PULL: '9' }), log: silent })).outcome, 'ignored');

  const pushed = diagnosisRepo();
  assert.equal((await runPushed({ client: pushed, env: env({ SPECKIT_PULL: '11' }), log: silent })).outcome, 'checking');
  assert.deepEqual([pushed.repo.runs.at(-1).workflow, pushed.repo.runs.at(-1).inputs.mode, pushed.repo.runs.at(-1).inputs.round], ['speckit-analyze.lock.yml', 'check', '0']);
  await assert.rejects(() => main(['nope'], { env: {}, client: pushed }), /Usage/);
});

test('the guard removes Spec Kit pull requests from stacks and keeps amendments on their implementation branch', async () => {
  const github = diagnosisRepo();
  assert.equal((await main(['guard'], { env: env({ SPECKIT_PULL: '11' }), client: github, log: silent })), 0);
  assert.deepEqual([github.unstacked, github.comments.length], [undefined, 0], 'a correct amendment is left alone');

  amendmentPull(github).stack = { number: 4 };
  github.repo.pulls[0].stack = { number: 4 };
  amendmentPull(github).base = { ref: 'main', sha: 'sha-main' };
  const guarded = await runGuard({ client: github, env: env({ SPECKIT_PULL: '11' }), log: silent });
  assert.equal(guarded.outcome, 'guarded');
  assert.deepEqual(github.unstacked, [4]);
  assert.equal(amendmentPull(github).base.ref, 'speckit/f');
  assert.match(github.comments.at(-1).body, /^This pull request was removed from its pull request stack\.[\s\S]*set back from `main` to `speckit\/f`/);
  assert.equal(github.repo.pulls[0].stack, null, 'unstacking dissolves the whole stack');

  // The orchestrator guards on every run, also when no event reached the guard job.
  github.repo.pulls[0].stack = { number: 5 };
  const lines = [];
  await maintainAmendment(github, env(), { folder: 'f', implementation: github.repo.pulls[0], twin: 5 }, { now: github.clock, report: (line) => lines.push(line) });
  assert.deepEqual(github.unstacked, [4, 5]);
  assert.match(lines.join(), /Guarded #9: unstacked/);
  assert.match(github.comments.find((comment) => comment.number === 9).body, /this unfinished implementation/);
  assert.equal((await runGuard({ client: github, env: env({ SPECKIT_PULL: '9' }), log: silent })).outcome, 'ok');
});

test('an amendment merged into another branch does not resume the implementation', async () => {
  const github = diagnosisRepo();
  amendmentPull(github).base = { ref: 'main' };
  github.closePull(11, { merged: true });
  assert.equal((await runClosed({ client: github, env: env({ SPECKIT_PULL: '11', SPECKIT_ACTOR: 'dev' }), log: silent })).outcome, 'misdirected');
  const note = github.comments.at(-1);
  assert.equal(note.number, 9);
  assert.match(note.body, /^<!-- speckit-amend:closed 11 -->\n\*\*Amendment #11 was merged into `main`\*\* by @dev, not into `speckit\/f`\.[\s\S]*Revert that merge/);
  assert.doesNotMatch(note.body, /speckit-implement:resume/);
  assert.deepEqual(github.repo.runs, []);
  assert.equal((await runClosed({ client: github, env: env({ SPECKIT_PULL: '11' }), log: silent })).outcome, 'handled');
});

test('the orchestrator checks unreported pushes, picks up feedback that no event delivered, and closes stuck checks', async () => {
  const github = diagnosisRepo();
  const context = { folder: 'f', implementation: github.repo.pulls[0], twin: 5 };
  const lines = [];
  const report = (line) => lines.push(line);
  assert.equal((await maintainAmendment(github, env(), context, { now: github.clock, report })).outcome, 'checking', 'a head without a check was pushed by a person');
  assert.match(lines.join(), /Checking the changes pushed to amendment #11/);
  assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs.mode], ['speckit-analyze.lock.yml', 'check']);
  assert.equal((await maintainAmendment(github, env(), context, { now: github.clock })).outcome, 'running');
  github.repo.runs.at(-1).status = 'completed';
  Object.assign(github.repo.checkRuns.at(-1), { status: 'completed', conclusion: 'success', external_id: 'speckit:amend-consistent' });
  assert.equal((await maintainAmendment(github, env(), context, { now: github.clock })).outcome, 'idle');

  github.repo.reviews = [{ id: 1, pull: 11, user: { login: 'dev' }, state: 'CHANGES_REQUESTED', body: 'Rework R6.', submitted_at: later(github) }];
  assert.equal((await maintainAmendment(github, env(), context, { now: github.clock, report })).outcome, 'rework');
  assert.match(lines.join(), /Started a rework of amendment #11 with 1 feedback item/);
  assert.equal((await maintainAmendment(github, env(), context, { now: github.clock })).outcome, 'running');
  const stuck = github.repo.checkRuns.at(-1);
  assert.equal((await maintainAmendment(github, env(), context, { now: github.clock + 2 * 60 * 60 * 1000 + 60_000 })).outcome, 'stale');
  assert.equal(stuck.conclusion, 'failure');
  assert.match(github.comments.at(-1).body, /did not finish\. Comment on this pull request to try again\./);
  assert.equal((await maintainAmendment(diagnosisRepo({ amendment: false }), env(), context)).outcome, 'none');
});

test('the orchestrator settles a merge the amendment branch\'s workflows did not report', async () => {
  const github = diagnosisRepo();
  const context = { folder: 'f', implementation: github.repo.pulls[0], twin: 5 };
  github.closePull(11, { merged: true });
  const lines = [];
  assert.equal((await maintainAmendment(github, env(), context, { report: (line) => lines.push(line) })).outcome, 'merged');
  assert.match(github.comments.at(-1).body, /^<!-- speckit-implement:resume -->\n<!-- speckit-amend:closed 11 -->\n\*\*Amendment #11 was merged\*\* by a person\./);
  assert.deepEqual(github.repo.runs, [], 'the orchestrator continues itself');
  assert.match(lines.join(), /Amendment #11 was merged/);
  assert.equal((await maintainAmendment(github, env(), context)).outcome, 'handled');

  const superseded = diagnosisRepo();
  await superseded.createComment(11, 'Superseded by a new diagnosis on #9.');
  superseded.closePull(11);
  assert.equal((await maintainAmendment(superseded, env(), { ...context, implementation: superseded.repo.pulls[0] })).outcome, 'superseded');
});
