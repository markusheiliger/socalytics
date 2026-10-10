import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import {
  CHECK_ATTEMPT,
  CHECK_CONFLICT,
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
  MAX_PULL_BODY,
  neutralizeMarkers,
  nextTask,
  nextTaskGroup,
  parseGuidance,
  parseMaxParallel,
  isImplementRunOf,
  isPerTaskRunOf,
  segmentLabel,
  MAX_STAGES_PER_RUN,
  parseAttemptCheckId,
  parseAttempts,
  parseMaxActiveSpecs,
  planStages,
  renderConvergePrompt,
  renderGuidanceMarker,
  renderAmendmentHowTo,
  renderNextSteps,
  renderPullRequestBody,
  renderPullRequestTitle,
  renderResolvePrompt,
  renderStartComment,
  renderAttemptMarker,
  renderImplementRunName,
  renderTaskPrompt,
  TASK_TEST_SCOPE,
  RESUME_COMMENT_MARKER,
  START_COMMENT_MARKER,
  setAmendmentStatus,
  stepLabel,
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
  assert.equal(renderImplementRunName(38), 'Spec Kit implement #38');
  assert.equal(isImplementRunOf('Spec Kit implement #38', 38), true);
  assert.equal(isImplementRunOf('Spec Kit implement #38 T002 attempt 3', 38), true, 'runs of the per-task workflow count');
  assert.equal(isImplementRunOf('Spec Kit implement #381', 38), false);
  assert.equal(isImplementRunOf('Spec Kit diagnose #38', 38), false);
  const comment = (body, login = 'github-actions[bot]', at = '2026-10-06T11:00:00Z') => ({ body, user: { login }, created_at: at });
  const marker = renderAttemptMarker({ step: 'task', task: 'T002', attempt: 2, outcome: 'failure' });
  assert.equal(marker, '<!-- speckit-implement:attempt {"step":"task","task":"T002","attempt":2,"outcome":"failure"} -->');
  assert.deepEqual(parseAttempts([
    comment(`${marker}\n**T002 attempt 2 failed**`),
    comment(renderAttemptMarker({ step: 'converge', attempt: 1, outcome: 'success' }), undefined, '2026-10-06T10:00:00Z'),
    comment(marker, 'mallory'),
    comment('<!-- speckit-implement:attempt {"step":"merge","outcome":"failure"} -->'),
    comment('<!-- speckit-implement:attempt {broken -->'),
  ]), [
    { step: 'converge', task: null, attempt: 1, outcome: 'success', at: '2026-10-06T10:00:00Z' },
    { step: 'task', task: 'T002', attempt: 2, outcome: 'failure', at: '2026-10-06T11:00:00Z' },
  ], 'only workflow-authored, valid markers count, oldest first');
  assert.equal(attemptCheckId({ step: 'task', task: 'T002', attempt: 3 }), 'speckit:attempt:task:T002:3');
  assert.deepEqual(parseAttemptCheckId('speckit:attempt:converge::1'), { step: 'converge', task: null, attempt: 1 });
  assert.equal(parseAttemptCheckId(CHECK_ATTEMPT), null);
  assert.equal(renderTaskPrompt('T004'), `/speckit-implement Implement only task T004. Do not implement any other task. Do not commit and do not push. ${TASK_TEST_SCOPE}`);
  assert.match(TASK_TEST_SCOPE, /Do not run the whole test suite/);
  assert.match(renderTaskPrompt('T004', 'f'), /^\/speckit-implement Implement only task T004 of the spec in `specs\/f\/` \(its `tasks\.md`\); ignore the other specs\. Do not implement any other task\./);
  assert.match(renderConvergePrompt(), /^\/speckit-converge /);
  assert.match(renderResolvePrompt({ folder: 'f', files: ['a.cs', 'b.md'] }), /`specs\/f`[\s\S]*`a\.cs`, `b\.md`[\s\S]*Do not change any other file/);
  assert.equal(stepLabel({ step: 'task', task: 'T001' }), 'T001');
  assert.equal(stepLabel({ step: 'converge' }), 'convergence');
});

