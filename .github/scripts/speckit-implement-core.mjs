import { renderSpecLine } from './speckit-prepare-core.mjs';

export const BRANCH_PREFIX = 'speckit/';
export const CHECK_RUN_NAME = 'Spec Kit implementation';
export const START_COMMENT_MARKER = '<!-- speckit-implement:start -->';
export const RESUME_COMMENT_MARKER = '<!-- speckit-implement:resume -->';
export const DONE_COMMENT_MARKER = '<!-- speckit-implement:done -->';
export const BOT_LOGIN = 'github-actions[bot]';
export const MAX_TASK_ATTEMPTS = 3;
export const MAX_CONVERGE_ROUNDS = 3;
// A merge check run still in progress after this long belongs to a cancelled or crashed run.
export const MERGE_STALE_MS = 2 * 60 * 60 * 1000;
// Worker workflows and their run names. Merges run as jobs of the orchestrator and are tracked by check runs.
export const STEPS = {
  task: { file: 'speckit-implement.yml', prefix: 'Spec Kit implement' },
  converge: { file: 'speckit-converge.yml', prefix: 'Spec Kit converge' },
  resolve: { file: 'speckit-resolve.yml', prefix: 'Spec Kit resolve' },
};
export const ORCHESTRATE_WORKFLOW_FILE = 'speckit-orchestrate.yml';
export const PREPARE_WORKFLOW_FILE = 'speckit-prepare.yml';
// Check run external IDs tell the orchestrator what a check run means.
export const CHECK_PROGRESS = 'speckit:progress';
export const CHECK_ATTEMPT = 'speckit:attempt';
export const CHECK_LIMIT = 'speckit:limit';
export const CHECK_DONE = 'speckit:done';
export const CHECK_MERGE = 'speckit:merge';
export const CHECK_CONFLICT = 'speckit:conflict';
// Paths the agent must never change; specs/<folder>/tasks.md may only receive the target task's tick.
export const PROTECTED_PREFIXES = ['.github/', '.specify/', 'specs/'];

const HEADING_PATTERN = /^(#{2,4})\s+(.+?)\s*$/;
const TASK_PATTERN = /^\s*[-*] \[( |x|X)\]\s+(T\d{3,})\b\s*(.*)$/;
const CONVERGENCE_HEADING_PATTERN = /^##\s+Phase\s+\d+\s*:\s*Convergence\b/i;

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

// Run names of the worker workflows: "Spec Kit implement #<twin> T001 attempt 1", "Spec Kit converge #<twin> attempt 1".
export function renderStepRunName({ step, twin, task, attempt }) {
  return `${STEPS[step].prefix} #${twin} ${step === 'task' ? `${task} ` : ''}attempt ${attempt}`;
}

export function parseStepRunName(step, title) {
  const pattern = step === 'task'
    ? new RegExp(`^${STEPS.task.prefix} #(\\d+) (T\\d{3,}) attempt (\\d+)$`)
    : new RegExp(`^${STEPS[step].prefix} #(\\d+)() attempt (\\d+)$`);
  const match = String(title ?? '').match(pattern);
  return match ? { step, twin: Number(match[1]), task: match[2] || null, attempt: Number(match[3]) } : null;
}

export function renderTaskPrompt(taskId) {
  return `/speckit-implement Implement only task ${taskId}. Do not implement any other task. Do not commit and do not push.`;
}

export function renderConvergePrompt() {
  return '/speckit-converge Assess the implementation and append remaining work as new tasks if needed. Do not commit and do not push.';
}

export function renderResolvePrompt({ folder, files }) {
  return [
    `The working tree is in the middle of merging the default branch into the implementation branch of the spec \`specs/${folder}\`.`,
    `Git reported merge conflicts in these files: ${files.map((file) => `\`${file}\``).join(', ')}.`,
    'Resolve every conflict: edit only these files, remove all conflict markers, and combine both sides so that the',
    'intent of the spec and the changes from the default branch are both kept. Use the spec, plan, and tasks in',
    `\`specs/${folder}\` to understand the implementation side. Do not change any other file, and do not run git`,
    'commit, merge, checkout, reset, restore, stash, rebase, or push.',
  ].join(' ');
}

