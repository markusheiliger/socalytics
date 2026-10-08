import { artifactUrl, renderSpecLine } from './speckit-prepare-core.mjs';

export const BRANCH_PREFIX = 'speckit/';
export const CHECK_RUN_NAME = 'Spec Kit implementation';
export const START_COMMENT_MARKER = '<!-- speckit-implement:start -->';
export const RESUME_COMMENT_MARKER = '<!-- speckit-implement:resume -->';
export const DONE_COMMENT_MARKER = '<!-- speckit-implement:done -->';
export const BOT_LOGIN = 'github-actions[bot]';
export const MAX_TASK_ATTEMPTS = 3;
// Default number of `[P]` tasks of one spec that run at the same time (repository variable SPECKIT_MAX_PARALLEL_TASKS).
export const DEFAULT_MAX_PARALLEL_TASKS = 3;
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
// The solution's environment actions. Agents may change them, but only in a standalone environment spec, whose
// pull request always waits for a person's review.
export const ENVIRONMENT_PREFIXES = ['.github/actions/environment-setup/', '.github/actions/environment-verify/'];

export const isEnvironmentPath = (file) => ENVIRONMENT_PREFIXES.some((prefix) => file.startsWith(prefix));
export const isProtectedPath = (file) => !isEnvironmentPath(file) && PROTECTED_PREFIXES.some((prefix) => file.startsWith(prefix));

// Reasons why a branch mixes changes to the environment actions with other changes. `branchPaths` are all paths
// the implementation branch changes compared to the default branch.
export function environmentExclusivityReasons({ folder, branchPaths }) {
  if (!branchPaths.some(isEnvironmentPath)) return [];
  const tasksPath = `specs/${folder}/tasks.md`;
  const others = branchPaths.filter((file) => !isEnvironmentPath(file) && file !== tasksPath);
  return others.length > 0
    ? [`changes to the environment actions must come from a standalone environment spec, but the branch also changes ${others.join(', ')}`]
    : [];
}

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

// Tasks in file order with their `[P]` marker and the heading (any level) they are listed under.
function tasksWithSections(tasksMarkdown) {
  const tasks = [];
  let section = '';
  for (const line of String(tasksMarkdown ?? '').split(/\r?\n/)) {
    const heading = line.match(HEADING_PATTERN);
    if (heading) {
      section = line;
      continue;
    }
    const task = line.match(TASK_PATTERN);
    if (!task) continue;
    const tags = task[3].match(/^(?:\[[^\]]+\]\s*)+/)?.[0] ?? '';
    tasks.push({ id: task[2], text: task[3].trim(), done: task[1] !== ' ', parallel: /\[P\]/.test(tags), section });
  }
  return tasks;
}

// The tasks that may run now: the first unticked task, followed (only when it is marked `[P]`) by the next unticked
// `[P]` tasks under the same heading, up to `max`. An unticked task without `[P]` or a new heading ends the group,
// so a group never skips over unfinished sequential work.
export function nextTaskGroup(tasksMarkdown, max = 1) {
  const tasks = tasksWithSections(tasksMarkdown);
  const start = tasks.findIndex((task) => !task.done);
  if (start < 0) return [];
  const first = tasks[start];
  const group = [first];
  if (!first.parallel) return group;
  for (const task of tasks.slice(start + 1)) {
    if (group.length >= max || task.section !== first.section) break;
    if (task.done) continue;
    if (!task.parallel) break;
    group.push(task);
  }
  return group;
}

// Parses SPECKIT_MAX_PARALLEL_TASKS; anything but a positive integer falls back to the default.
export function parseMaxParallel(value) {
  const number = Number(String(value ?? '').trim());
  return Number.isInteger(number) && number > 0 ? number : DEFAULT_MAX_PARALLEL_TASKS;
}

// Checks `taskId` in a tasks.md; null when the task is missing or already checked.
export function tickTask(tasksMarkdown, taskId) {
  const lines = String(tasksMarkdown ?? '').split('\n');
  const matchOf = (line) => line.replace(/\r$/, '').match(TASK_PATTERN);
  const index = lines.findIndex((line) => matchOf(line)?.[2] === taskId);
  if (index < 0 || matchOf(lines[index])[1] !== ' ') return null;
  lines[index] = lines[index].replace('[ ]', '[x]');
  return lines.join('\n');
}