test('picks the newest check run', () => {
  assert.equal(latestCheckRun([{ id: 2 }, { id: 9 }, { id: 4 }]).id, 9);
  assert.equal(latestCheckRun([]), null);
});

const attempt = (task, outcome = 'failure', at = '2026-10-06T11:00:00Z', step = 'task') => ({ step, task, attempt: 1, outcome, at });
const stages = (...list) => ({ action: 'stages', step: 'task', stages: list, phaseEnd: true, lastIsGroup: false, phase: 'Phase 1', stale: [] });

test('decides the next stages of an implementation from the attempt ledger', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const decide = (fields) => decideNext({ tasksMarkdown: TASKS, windowStart, ...fields });

  assert.deepEqual(decide({ done: true }), { action: 'done' });
  assert.deepEqual(decide({}), stages([{ task: 'T002', attempt: 1 }], [{ task: 'T003', attempt: 1 }]));
  assert.deepEqual(decide({ attempts: [attempt('T001'), attempt('T002')] }), stages([{ task: 'T002', attempt: 2 }], [{ task: 'T003', attempt: 1 }]));
  assert.deepEqual(decide({ attempts: [attempt('T002', 'failure', '2026-10-06T09:00:00Z')] }), stages([{ task: 'T002', attempt: 1 }], [{ task: 'T003', attempt: 1 }]), 'attempts before the window do not count');
  assert.deepEqual(decide({ attempts: [attempt('T002'), attempt('T002', 'requeue'), attempt('T002')] }), stages([{ task: 'T002', attempt: 4 }], [{ task: 'T003', attempt: 1 }]), 'a redo is no failure');
  assert.deepEqual(decide({ attempts: [attempt('T002'), attempt('T002'), attempt('T002')] }), { action: 'limit', step: 'task', task: 'T002', attempts: 3, stale: [] });
  const noOps = Array.from({ length: 6 }, () => attempt('T002', 'requeue'));
  assert.deepEqual(decide({ attempts: noOps }), { action: 'limit', step: 'task', task: 'T002', attempts: 6, stale: [] }, 'endless redos stop too');
  assert.deepEqual(decide({ checks: [{ id: 1, external_id: CHECK_DONE, conclusion: 'success' }] }), { action: 'done' });
  const limited = { id: 1, external_id: CHECK_LIMIT, conclusion: 'failure', started_at: '2026-10-06T11:30:00Z' };
  assert.deepEqual(decide({ checks: [limited] }), { action: 'failed', diagnosis: { state: 'none', check: limited, count: 0 } });
  assert.equal(decide({ checks: [limited], resumedAt: '2026-10-06T11:45:00Z' }).action, 'stages', 'a resume after the stop continues');
  assert.equal(decide({ checks: [limited], resumedAt: '2026-10-06T11:15:00Z' }).action, 'failed', 'an older resume does not');
  assert.deepEqual(decide({ maxStages: 1 }), { ...stages([{ task: 'T002', attempt: 1 }]), phaseEnd: false }, 'a long phase continues in the next run');
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
  assert.equal(decide({ checks: [limited, reported], resumedAt: '2026-10-06T11:45:00Z' }).action, 'stages', 'a resume comment after the stop resumes');
  assert.equal(decide({ checks: [limited, reported], resumedAt: '2026-10-06T11:15:00Z' }).action, 'failed', 'an older resume comment does not');
});
test('renders the next steps a person can take', () => {
  const plain = renderNextSteps().join('\n');
  assert.match(plain, /\/speckit diagnose \[notes\][\s\S]*\/speckit resume \[guidance\]/);
  assert.doesNotMatch(plain, /\/speckit (sync|apply)/);
  assert.match(plain, /push a fix to the implementation branch/);
  const auto = renderNextSteps({ autoDiagnosis: true, behindMain: true }).join('\n');
  assert.match(auto, /A diagnosis starts automatically/);
  assert.doesNotMatch(auto, /\/speckit diagnose/);
  assert.match(auto, /```text\n {3}\/speckit sync\n {3}```/);
  const diagnosed = renderNextSteps({ options: [{ title: 'Retry with guidance', command: '/speckit resume Use the helper' }], amendmentPull: 7, diagnosed: true }).join('\n');
  assert.match(diagnosed, /^\*\*Review the amendment #7\*\*: merge it to apply it[\s\S]*Instead of the amendment[\s\S]*\/speckit resume Use the helper[\s\S]*Diagnose again/);
  assert.doesNotMatch(diagnosed, /\/speckit (apply|revise|discard)/);
  assert.doesNotMatch(diagnosed, /\/speckit resume \[guidance\]/, 'an option already covers resume');
  const recommended = renderNextSteps({ options: [{ title: 'Diagnose with these notes', command: '/speckit diagnose keep it' }], diagnosed: true }).join('\n');
  assert.equal(recommended.match(/\/speckit diagnose/g).length, 1, 'a command an option suggests is not repeated');
  const howTo = renderAmendmentHowTo(49).join('\n');
  assert.match(howTo, /\*\*Merge\*\* this pull request[\s\S]*continues on #49[\s\S]*\*Request changes\*[\s\S]*\*\*Close\*\*/);
  const status = setAmendmentStatus('Body from the agent.', ['⏳ checking']);
  assert.equal(status, '<!-- speckit-amend:status -->\n⏳ checking\n<!-- /speckit-amend:status -->\n\nBody from the agent.');
  assert.equal(setAmendmentStatus(status, ['✅ done']), '<!-- speckit-amend:status -->\n✅ done\n<!-- /speckit-amend:status -->\n\nBody from the agent.');
});

test('carries guidance through a marker, and validates amendments', () => {
  const marker = renderGuidanceMarker('Use `x` --> <!-- y');
  assert.equal(parseGuidance(`intro\n${marker}\nrest`), 'Use `x` --> <!-- y');
  assert.equal(parseGuidance('no marker'), null);
  assert.equal(parseGuidance('<!-- speckit-implement:guidance !!! -->'), null);

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
  assert.deepEqual([parseMaxParallel(undefined), parseMaxParallel(''), parseMaxParallel('1'), parseMaxParallel(' 5 '), parseMaxParallel('0'), parseMaxParallel('x')], [3, 3, 1, 5, 3, 3]);
});

test('plans the stages of a phase: single tasks, and [P] groups that run in parallel', () => {
  const tasks = [
    '## Phase 1: Setup', '- [x] T001 a', '- [ ] T002 b',
    '## Phase 2: Story', '### Tests', '- [ ] T003 [P] c', '- [ ] T004 [P] d', '### Implementation', '- [ ] T005 [P] e', '- [ ] T006 f', '- [ ] T007 [P] g', '- [ ] T008 [P] h',
    '## Phase 3: Polish', '- [ ] T009 i', '',
  ].join('\r\n');
  const tick = (markdown, ...ids) => ids.reduce((text, id) => text.replace(`- [ ] ${id}`, `- [x] ${id}`), markdown);
  assert.deepEqual(planStages(tasks), { stages: [['T002']], phaseEnd: true, lastIsGroup: false, phase: 'Phase 1: Setup' }, 'a phase is planned on its own');
  assert.deepEqual(planStages(tick(tasks, 'T002')), { stages: [['T003', 'T004'], ['T005'], ['T006'], ['T007', 'T008']], phaseEnd: true, lastIsGroup: true, phase: 'Phase 2: Story' }, 'a ### heading ends a group, not the phase');
  assert.deepEqual(planStages(tick(tasks, 'T002', 'T004')), { stages: [['T003'], ['T005'], ['T006'], ['T007', 'T008']], phaseEnd: true, lastIsGroup: true, phase: 'Phase 2: Story' }, 'a landed sibling is skipped');
  assert.deepEqual(planStages(tick(tasks, 'T002'), { maxStages: 2 }), { stages: [['T003', 'T004'], ['T005']], phaseEnd: false, lastIsGroup: false, phase: 'Phase 2: Story' });
  assert.deepEqual(planStages(tick(tasks, 'T002', 'T003', 'T004', 'T005', 'T006', 'T007', 'T008')), { stages: [['T009']], phaseEnd: true, lastIsGroup: false, phase: 'Phase 3: Polish' });
  assert.deepEqual(planStages(tick(tasks, 'T002', 'T003', 'T004', 'T005', 'T007', 'T008')), { stages: [['T006']], phaseEnd: true, lastIsGroup: false, phase: 'Phase 2: Story' }, 'an unchecked task between checked ones');
  assert.deepEqual(planStages('- [ ] T001 a\n- [ ] T002 [P] b\n- [ ] T003 [P] c\n'), { stages: [['T001'], ['T002', 'T003']], phaseEnd: true, lastIsGroup: true, phase: null }, 'no headings: one phase');
  assert.deepEqual(planStages('- [x] T001 a\n'), { stages: [], phaseEnd: false, lastIsGroup: false, phase: null });
  assert.deepEqual([parseMaxActiveSpecs(undefined), parseMaxActiveSpecs('5'), parseMaxActiveSpecs('0'), parseMaxActiveSpecs('x')], [3, 5, 3, 3]);
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

test('decides the stages of a [P] group and its limit', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const decide = (fields) => decideNext({ tasksMarkdown: PARALLEL_TASKS, windowStart, ...fields });
  const group = [{ task: 'T002', attempt: 1 }, { task: 'T003', attempt: 1 }, { task: 'T004', attempt: 1 }, { task: 'T005', attempt: 1 }];
  assert.deepEqual(decide({}), { ...stages(group, [{ task: 'T006', attempt: 1 }], [{ task: 'T007', attempt: 1 }]) });
  const afterLand = PARALLEL_TASKS.replace('- [ ] T002', '- [x] T002');
  assert.deepEqual(decide({ tasksMarkdown: afterLand, attempts: [attempt('T002', 'success'), attempt('T003')] }).stages[0], [{ task: 'T003', attempt: 2 }, { task: 'T004', attempt: 1 }, { task: 'T005', attempt: 1 }], 'landed siblings leave the group');
  assert.deepEqual(decide({ attempts: [attempt('T003'), attempt('T003'), attempt('T003')] }), { action: 'limit', step: 'task', task: 'T003', attempts: 3, stale: [] }, 'a member at its limit stops the group');
});

test('decides convergence, conflict resolution, and merging after the last task', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const now = Date.parse('2026-10-06T20:00:00Z');
  const at = (hour) => `2026-10-06T${String(hour).padStart(2, '0')}:00:00Z`;
  const step = (name, hour, outcome = 'success') => attempt(null, outcome, at(hour), name);
  const check = (id, external_id, fields = {}) => ({ id, external_id, status: 'completed', conclusion: 'neutral', started_at: at(12), ...fields });
  const done = '- [x] T001 a\n';
  const decide = (fields) => decideNext({ tasksMarkdown: done, windowStart, now, ...fields });
  const taskDone = attempt('T001', 'success', at(11));
  const single = (name, number) => ({ action: 'stages', step: name, stages: [[{ task: null, attempt: number }]], phaseEnd: false, lastIsGroup: false, stale: [] });

  assert.deepEqual(decide({ attempts: [taskDone] }), single('converge', 1));
  assert.deepEqual(decide({ attempts: [step('converge', 10), taskDone] }), single('converge', 1), 'a convergence older than the last task does not count');
  assert.deepEqual(decide({ attempts: [taskDone, step('converge', 12, 'failure'), step('converge', 13, 'failure'), step('converge', 14, 'failure')] }), { action: 'limit', step: 'converge', attempts: 3, stale: [] });

  const converged = [taskDone, step('converge', 12)];
  assert.deepEqual(decide({ attempts: converged }), { action: 'merge', step: 'merge', attempt: 1, stale: [] });
  const merges = [check(1, CHECK_MERGE, { conclusion: 'failure' }), check(2, CHECK_MERGE, { conclusion: 'neutral' })];
  assert.deepEqual(decide({ attempts: converged, checks: merges }), { action: 'merge', step: 'merge', attempt: 3, stale: [] });
  const failedMerges = [1, 2, 3].map((id) => check(id, CHECK_MERGE, { conclusion: 'failure' }));
  assert.deepEqual(decide({ attempts: converged, checks: failedMerges }), { action: 'limit', step: 'merge', attempts: 3, stale: [] });
  assert.deepEqual(decide({ attempts: converged, checks: failedMerges, windowStart: at(13) }), { action: 'merge', step: 'merge', attempt: 1, stale: [] }, 'a person pushing starts a new window');

  assert.deepEqual(decide({ attempts: converged, checks: [check(1, CHECK_MERGE, { status: 'in_progress', started_at: at(19) })] }), { action: 'wait' });
  const stale = check(1, CHECK_MERGE, { status: 'in_progress', started_at: at(15) });
  assert.deepEqual(decide({ attempts: converged, checks: [stale] }), { action: 'merge', step: 'merge', attempt: 2, stale: [stale] });

  const conflict = check(5, CHECK_CONFLICT);
  assert.deepEqual(decide({ attempts: converged, checks: [check(4, CHECK_MERGE), conflict] }), single('resolve', 1));
  assert.deepEqual(decide({ attempts: [...converged, step('resolve', 13, 'failure')], checks: [conflict] }), single('resolve', 2), 'a failed resolve keeps the conflict state');
  assert.deepEqual(decide({ attempts: converged, checks: [conflict, check(7, CHECK_MERGE)] }), { action: 'merge', step: 'merge', attempt: 2, stale: [] });
  const earlierResolves = [step('resolve', 11, 'failure'), step('resolve', 11, 'failure'), step('resolve', 11)];
  assert.deepEqual(decide({ attempts: [...converged, ...earlierResolves], checks: [conflict] }), single('resolve', 1), 'only attempts at the current conflict count');
  const limited = check(8, CHECK_LIMIT, { conclusion: 'failure' });
  assert.deepEqual(decide({ attempts: converged, checks: [limited], resumedAt: at(13), windowStart: at(13) }), { action: 'merge', step: 'merge', attempt: 1, stale: [] });
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

test('accepts only the target task tick in tasks.md and no protected paths', () => {
  const folder = 'f';
  const before = '# Tasks\n\n- [ ] T001 One\n- [ ] T002 Two\n';
  const ticked = before.replace('- [ ] T001', '- [X] T001');
  const check = (fields) => validateTaskChange({ folder, taskId: 'T001', before, after: ticked, changedPaths: ['src/a.cs', 'specs/f/tasks.md'], ...fields });

  assert.deepEqual(check({}), []);
  assert.deepEqual(check({ after: before.replace('- [ ] T001', '- [x] T001') }), []);
  assert.match(check({ after: before }).join(), /T001 was not checked/);
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
    { user: { login: 'github-actions[bot]' }, body: `${RESUME_COMMENT_MARKER}\nresumed`, created_at: 'a' },
    { user: { login: 'github-actions[bot]' }, body: `failed: agent wrote ${RESUME_COMMENT_MARKER}`, created_at: 'b' },
    { user: { login: 'mallory' }, body: RESUME_COMMENT_MARKER, created_at: 'c' },
  ];
  assert.deepEqual(markerTimes(comments, RESUME_COMMENT_MARKER), ['a']);
  assert.equal(neutralizeMarkers(`x ${RESUME_COMMENT_MARKER} y`), 'x &lt;!-- speckit-implement:resume --> y');
});

test('the implementation workflow runs the planned stages as a matrix, one at a time, stopping at the first that fails', () => {
  const workflow = readFileSync(new URL('../workflows/speckit-implement.yml', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
  const stages = workflow.match(/^ {2}stages:\n[\s\S]*?(?=^ {2}\S|^ {2}#)/m)?.[0] ?? '';
  assert.match(stages, /max-parallel: 1\n\s+fail-fast: true\n\s+matrix: \$\{\{ fromJSON\(needs\.decide\.outputs\.stage_matrix\) \}\}/);
  assert.match(stages, /uses: \.\/\.github\/workflows\/speckit-stage\.yml/);
  assert.match(workflow, /continue:[\s\S]*?needs: \[decide, stages, phase-check, merge-verify, merge-land\]/);
  assert.equal(MAX_STAGES_PER_RUN, 256, 'GitHub runs at most 256 matrix jobs per run');
});

test('labels the next run of the chain with what it tackles', () => {
  const tasks = '## Phase 3: User Story 1 - Upload a Source Recording for a Match (Priority: P1) 🎯 MVP\n- [ ] T013 [P] a\n- [ ] T014 [P] b\n- [ ] T015 c\n';
  const decide = (fields) => decideNext({ tasksMarkdown: tasks, windowStart: '2026-10-06T10:00:00Z', ...fields });
  assert.equal(segmentLabel(decide({})), 'Phase 3: User Story 1 - Upload a Source Recording for a Match (Priority: P1) 🎯 MVP', 'the phase heading as written, without "## "');
  assert.equal(segmentLabel(decide({ attempts: [attempt('T014')] })), 'Phase 3: User Story 1 - Upload a Source Recording for a Match (Priority: P1) 🎯 MVP', 'a retry within the phase keeps its name');
  assert.equal(segmentLabel(decideNext({ tasksMarkdown: '- [ ] T001 a\n- [ ] T002 b\n', windowStart: '2026-10-06T10:00:00Z' })), 'T001–T002', 'no phase heading: the tasks');
  assert.equal(segmentLabel({ action: 'stages', step: 'converge', stages: [[{ task: null, attempt: 2 }]] }), 'Convergence (retry: convergence attempt 2)');
  assert.equal(segmentLabel({ action: 'stages', step: 'resolve', stages: [[{ task: null, attempt: 1 }]] }), 'Conflict resolution');
  assert.deepEqual([segmentLabel({ action: 'merge', attempt: 1 }), segmentLabel({ action: 'merge', attempt: 2 })], ['Merge', 'Merge (attempt 2)']);
  assert.equal(segmentLabel({ action: 'limit', step: 'task', task: 'T014' }), 'Stop: T014 reached the attempt limit');
  assert.deepEqual([segmentLabel({ action: 'failed', diagnosis: { state: 'none' } }), segmentLabel({ action: 'failed', diagnosis: { state: 'stale' } })], ['Start a diagnosis', 'Close the unfinished diagnosis']);
  assert.deepEqual([segmentLabel({ action: 'done' }), segmentLabel(null)], ['', '']);
  assert.equal(renderImplementRunName(35, 'Merge'), 'Spec Kit implement #35 · Merge');
  assert.equal(isImplementRunOf('Spec Kit implement #35 · Merge', 35), true);
  assert.deepEqual([isPerTaskRunOf('Spec Kit implement #35 T002 attempt 1', 35), isPerTaskRunOf('Spec Kit implement #35 · Phase 1 · T002', 35)], [true, false]);
});
