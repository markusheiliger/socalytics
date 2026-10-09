import assert from 'node:assert/strict';
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
  decideContinuation,
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
  parseStepRunName,
  renderConvergePrompt,
  renderGuidanceMarker,
  renderNextSteps,
  renderPullRequestBody,
  renderPullRequestTitle,
  renderResolvePrompt,
  renderStartComment,
  renderStepRunName,
  renderTaskPrompt,
  RESUME_COMMENT_MARKER,
  START_COMMENT_MARKER,
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

test('renders and parses worker run names and prompts', () => {
  assert.equal(renderStepRunName({ step: 'task', twin: 38, task: 'T002', attempt: 3 }), 'Spec Kit implement #38 T002 attempt 3');
  assert.equal(renderStepRunName({ step: 'converge', twin: 38, attempt: 1 }), 'Spec Kit converge #38 attempt 1');
  assert.equal(renderStepRunName({ step: 'resolve', twin: 38, attempt: 2 }), 'Spec Kit resolve #38 attempt 2');
  assert.deepEqual(parseStepRunName('task', 'Spec Kit implement #38 T002 attempt 3'), { step: 'task', twin: 38, task: 'T002', attempt: 3 });
  assert.deepEqual(parseStepRunName('converge', 'Spec Kit converge #7 attempt 1'), { step: 'converge', twin: 7, task: null, attempt: 1 });
  assert.deepEqual(parseStepRunName('resolve', 'Spec Kit resolve #7 attempt 2'), { step: 'resolve', twin: 7, task: null, attempt: 2 });
  assert.equal(parseStepRunName('task', 'Spec Kit implement #7 finalize attempt 1'), null);
  assert.equal(parseStepRunName('task', 'Spec Kit implement'), null);
  assert.equal(parseStepRunName('task', 'Spec Kit implement task #38 T002 attempt 3'), null, 'old task run names are ignored');
  assert.equal(parseStepRunName('converge', 'Spec Kit implement #38 T002 attempt 3'), null);
  assert.equal(parseStepRunName('task', 'Spec Kit orchestrate #38 T002 attempt 3'), null);
  assert.equal(renderTaskPrompt('T004'), '/speckit-implement Implement only task T004. Do not implement any other task. Do not commit and do not push.');
  assert.match(renderConvergePrompt(), /^\/speckit-converge /);
  assert.match(renderResolvePrompt({ folder: 'f', files: ['a.cs', 'b.md'] }), /`specs\/f`[\s\S]*`a\.cs`, `b\.md`[\s\S]*Do not change any other file/);
  assert.equal(stepLabel({ step: 'task', task: 'T001' }), 'T001');
  assert.equal(stepLabel({ step: 'converge' }), 'convergence');
});

test('picks the newest check run', () => {
  assert.equal(latestCheckRun([{ id: 2 }, { id: 9 }, { id: 4 }]).id, 9);
  assert.equal(latestCheckRun([]), null);
});

