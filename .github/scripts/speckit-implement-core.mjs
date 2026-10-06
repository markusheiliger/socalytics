import { renderSpecLine } from './speckit-prepare-core.mjs';

export const BRANCH_PREFIX = 'speckit/';
export const CHECK_RUN_NAME = 'Spec Kit implementation';
export const START_COMMENT_MARKER = '<!-- speckit-implement:start -->';
export const RESUME_COMMENT_MARKER = '<!-- speckit-implement:resume -->';
export const DONE_COMMENT_MARKER = '<!-- speckit-implement:done -->';
export const BOT_LOGIN = 'github-actions[bot]';
export const IMPLEMENT_WORKFLOW_FILE = 'speckit-implement.yml';
export const TASK_RUN_PREFIX = 'Spec Kit implement';
export const FINALIZE_TASK = 'finalize';
export const MAX_TASK_ATTEMPTS = 3;
// Check run external IDs tell the orchestrator what a check run means.
export const CHECK_PROGRESS = 'speckit:progress';
export const CHECK_ATTEMPT = 'speckit:attempt';
export const CHECK_LIMIT = 'speckit:limit';
export const CHECK_DONE = 'speckit:done';
// Paths the agent must never change; specs/<folder>/tasks.md may only receive the target task's tick.
export const PROTECTED_PREFIXES = ['.github/', '.specify/', 'specs/'];