// Number of convergence phases that /speckit-converge appended to a tasks.md.
export function convergenceRounds(tasksMarkdown) {
  return String(tasksMarkdown ?? '').split(/\r?\n/).filter((line) => CONVERGENCE_HEADING_PATTERN.test(line)).length;
}

export function hasConflictMarkers(text) {
  return /^(<{7}|>{7})( |$)/m.test(String(text ?? '')) || /^={7}$/m.test(String(text ?? ''));
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

const checkTime = (check) => Date.parse(check.started_at ?? check.created_at ?? 0);
const latestSuccess = (runs) => Math.max(0, ...runs.filter((run) => run.status === 'completed' && run.conclusion === 'success').map((run) => Date.parse(run.created_at)));

// Decides how the orchestrator continues an open implementation pull request.
// `runs` maps each worker step (task, converge, resolve) to that twin's runs since the pull request was opened
// ({ task, status, conclusion, created_at }); `checks` are the `Spec Kit implementation` check runs of the current
// head; `windowStart` starts the current attempt window (pull request, manual resume, or a person's push); `done`
// tells whether the implementation was finalized for review; `resume` whether a person asked to resume this twin.
// Order: next unchecked task → converge (until a successful converge is newer than the last successful task run)
// → merge, or resolve after a merge found conflicts on this head. Only unsuccessful runs count as attempts;
// the total number of runs per step is capped separately so no-op runs cannot loop forever.
export function decideContinuation({ tasksMarkdown, checks = [], runs = {}, windowStart, done = false, resume = false, now = Date.now() }) {
  const all = Object.values(runs).flat();
  if (all.some((run) => run.status !== 'completed')) return { action: 'wait' };
  const mergeChecks = checks.filter((check) => check.external_id === CHECK_MERGE);
  const stale = mergeChecks.filter((check) => check.status !== 'completed' && now - checkTime(check) >= MERGE_STALE_MS);
  if (mergeChecks.some((check) => check.status !== 'completed' && !stale.includes(check))) return { action: 'wait' };
  const latest = latestCheckRun(checks);
  if (done || (latest?.external_id === CHECK_DONE && latest.conclusion === 'success')) return { action: 'done' };
  const failed = latest?.external_id === CHECK_LIMIT && latest.conclusion === 'failure';
  if (failed && !resume) return { action: 'failed' };

  const since = Date.parse(windowStart);
  const taskRuns = runs.task ?? [];
  const next = nextTask(tasksMarkdown);
  let step;
  let candidates;
  if (next) {
    step = { step: 'task', task: next.id };
    candidates = taskRuns.filter((run) => run.task === next.id);
  } else {
    const lastTask = latestSuccess(taskRuns);
    const converged = (runs.converge ?? []).length > 0 && latestSuccess(runs.converge) > lastTask;
    if (!converged) {
      step = { step: 'converge' };
      candidates = (runs.converge ?? []).filter((run) => Date.parse(run.created_at) > lastTask);
    } else if (latestCheckRun(checks.filter((check) => [CHECK_MERGE, CHECK_CONFLICT].includes(check.external_id)))?.external_id === CHECK_CONFLICT) {
      step = { step: 'resolve' };
      // Only attempts at the current conflict count; earlier, already resolved conflicts do not.
      const conflictAt = checkTime(latestCheckRun(checks.filter((check) => check.external_id === CHECK_CONFLICT)));
      candidates = (runs.resolve ?? []).filter((run) => Date.parse(run.created_at) >= conflictAt);
    } else {
      step = { step: 'merge' };
      candidates = null;
    }
  }
  if (failed) return { action: 'resume', ...step, attempt: 1, stale };

  if (step.step === 'merge') {
    const inWindow = mergeChecks.filter((check) => checkTime(check) >= since);
    const attempts = inWindow.filter((check) => stale.includes(check) || check.conclusion === 'failure').length;
    if (attempts >= MAX_TASK_ATTEMPTS || inWindow.length >= MAX_TASK_ATTEMPTS * 2) return { action: 'limit', ...step, attempts: inWindow.length, stale };
    return { action: 'merge', ...step, attempt: inWindow.length + 1, stale };
  }
  const inWindow = candidates.filter((run) => Date.parse(run.created_at) >= since);
  const attempts = inWindow.filter((run) => run.conclusion !== 'success').length;
  if (attempts >= MAX_TASK_ATTEMPTS || inWindow.length >= MAX_TASK_ATTEMPTS * 2) return { action: 'limit', ...step, attempts: inWindow.length, stale };
  return { action: 'dispatch', ...step, attempt: inWindow.length + 1, stale };
}

// Human-readable name of a step for comments and summaries.
export function stepLabel({ step, task }) {
  if (step === 'task') return task;
  return { converge: 'convergence', resolve: 'conflict resolution', merge: 'merge' }[step] ?? step;
}

// Reasons why a /speckit-converge change cannot be accepted. It may only append a convergence phase with new,
// unchecked tasks to specs/<folder>/tasks.md. `limit` is true when the append would exceed the round limit.
export function validateConvergeChange({ folder, before, after, changedPaths }) {
  const reasons = [];
  const tasksPath = `specs/${folder}/tasks.md`;
  const others = changedPaths.filter((file) => file !== tasksPath);
  if (others.length > 0) reasons.push(`convergence may only change \`${tasksPath}\`, but it changed ${others.join(', ')}`);
  const normalize = (text) => String(text ?? '').replaceAll('\r\n', '\n').replace(/\n+$/, '');
  const base = normalize(before);
  const next = normalize(after);
  if (!next.startsWith(base) || (next.length > base.length && next[base.length] !== '\n')) {
    reasons.push(`convergence may only append to \`${tasksPath}\`, not change existing lines`);
    return { reasons, appended: [], limit: false };
  }
  const appendedText = next.slice(base.length);
  const appended = listTasks(appendedText);
  const known = new Set(listTasks(base).map((task) => task.id));
  const maxId = Math.max(0, ...[...known].map((id) => Number(id.slice(1))));
  if (appendedText.trim() && convergenceRounds(appendedText) !== 1) reasons.push('convergence must append exactly one `## Phase N: Convergence` section');
  if (appendedText.trim() && appended.length === 0) reasons.push('the appended convergence section contains no tasks');
  for (const task of appended) {
    if (known.has(task.id) || Number(task.id.slice(1)) <= maxId) reasons.push(`appended task ${task.id} does not have a new ID`);
    if (task.done) reasons.push(`appended task ${task.id} is already checked`);
    known.add(task.id);
  }
  const limit = appended.length > 0 && convergenceRounds(base) >= MAX_CONVERGE_ROUNDS;
  if (limit) reasons.push(`convergence still found gaps after ${MAX_CONVERGE_ROUNDS} rounds`);
  return { reasons, appended, limit };
}

// Adds appended convergence tasks to the task list of the pull request body and updates the count.
export function appendPullRequestTasks(body, tasks, heading) {
  const lines = tasks.map((task) => `- [ ] ${task.id} ${task.text}`.trimEnd());
  const text = String(body ?? '').replace(/^## Tasks \((\d+)\)$/m, (match, count) => `## Tasks (${Number(count) + tasks.length})`);
  return `${text.replace(/\s+$/, '')}\n\n### ${heading}\n\n${lines.join('\n')}\n`;
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
    'Then `Spec Kit converge` checks the result against the spec, and the implementation is merged into the default branch once it converged and passed the full verification.',
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