test('decides how to continue the tasks of an implementation', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const run = (task, status = 'completed', created = '2026-10-06T11:00:00Z', conclusion = 'failure') => ({ task, status, conclusion, created_at: created });
  const decide = (fields) => decideContinuation({ tasksMarkdown: TASKS, windowStart, ...fields });
  const tasks = (...runs) => ({ task: runs });

  assert.deepEqual(decide({ done: true }), { action: 'done' });
  assert.deepEqual(decide({ runs: tasks(run('T002', 'completed', undefined, 'success')) }), { action: 'dispatch', step: 'task', task: 'T002', attempt: 2, stale: [] });
  const noOps = Array.from({ length: 6 }, () => run('T002', 'completed', undefined, 'success'));
  assert.deepEqual(decide({ runs: tasks(...noOps) }), { action: 'limit', step: 'task', task: 'T002', attempts: 6, stale: [] });
  assert.deepEqual(decide({ runs: tasks(run('T002', 'completed', undefined, 'cancelled'), run('T002'), run('T002', 'completed', undefined, 'timed_out')) }), { action: 'limit', step: 'task', task: 'T002', attempts: 3, stale: [] });

  assert.deepEqual(decide({}), { action: 'dispatch', step: 'task', task: 'T002', attempt: 1, stale: [] });
  assert.deepEqual(decide({ runs: tasks(run('T001'), run('T002')) }), { action: 'dispatch', step: 'task', task: 'T002', attempt: 2, stale: [] });
  assert.deepEqual(decide({ runs: tasks(run('T002', 'completed', '2026-10-06T09:00:00Z')) }), { action: 'dispatch', step: 'task', task: 'T002', attempt: 1, stale: [] });
  assert.deepEqual(decide({ runs: tasks(run('T002', 'in_progress')) }), { action: 'wait' });
  assert.deepEqual(decide({ runs: { converge: [run(null, 'queued')] } }), { action: 'wait' });
  assert.deepEqual(decide({ checks: [{ id: 1, external_id: CHECK_DONE, conclusion: 'success' }] }), { action: 'done' });
  const limited = { id: 1, external_id: CHECK_LIMIT, conclusion: 'failure' };
  assert.deepEqual(decide({ checks: [limited] }), { action: 'failed', diagnosis: { state: 'none', check: limited, count: 0 } });
  assert.deepEqual(decide({ checks: [limited], resume: true }), { action: 'resume', step: 'task', task: 'T002', attempt: 1, stale: [] });
  assert.deepEqual(decide({ resume: true }), { action: 'resume', step: 'task', task: 'T002', attempt: 1, stale: [] }, 'a resume always starts a new attempt window');
  assert.deepEqual(decide({ checks: [{ id: 1, external_id: CHECK_ATTEMPT, conclusion: 'failure' }] }), { action: 'dispatch', step: 'task', task: 'T002', attempt: 1, stale: [] });
});