const HEADING_PATTERN = /^(#{2,4})\s+(.+?)\s*$/;
const TASK_PATTERN = /^\s*[-*] \[( |x|X)\]\s+(T\d{3,})\b\s*(.*)$/;
const RUN_NAME_PATTERN = new RegExp(`^${TASK_RUN_PREFIX} #(\\d+) (T\\d{3,}|finalize) attempt (\\d+)$`);

export function implementationBranch(folder) {
  return `${BRANCH_PREFIX}${folder}`;
}

// Extracts task checkboxes from tasks.md, keeping the headings (## to ####) that contain tasks.
export function extractTasks(tasksMarkdown) {
  const items = [];
  const path = [];
  const emitted = [];
  let count = 0;
  for (const line of (tasksMarkdown ?? '').split(/\r?\n/)) {
    const heading = line.match(HEADING_PATTERN);
    if (heading) {
      const level = heading[1].length;
      path.length = level - 1;
      path[level - 2] = heading[2];
      continue;
    }
    const task = line.match(TASK_PATTERN);
    if (!task) continue;
    for (let index = 0; index < path.length; index += 1) {
      if (path[index] === undefined) continue;
      if (emitted[index] !== path[index]) {
        items.push({ type: 'heading', level: index + 2, text: path[index] });
        emitted[index] = path[index];
        emitted.length = index + 1;
      }
    }
    items.push({ type: 'task', id: task[2], text: task[3].trim(), done: task[1] !== ' ' });
    count += 1;
  }
  return { items, count };
}

// The tasks of a tasks.md in file order with their done state.
export function listTasks(tasksMarkdown) {
  return extractTasks(tasksMarkdown).items.filter((item) => item.type === 'task');
}

// The first unticked task; tasks are always implemented strictly in file order.
export function nextTask(tasksMarkdown) {
  return listTasks(tasksMarkdown).find((task) => !task.done) ?? null;
}

export function taskProgress(tasksMarkdown) {
  const tasks = listTasks(tasksMarkdown);
  return { done: tasks.filter((task) => task.done).length, total: tasks.length };
}

export function renderTaskRunName(twin, task, attempt) {
  return `${TASK_RUN_PREFIX} #${twin} ${task} attempt ${attempt}`;
}

export function parseTaskRunName(title) {
  const match = String(title ?? '').match(RUN_NAME_PATTERN);
  return match ? { twin: Number(match[1]), task: match[2], attempt: Number(match[3]) } : null;
}

export function renderTaskPrompt(taskId) {
  return `/speckit-implement Implement only task ${taskId}. Do not implement any other task. Do not commit and do not push.`;
}

// The newest check run (highest ID) of a list, or null.
export function latestCheckRun(checkRuns) {
  return [...checkRuns].sort((a, b) => b.id - a.id)[0] ?? null;
}

// Creation times of workflow-authored comments that start with a marker; text inside other comments cannot forge them.
export function markerTimes(comments, marker) {
  return comments
    .filter((comment) => comment.user?.login === BOT_LOGIN && String(comment.body ?? '').startsWith(marker))
    .map((comment) => comment.created_at);
}

// Neutralizes HTML comments in untrusted text before it is posted, so it cannot carry workflow markers.
export function neutralizeMarkers(text) {
  return String(text ?? '').replaceAll('<!--', '&lt;!--');
}

// Decides how the orchestrator continues an open implementation pull request.
// `runs` are the task workflow runs of this twin ({ task, status, conclusion, created_at }), `windowStart` the
// start of the current attempt window, `done` whether the implementation was finalized, and `resume` whether a
// person asked to resume this twin. Only unsuccessful runs count as attempts; successful runs that left the task
// unchecked were no-ops, and their number is capped separately so they cannot loop forever.
export function decideContinuation({ tasksMarkdown, latestCheck, runs, windowStart, done = false, resume }) {
  if (runs.some((run) => run.status !== 'completed')) return { action: 'wait' };
  if (done || (latestCheck?.external_id === CHECK_DONE && latestCheck.conclusion === 'success')) return { action: 'done' };
  const failed = latestCheck?.external_id === CHECK_LIMIT && latestCheck.conclusion === 'failure';
  if (failed && !resume) return { action: 'failed' };
  const task = nextTask(tasksMarkdown)?.id ?? FINALIZE_TASK;
  if (failed) return { action: 'resume', task, attempt: 1 };
  const since = Date.parse(windowStart);
  const forTask = runs.filter((run) => run.task === task && Date.parse(run.created_at) >= since);
  const attempts = forTask.filter((run) => run.conclusion !== 'success').length;
  if (attempts >= MAX_TASK_ATTEMPTS || forTask.length >= MAX_TASK_ATTEMPTS * 2) return { action: 'limit', task, attempts: forTask.length };
  return { action: 'dispatch', task, attempt: forTask.length + 1 };
}

// Reasons why the agent's change for `taskId` cannot be accepted. `before` and `after` are the contents of
// specs/<folder>/tasks.md; `changedPaths` are all changed repository paths.
export function validateTaskChange({ folder, taskId, before, after, changedPaths }) {
  const reasons = [];
  const tasksPath = `specs/${folder}/tasks.md`;
  const protectedPaths = changedPaths.filter((file) => file !== tasksPath && PROTECTED_PREFIXES.some((prefix) => file.startsWith(prefix)));
  if (protectedPaths.length > 0) reasons.push(`changed protected paths: ${protectedPaths.join(', ')}`);

  const beforeLines = (before ?? '').split(/\r?\n/);
  const afterLines = (after ?? '').split(/\r?\n/);
  const differing = beforeLines.length === afterLines.length
    ? beforeLines.map((line, index) => index).filter((index) => beforeLines[index] !== afterLines[index])
    : null;
  const ticked = (index) => {
    const was = beforeLines[index].match(TASK_PATTERN);
    const now = afterLines[index].match(TASK_PATTERN);
    return was && now && was[2] === taskId && now[2] === taskId && was[1] === ' ' && now[1] !== ' '
      && beforeLines[index].replace(/\[ \]/, '[x]') === afterLines[index].replace(/\[[xX]\]/, '[x]');
  };
  if (differing === null || differing.length > 1 || (differing.length === 1 && !ticked(differing[0]))) {
    reasons.push(`\`${tasksPath}\` may only change by checking ${taskId}`);
  } else if (differing.length === 0) {
    reasons.push(`${taskId} was not checked in \`${tasksPath}\`, so the task is not complete`);
  }
  return reasons;
}

export function tickPullRequestBody(body, taskId) {
  return String(body ?? '').replace(new RegExp(`^- \\[ \\] ${taskId}\\b`, 'm'), `- [x] ${taskId}`);
}

export function renderPullRequestTitle(spec) {
  return `Implement: ${spec.title}`;
}

export function renderPullRequestBody({ twinNumber, folder, tasks, context }) {
  const lines = tasks.items.map((item) => (item.type === 'heading'
    ? `\n${'#'.repeat(Math.min(item.level + 1, 6))} ${item.text}\n`
    : `- [ ] ${item.id} ${item.text}`.trimEnd()));
  return [
    '> [!NOTE]',
    '> Draft pull request prepared by the `Spec Kit orchestrate` workflow. It tracks the implementation status',
    '> with the `Spec Kit implementation` check run and documents progress in comments.',
    '',
    `Closes #${twinNumber}`,
    '',
    renderSpecLine(context, folder),
    '',
    `## Tasks (${tasks.count})`,
    ...(lines.length > 0 ? lines : ['', '_No tasks were found in `tasks.md`._']),
    '',
  ].join('\n').replace(/\n{3,}/g, '\n\n');
}

export function renderStartComment({ twinNumber, folder, taskCount }) {
  return [
    START_COMMENT_MARKER,
    `Implementation workspace prepared for #${twinNumber} (\`specs/${folder}\`): ${taskCount} task(s) queued.`,
    '',
    'The `Spec Kit implement` workflow implements them one at a time, in order. Each task is verified, committed, and reported here.',
  ].join('\n');
}

export function checkRunOutput(taskCount) {
  return {
    title: `0 of ${taskCount} tasks implemented`,
    summary: 'The implementation workspace is prepared. Tasks are implemented one at a time, in order.',
  };
}

export function progressOutput({ done, total }, note) {
  return {
    title: `${done} of ${total} tasks implemented`,
    summary: note ?? 'Waiting for the next task run.',
  };
}

// Decides what the latest implementation PR of a flagged twin means for the current flag.
// `pulls` is newest first; `flaggedAt` is when the current implement flag was set.
export function decideLifecycle({ pulls, flaggedAt }) {
  const latest = pulls[0];
  if (!latest) return { state: 'ready', reset: false };
  if (latest.state === 'open') return { state: 'in-progress', pull: latest };
  const closedAfterFlag = !flaggedAt || Date.parse(latest.closed_at) >= Date.parse(flaggedAt);
  if (latest.merged_at) return closedAfterFlag ? { state: 'merged', pull: latest } : { state: 'ready', reset: true };
  return closedAfterFlag ? { state: 'fallback', pull: latest } : { state: 'ready', reset: true };
}
