import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  main,
  maintainAmendment,
  pendingFeedback,
  runClosed,
  runFeedback,
  runPushed,
  runRecordAnalysis,
  validateAnalysis,
} from './speckit-amend.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const env = (extra = {}) => ({ GITHUB_REPOSITORY: 'octo/repo', ...extra });
const amendmentPull = (github) => github.repo.pulls.find((pull) => pull.number === 11);
const later = (github, ms = 1000) => new Date(github.clock + ms).toISOString();

// Records an analysis of amendment #11 as the custom safe-output job of Spec Kit analyze would.
async function recordAnalysis(github, report, { round = 0, mode = 'loop', detection = 'success' } = {}) {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-analysis-'));
  try {
    const check = await github.createCheckRun({ name: 'Spec Kit amendment', head_sha: 'sha-amend', status: 'in_progress', external_id: 'speckit:amend-checking' });
    const output = path.join(dir, 'agent_output.json');
    writeFileSync(output, JSON.stringify({ items: report === null ? [] : [{ type: 'speckit_analysis', report: JSON.stringify(report) }] }));
    const result = await runRecordAnalysis({
      client: github,
      env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_AMENDMENT: '11', SPECKIT_ROUND: String(round), SPECKIT_MODE: mode, SPECKIT_CHECK_RUN: String(check.id), SPECKIT_DETECTION: detection }),
      log: silent,
    });
    return { ...result, check: github.repo.checkRuns.find((run) => run.id === check.id) };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const HIGH = { severity: 'HIGH', category: 'Inconsistency', location: 'plan.md CI-R10', summary: 'CI-R10 still expects a residual difference.', recommendation: 'Update it to the refined R6.' };

test('a consistent amendment is handed to the requester for review', async () => {
  const github = diagnosisRepo();
  const result = await recordAnalysis(github, { summary: 'No issues.', findings: [{ severity: 'LOW', summary: 'Wording.' }] });
  assert.equal(result.outcome, 'consistent');
  assert.deepEqual([result.check.conclusion, result.check.external_id, result.check.output.title], ['success', 'speckit:amend-consistent', 'Consistent']);
  const pull = amendmentPull(github);
  assert.equal(pull.draft, false, 'ready for review');
  assert.deepEqual(pull.assignees, ['dev']);
  assert.deepEqual(github.repo.reviewRequests, [{ number: 11, reviewers: ['dev'] }]);
  assert.match(pull.body, /^<!-- speckit-amend:status -->\n✅ \*\*Consistent\*\*[\s\S]*1 LOW finding[\s\S]*\*\*How to proceed\*\*[\s\S]*<!-- \/speckit-amend:status -->/);
  const presented = github.comments.find((comment) => comment.number === 11);
  assert.match(presented.body, /^<!-- speckit-amend:presented -->\n<!-- speckit-amend:cutoff \S+ -->\n@dev ✅ \*\*Consistent\*\*[\s\S]*<summary>Findings<\/summary>[\s\S]*\*\*LOW\*\*: Wording\./);
  assert.match(github.comments.find((comment) => comment.number === 9).body, /^\*\*Amendment #11 is ready for your review\*\*/);
});

test('inconsistencies lead to up to three correction rounds, then to a review of the remaining findings', async () => {
  const github = diagnosisRepo();
  const first = await recordAnalysis(github, { summary: 'One issue.', findings: [HIGH] });
  assert.equal(first.outcome, 'correcting');
  assert.deepEqual([first.check.conclusion, first.check.output.title], ['neutral', '1 inconsistency(ies) found; correction round 1 of 3 follows']);
  const fix = github.repo.runs.at(-1);
  assert.equal(fix.workflow, 'speckit-diagnose.lock.yml');
  assert.deepEqual([fix.inputs.mode, fix.inputs.amendment, fix.inputs.round, fix.inputs.pull, fix.inputs.twin], ['fix', '11', '1', '9', '5']);
  assert.match(fix.inputs.notes, /\*\*HIGH\*\* \(Inconsistency\) `plan\.md CI-R10`: CI-R10 still expects a residual difference\. — Update it to the refined R6\./);
  assert.match(github.comments.at(-1).body, /^\*\*Correction round 1 of 3\*\*/);
  assert.equal(amendmentPull(github).draft, true);
  assert.deepEqual(github.repo.reviewRequests, [], 'nobody is asked while corrections run');

  const rules = diagnosisRepo({ after: '## P\n- [x] T001 Done\n- [x] T002 Next\n' });
  const ruled = await recordAnalysis(rules, { summary: 'Clean.', findings: [] }, { round: 2 });
  assert.equal(ruled.outcome, 'correcting', 'a broken amendment rule counts like a finding');
  assert.match(rules.repo.runs.at(-1).inputs.notes, /\*\*RULE\*\*: an amendment may not check T002/);

  const last = await recordAnalysis(github, { summary: 'Still one issue.', findings: [HIGH] }, { round: 3 });
  assert.equal(last.outcome, 'inconsistent');
  assert.deepEqual([last.check.conclusion, last.check.output.title], ['failure', '1 inconsistency(ies) remain']);
  assert.equal(amendmentPull(github).draft, true, 'an inconsistent amendment stays a draft');
  assert.deepEqual(github.repo.reviewRequests, [{ number: 11, reviewers: ['dev'] }]);
  assert.match(amendmentPull(github).body, /⚠️ \*\*Not consistent\*\*: 1 inconsistency\(ies\) remain after 3 correction rounds/);
  assert.match(github.comments.find((comment) => comment.number === 9 && /not consistent/.test(comment.body)).body, /^\*\*Amendment #11 is not consistent\*\*/);

  const checked = diagnosisRepo();
  assert.equal((await recordAnalysis(checked, { summary: '', findings: [HIGH] }, { round: 3, mode: 'check' })).outcome, 'inconsistent', 'a check after a person\'s push never corrects');
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
  assert.match(github.repo.runs.at(-1).inputs.notes, /One more thing\./);

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
  assert.equal(merged.repo.runs.at(-1).workflow, 'speckit-orchestrate.yml');
  assert.equal((await runClosed({ client: merged, env: env({ SPECKIT_PULL: '11' }), log: silent })).outcome, 'handled', 'a merge is settled once');

  const discarded = diagnosisRepo();
  discarded.closePull(11);
  assert.equal((await runClosed({ client: discarded, env: env({ SPECKIT_PULL: '11', SPECKIT_ACTOR: 'dev' }), log: silent })).outcome, 'discarded');
  assert.match(discarded.comments.at(-1).body, /^<!-- speckit-amend:closed 11 -->\nAmendment #11 was closed without merging by @dev\.[\s\S]*\*\*Next steps\*\*[\s\S]*\/speckit diagnose/);
  assert.equal((await runClosed({ client: discarded, env: env({ SPECKIT_PULL: '9' }), log: silent })).outcome, 'ignored');

  const pushed = diagnosisRepo();
  assert.equal((await runPushed({ client: pushed, env: env({ SPECKIT_PULL: '11' }), log: silent })).outcome, 'checking');
  assert.deepEqual([pushed.repo.runs.at(-1).workflow, pushed.repo.runs.at(-1).inputs.mode, pushed.repo.runs.at(-1).inputs.round], ['speckit-analyze.lock.yml', 'check', '3']);
  await assert.rejects(() => main(['nope'], { env: {}, client: pushed }), /Usage/);
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