// Checks every task of a pull request body that is checked in tasks.md; concurrent lands may each miss a sibling's tick.
export function syncPullRequestTicks(body, tasksMarkdown) {
  return listTasks(tasksMarkdown).filter((task) => task.done).reduce((text, task) => tickPullRequestBody(text, task.id), String(body ?? ''));
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
// Order: next unchecked task (or group of `[P]` tasks, see nextTaskGroup) → converge (until a successful converge is
// newer than the last successful task run) → merge, or resolve after a merge found conflicts on this head. Only
// unsuccessful runs count as attempts; the total number of runs per step is capped separately so no-op runs (such
// as a parallel task that had to be redone from a newer head) cannot loop forever. While tasks of a group run,
// free slots (up to `maxParallel`) are filled with the group's other tasks; everything else waits for them.
export function decideContinuation({ tasksMarkdown, checks = [], runs = {}, windowStart, done = false, resume = false, now = Date.now(), maxParallel = 1 }) {
  const active = Object.entries(runs).flatMap(([step, list]) => list.filter((run) => run.status !== 'completed').map((run) => ({ ...run, step })));
  if (active.some((run) => run.step !== 'task')) return { action: 'wait' };
  const mergeChecks = checks.filter((check) => check.external_id === CHECK_MERGE);
  const stale = mergeChecks.filter((check) => check.status !== 'completed' && now - checkTime(check) >= MERGE_STALE_MS);
  if (mergeChecks.some((check) => check.status !== 'completed' && !stale.includes(check))) return { action: 'wait' };
  const latest = latestCheckRun(checks);
  const isDone = done || (latest?.external_id === CHECK_DONE && latest.conclusion === 'success');
  const failed = latest?.external_id === CHECK_LIMIT && latest.conclusion === 'failure';
  if (active.length > 0 && (isDone || failed)) return { action: 'wait' };
  if (isDone) return { action: 'done' };
  if (failed && !resume) return { action: 'failed' };

  const since = Date.parse(windowStart);
  const taskRuns = runs.task ?? [];
  const group = nextTaskGroup(tasksMarkdown, maxParallel);
  if (group.length > 0) {
    if (failed) return { action: 'resume', step: 'task', task: group[0].id, attempt: 1, stale };
    const busy = new Set(active.map((run) => run.task));
    // Runs outside the group (a sequential task, or a task that landed but whose run has not ended) finish first.
    if ([...busy].some((task) => !group.some((member) => member.id === task))) return { action: 'wait' };
    const ready = [];
    const redone = new Set();
    let limit = null;
    for (const task of group) {
      const inWindow = taskRuns.filter((run) => run.task === task.id && Date.parse(run.created_at) >= since);
      const attempts = inWindow.filter((run) => run.status === 'completed' && run.conclusion !== 'success').length;
      // A successful run that left the task unchecked had to be redone because a sibling landed first.
      if (inWindow.some((run) => run.status === 'completed' && run.conclusion === 'success')) redone.add(task.id);
      if (attempts >= MAX_TASK_ATTEMPTS || inWindow.length >= MAX_TASK_ATTEMPTS * 2) {
        limit ??= { task: task.id, attempts: inWindow.length };
      } else if (!busy.has(task.id)) {
        ready.push({ task: task.id, attempt: inWindow.length + 1 });
      }
    }
    // A task at its limit stops the implementation once its running siblings have finished.
    if (limit) return active.length > 0 ? { action: 'wait' } : { action: 'limit', step: 'task', ...limit, stale };
    // While a task is being redone, no new siblings start, so a slow task cannot keep losing the race to land.
    const candidates = redone.size > 0 ? ready.filter((item) => redone.has(item.task)) : ready;
    const chosen = candidates.slice(0, Math.max(0, maxParallel - active.length));
    if (chosen.length === 0) return { action: 'wait' };
    if (chosen.length === 1) return { action: 'dispatch', step: 'task', ...chosen[0], stale };
    return { action: 'dispatch-tasks', step: 'task', tasks: chosen, stale };
  }
  if (active.length > 0) return { action: 'wait' };

  let step;
  let candidates;
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

// GitHub rejects pull request bodies above 65,536 characters; this leaves room for later appended tasks.
export const MAX_PULL_BODY = 60000;
const TASK_SUMMARY_CHARS = 160;

// A short form of a task for lists: its leading tags (such as `[P] [US1]`) and its first sentence, cut on a word
// boundary to at most 160 characters. tasks.md keeps the full text.
export function summarizeTask(text) {
  const value = String(text ?? '').replace(/\s+/g, ' ').trim();
  const tags = value.match(/^(?:\[[^\]]+\]\s*)+/)?.[0] ?? '';
  const rest = value.slice(tags.length);
  const sentence = rest.match(/^(.+?[.!?])(?=\s|$)/)?.[1] ?? rest;
  let summary = sentence;
  if (summary.length > TASK_SUMMARY_CHARS) {
    const cut = summary.slice(0, TASK_SUMMARY_CHARS);
    const space = cut.lastIndexOf(' ');
    summary = `${(space > TASK_SUMMARY_CHARS / 2 ? cut.slice(0, space) : cut).replace(/[\s,;:(]+$/, '')}`;
    if ((summary.match(/`/g) ?? []).length % 2 === 1) summary += '`';
    summary += ' …';
  } else if (sentence.length < rest.length) {
    summary += ' …';
  }
  return `${tags}${summary}`.trim();
}

const taskLine = (task, short) => `- [ ] ${task.id} ${short ? (task.text.match(/^(?:\[[^\]]+\]\s*)+/)?.[0] ?? '').trim() : summarizeTask(task.text)}`.trimEnd();

// Adds appended convergence tasks to the task list of the pull request body and updates the count. Tasks are listed
// in short form; when even that would exceed the body limit, only their IDs are listed, and otherwise a note.
export function appendPullRequestTasks(body, tasks, heading) {
  const text = String(body ?? '').replace(/^## Tasks \((\d+)\)$/m, (match, count) => `## Tasks (${Number(count) + tasks.length})`);
  const render = (lines) => `${text.replace(/\s+$/, '')}\n\n### ${heading}\n\n${lines.join('\n')}\n`;
  for (const short of [false, true]) {
    const result = render(tasks.map((task) => taskLine(task, short)));
    if (result.length <= MAX_PULL_BODY) return result;
  }
  return render([`_${tasks.length} task(s) were appended; see \`tasks.md\` on the implementation branch._`]);
}

// Reasons why the agent's change for `taskId` cannot be accepted. `before` and `after` are the contents of
// specs/<folder>/tasks.md; `changedPaths` are all changed repository paths.
export function validateTaskChange({ folder, taskId, before, after, changedPaths }) {
  const reasons = [];
  const tasksPath = `specs/${folder}/tasks.md`;
  const protectedPaths = changedPaths.filter((file) => file !== tasksPath && isProtectedPath(file));
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
  const render = (short, limit = Infinity) => {
    let shown = 0;
    const lines = [];
    for (const item of tasks.items) {
      if (item.type === 'heading') {
        lines.push(`\n${'#'.repeat(Math.min(item.level + 1, 6))} ${item.text}\n`);
      } else if (shown < limit) {
        lines.push(taskLine(item, short));
        shown += 1;
      }
    }
    const hidden = tasks.count - Math.min(tasks.count, limit);
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
      '',
      `Short form; the full task texts are in [\`tasks.md\`](${artifactUrl({ ...context, branch: implementationBranch(folder) }, folder, 'tasks.md')}).`,
      ...(lines.length > 0 ? lines : ['', '_No tasks were found in `tasks.md`._']),
      ...(hidden > 0 ? ['', `_${hidden} more task(s) are not listed because the description would be too long._`] : []),
      '',
    ].join('\n').replace(/\n{3,}/g, '\n\n');
  };
  for (const short of [false, true]) {
    const body = render(short);
    if (body.length <= MAX_PULL_BODY) return body;
  }
  let limit = tasks.count;
  let body = render(true, limit);
  while (body.length > MAX_PULL_BODY && limit > 0) {
    limit = Math.floor(limit * 0.9);
    body = render(true, limit);
  }
  return body;
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