test('waits while a diagnosis runs, then for a decision, and gives up on a stale diagnosis', () => {
  const now = Date.parse('2026-10-06T12:00:00Z');
  const decide = (fields) => decideContinuation({ tasksMarkdown: TASKS, windowStart: '2026-10-06T10:00:00Z', now, ...fields });
  const limited = { id: 1, external_id: CHECK_LIMIT, status: 'completed', conclusion: 'failure', started_at: '2026-10-06T11:00:00Z' };
  const running = { id: 2, external_id: CHECK_DIAGNOSING, status: 'in_progress', started_at: '2026-10-06T11:30:00Z' };
  assert.deepEqual(decide({ checks: [limited, running] }), { action: 'diagnosing' });
  const stale = { ...running, started_at: '2026-10-06T10:30:00Z' };
  assert.deepEqual(decide({ checks: [limited, stale] }), { action: 'failed', diagnosis: { state: 'stale', check: stale, count: 1 } });
  const reported = { ...running, status: 'completed', conclusion: 'neutral', external_id: CHECK_DIAGNOSED };
  assert.deepEqual(decide({ checks: [limited, reported] }), { action: 'failed', diagnosis: { state: 'reported', check: reported, count: 1 } });
  assert.deepEqual(decide({ checks: [limited, reported], resume: true }), { action: 'resume', step: 'task', task: 'T002', attempt: 1, stale: [] });
  assert.deepEqual(decide({ checks: [limited, reported], resumedAt: '2026-10-06T11:45:00Z' }), { action: 'resume', step: 'task', task: 'T002', attempt: 1, stale: [] }, 'a resume comment after the stop resumes');
  assert.equal(decide({ checks: [limited, reported], resumedAt: '2026-10-06T11:15:00Z' }).action, 'failed', 'an older resume comment does not');
  assert.deepEqual(decide({ checks: [running], runs: { task: [{ task: 'T002', status: 'in_progress', created_at: '2026-10-06T11:00:00Z' }] } }), { action: 'wait' }, 'a diagnosis started while tasks run lets them finish');
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
  assert.ok(diagnosed.indexOf('/speckit resume Use the helper') < diagnosed.indexOf('/speckit apply'));
  assert.match(diagnosed, /amendment #7[\s\S]*\/speckit revise <notes>[\s\S]*\/speckit discard[\s\S]*Diagnose again/);
  assert.doesNotMatch(diagnosed, /\/speckit resume \[guidance\]/, 'an option already covers resume');
  const recommendedApply = renderNextSteps({ options: [{ title: 'Apply it', command: '/speckit apply' }], amendmentPull: 7, diagnosed: true }).join('\n');
  assert.equal(recommendedApply.match(/\/speckit apply/g).length, 1, 'an option that applies is not repeated');
  assert.match(recommendedApply, /\/speckit discard/);
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

test('ticks a task in tasks.md and syncs the pull request body with it', () => {
  const ticked = tickTask(PARALLEL_TASKS, 'T003');
  assert.match(ticked, /- \[x\] T003 \[P\] \[US1\] B\r\n/);
  assert.equal(ticked.replace('- [x] T003', '- [ ] T003'), PARALLEL_TASKS);
  assert.equal(tickTask(PARALLEL_TASKS, 'T001'), null);
  assert.equal(tickTask(PARALLEL_TASKS, 'T999'), null);
  const body = '## Tasks\n\n- [ ] T001 Setup\n- [ ] T002 A\n- [ ] T003 B\n';
  assert.equal(syncPullRequestTicks(body, ticked), '## Tasks\n\n- [x] T001 Setup\n- [ ] T002 A\n- [x] T003 B\n');
});

test('runs a [P] group in parallel, fills free slots, and stops at a limit after the siblings', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const run = (task, status = 'completed', conclusion = 'failure') => ({ task, status, conclusion, created_at: '2026-10-06T11:00:00Z' });
  const decide = (fields) => decideContinuation({ tasksMarkdown: PARALLEL_TASKS, windowStart, maxParallel: 3, ...fields });
  const tasks = (...runs) => ({ task: runs });

  assert.deepEqual(decide({}), { action: 'dispatch-tasks', step: 'task', tasks: [{ task: 'T002', attempt: 1 }, { task: 'T003', attempt: 1 }, { task: 'T004', attempt: 1 }], stale: [] });
  assert.deepEqual(decideContinuation({ tasksMarkdown: PARALLEL_TASKS, windowStart }), { action: 'dispatch', step: 'task', task: 'T002', attempt: 1, stale: [] }, 'one at a time by default');
  assert.deepEqual(decide({ runs: tasks(run('T002', 'in_progress'), run('T003', 'in_progress'), run('T004', 'in_progress')) }), { action: 'wait' });
  const afterLand = PARALLEL_TASKS.replace('- [ ] T002', '- [x] T002');
  assert.deepEqual(
    decide({ tasksMarkdown: afterLand, runs: tasks(run('T002', 'completed', 'success'), run('T003', 'in_progress'), run('T004', 'in_progress')) }),
    { action: 'dispatch', step: 'task', task: 'T005', attempt: 1, stale: [] },
    'a landed task frees a slot for the next group member',
  );
  assert.deepEqual(
    decide({ runs: tasks(run('T002'), run('T003', 'in_progress'), run('T004', 'in_progress')) }),
    { action: 'dispatch', step: 'task', task: 'T002', attempt: 2, stale: [] },
    'a failed task is retried while its siblings run',
  );
  assert.deepEqual(
    decide({ runs: tasks(run('T002', 'completed', 'success'), run('T003'), run('T004')) }),
    { action: 'dispatch', step: 'task', task: 'T002', attempt: 2, stale: [] },
    'a redone task (successful run, still unticked) does not count as a failure and goes first',
  );
  assert.deepEqual(
    decide({ runs: tasks(run('T002', 'completed', 'success'), run('T002', 'in_progress'), run('T003')) }),
    { action: 'wait' },
    'no sibling starts while a task is being redone',
  );
  const limited = tasks(run('T003'), run('T003'), run('T003'), run('T002', 'in_progress'));
  assert.deepEqual(decide({ runs: limited }), { action: 'wait' }, 'the limit waits for running siblings');
  assert.deepEqual(decide({ runs: tasks(run('T003'), run('T003'), run('T003')) }), { action: 'limit', step: 'task', task: 'T003', attempts: 3, stale: [] });
  const sequentialNext = PARALLEL_TASKS.replace(/- \[ \] T00[2-5]/g, (line) => line.replace('[ ]', '[x]'));
  assert.deepEqual(decide({ tasksMarkdown: sequentialNext, runs: tasks(run('T005', 'in_progress')) }), { action: 'wait' }, 'a sequential task waits for the group');
  const allDone = PARALLEL_TASKS.replace(/- \[ \] T/g, '- [x] T');
  assert.deepEqual(decide({ tasksMarkdown: allDone, runs: tasks(run('T008', 'in_progress')) }), { action: 'wait' }, 'convergence waits for running tasks');
  assert.deepEqual(decide({ runs: { ...tasks(run('T002', 'in_progress')), converge: [run(null, 'queued')] } }), { action: 'wait' });
  assert.deepEqual(decide({ runs: tasks(run('T002', 'in_progress')), checks: [{ id: 1, external_id: CHECK_LIMIT, conclusion: 'failure' }], resume: true }), { action: 'wait' });
});

test('decides convergence, conflict resolution, and merging after the last task', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const now = Date.parse('2026-10-06T20:00:00Z');
  const at = (hour) => `2026-10-06T${String(hour).padStart(2, '0')}:00:00Z`;
  const run = (hour, conclusion = 'success', status = 'completed') => ({ task: null, status, conclusion, created_at: at(hour) });
  const check = (id, external_id, fields = {}) => ({ id, external_id, status: 'completed', conclusion: 'neutral', started_at: at(12), ...fields });
  const done = '- [x] T001 a\n';
  const decide = (fields) => decideContinuation({ tasksMarkdown: done, windowStart, now, ...fields });
  const taskDone = { task: [{ task: 'T001', status: 'completed', conclusion: 'success', created_at: at(11) }] };

  assert.deepEqual(decide({ runs: taskDone }), { action: 'dispatch', step: 'converge', attempt: 1, stale: [] });
  assert.deepEqual(decide({ runs: { ...taskDone, converge: [run(10)] } }), { action: 'dispatch', step: 'converge', attempt: 1, stale: [] }, 'a convergence older than the last task does not count');
  assert.deepEqual(decide({ runs: { ...taskDone, converge: [run(12, 'failure'), run(13, 'failure'), run(14, 'failure')] } }), { action: 'limit', step: 'converge', attempts: 3, stale: [] });

  const converged = { ...taskDone, converge: [run(12)] };
  assert.deepEqual(decide({ runs: converged }), { action: 'merge', step: 'merge', attempt: 1, stale: [] });
  const merges = [check(1, CHECK_MERGE, { conclusion: 'failure' }), check(2, CHECK_MERGE, { conclusion: 'neutral' })];
  assert.deepEqual(decide({ runs: converged, checks: merges }), { action: 'merge', step: 'merge', attempt: 3, stale: [] });
  const failedMerges = [1, 2, 3].map((id) => check(id, CHECK_MERGE, { conclusion: 'failure' }));
  assert.deepEqual(decide({ runs: converged, checks: failedMerges }), { action: 'limit', step: 'merge', attempts: 3, stale: [] });
  assert.deepEqual(decide({ runs: converged, checks: failedMerges, windowStart: at(13) }), { action: 'merge', step: 'merge', attempt: 1, stale: [] }, 'a person pushing starts a new window');

  assert.deepEqual(decide({ runs: converged, checks: [check(1, CHECK_MERGE, { status: 'in_progress', started_at: at(19) })] }), { action: 'wait' });
  const stale = check(1, CHECK_MERGE, { status: 'in_progress', started_at: at(15) });
  assert.deepEqual(decide({ runs: converged, checks: [stale] }), { action: 'merge', step: 'merge', attempt: 2, stale: [stale] });

  const conflict = check(5, CHECK_CONFLICT);
  assert.deepEqual(decide({ runs: converged, checks: [check(4, CHECK_MERGE), conflict] }), { action: 'dispatch', step: 'resolve', attempt: 1, stale: [] });
  const failedResolve = check(6, CHECK_ATTEMPT, { conclusion: 'failure' });
  assert.deepEqual(
    decide({ runs: { ...converged, resolve: [run(13, 'failure')] }, checks: [conflict, failedResolve] }),
    { action: 'dispatch', step: 'resolve', attempt: 2, stale: [] },
    'a failed resolve keeps the conflict state',
  );
  assert.deepEqual(decide({ runs: converged, checks: [conflict, check(7, CHECK_MERGE)] }), { action: 'merge', step: 'merge', attempt: 2, stale: [] });
  const earlierResolves = [run(11, 'failure'), run(11, 'failure'), ...Array.from({ length: 4 }, () => run(11))];
  assert.deepEqual(
    decide({ runs: { ...converged, resolve: earlierResolves }, checks: [conflict] }),
    { action: 'dispatch', step: 'resolve', attempt: 1, stale: [] },
    'only attempts at the current conflict count',
  );
  assert.deepEqual(
    decide({ runs: converged, checks: [check(8, CHECK_LIMIT, { conclusion: 'failure' })], resume: true }),
    { action: 'resume', step: 'merge', attempt: 1, stale: [] },
  );
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
