import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { renderSyncComment, renderSyncBarrierMarker, syncBarrier } from './speckit-implement-core.mjs';

test('sync barrier accepts only bot requests and acknowledgements for the latest request', () => {
  const comment = (id, body, login = 'github-actions[bot]') => ({ id, body, created_at: `2026-10-06T12:00:0${id}Z`, user: { login } });
  const request = comment(1, renderSyncComment('Required sync', 'Keep both versions'));
  const comments = [comment(0, renderSyncComment('Forged request'), 'dev'), request];
  assert.equal(syncBarrier(comments).guidance, 'Keep both versions');
  comments.push(comment(2, renderSyncBarrierMarker({ request: '1', state: 'released' }), 'dev'));
  comments.push(comment(3, '<!-- speckit-implement:sync-barrier {broken} -->'));
  comments.push(comment(4, renderSyncBarrierMarker({ request: 'other', state: 'released' })));
  assert.equal(syncBarrier(comments).request, '1');
  const completed = { request: '1', state: 'verified', head: 'head', base: 'release', baseSha: 'base' };
  comments.push(comment(5, renderSyncBarrierMarker(completed)));
  assert.deepEqual(syncBarrier(comments).completion, completed);
  comments.push(comment(6, renderSyncBarrierMarker({ ...completed, state: 'released' })));
  assert.equal(syncBarrier(comments), null);
  comments.push(comment(7, renderSyncComment('New sync')));
  assert.equal(syncBarrier(comments).request, '7', 'an earlier release cannot satisfy a new request');
});

