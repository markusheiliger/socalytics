import assert from 'node:assert/strict';
import test from 'node:test';

import {
  CHECK_ATTEMPT,
  CHECK_DONE,
  CHECK_LIMIT,
  checkRunOutput,
  decideContinuation,
  decideLifecycle,
  extractTasks,
  implementationBranch,
  latestCheckRun,
  listTasks,
  markerTimes,
  neutralizeMarkers,
  nextTask,
  parseTaskRunName,
  renderPullRequestBody,
  renderPullRequestTitle,
  renderStartComment,
  renderTaskPrompt,
  renderTaskRunName,
  RESUME_COMMENT_MARKER,
  START_COMMENT_MARKER,
  taskProgress,
  tickPullRequestBody,
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
  assert.match(body, /## Tasks \(1\)\n\n### Phase 1\n\n- \[ \] T001 done already/);
  assert.doesNotMatch(body, /\n{3,}/);
  assert.match(renderPullRequestBody({ twinNumber: 1, folder: 'f', context, tasks: extractTasks('') }), /No tasks were found/);

  const comment = renderStartComment({ twinNumber: 33, folder: 'f', taskCount: 4 });
  assert.ok(comment.startsWith(START_COMMENT_MARKER));
  assert.match(comment, /#33 \(`specs\/f`\): 4 task\(s\) queued/);
  assert.equal(checkRunOutput(4).title, '0 of 4 tasks implemented');
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

test('renders and parses task run names and the task prompt', () => {
  assert.equal(renderTaskRunName(38, 'T002', 3), 'Spec Kit implement task #38 T002 attempt 3');
  assert.deepEqual(parseTaskRunName('Spec Kit implement task #38 T002 attempt 3'), { twin: 38, task: 'T002', attempt: 3 });
  assert.deepEqual(parseTaskRunName('Spec Kit implement task #7 finalize attempt 1'), { twin: 7, task: 'finalize', attempt: 1 });
  assert.equal(parseTaskRunName('Spec Kit implement task'), null);
  assert.equal(renderTaskPrompt('T004'), '/speckit-implement Implement only task T004. Do not implement any other task. Do not commit and do not push.');
});

test('picks the newest check run', () => {
  assert.equal(latestCheckRun([{ id: 2 }, { id: 9 }, { id: 4 }]).id, 9);
  assert.equal(latestCheckRun([]), null);
});

test('decides how to continue an implementation', () => {
  const windowStart = '2026-10-06T10:00:00Z';
  const run = (task, status = 'completed', created = '2026-10-06T11:00:00Z', conclusion = 'failure') => ({ task, status, conclusion, created_at: created });
  const decide = (fields) => decideContinuation({ tasksMarkdown: TASKS, latestCheck: null, runs: [], windowStart, resume: false, ...fields });

  assert.deepEqual(decide({ done: true }), { action: 'done' });
  assert.deepEqual(decide({ runs: [run('T002', 'completed', undefined, 'success')] }), { action: 'dispatch', task: 'T002', attempt: 2 });
  const noOps = Array.from({ length: 6 }, () => run('T002', 'completed', undefined, 'success'));
  assert.deepEqual(decide({ runs: noOps }), { action: 'limit', task: 'T002', attempts: 6 });
  assert.deepEqual(decide({ runs: [run('T002', 'completed', undefined, 'cancelled'), run('T002'), run('T002', 'completed', undefined, 'timed_out')] }), { action: 'limit', task: 'T002', attempts: 3 });

  assert.deepEqual(decide({}), { action: 'dispatch', task: 'T002', attempt: 1 });
  assert.deepEqual(decide({ runs: [run('T001'), run('T002')] }), { action: 'dispatch', task: 'T002', attempt: 2 });
  assert.deepEqual(decide({ runs: [run('T002', 'completed', '2026-10-06T09:00:00Z')] }), { action: 'dispatch', task: 'T002', attempt: 1 });
  assert.deepEqual(decide({ runs: [run('T002'), run('T002'), run('T002')] }), { action: 'limit', task: 'T002', attempts: 3 });
  assert.deepEqual(decide({ runs: [run('T002', 'in_progress')] }), { action: 'wait' });
  assert.deepEqual(decide({ tasksMarkdown: '- [x] T001 a\n' }), { action: 'dispatch', task: 'finalize', attempt: 1 });
  assert.deepEqual(decide({ latestCheck: { external_id: CHECK_DONE, conclusion: 'success' } }), { action: 'done' });
  const limited = { external_id: CHECK_LIMIT, conclusion: 'failure' };
  assert.deepEqual(decide({ latestCheck: limited }), { action: 'failed' });
  assert.deepEqual(decide({ latestCheck: limited, resume: true }), { action: 'resume', task: 'T002', attempt: 1 });
  assert.deepEqual(decide({ latestCheck: { external_id: CHECK_ATTEMPT, conclusion: 'failure' } }), { action: 'dispatch', task: 'T002', attempt: 1 });
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