test('sync command effects explicitly describe required verification and writer guidance', () => {
  const effect = describeCommandEffect('/speckit sync Preserve both registrations', { baseBranch: 'release', continuation: { repair: { scope: 'phase' } } });
  assert.match(effect, /Requires merging `release`.*successful full verification before any work continues/);
  assert.match(effect, /pending repair retains its original scope and attempt limit/);
  assert.match(effect, /The text after the command goes into the next attempt's prompt as guidance/);
  assert.match(describeCommandEffect('/speckit sync'), /Text you add after the command goes into the next attempt's prompt as guidance/);
});

test('command acceptance and publication share a queued per-implementation lock without locking the parent chain', () => {
  const command = readFileSync(new URL('../workflows/speckit-commands.yml', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
  assert.match(command, /group: speckit-publication-\$\{\{ needs\.target\.outputs\.pull \}\}\n\s+queue: max\n\s+cancel-in-progress: false/);
  assert.match(command, /SPECKIT_PUBLICATION_PULL: \$\{\{ needs\.target\.outputs\.pull \}\}/);
  for (const [workflow, jobs] of [
    ['task', ['land']], ['converge', ['land']], ['resolve', ['land']],
    ['repair', ['land', 'report']], ['merge', ['archive', 'land']], ['spec', ['sync']],
  ]) {
    const text = readFileSync(new URL(`../workflows/speckit-implement-${workflow}.yml`, import.meta.url), 'utf8').replace(/\r\n/g, '\n');
    for (const job of jobs) {
      const block = text.match(new RegExp(`^  ${job}:\\n[\\s\\S]*?(?=^  [a-z_-]+:|$(?![\\s\\S]))`, 'm'))?.[0];
      assert.ok(block, `${workflow}/${job} exists`);
      assert.match(block, /group: speckit-publication-\$\{\{ inputs\.pull \}\}\n\s+queue: max\n\s+cancel-in-progress: false/);
    }
  }
  const parent = readFileSync(new URL('../workflows/speckit-implement-spec.yml', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
  assert.match(parent, /^concurrency:\n  group: speckit-implement-/m);
  assert.doesNotMatch(parent, /^  group: speckit-publication-/m, 'the chain must not hold the publication lock at workflow level while waiting for reusable jobs');
});

import { integrationTarget } from './speckit-spec.mjs';
import {
  readSpecTasks,
  tasksPathOf,
  baseBranchFromEnv,
  baseBranchOf,
  defaultBranchOf,
  CHECK_ATTEMPT,
  CHECK_CONFLICT,
  CHECK_SYNC_CONFLICT,
  MAX_SYNC_RESOLVE_ATTEMPTS,
  CHECK_DIAGNOSED,
  CHECK_DIAGNOSING,
  CHECK_DONE,
  CHECK_LIMIT,
  CHECK_MERGE,
  appendPullRequestTasks,
  checkRunOutput,
  convergenceRounds,
  attemptCheckId,
  decideNext,
  decideLifecycle,
  environmentExclusivityReasons,
  extractTasks,
  hasConflictMarkers,
  implementationBranch,
  isEnvironmentPath,
  isProtectedPath,
  latestCheckRun,
  listTasks,
  markerTimes,
  MAX_WRITER_GUIDANCE,
  MAX_PULL_BODY,
  neutralizeMarkers,
  normalizeWriterGuidance,
  appendWriterGuidance,
  nextTask,
  nextTaskGroup,
  parseGuidance,
  isImplementRunOf,
  isTwinRunOf,
  phaseSubject,
  specTitleOfPull,
  taskSubject,
  taskJobName,
  renderImplementTitle,
  MAX_MATRIX_JOBS_PER_RUN,
  parseAttemptCheckId,
  parseAttempts,
  parseMaxActiveSpecs,
  planTasks,
  describeParallelGroups,
  renderConvergePrompt,
  renderGuidanceMarker,
  renderRepairPrompt,
  renderRepairMarker,
  unresolvedRepair,
  renderAmendmentHowTo,
  renderFixHowTo,
  renderNextSteps,
  CATEGORY_MEANING,
  describeCommandEffect,
  describeContinuation,
  parseNextCommand,
  renderPullRequestBody,
  renderPullRequestTitle,
  renderResolvePrompt,
  renderRetryComment,
  renderStartComment,
  renderAttemptMarker,
  renderImplementRunName,
  renderTaskPrompt,
  TASK_TEST_SCOPE,
  LEGACY_RESUME_COMMENT_MARKER,
  START_COMMENT_MARKER,
  setAmendmentStatus,
  kindLabel,
  summarizeTask,
  syncPullRequestTaskList,
  syncPullRequestTicks,
  taskProgress,
  tickPullRequestBody,
  tickTask,
  validateAmendment,
  validateConvergeChange,
  validateTaskChange,
} from './speckit-implement-core.mjs';

const context = { serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' };

test('extracts tasks with the headings that contain them', () => {
  const markdown = [
    '# Tasks: Feature',
    '## Format: `[ID] [P?] [Story] Description`',
    '- not a task',
    '## Phase 1: Setup',
    '**Purpose**: setup',
    '- [ ] T001 Create project',
    '- [X] T002 [P] Configure lint',
    '## Phase 2: Empty',
    '## Phase 3: User Story 1',
    '### Tests',
    '* [ ] T010 [P] [US1] Contract test',
    '### Implementation',
    '  - [ ] T1000 [US1] Large ID',
    '- [ ] ST001 not a task id',
  ].join('\r\n');
  const tasks = extractTasks(markdown);
  assert.equal(tasks.count, 4);
  assert.deepEqual(tasks.items, [
    { type: 'heading', level: 2, text: 'Phase 1: Setup' },
    { type: 'task', id: 'T001', text: 'Create project', done: false },
    { type: 'task', id: 'T002', text: '[P] Configure lint', done: true },
    { type: 'heading', level: 2, text: 'Phase 3: User Story 1' },
    { type: 'heading', level: 3, text: 'Tests' },
    { type: 'task', id: 'T010', text: '[P] [US1] Contract test', done: false },
    { type: 'heading', level: 3, text: 'Implementation' },
    { type: 'task', id: 'T1000', text: '[US1] Large ID', done: false },
  ]);
  assert.deepEqual(extractTasks(null), { items: [], count: 0 });
});

test('renders the pull request title, body, comment, and check run text', () => {
  assert.equal(implementationBranch('20261005-130700-x'), 'speckit/20261005-130700-x');
  assert.equal(renderPullRequestTitle({ title: 'Platform Persistence Foundation' }), 'Implement: Platform Persistence Foundation');

  const body = renderPullRequestBody({ twinNumber: 33, folder: 'f', context, tasks: extractTasks('## Phase 1\n- [x] T001 done already\n') });
  assert.match(body, /^Closes #33$/m);
  assert.match(body, /^\*\*Spec\*\*: \[`specs\/f`\]\(https:\/\/github\.com\/octo\/repo\/tree\/main\/specs\/f\)$/m);
  assert.match(body, /## Tasks \(1\)\n\nShort form; the full task texts are in \[`tasks\.md`\]\(https:\/\/github\.com\/octo\/repo\/blob\/speckit\/f\/specs\/f\/tasks\.md\)\.\n\n### Phase 1\n\n- \[ \] T001 done already/);
  assert.doesNotMatch(body, /\n{3,}/);
  assert.match(renderPullRequestBody({ twinNumber: 1, folder: 'f', context, tasks: extractTasks('') }), /No tasks were found/);

  const comment = renderStartComment({ twinNumber: 33, folder: 'f', taskCount: 4 });
  assert.ok(comment.startsWith(START_COMMENT_MARKER));
  assert.match(comment, /#33 \(`specs\/f`\): 4 task\(s\) queued/);
  assert.equal(checkRunOutput(4).title, '0 of 4 tasks implemented');
});

test('summarizes a task to its tags and first sentence', () => {
  assert.equal(summarizeTask('[P] [US1] Add `src/A.cs` (FR-1). Then assert that `x.y` works.'), '[P] [US1] Add `src/A.cs` (FR-1). …');
  assert.equal(summarizeTask('Short task'), 'Short task');
  assert.equal(summarizeTask('[P] Ends with a period.'), '[P] Ends with a period.');
  const long = summarizeTask(`[US2] Implement ${'word '.repeat(60)}end`);
  assert.ok(long.length <= 170, long);
  assert.match(long, /^\[US2\] Implement word word.* …$/);
  const code = summarizeTask(`Add \`${'a/'.repeat(100)}File.cs\` now`);
  assert.equal((code.match(/`/g) ?? []).length % 2, 0, 'an open code span is closed');
  assert.equal(summarizeTask('multi\r\nline   text'), 'multi line text');
});

test('keeps the pull request body under GitHub\'s limit for large task lists', () => {
  const big = (count, size) => `## Phase 1\n\n${Array.from({ length: count }, (_, index) => `- [ ] T${String(index + 1).padStart(3, '0')} [P] [US1] Task ${index}. ${'x'.repeat(size)}`).join('\n')}\n`;
  const body = renderPullRequestBody({ twinNumber: 34, folder: 'f', context, tasks: extractTasks(big(40, 2700)) });
  assert.ok(body.length < MAX_PULL_BODY, `${body.length}`);
  assert.match(body, /- \[ \] T040 \[P\] \[US1\] Task 39\. …/);
  assert.match(tickPullRequestBody(body, 'T040'), /- \[x\] T040 /);

  const many = renderPullRequestBody({ twinNumber: 34, folder: 'f', context, tasks: extractTasks(big(2500, 10)) });
  assert.ok(many.length <= MAX_PULL_BODY, `${many.length}`);
  assert.match(many, /- \[ \] T001 \[P\] \[US1\]$/m, 'falls back to IDs and tags');
  const huge = renderPullRequestBody({ twinNumber: 34, folder: 'f', context, tasks: extractTasks(big(4000, 10)) });
  assert.ok(huge.length <= MAX_PULL_BODY, `${huge.length}`);
  assert.match(huge, /_\d+ more task\(s\) are not listed because the description would be too long\._/);

  const appended = appendPullRequestTasks('## Tasks (1)\n\n- [x] T001 a\n', [{ id: 'T002', text: `[P] First sentence. ${'y'.repeat(5000)}` }], 'Phase 2: Convergence');
  assert.match(appended, /- \[ \] T002 \[P\] First sentence\. …\n$/);
  const full = appendPullRequestTasks(`## Tasks (1)\n\n${'z'.repeat(MAX_PULL_BODY)}\n`, [{ id: 'T002', text: 'b' }], 'Phase 2: Convergence');
  assert.match(full, /_1 task\(s\) were appended; see `tasks\.md` on the implementation branch\._\n$/);
});

test('decides the pull request lifecycle relative to the flag time', () => {
  const flaggedAt = '2026-10-06T10:00:00Z';
  const pull = (fields) => ({ number: 7, state: 'closed', merged_at: null, closed_at: null, ...fields });
  assert.deepEqual(decideLifecycle({ pulls: [], flaggedAt }), { state: 'ready', reset: false });
  assert.equal(decideLifecycle({ pulls: [pull({ state: 'open' })], flaggedAt }).state, 'in-progress');
  assert.equal(decideLifecycle({ pulls: [pull({ closed_at: '2026-10-06T11:00:00Z' })], flaggedAt }).state, 'fallback');
  assert.deepEqual(decideLifecycle({ pulls: [pull({ closed_at: '2026-10-06T09:00:00Z' })], flaggedAt }), { state: 'ready', reset: true });
  assert.equal(decideLifecycle({ pulls: [pull({ closed_at: '2026-10-06T11:00:00Z', merged_at: '2026-10-06T11:00:00Z' })], flaggedAt }).state, 'merged');
  assert.deepEqual(decideLifecycle({ pulls: [pull({ closed_at: '2026-10-06T09:00:00Z', merged_at: '2026-10-06T09:00:00Z' })], flaggedAt }), { state: 'ready', reset: true });
  assert.equal(decideLifecycle({ pulls: [pull({ closed_at: '2026-10-06T09:00:00Z' })], flaggedAt: null }).state, 'fallback');
});

const TASKS = '## Phase 1\n\n- [x] T001 First\n- [ ] T002 [P] Second\n- [ ] T003 Third\n';

test('lists tasks, the next task, and progress in file order', () => {
  assert.deepEqual(listTasks(TASKS).map((task) => [task.id, task.done]), [['T001', true], ['T002', false], ['T003', false]]);
  assert.deepEqual(nextTask(TASKS), { type: 'task', id: 'T002', text: '[P] Second', done: false });
  assert.deepEqual(taskProgress(TASKS), { done: 1, total: 3 });
  assert.equal(nextTask('- [x] T001 done\n'), null);
});

test('renders the chain run name, attempt markers, and prompts', () => {
  assert.equal(renderImplementRunName(38), 'Spec Kit implement spec #38');
  assert.equal(isImplementRunOf('Spec Kit implement spec #38', 38), true);
  assert.equal(isImplementRunOf('Spec Kit implement spec #38 · Recording Lineage and Upload', 38), true);
  assert.equal(isImplementRunOf('Spec Kit implement spec #381', 38), false);
  assert.equal(isImplementRunOf('Spec Kit implement #38 T002 attempt 3', 38), false, 'runs of the earlier workflows are not runs of this one');
  assert.equal(isImplementRunOf('Spec Kit diagnose #38', 38), false);
  assert.equal(isTwinRunOf('speckit-diagnose.lock.yml', 'Spec Kit diagnose spec #38 · Diagnose why the implementation stopped', 38), true);
  assert.equal(isTwinRunOf('speckit-diagnose.lock.yml', 'Spec Kit diagnose spec #381 · Revise the amendment with review feedback', 38), false);
  assert.equal(isTwinRunOf('speckit-specify.lock.yml', 'Spec Kit specify spec #38 · Write a new spec that unblocks the implementation', 38), true);
  assert.equal(isTwinRunOf('speckit-analyze.lock.yml', 'Spec Kit diagnose spec #38 · Diagnose why the implementation stopped', 38), false);
  assert.equal(isTwinRunOf('speckit-specify.lock.yml', '[Spec Kit specify] spec #38 · Author a spec for the cause outside the stopped implementation (#41)', 38), true, 'runs named with brackets');
  assert.equal(isTwinRunOf('speckit-diagnose.lock.yml', 'Spec Kit diagnose #38 · why #41 stopped', 38), true, 'runs named before the spec reference');
  assert.equal(isTwinRunOf('speckit-diagnose.lock.yml', 'Spec Kit diagnose #38', 38), true, 'runs named before the mode was added');
  assert.equal(isTwinRunOf('speckit-diagnose.lock.yml', 'Spec Kit diagnose #381 · fix #82 round 1', 38), false);
  assert.equal(isTwinRunOf('speckit-analyze.lock.yml', 'Spec Kit analyze #38 · check #82 after round 1', 38), true);
  assert.equal(isTwinRunOf('speckit-analyze.lock.yml', 'Spec Kit diagnose #38 · why #41 stopped', 38), false);
  const comment = (body, login = 'github-actions[bot]', at = '2026-10-06T11:00:00Z') => ({ body, user: { login }, created_at: at });
  const marker = renderAttemptMarker({ kind: 'task', task: 'T002', attempt: 2, outcome: 'failure' });
  assert.equal(marker, '<!-- speckit-implement:attempt {"kind":"task","task":"T002","attempt":2,"outcome":"failure"} -->');
  assert.deepEqual(parseAttempts([
    comment(`${marker}\n**T002 attempt 2 failed**`),
    comment(renderAttemptMarker({ kind: 'converge', attempt: 1, outcome: 'success' }), undefined, '2026-10-06T10:00:00Z'),
    comment(renderAttemptMarker({ kind: 'repair', attempt: 1, outcome: 'failure' }), undefined, '2026-10-06T10:30:00Z'),
    comment(marker, 'mallory'),
    comment('<!-- speckit-implement:attempt {"kind":"merge","outcome":"failure"} -->'),
    comment('<!-- speckit-implement:attempt {"step":"task","task":"T001","attempt":1,"outcome":"success"} -->', undefined, '2026-10-06T09:00:00Z'),
    comment('<!-- speckit-implement:attempt {broken -->'),
  ]), [
    { kind: 'task', task: 'T001', attempt: 1, outcome: 'success', at: '2026-10-06T09:00:00Z' },
    { kind: 'converge', task: null, attempt: 1, outcome: 'success', at: '2026-10-06T10:00:00Z' },
    { kind: 'repair', task: null, attempt: 1, outcome: 'failure', at: '2026-10-06T10:30:00Z' },
    { kind: 'task', task: 'T002', attempt: 2, outcome: 'failure', at: '2026-10-06T11:00:00Z' },
  ], 'only workflow-authored, valid markers count, oldest first; markers written before `kind` carry it as `step`');
  assert.equal(attemptCheckId({ kind: 'task', task: 'T002', attempt: 3 }), 'speckit:attempt:task:T002:3');
  assert.deepEqual(parseAttemptCheckId('speckit:attempt:converge::1'), { kind: 'converge', task: null, attempt: 1 });
  assert.deepEqual(parseAttemptCheckId('speckit:attempt:repair::2'), { kind: 'repair', task: null, attempt: 2 });
  assert.equal(parseAttemptCheckId(CHECK_ATTEMPT), null);
  assert.equal(renderTaskPrompt('T004'), `/speckit-implement Implement only task T004. Do not implement any other task. Do not edit \`tasks.md\`: the tooling checks the task's box after verification. Do not commit and do not push. ${TASK_TEST_SCOPE}`);
  assert.match(TASK_TEST_SCOPE, /Do not run the whole test suite/);
  assert.match(renderTaskPrompt('T004', 'f'), /^\/speckit-implement Implement only task T004 of the spec in `specs\/f\/` \(its `tasks\.md`\); ignore the other specs\. Do not implement any other task\./);
  assert.match(renderConvergePrompt(), /^\/speckit-converge /);
  assert.match(renderResolvePrompt({ folder: 'f', files: ['a.cs', 'b.md'] }), /`specs\/f`[\s\S]*`a\.cs`, `b\.md`[\s\S]*smallest change to other files[\s\S]*never delete them, and never weaken a test, assertion, or requirement[\s\S]*A person reviews such changes/);
  assert.match(renderResolvePrompt({ folder: 'f', files: ['a.cs'], base: 'main', guidance: '  Keep\nboth sides.  ' }), /Guidance from the person who retried this implementation: Keep both sides\.$/);
  assert.match(renderRepairPrompt({ repair: { scope: 'group', tasks: ['T001'] }, folder: 'f', guidance: '  Reuse\nexisting coverage. ' }), /Writer-approved guidance: Reuse existing coverage\.$/);
  assert.equal(kindLabel({ kind: 'task', task: 'T001' }), 'T001');
  assert.equal(kindLabel({ kind: 'converge' }), 'convergence');
});

test('picks the newest check run', () => {
  assert.equal(latestCheckRun([{ id: 2 }, { id: 9 }, { id: 4 }]).id, 9);
  assert.equal(latestCheckRun([]), null);
});

const attempt = (task, outcome = 'failure', at = '2026-10-06T11:00:00Z', kind = 'task') => ({ kind, task, attempt: 1, outcome, at });
const order = (...list) => ({ action: 'tasks', kind: 'task', order: list, phaseEnd: true, lastIsParallel: false, phase: 'Phase 1', stale: [] });

test('decides the next tasks of an implementation from the attempt ledger', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const decide = (fields) => decideNext({ tasksMarkdown: TASKS, windowStart, ...fields });

  assert.deepEqual(decide({ done: true }), { action: 'done' });
  assert.deepEqual(decide({}), order([{ task: 'T002', attempt: 1 }], [{ task: 'T003', attempt: 1 }]));
  assert.deepEqual(decide({ attempts: [attempt('T001'), attempt('T002')] }), order([{ task: 'T002', attempt: 2 }], [{ task: 'T003', attempt: 1 }]));
  assert.deepEqual(decide({ attempts: [attempt('T002', 'failure', '2026-10-06T09:00:00Z')] }), order([{ task: 'T002', attempt: 1 }], [{ task: 'T003', attempt: 1 }]), 'attempts before the window do not count');
  assert.deepEqual(decide({ attempts: [attempt('T002'), attempt('T002', 'requeue'), attempt('T002', 'requeue')] }), order([{ task: 'T002', attempt: 4 }], [{ task: 'T003', attempt: 1 }]), 'a redo is no failure');
  assert.deepEqual(decide({ attempts: [attempt('T002'), attempt('T002')] }), { action: 'limit', kind: 'task', task: 'T002', attempts: 2, stale: [] });
  const noOps = Array.from({ length: 4 }, () => attempt('T002', 'requeue'));
  assert.deepEqual(decide({ attempts: noOps }), { action: 'limit', kind: 'task', task: 'T002', attempts: 4, stale: [] }, 'endless redos stop too');
  assert.deepEqual(decide({ checks: [{ id: 1, external_id: CHECK_DONE, conclusion: 'success' }] }), { action: 'done' });
  const limited = { id: 1, external_id: CHECK_LIMIT, conclusion: 'failure', started_at: '2026-10-06T11:30:00Z' };
  assert.deepEqual(decide({ checks: [limited] }), { action: 'failed', diagnosis: { state: 'none', check: limited, count: 0 } });
  assert.equal(decide({ checks: [limited], resumedAt: '2026-10-06T11:45:00Z' }).action, 'tasks', 'a retry after the stop continues');
  assert.equal(decide({ checks: [limited], resumedAt: '2026-10-06T11:15:00Z' }).action, 'failed', 'an older retry does not');
  assert.deepEqual(decide({ maxEntries: 1 }), { ...order([{ task: 'T002', attempt: 1 }]), phaseEnd: false }, 'a long phase continues in the next run');
});

test('waits while a diagnosis runs, then for a decision, and gives up on a stale diagnosis', () => {
  const now = Date.parse('2026-10-06T12:00:00Z');
  const decide = (fields) => decideNext({ tasksMarkdown: TASKS, windowStart: '2026-10-06T10:00:00Z', now, ...fields });
  const limited = { id: 1, external_id: CHECK_LIMIT, status: 'completed', conclusion: 'failure', started_at: '2026-10-06T11:00:00Z' };
  const running = { id: 2, external_id: CHECK_DIAGNOSING, status: 'in_progress', started_at: '2026-10-06T11:30:00Z' };
  assert.deepEqual(decide({ checks: [limited, running] }), { action: 'diagnosing' });
  const stale = { ...running, started_at: '2026-10-06T10:30:00Z' };
  assert.deepEqual(decide({ checks: [limited, stale] }), { action: 'failed', diagnosis: { state: 'stale', check: stale, count: 1 } });
  const reported = { ...running, status: 'completed', conclusion: 'neutral', external_id: CHECK_DIAGNOSED };
  assert.deepEqual(decide({ checks: [limited, reported] }), { action: 'failed', diagnosis: { state: 'reported', check: reported, count: 1 } });
  assert.equal(decide({ checks: [limited, reported], resumedAt: '2026-10-06T11:45:00Z' }).action, 'tasks', 'a retry comment after the stop retries');
  assert.equal(decide({ checks: [limited, reported], resumedAt: '2026-10-06T11:15:00Z' }).action, 'failed', 'an older retry comment does not');
});
test('renders the next steps a person can take', () => {
  const plain = renderNextSteps().join('\n');
  assert.match(plain, /^\*\*Recommended next step\*\*: Wait for the diagnosis\.\n\n- \*\*Why\*\*: It starts automatically/);
  assert.match(plain, /<details><summary>Other options<\/summary>[\s\S]*- \*\*Retry what stopped\*\*\n {2}- Why: [^\n]+\n {2}- What it does: Does not merge the base branch[\s\S]*\/speckit retry[\s\S]*Push a fix yourself, or close the pull request[\s\S]*<\/details>$/);
  assert.doesNotMatch(plain, /\/speckit (diagnose|resume|sync|apply)/);
  const auto = renderNextSteps({ diagnosisUrl: 'https://github.com/octo/repo/actions/workflows/speckit-diagnose.lock.yml', behindBase: 'release', baseBranch: 'release', continuation: 'continues with T004' }).join('\n');
  assert.match(auto, /^\*\*Recommended next step\*\*: \[Wait for the diagnosis\]\(https:\/\/github\.com\/octo\/repo\/actions\/workflows\/speckit-diagnose\.lock\.yml\)\.\n\n- \*\*Why\*\*: It starts automatically/);
  assert.doesNotMatch(auto, /\/speckit diagnose/);
  assert.match(auto, /- What it does: Does not merge `release`: the implementation continues with T004 on the branch as it is, with a fresh attempt count \(at most 2 failed attempts\)/);
  assert.doesNotMatch(auto, /Merge `release` first|\/speckit (resume|sync)/);
  const diagnosed = renderNextSteps({ options: [{ title: 'Retry with guidance', why: 'The agent mixed up the paths.', command: '/speckit retry Use the helper' }], amendment: { number: 7, title: 'Amend: Clarify R6', html_url: 'https://github.com/octo/repo/pull/7', status: 'consistent' }, diagnosed: true }).join('\n');
  assert.match(diagnosed, /^\*\*Recommended next step\*\*: \[Review and merge the amendment #7: Amend: Clarify R6 \(consistent\)\]\(https:\/\/github\.com\/octo\/repo\/pull\/7\)\.\n\n- \*\*Why\*\*: [^\n]+\n- \*\*What it does\*\*: Merging applies the amendment[\s\S]*Other options[\s\S]*- Why: The agent mixed up the paths\.[\s\S]*\/speckit retry Use the helper/);
  assert.doesNotMatch(diagnosed, /Comment this on the pull request/, 'merging the amendment needs no command');
  assert.doesNotMatch(diagnosed, /\/speckit (apply|revise|discard)/);
  assert.equal(diagnosed.match(/\/speckit retry/g).length, 1, 'an option already covers retry');
  assert.match(renderNextSteps({ amendmentPull: 7 }).join('\n'), /^\*\*Recommended next step\*\*: Review and merge the amendment #7\./);
  for (const block of [plain, auto, diagnosed]) {
    for (const fence of block.matchAll(/```text\n([\s\S]*?)```/g)) assert.doesNotMatch(fence[1], /<[A-Za-z][^>]*>|\[[A-Za-z][^\]]*\]/, `no placeholder in a command: ${fence[1]}`);
  }
  const howTo = renderAmendmentHowTo(49).join('\n');
  assert.match(howTo, /\*\*Merge\*\* this pull request[\s\S]*continues on #49[\s\S]*\*Request changes\*[\s\S]*\*\*Close\*\*/);
  assert.match(renderFixHowTo(75, 'main').join('\n'), /\*\*Merge\*\* this pull request to add the spec to `main`[\s\S]*signs it off with `\/speckit implement`[\s\S]*#75 waits for it and retries by itself[\s\S]*\*\*Close\*\*/);
  const status = setAmendmentStatus('Body from the agent.', ['⏳ checking']);
  assert.equal(status, '<!-- speckit-amend:status -->\n⏳ checking\n<!-- /speckit-amend:status -->\n\nBody from the agent.');
  assert.equal(setAmendmentStatus(status, ['✅ done']), '<!-- speckit-amend:status -->\n✅ done\n<!-- /speckit-amend:status -->\n\nBody from the agent.');
});

test('describes what each command does and what a restart continues with', () => {
  const context = { continuation: { repair: { scope: 'phase' } } };
  for (const force of ['--force', '--FORCE']) {
    const bare = describeCommandEffect(`/speckit retry ${force}`, context);
    assert.match(bare, /reverifies without running a repair agent.*pending phase verification/);
    assert.match(bare, /`--force` is a flag.*not guidance\.$/);
    assert.match(bare, /if this repair attempt fails, the implementation stops for a person again/);
    const guided = describeCommandEffect(`/speckit retry ${force} Inspect the logs`, context);
    assert.match(guided, /runs a repair agent with the guidance, then reverifies/);
    assert.match(guided, /`--force` is a flag/);
  }
  assert.deepEqual(parseNextCommand('/speckit retry Rerun as is'), { verb: 'retry', argument: 'Rerun as is', flags: [], unknownFlags: [], guidance: 'Rerun as is' });
  assert.deepEqual(parseNextCommand('/speckit retry --force; Rerun'), { verb: 'retry', argument: '--force; Rerun', flags: ['--force'], unknownFlags: [], guidance: 'Rerun' });
  assert.match(describeCommandEffect('/speckit retry --force;', context), /`--force` is a flag/);
  assert.equal(parseNextCommand('please /speckit retry'), null);
  assert.match(describeCommandEffect('/speckit retry', { baseBranch: 'main', continuation: 'continues with the convergence' }), /^Does not merge `main`: the implementation continues with the convergence on the branch as it is[\s\S]*Text you add after the command/);
  assert.equal(describeCommandEffect('/speckit resume Use x', { baseBranch: 'main' }), null);
  assert.match(describeCommandEffect('/speckit specify Make it robust', { baseBranch: 'main' }), /authors a new spec for the cause, guided by the text after the command, and opens it as a pull request against `main`; it is checked for consistency and merged automatically[\s\S]*signs it off with `\/speckit implement`[\s\S]*retries by itself on its current phase base/);
  assert.equal(describeCommandEffect('/speckit fix Make it robust', { baseBranch: 'main' }), null, 'fix is no command anymore');
  assert.equal(describeCommandEffect('/speckit help'), null);
  for (const verb of ['artifacts', 'retry', 'outside', 'decision', 'unknown']) assert.ok(CATEGORY_MEANING[verb]);

  const limit = { external_id: CHECK_LIMIT, status: 'completed', conclusion: 'failure', output: { title: 'The phase verification failed' }, started_at: 'a' };
  const started = '## P1\n\n- [x] T004 a\n- [ ] T005 b\n- [ ] T006 c\n';
  const fresh = '## P1\n\n- [x] T004 a\n\n## P2\n\n- [ ] T005 b\n- [ ] T006 c\n';
  assert.deepEqual(describeContinuation({ action: 'tasks', kind: 'task', order: [[{ task: 'T005' }, { task: 'T006' }]] }, [], started), { text: 'continues with T005, T006', mergesBase: false });
  assert.equal(describeContinuation({ action: 'tasks', kind: 'task', order: [[{ task: 'T005' }]] }, [], fresh).mergesBase, true, 'a phase that has not started merges the base branch first');
  assert.match(describeContinuation({ action: 'tasks', kind: 'converge' }, [limit]).text, /^continues with the convergence \(the failed verification is not repeated by itself; the next verification, at the latest the full verification before the merge, covers it\)$/);
  assert.equal(describeContinuation({ action: 'tasks', kind: 'converge' }).mergesBase, true);
  assert.deepEqual(describeContinuation({ action: 'tasks', kind: 'resolve' }), { text: 'continues with the conflict resolution', mergesBase: false });
  assert.match(describeContinuation({ action: 'merge' }).text, /^continues with the merge, which first merges the base branch and runs the full verification$/);
  assert.equal(describeContinuation({ action: 'wait' }), null);
  assert.match(describeCommandEffect('/speckit retry', { baseBranch: 'main', continuation: { text: 'continues with the convergence', mergesBase: true } }), /^The implementation continues with the convergence, with a fresh attempt count[\s\S]*That work merges `main` first at its normal synchronization boundary/);
});

test('carries guidance through a marker, and validates amendments', () => {
  const marker = renderGuidanceMarker('Use `x` --> <!-- y');
  assert.equal(parseGuidance(`intro\n${marker}\nrest`), 'Use `x` --> <!-- y');
  assert.equal(parseGuidance('no marker'), null);
  assert.equal(parseGuidance('<!-- speckit-implement:guidance !!! -->'), null);
  assert.equal(normalizeWriterGuidance('  first\n\n second  '), 'first second');
  assert.equal(normalizeWriterGuidance(` ${'x'.repeat(MAX_WRITER_GUIDANCE + 100)} `).length, MAX_WRITER_GUIDANCE);
  assert.equal(appendWriterGuidance('Do the work.', ''), 'Do the work.');
  const retried = renderRetryComment('Retried.', 'x'.repeat(MAX_WRITER_GUIDANCE + 100));
  assert.equal(parseGuidance(retried)?.length, MAX_WRITER_GUIDANCE, 'retry guidance is capped');

  const before = '## P\n- [x] T001 Done\n- [ ] T002 Next\n- [ ] T003 Later\n';
  const ok = validateAmendment({ folder: 'f', changedPaths: ['specs/f/research.md', 'specs/f/tasks.md', 'specs/f/spec.md'], beforeTasks: before, afterTasks: '## P\n- [ ] T001 Done\n- [ ] T004 Fix first\n- [ ] T002 Next, clarified\n' });
  assert.deepEqual(ok.reasons, []);
  assert.deepEqual(ok.notes, ['it changes the requirements in `spec.md`', 'T001 is unchecked and will be implemented again', 'it adds T004', 'it changes T002', 'it removes T003']);
  const bad = validateAmendment({ folder: 'f', changedPaths: ['src/a.cs', 'specs/f/tasks.md'], beforeTasks: before, afterTasks: '## P\n- [x] T002 Next\n- [ ] T003 Later\n- [ ] T003 Again\n' });
  assert.match(bad.reasons.join('\n'), /may only change files in `specs\/f\/`, but it changes `src\/a\.cs`/);
  assert.match(bad.reasons.join('\n'), /may not check T002/);
  assert.match(bad.reasons.join('\n'), /T003 appears more than once/);
  assert.match(bad.reasons.join('\n'), /may not remove the completed task T001/);
});

test('renders the pull request task list from tasks.md, keeping the rest of the body', () => {
  const body = renderPullRequestBody({ twinNumber: 5, folder: 'f', context, tasks: extractTasks('## P\n- [ ] T001 A\n- [ ] T002 B\n') });
  const edited = body.replace('Closes #5', 'Closes #5\n\nA note a person added.');
  const synced = syncPullRequestTaskList(edited, { twinNumber: 5, folder: 'f', context, tasksMarkdown: '## P\n- [x] T001 A\n- [ ] T004 Fix first\n- [ ] T002 B, clarified\n' });
  assert.match(synced, /A note a person added\./);
  assert.match(synced, /## Tasks \(3\)[\s\S]*- \[x\] T001 A\n- \[ \] T004 Fix first\n- \[ \] T002 B, clarified/);
  assert.equal(syncPullRequestTaskList('no task section', { twinNumber: 5, folder: 'f', context, tasksMarkdown: '- [ ] T001 A\n' }), 'no task section');

  const withNote = `${synced}\nA note below the task list.\n`;
  const resynced = syncPullRequestTaskList(withNote, { twinNumber: 5, folder: 'f', context, tasksMarkdown: '## P\n- [x] T001 A\n- [x] T004 Fix first\n- [ ] T002 B, clarified\n' });
  assert.match(resynced, /- \[x\] T004 Fix first[\s\S]*<!-- speckit-implement:tasks-end -->\n\nA note below the task list\.\n$/);
  assert.equal(resynced.match(/## Tasks/g).length, 1);
  const longPrefix = `${'p'.repeat(64_000)}\n${synced}`;
  const fallback = syncPullRequestTaskList(longPrefix, { twinNumber: 5, folder: 'f', context, tasksMarkdown: '## P\n- [x] T001 A\n- [x] T004 Fix first\n- [ ] T002 B, clarified\n' });
  assert.ok(fallback.length <= 65536);
  assert.match(fallback, /- \[x\] T004 Fix first/, 'a body that would grow too long only gets its checks updated');
});

const PARALLEL_TASKS = [
  '## Phase 1',
  '',
  '- [x] T001 Setup',
  '- [ ] T002 [P] [US1] A',
  '- [ ] T003 [P] [US1] B',
  '- [ ] T004 [US1] [P] C',
  '- [ ] T005 [P] D',
  '- [ ] T006 Sequential',
  '- [ ] T007 [P] After the sequential task',
  '',
  '## Phase 2',
  '',
  '- [ ] T008 [P] Other phase',
  '',
].join('\r\n');

test('groups consecutive unticked [P] tasks under one heading', () => {
  const ids = (markdown, max) => nextTaskGroup(markdown, max).map((task) => task.id);
  assert.deepEqual(ids(PARALLEL_TASKS, 3), ['T002', 'T003', 'T004']);
  assert.deepEqual(ids(PARALLEL_TASKS, 10), ['T002', 'T003', 'T004', 'T005']);
  assert.deepEqual(ids(PARALLEL_TASKS, 1), ['T002']);
  assert.deepEqual(ids(PARALLEL_TASKS.replace('- [ ] T003', '- [x] T003'), 3), ['T002', 'T004', 'T005'], 'a landed sibling is skipped');
  assert.deepEqual(ids(PARALLEL_TASKS.replace('- [ ] T002 [P]', '- [ ] T002'), 3), ['T002'], 'a sequential task runs alone');
  assert.deepEqual(ids(PARALLEL_TASKS.replace(/- \[ \] T00[2-6]/g, (line) => line.replace('[ ]', '[x]')), 3), ['T007']);
  assert.deepEqual(ids(PARALLEL_TASKS.replace(/- \[ \] T00[2-7]/g, (line) => line.replace('[ ]', '[x]')), 3), ['T008'], 'a group never crosses a heading');
  assert.deepEqual(ids('- [ ] T001 Not [P] in the text\n- [ ] T002 [P] x\n', 3), ['T001'], 'only leading tags count');
  assert.deepEqual(ids('- [x] T001 done\n', 3), []);
});

test('plans the task order of a phase: single tasks, and [P] tasks that run in parallel', () => {
  const tasks = [
    '## Phase 1: Setup', '- [x] T001 a', '- [ ] T002 b',
    '## Phase 2: Story', '### Tests', '- [ ] T003 [P] c', '- [ ] T004 [P] d', '### Implementation', '- [ ] T005 [P] e', '- [ ] T006 f', '- [ ] T007 [P] g', '- [ ] T008 [P] h',
    '## Phase 3: Polish', '- [ ] T009 i', '',
  ].join('\r\n');
  const tick = (markdown, ...ids) => ids.reduce((text, id) => text.replace(`- [ ] ${id}`, `- [x] ${id}`), markdown);
  assert.deepEqual(planTasks(tasks), { order: [['T002']], phaseEnd: true, lastIsParallel: false, phase: 'Phase 1: Setup' }, 'a phase is planned on its own');
  assert.deepEqual(planTasks(tick(tasks, 'T002')), { order: [['T003', 'T004'], ['T005'], ['T006'], ['T007', 'T008']], phaseEnd: true, lastIsParallel: true, phase: 'Phase 2: Story' }, 'a ### heading ends a group, not the phase');
  assert.deepEqual(planTasks(tick(tasks, 'T002', 'T004')), { order: [['T003'], ['T005'], ['T006'], ['T007', 'T008']], phaseEnd: true, lastIsParallel: true, phase: 'Phase 2: Story' }, 'a landed sibling is skipped');
  assert.deepEqual(planTasks(tick(tasks, 'T002'), { maxEntries: 2 }), { order: [['T003', 'T004'], ['T005']], phaseEnd: false, lastIsParallel: false, phase: 'Phase 2: Story' });
  assert.deepEqual(planTasks(tick(tasks, 'T002', 'T003', 'T004', 'T005', 'T006', 'T007', 'T008')), { order: [['T009']], phaseEnd: true, lastIsParallel: false, phase: 'Phase 3: Polish' });
  assert.deepEqual(planTasks(tick(tasks, 'T002', 'T003', 'T004', 'T005', 'T007', 'T008')), { order: [['T006']], phaseEnd: true, lastIsParallel: false, phase: 'Phase 2: Story' }, 'an unchecked task between checked ones');
  assert.deepEqual(planTasks('- [ ] T001 a\n- [ ] T002 [P] b\n- [ ] T003 [P] c\n'), { order: [['T001'], ['T002', 'T003']], phaseEnd: true, lastIsParallel: true, phase: null }, 'no headings: one phase');
  assert.deepEqual(planTasks('- [x] T001 a\n'), { order: [], phaseEnd: false, lastIsParallel: false, phase: null });
  assert.deepEqual([parseMaxActiveSpecs(undefined), parseMaxActiveSpecs('5'), parseMaxActiveSpecs('0'), parseMaxActiveSpecs('x')], [3, 5, 3, 3]);
});

test('describes the [P] groups of every phase as the phase workflow forms them', () => {
  const tasks = [
    '# Tasks', '',
    '## Phase 1: Setup', '- [x] T001 a', '- [ ] T002 b',
    '## Phase 2: Story',
    '### Providers', '- [ ] T003 [P] c', '- [ ] T004 [P] d',
    '### Registration', '- [ ] T005 [P] e', '- [ ] T006 [P] f',
    '- [ ] T007 g', '',
    '## Phase 3: Polish', '- [x] T008 h', '',
  ].join('\r\n');
  assert.deepEqual(describeParallelGroups(tasks), {
    phases: [
      { phase: 'Phase 1: Setup', done: 1, open: 1, order: [{ parallel: false, heading: 'Phase 1: Setup', tasks: ['T002'] }] },
      {
        phase: 'Phase 2: Story',
        done: 0,
        open: 5,
        order: [
          { parallel: true, heading: 'Providers', tasks: ['T003', 'T004'] },
          { parallel: true, heading: 'Registration', tasks: ['T005', 'T006'] },
          { parallel: false, heading: 'Registration', tasks: ['T007'] },
        ],
      },
      { phase: 'Phase 3: Polish', done: 1, open: 0, order: [] },
    ],
    warnings: [],
  }, 'a ### heading separates two groups that follow each other');

  const merged = describeParallelGroups(['## Phase 1', '### Work', '- [ ] T001 [P] a', '### Work', '- [ ] T002 [P] b', '##### Too deep', '- [ ] T003 [P] c', ''].join('\n'));
  assert.deepEqual(merged.phases[0].order.map((entry) => entry.tasks), [['T001', 'T002', 'T003']]);
  assert.deepEqual(merged.warnings, [
    'line 6: `##### Too deep` is deeper than `####`, so it does not end a group',
    'T001, T002, T003 form one group because the heading `### Work` repeats (lines 2, 4); give each group its own heading text',
  ]);
  assert.deepEqual(describeParallelGroups('- [ ] T001 [P] a\n- [ ] T002 [P] b\n'), { phases: [{ phase: null, done: 0, open: 2, order: [{ parallel: true, heading: null, tasks: ['T001', 'T002'] }] }], warnings: [] }, 'no headings: one phase');
  assert.deepEqual(describeParallelGroups(''), { phases: [], warnings: [] });
});
test('ticks a task in tasks.md and syncs the pull request body with it', () => {
  const ticked = tickTask(PARALLEL_TASKS, 'T003');
  assert.match(ticked, /- \[x\] T003 \[P\] \[US1\] B\r\n/);
  assert.equal(ticked.replace('- [x] T003', '- [ ] T003'), PARALLEL_TASKS);
  assert.equal(tickTask(PARALLEL_TASKS, 'T001'), null);
  assert.equal(tickTask(PARALLEL_TASKS, 'T999'), null);
  const body = '## Tasks\n\n- [ ] T001 Setup\n- [ ] T002 A\n- [ ] T003 B\n';
  assert.equal(syncPullRequestTicks(body, ticked), '## Tasks\n\n- [x] T001 Setup\n- [ ] T002 A\n- [x] T003 B\n');
});

test('decides parallel [P] tasks and their limit', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const decide = (fields) => decideNext({ tasksMarkdown: PARALLEL_TASKS, windowStart, ...fields });
  const group = [{ task: 'T002', attempt: 1 }, { task: 'T003', attempt: 1 }, { task: 'T004', attempt: 1 }, { task: 'T005', attempt: 1 }];
  assert.deepEqual(decide({}), { ...order(group, [{ task: 'T006', attempt: 1 }], [{ task: 'T007', attempt: 1 }]) });
  const afterLand = PARALLEL_TASKS.replace('- [ ] T002', '- [x] T002');
  assert.deepEqual(decide({ tasksMarkdown: afterLand, attempts: [attempt('T002', 'success'), attempt('T003')] }).order[0], [{ task: 'T003', attempt: 2 }, { task: 'T004', attempt: 1 }, { task: 'T005', attempt: 1 }], 'landed siblings leave the group');
  assert.deepEqual(decide({ attempts: [attempt('T003'), attempt('T003'), attempt('T003')] }), { action: 'limit', kind: 'task', task: 'T003', attempts: 3, stale: [] }, 'a member at its limit stops the group');
});

test('decides convergence, conflict resolution, and merging after the last task', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const now = Date.parse('2026-10-06T20:00:00Z');
  const at = (hour) => `2026-10-06T${String(hour).padStart(2, '0')}:00:00Z`;
  const kind = (name, hour, outcome = 'success') => attempt(null, outcome, at(hour), name);
  const check = (id, external_id, fields = {}) => ({ id, external_id, status: 'completed', conclusion: 'neutral', started_at: at(12), ...fields });
  const done = '- [x] T001 a\n';
  const decide = (fields) => decideNext({ tasksMarkdown: done, windowStart, now, ...fields });
  const taskDone = attempt('T001', 'success', at(11));
  const single = (name, number) => ({ action: 'tasks', kind: name, order: [[{ task: null, attempt: number }]], phaseEnd: false, lastIsParallel: false, stale: [] });

  assert.deepEqual(decide({ attempts: [taskDone] }), single('converge', 1));
  assert.deepEqual(decide({ attempts: [kind('converge', 10), taskDone] }), single('converge', 1), 'a convergence older than the last task does not count');
  assert.deepEqual(decide({ attempts: [taskDone, kind('converge', 12, 'failure'), kind('converge', 13, 'failure'), kind('converge', 14, 'failure')] }), { action: 'limit', kind: 'converge', attempts: 3, stale: [] });

  const converged = [taskDone, kind('converge', 12)];
  assert.deepEqual(decide({ attempts: converged }), { action: 'merge', kind: 'merge', attempt: 1, stale: [] });
  const merges = [check(1, CHECK_MERGE, { conclusion: 'failure' }), check(2, CHECK_MERGE, { conclusion: 'neutral' })];
  assert.deepEqual(decide({ attempts: converged, checks: merges }), { action: 'merge', kind: 'merge', attempt: 3, stale: [] });
  const failedMerges = [1, 2, 3].map((id) => check(id, CHECK_MERGE, { conclusion: 'failure' }));
  assert.deepEqual(decide({ attempts: converged, checks: failedMerges }), { action: 'limit', kind: 'merge', attempts: 3, stale: [] });
  assert.deepEqual(decide({ attempts: converged, checks: failedMerges, windowStart: at(13) }), { action: 'merge', kind: 'merge', attempt: 1, stale: [] }, 'a person pushing starts a new window');

  assert.deepEqual(decide({ attempts: converged, checks: [check(1, CHECK_MERGE, { status: 'in_progress', started_at: at(19) })] }), { action: 'wait' });
  const stale = check(1, CHECK_MERGE, { status: 'in_progress', started_at: at(15) });
  assert.deepEqual(decide({ attempts: converged, checks: [stale] }), { action: 'merge', kind: 'merge', attempt: 2, stale: [stale] });

  const conflict = check(5, CHECK_CONFLICT);
  assert.deepEqual(decide({ attempts: converged, checks: [check(4, CHECK_MERGE), conflict] }), single('resolve', 1));
  assert.deepEqual(decide({ attempts: [...converged, kind('resolve', 13, 'failure')], checks: [conflict] }), single('resolve', 2), 'a failed resolve keeps the conflict state');
  assert.equal(decide({ attempts: [...converged, kind('resolve', 13)], checks: [conflict] }).action, 'merge', 'successful no-op sync resolves an old conflict on the same head');
  assert.deepEqual(decide({ attempts: converged, checks: [conflict, check(7, CHECK_MERGE)] }), { action: 'merge', kind: 'merge', attempt: 2, stale: [] });
  const earlierResolves = [kind('resolve', 11, 'failure'), kind('resolve', 11, 'failure'), kind('resolve', 11)];
  assert.deepEqual(decide({ attempts: [...converged, ...earlierResolves], checks: [conflict] }), single('resolve', 1), 'only attempts at the current conflict count');
  const limited = check(8, CHECK_LIMIT, { conclusion: 'failure' });
  assert.deepEqual(decide({ attempts: converged, checks: [limited], resumedAt: at(13), windowStart: at(13) }), { action: 'merge', kind: 'merge', attempt: 1, stale: [] });
});

test('resolves conflicts with the base branch before a phase, best effort', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const now = Date.parse('2026-10-06T20:00:00Z');
  const at = (hour) => `2026-10-06T${String(hour).padStart(2, '0')}:00:00Z`;
  const resolve = (hour, outcome) => attempt(null, outcome, at(hour), 'resolve');
  const open = '- [x] T001 a\n- [ ] T002 b\n';
  const syncConflict = { id: 1, external_id: CHECK_SYNC_CONFLICT, status: 'completed', conclusion: 'neutral', started_at: at(12) };
  const decide = (fields) => decideNext({ tasksMarkdown: open, windowStart, now, checks: [syncConflict], ...fields });
  const resolveNext = (number) => ({ action: 'tasks', kind: 'resolve', sync: true, order: [[{ task: null, attempt: number }]], phaseEnd: false, lastIsParallel: false, stale: [] });

  assert.deepEqual(decide({}), resolveNext(1), 'the conflict is resolved before the phase');
  assert.deepEqual(decide({ attempts: [resolve(11, 'failure'), resolve(13, 'failure')] }), resolveNext(2), 'only attempts at this conflict count');
  const phase = decideNext({ tasksMarkdown: open, windowStart, now });
  assert.deepEqual(decide({ attempts: [resolve(13, 'success')] }), phase, 'successful no-op sync does not revive a best-effort conflict on the same head');
  assert.deepEqual(decide({ attempts: [resolve(13, 'failure'), resolve(14, 'failure')] }), { ...phase, syncGivenUp: syncConflict }, `after ${MAX_SYNC_RESOLVE_ATTEMPTS} failed attempts the phase continues on the current base`);
  assert.deepEqual(decide({ attempts: [resolve(13, 'attention')] }), { ...phase, syncGivenUp: syncConflict }, 'conflicts that need a person are left to the merge');
  const converging = decideNext({ tasksMarkdown: '- [x] T001 a\n', windowStart, now, checks: [syncConflict], attempts: [attempt('T001', 'success', at(11))] });
  assert.deepEqual(converging, resolveNext(1), 'a conflict before the convergence is resolved first, too');
  const convergeGivenUp = decideNext({ tasksMarkdown: '- [x] T001 a\n', windowStart, now, checks: [syncConflict], attempts: [attempt('T001', 'success', at(11)), resolve(13, 'attention')] });
  assert.deepEqual([convergeGivenUp.kind, convergeGivenUp.syncGivenUp], ['converge', syncConflict], 'and the convergence continues on its base when it cannot be resolved');
  const converged = [attempt('T001', 'success', at(11)), attempt(null, 'success', at(12), 'converge')];
  const mergeGivenUp = decideNext({ tasksMarkdown: '- [x] T001 a\n', windowStart, now, checks: [syncConflict], attempts: [...converged, resolve(13, 'failure'), resolve(14, 'failure')] });
  assert.deepEqual([mergeGivenUp.action, mergeGivenUp.syncGivenUp], ['merge', syncConflict]);
  assert.equal(integrationTarget({ action: 'repair', syncGivenUp: syncConflict }), null);
  assert.equal(integrationTarget({ action: 'repair' }), null);
});
test('environment specs: only the environment actions, and only without other changes on the branch', () => {
  assert.equal(isEnvironmentPath('.github/actions/environment-setup/action.yml'), true);
  assert.equal(isEnvironmentPath('.github/actions/other/action.yml'), false);
  assert.equal(isProtectedPath('.github/actions/environment-verify/action.yml'), false);
  assert.equal(isProtectedPath('.github/workflows/speckit-implement.yml'), true);
  assert.equal(isProtectedPath('.github/actions/other/action.yml'), true);

  const before = '- [ ] T001 One\n';
  const after = '- [x] T001 One\n';
  const task = (changedPaths) => validateTaskChange({ folder: 'f', taskId: 'T001', before, after, changedPaths });
  assert.deepEqual(task(['specs/f/tasks.md', '.github/actions/environment-setup/action.yml']), []);
  assert.match(task(['specs/f/tasks.md', '.github/actions/other/action.yml']).join(), /changed protected paths/);

  const exclusive = (branchPaths) => environmentExclusivityReasons({ folder: 'f', branchPaths });
  assert.deepEqual(exclusive(['specs/f/tasks.md', '.github/actions/environment-verify/action.yml']), []);
  assert.deepEqual(exclusive(['src/a.cs', 'specs/f/tasks.md']), []);
  assert.match(exclusive(['.github/actions/environment-setup/action.yml', 'src/a.cs', 'specs/f/tasks.md']).join(), /standalone environment spec, but the branch also changes src\/a\.cs$/);
});

test('accepts only an appended convergence phase with new unchecked tasks', () => {
  const folder = 'f';
  const before = '# Tasks\n\n## Phase 1: Setup\n\n- [x] T001 One\n- [x] T002 Two\n';
  const phase = (n, tasks) => `\n## Phase ${n}: Convergence\n\n${tasks}`;
  const check = (after, changedPaths = ['specs/f/tasks.md'], base = before) => validateConvergeChange({ folder, before: base, after, changedPaths });

  const ok = check(`${before}${phase(2, '- [ ] T003 Add the missing guard (FR-002)\n- [ ] T004 Cover SC-001\n')}`);
  assert.deepEqual(ok.reasons, []);
  assert.deepEqual(ok.appended.map((task) => task.id), ['T003', 'T004']);
  assert.equal(ok.limit, false);
  assert.deepEqual(check(before, []), { reasons: [], appended: [], limit: false });

  assert.match(check(`${before}${phase(2, '- [ ] T003 x\n')}`, ['specs/f/tasks.md', 'src/a.cs']).reasons.join(), /may only change `specs\/f\/tasks\.md`, but it changed src\/a\.cs/);
  assert.match(check(before.replace('Two', 'Changed') + phase(2, '- [ ] T003 x\n')).reasons.join(), /may only append/);
  assert.match(check(`${before}${phase(2, '- [ ] T002 x\n')}`).reasons.join(), /T002 does not have a new ID/);
  assert.match(check(`${before}${phase(2, '- [x] T003 x\n')}`).reasons.join(), /T003 is already checked/);
  assert.match(check(`${before}\n## Phase 2: Polish\n\n- [ ] T003 x\n`).reasons.join(), /exactly one `## Phase N: Convergence` section/);
  assert.match(check(`${before}${phase(2, 'no tasks\n')}`).reasons.join(), /contains no tasks/);

  const three = `${before}${phase(2, '- [x] T003 a\n')}${phase(3, '- [x] T004 b\n')}${phase(4, '- [x] T005 c\n')}`;
  assert.equal(convergenceRounds(three), 3);
  const limited = check(`${three}${phase(5, '- [ ] T006 d\n')}`, undefined, three);
  assert.equal(limited.limit, true);
  assert.match(limited.reasons.join(), /still found gaps after 3 rounds/);
});

test('detects conflict markers and appends convergence tasks to the pull request body', () => {
  assert.equal(hasConflictMarkers('a\n<<<<<<< HEAD\nb\n=======\nc\n>>>>>>> main\n'), true);
  assert.equal(hasConflictMarkers('a\n=======\n'), true);
  assert.equal(hasConflictMarkers('# Title\n\n======= not a marker\n<<<<<<<<\n'), false);
  const body = '## Tasks (2)\n\n- [x] T001 a\n- [x] T002 b\n';
  assert.equal(
    appendPullRequestTasks(body, [{ id: 'T003', text: 'c' }], 'Phase 2: Convergence'),
    '## Tasks (3)\n\n- [x] T001 a\n- [x] T002 b\n\n### Phase 2: Convergence\n\n- [ ] T003 c\n',
  );
});

test('accepts an untouched tasks.md or only the target task tick, and no protected paths', () => {
  const folder = 'f';
  const before = '# Tasks\n\n- [ ] T001 One\n- [ ] T002 Two\n';
  const ticked = before.replace('- [ ] T001', '- [X] T001');
  const check = (fields) => validateTaskChange({ folder, taskId: 'T001', before, after: ticked, changedPaths: ['src/a.cs', 'specs/f/tasks.md'], ...fields });

  assert.deepEqual(check({}), []);
  assert.deepEqual(check({ after: before.replace('- [ ] T001', '- [x] T001') }), []);
  assert.deepEqual(check({ after: before, changedPaths: ['src/a.cs'] }), [], 'the land job checks the task');
  assert.match(check({ after: before.replace('- [ ] T002', '- [x] T002') }).join(), /may only change by checking T001/);
  assert.match(check({ after: ticked.replace('One', 'Uno') }).join(), /may only change by checking T001/);
  assert.match(check({ after: `${ticked}\n- [ ] T003 New\n` }).join(), /may only change by checking T001/);
  assert.match(check({ changedPaths: ['.github/workflows/x.yml', 'specs/f/spec.md', '.specify/memory/constitution.md', 'specs/f/tasks.md'] }).join(),
    /changed protected paths: \.github\/workflows\/x\.yml, specs\/f\/spec\.md, \.specify\/memory\/constitution\.md/);
});

test('ticks a task in the pull request body', () => {
  const body = '## Tasks\n\n- [ ] T001 One\n- [ ] T0010 Other\n';
  assert.equal(tickPullRequestBody(body, 'T001'), '## Tasks\n\n- [x] T001 One\n- [ ] T0010 Other\n');
  assert.equal(tickPullRequestBody(body, 'T009'), body);
});

test('accepts only workflow-authored markers and neutralizes markers in untrusted text', () => {
  const comments = [
    { user: { login: 'github-actions[bot]' }, body: `${LEGACY_RESUME_COMMENT_MARKER}\nresumed`, created_at: 'a' },
    { user: { login: 'github-actions[bot]' }, body: `failed: agent wrote ${LEGACY_RESUME_COMMENT_MARKER}`, created_at: 'b' },
    { user: { login: 'mallory' }, body: LEGACY_RESUME_COMMENT_MARKER, created_at: 'c' },
  ];
  assert.deepEqual(markerTimes(comments, LEGACY_RESUME_COMMENT_MARKER), ['a']);
  assert.equal(neutralizeMarkers(`x ${LEGACY_RESUME_COMMENT_MARKER} y`), 'x &lt;!-- speckit-implement:resume --> y');
});

test('keeps durable repair state until the matching repair is resolved', () => {
  const repair = { id: 'group:1:abc', state: 'pending', scope: 'group', tasks: ['T001', 'T002'], at: '2026-01-01T00:00:00Z' };
  const pending = { id: 1, user: { login: 'github-actions[bot]' }, body: renderRepairMarker(repair), created_at: repair.at };
  assert.deepEqual(unresolvedRepair([pending]), { ...repair, commentId: 1 });
  assert.equal(unresolvedRepair([
    pending,
    { id: 2, user: { login: 'mallory' }, body: renderRepairMarker({ id: repair.id, state: 'resolved' }), created_at: '2026-01-01T00:01:00Z' },
  ]).id, repair.id, 'people cannot forge repair resolution markers');
  assert.equal(unresolvedRepair([
    pending,
    { id: 3, user: { login: 'github-actions[bot]' }, body: renderRepairMarker({ id: repair.id, state: 'resolved', at: '2026-01-01T00:02:00Z' }), created_at: '2026-01-01T00:02:00Z' },
  ]), null);
});

test('the implementation workflows give task, group, repair, convergence, conflict resolution, and merge dedicated entry points', () => {
  const read = (name) => readFileSync(new URL(`../workflows/${name}`, import.meta.url), 'utf8').replace(/\r\n/g, '\n');
  const job = (workflow, name) => workflow.match(new RegExp(`^ {2}${name}:\\n[\\s\\S]*?(?=^ {2}\\S|^ {2}#|(?![\\s\\S]))`, 'm'))?.[0] ?? '';
  const spec = read('speckit-implement-spec.yml');
  const phase = read('speckit-implement-phase.yml');
  const group = read('speckit-implement-group.yml');
  const task = read('speckit-implement-task.yml');
  const repair = read('speckit-implement-repair.yml');
  const converge = read('speckit-implement-converge.yml');
  const resolve = read('speckit-implement-resolve.yml');
  const merge = read('speckit-implement-merge.yml');
  assert.match(spec, /^name: Spec Kit implement spec\n/);
  assert.match(phase, /^name: Spec Kit implement phase\n/);
  assert.match(group, /^name: Spec Kit implement group\n/);
  assert.match(task, /^name: Spec Kit implement task\n/);
  assert.match(repair, /^name: Spec Kit implement repair\n/);
  assert.match(converge, /^name: Spec Kit implement convergence\n/);
  assert.match(resolve, /^name: Spec Kit implement conflict resolution\n/);
  assert.match(merge, /^name: Spec Kit implement merge\n/);
  assert.match(spec, /^run-name: "Spec Kit implement spec #\$\{\{ inputs\.twin \}\}\$\{\{ inputs\.title && format\(' · \{0\}', inputs\.title\) \|\| '' \}\}"$/m);
  assert.match(job(spec, 'phase'), /name: \$\{\{ needs\.decide\.outputs\.action == 'implement' && needs\.decide\.outputs\.job_name \|\| 'Implement a phase' \}\}[\s\S]*uses: \.\/\.github\/workflows\/speckit-implement-phase\.yml/);
  assert.match(job(spec, 'repair'), /needs\.decide\.outputs\.action == 'repair'[\s\S]*uses: \.\/\.github\/workflows\/speckit-implement-repair\.yml/);
  assert.match(job(spec, 'converge'), /name: \$\{\{ needs\.decide\.outputs\.action == 'converge' && needs\.decide\.outputs\.job_name \|\| 'Convergence' \}\}[\s\S]*uses: \.\/\.github\/workflows\/speckit-implement-converge\.yml/);
  assert.match(job(spec, 'resolve'), /name: \$\{\{ needs\.decide\.outputs\.action == 'resolve' && needs\.decide\.outputs\.job_name \|\| 'Conflict resolution' \}\}[\s\S]*uses: \.\/\.github\/workflows\/speckit-implement-resolve\.yml/);
  assert.match(job(spec, 'merge'), /name: Merge implementation[\s\S]*uses: \.\/\.github\/workflows\/speckit-implement-merge\.yml[\s\S]*head: \$\{\{ needs\.decide\.outputs\.head \}\}[\s\S]*check_run: \$\{\{ needs\.decide\.outputs\.check_run \}\}/);
  assert.doesNotMatch(task, /inputs\.kind|SPECKIT_KIND/, 'the task workflow has no operation discriminator');
  assert.match(converge, /speckit-converge\.mjs" package[\s\S]*speckit-converge\.mjs land-tasks/);
  assert.match(resolve, /speckit-resolve\.mjs" integrate[\s\S]*speckit-resolve\.mjs" package[\s\S]*speckit-resolve\.mjs" verdict[\s\S]*speckit-resolve\.mjs land-tasks/);
  assert.match(job(resolve, 'work'), /SPECKIT_GUIDANCE: \$\{\{ matrix\.guidance \}\}/);
  assert.equal([...job(repair, 'work').matchAll(/^ {6}SPECKIT_REPAIR_SCOPE:/gm)].length, 1, 'repair work declares its scope once');
  // Pull model: every run first merges the base branch in its own job; decide follows, so a conflict is resolved in
  // the same run, and the work after decide also runs when that merge failed.
  assert.match(job(spec, 'sync'), /name: Merge the base branch\n\s+if: github\.ref == format\('refs\/heads\/\{0\}', github\.event\.repository\.default_branch\)[\s\S]*run: node \.github\/scripts\/speckit-spec\.mjs sync/);
  assert.doesNotMatch(spec, /inputs\.resume|SPECKIT_RESUME/);
  assert.match(job(spec, 'decide'), /needs: sync\n[\s\S]*if: \$\{\{ !cancelled\(\) && github\.ref == /);
  for (const name of ['phase', 'repair', 'converge', 'resolve', 'merge']) assert.match(job(spec, name), /if: \$\{\{ !cancelled\(\) && needs\.decide\.result == 'success' && /, name);
  // GitHub shows the name of a skipped job that is not a reusable workflow call unevaluated, so such jobs have fixed names.
  for (const [label, workflow] of [['spec', spec], ['phase', phase], ['group', group], ['task', task], ['repair', repair]]) {
    for (const [, name, body] of workflow.matchAll(/^ {2}([\w-]+):\n((?: {4}[^\n]*\n|\n)+)/gm)) {
      if (/^ {4}name: \$\{\{/m.test(body) && /^ {4}if: (?!always\(\))/m.test(body) && !/^ {4}uses: /m.test(body)) assert.fail(`${label}: job ${name} can be skipped, so its name must not be an expression`);
    }
  }

  for (const [label, workflow] of [['spec', spec], ['phase', phase], ['group', group], ['task', task], ['repair', repair]]) {
    assert.doesNotMatch(workflow, /name: [^\n]*['"]?[123] · /, `${label}: job names say what the job does, without step numbers`);
  }
  // The merge of the base branch, one decision, exactly one of the alternatives, and the next run.
  assert.deepEqual([...spec.matchAll(/^ {2}([\w-]+):\n {4}name: /gm)].map((match) => match[1]),
    ['refuse', 'sync', 'decide', 'phase', 'repair', 'converge', 'resolve', 'merge', 'continue']);
  assert.match(spec, /continue:[\s\S]*?needs: \[sync, decide, phase, repair, converge, resolve, merge\][\s\S]*SPECKIT_PHASE_CHECK: \$\{\{ needs\.phase\.outputs\.verified \}\}/);
  const groups = job(phase, 'groups');
  assert.match(groups, /name: \$\{\{ matrix\.name \}\}/);
  assert.match(groups, /max-parallel: 1\n\s+fail-fast: true\n\s+matrix: \$\{\{ fromJSON\(needs\.plan\.outputs\.task_matrix\) \}\}/);
  assert.match(groups, /uses: \.\/\.github\/workflows\/speckit-implement-group\.yml/);
  const groupTasks = job(group, 'tasks');
  assert.match(groupTasks, /matrix: \$\{\{ fromJSON\(inputs\.order\)\[inputs\.index\] \}\}[\s\S]*uses: \.\/\.github\/workflows\/speckit-implement-task\.yml/);
  assert.doesNotMatch(groupTasks, /max-parallel/, 'all [P] tasks of a group run at once');
  assert.doesNotMatch(job(task, 'work'), /strategy:|matrix\./, 'a task workflow implements exactly one task');
  assert.match(job(task, 'work'), /SPECKIT_TASK: \$\{\{ inputs\.task \}\}/);
  assert.match(phase, /verified:[\s\S]*?value: \$\{\{ jobs\.verify\.result \}\}/);
  // The pull request's base branch flows from decide to every job that merges or compares with git; the default
  // branch only selects the trusted tooling.
  assert.match(job(spec, 'decide'), /base_branch: \$\{\{ steps\.decide\.outputs\.base_branch \}\}/);
  for (const name of ['phase', 'repair', 'converge', 'resolve', 'merge']) assert.match(job(spec, name), /base: \$\{\{ needs\.decide\.outputs\.base_branch \}\}/, name);
  // The spec is archived on the implementation branch before the verification, which verifies and merges that head.
  const archive = job(merge, 'archive');
  assert.match(archive, /SPECKIT_BASE_BRANCH: \$\{\{ inputs\.base \}\}[\s\S]*run: node tooling\/\.github\/scripts\/speckit-merge\.mjs archive/);
  assert.doesNotMatch(archive, /environment-setup|environment-verify/, 'the archive job never runs agent-written code');
  assert.match(job(merge, 'verify'), /needs: archive[\s\S]*ref: \$\{\{ needs\.archive\.outputs\.head \}\}/);
  assert.match(job(merge, 'land'), /SPECKIT_CHECK_RUN: \$\{\{ needs\.archive\.outputs\.check_run \|\| inputs\.check_run \}\}/);
  assert.match(groups, /base: \$\{\{ inputs\.base \}\}/);
  for (const workflow of [phase, group, task, repair, converge, resolve, merge]) assert.match(workflow, /^ {6}base:\n {8}description: [^\n]+\n {8}required: true/m);
  for (const name of ['work', 'land']) assert.match(job(task, name), /SPECKIT_BASE_BRANCH: \$\{\{ inputs\.base \}\}/, name);
  for (const [label, workflow] of [['spec', spec], ['phase', phase], ['group', group], ['task', task], ['repair', repair], ['merge', merge]]) {
    assert.doesNotMatch(workflow, /SPECKIT_BRANCH:|\bmain\b/, `${label}: no SPECKIT_BRANCH and no assumed main`);
  }
  // Every verification runs once; its failure decides (a re-run of unchanged code only delayed real failures).
  const phaseVerify = job(phase, 'verify');
  assert.match(phaseVerify, /if: >-\n\s+!cancelled\(\) && needs\.plan\.outputs\.phase_check == 'true' && needs\.groups\.result == 'success'/);
  assert.match(phaseVerify, /- name: Verify the phase\n\s+if: [^\n]+\n\s+uses: \.\/tooling\/\.github\/actions\/environment-verify/, 'a failed phase verification fails the job');
  assert.match(phaseVerify, /BASE_BRANCH: \$\{\{ inputs\.base \}\}[\s\S]*git merge-base HEAD "origin\/\$BASE_BRANCH"/, 'the phase verification covers all changes of the branch');
  assert.match(job(group, 'verify'), /needs\.prepare\.outputs\.parallel == 'true'[\s\S]*- name: Verify the combined group[\s\S]*uses: \.\/tooling\/\.github\/actions\/environment-verify/);
  // Parallel tasks are verified together only when a landing combined a sibling's edits, with the affected tests.
  const groupVerify = job(group, 'verify');
  assert.match(groupVerify, /- name: Find combined edits\n\s+id: combined\n[\s\S]*run: node tooling\/\.github\/scripts\/speckit-group\.mjs combined-edits/);
  assert.match(groupVerify, /combined: \$\{\{ steps\.combined\.outputs\.combined \}\}/);
  for (const step of ['Set up the solution environment', 'Verify the combined group']) {
    assert.match(groupVerify, new RegExp(`- name: ${step}\\n\\s+if: steps\\.combined\\.outputs\\.combined == 'true' && `), step);
  }
  assert.match(groupVerify, /- name: Verify the combined group[\s\S]*mode: task\n/);
  assert.doesNotMatch(groupVerify, /mode: phase/, 'the phase verification is the full safety net, not the group');
  assert.match(job(group, 'report'), /SPECKIT_COMBINED: \$\{\{ needs\.verify\.outputs\.combined \|\| 'true' \}\}/);
  for (const [workflow, name] of [[task, 'work'], [merge, 'verify']]) {
    const text = job(workflow, name);
    assert.doesNotMatch(text, /verify-again|again\n/, name);
    assert.match(text, /SPECKIT_VERIFY_OUTCOME: \$\{\{ steps\.verify\.outcome \}\}/, name);
  }
  for (const workflow of [phase, group, task, repair, converge, resolve, merge, spec]) assert.doesNotMatch(workflow, /Verify [^\n]* again|SPECKIT_VERIFY_RETRIED/);
  // A called workflow cannot hold more permissions than the job that calls it.
  const permissions = (text) => Object.fromEntries([...(text.match(/permissions:\n((?: {6}[\w-]+: \w+\n)+)/)?.[1] ?? '').matchAll(/([\w-]+): (\w+)/g)].map((match) => [match[1], match[2]]));
  const rank = { none: 0, read: 1, write: 2 };
  const covers = (caller, called, label) => {
    for (const [scope, level] of Object.entries(called)) assert.ok(rank[caller[scope] ?? 'none'] >= rank[level], `${label}: ${scope}: ${level}`);
  };
  for (const name of ['prepare', 'tasks', 'verify', 'report']) covers(permissions(job(phase, 'groups')), permissions(job(group, name)), `phase groups covers group ${name}`);
  for (const name of ['work', 'land']) covers(permissions(job(group, 'tasks')), permissions(job(task, name)), `group tasks covers task ${name}`);
  for (const name of ['work', 'land', 'verify', 'report']) covers(permissions(job(spec, 'repair')), permissions(job(repair, name)), `spec repair covers repair ${name}`);
  for (const name of ['work', 'land']) covers(permissions(job(spec, 'converge')), permissions(job(converge, name)), `spec converge covers convergence ${name}`);
  for (const name of ['work', 'land']) covers(permissions(job(spec, 'resolve')), permissions(job(resolve, name)), `spec resolve covers resolution ${name}`);
  for (const name of ['archive', 'verify', 'land']) covers(permissions(job(spec, 'merge')), permissions(job(merge, name)), `spec merge covers merge ${name}`);
  for (const name of ['plan', 'groups', 'verify']) covers(permissions(job(spec, 'phase')), permissions(job(phase, name)), `spec phase covers phase ${name}`);
  assert.equal(MAX_MATRIX_JOBS_PER_RUN, 256, 'GitHub runs at most 256 matrix jobs per run');
  // GitHub does not evaluate the name of a skipped job in a called workflow and shows the raw expression, so a job that
  // can be skipped there has a static name. Only `groups` keeps its matrix title: it is skipped only when the plan
  // found nothing to do, and a matrix job cannot be named otherwise.
  for (const [label, workflow] of [['phase', phase], ['group', group], ['task', task], ['repair', repair], ['converge', converge], ['resolve', resolve], ['merge', merge]]) {
    for (const [, name, body] of workflow.matchAll(/^ {2}([\w-]+):\n([\s\S]*?)(?=^ {2}\S|^ {2}#|(?![\s\S]))/gm)) {
      const condition = body.match(/^ {4}if: (?:>-\n\s+)?(.*)$/m)?.[1] ?? null;
      const dynamic = /^ {4}name: .*\$\{\{/m.test(body);
      if (condition && condition.trim() !== 'always()' && name !== 'groups') assert.equal(dynamic, false, `${label} ${name}: a job that can be skipped needs a static name`);
    }
  }
});

test('titles every implementation run and job with its workflow, the twin, and its subject', () => {
  assert.equal(specTitleOfPull({ title: 'Implement: Recording Lineage and Upload' }), 'Recording Lineage and Upload');
  assert.equal(renderImplementRunName(35, 'Recording Lineage and Upload'), 'Spec Kit implement spec #35 · Recording Lineage and Upload');
  const heading = 'Phase 3: User Story 1 - Upload a Source Recording for a Match (Priority: P1) 🎯 MVP';
  assert.equal(planTasks(`## ${heading}\n- [ ] T013 a\n`).phase, heading, 'the phase heading as written, without "## "');
  assert.equal(renderImplementTitle('phase', 35, phaseSubject(heading)), `Spec Kit implement phase #35 · ${heading}`);
  assert.equal(phaseSubject(null), 'Tasks', 'a tasks.md without phase headings');
  assert.equal(renderImplementTitle('task', 35, taskSubject('task', [{ task: 'T013', summary: '[P] [US1] Create the upload session state …' }])), 'Spec Kit implement task #35 · T013 [P] [US1] Create the upload session state …');
  assert.equal(taskSubject('task', [{ task: 'T013', summary: 'a' }, { task: 'T014', summary: 'b' }]), 'T013, T014 in parallel');
  assert.deepEqual([taskSubject('converge', [{ task: '' }]), taskSubject('resolve', [{ task: '' }])], ['Convergence', 'Conflict resolution']);
  const ids = (...tasks) => tasks.map((task) => ({ task, summary: 'long title' }));
  assert.deepEqual([taskJobName(ids('T013')), taskJobName(ids('T014', 'T015')), taskJobName(ids('T016', 'T017', 'T018', 'T019'))],
    ['T013', 'T014, T015 in parallel', 'T016 … T019 in parallel (4 tasks)'], 'task job names stay short in the run graph');
});

test('the tooling never assumes a branch named main: the default branch comes from GitHub, the base from the pull request', () => {
  const dir = new URL('./', import.meta.url);
  const scripts = readdirSync(dir).filter((file) => /^speckit-.*\.mjs$/.test(file) && !file.endsWith('.test.mjs') && file !== 'speckit-test-helpers.mjs');
  for (const file of scripts) {
    const text = readFileSync(new URL(file, dir), 'utf8');
    assert.doesNotMatch(text, /SPECKIT_BRANCH\b/, `${file}: SPECKIT_BRANCH is split into SPECKIT_DEFAULT_BRANCH and the pull request base`);
    assert.doesNotMatch(text, /\|\| 'main'|'main'\s*[,)]|"main"/, `${file}: no fallback to a branch named main`);
  }
  assert.throws(() => defaultBranchOf({}), /SPECKIT_DEFAULT_BRANCH/);
  assert.equal(defaultBranchOf({ SPECKIT_DEFAULT_BRANCH: 'trunk' }), 'trunk');
  assert.throws(() => baseBranchOf({ number: 9, base: {} }), /#9 has no base branch/);
  assert.equal(baseBranchOf({ base: { ref: 'release/2026' } }), 'release/2026');
  assert.throws(() => baseBranchFromEnv({}), /SPECKIT_BASE_BRANCH/);
  assert.equal(baseBranchFromEnv({ SPECKIT_BASE_BRANCH: 'release/2026' }), 'release/2026');
});

test('finds the spec of an implementation branch in specs/ or, once archived for the merge, in specs/archive/', async () => {
  const files = new Map([['specs/archive/f/tasks.md@sha-arch', '- [x] T001 a\n'], ['specs/f/tasks.md@sha-live', '- [ ] T001 a\n']]);
  const client = { getFileContent: async (file, ref) => files.get(`${file}@${ref}`) ?? null };
  assert.deepEqual(await readSpecTasks(client, 'f', 'sha-live'), { tasksMarkdown: '- [ ] T001 a\n', archived: false });
  assert.deepEqual(await readSpecTasks(client, 'f', 'sha-arch'), { tasksMarkdown: '- [x] T001 a\n', archived: true });
  assert.deepEqual(await readSpecTasks(client, 'f', 'sha-none'), { tasksMarkdown: null, archived: false });
  assert.equal(tasksPathOf('f', { archived: true }), 'specs/archive/f/tasks.md');
  const before = '- [ ] T001 One\n';
  const after = '- [x] T001 One\n';
  assert.deepEqual(validateTaskChange({ folder: 'f', archived: true, taskId: 'T001', before, after, changedPaths: ['src/a.cs', 'specs/archive/f/tasks.md'] }), []);
  assert.match(validateTaskChange({ folder: 'f', archived: true, taskId: 'T001', before, after, changedPaths: ['specs/f/tasks.md'] }).join(), /changed protected paths: specs\/f\/tasks\.md/);
  assert.match(validateAmendment({ folder: 'f', archived: true, changedPaths: ['specs/f/plan.md'], beforeTasks: before, afterTasks: before }).reasons.join(), /may only change files in `specs\/archive\/f\/`/);
  assert.deepEqual(environmentExclusivityReasons({ folder: 'f', branchPaths: ['.github/actions/environment-verify/action.yml', 'specs/archive/f/tasks.md'] }), []);
  assert.match(renderTaskPrompt('T001', 'f', { archived: true }), /of the spec in `specs\/archive\/f\/`/);
  assert.match(renderResolvePrompt({ folder: 'f', files: ['a.cs'], base: 'release', archived: true }), /base branch `release` into the implementation branch of the spec `specs\/archive\/f`/);
});