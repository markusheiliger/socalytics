import { artifactUrl, renderSpecLine, specDir } from './speckit-prepare-core.mjs';

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
// The per-spec implementation chain: every run of this workflow decides what comes next for one spec (a phase, the
// convergence, a conflict resolution, or the merge), runs it, and starts the next run. A phase runs in the reusable
// phase workflow, which runs the phase's tasks in order (consecutive `[P]` tasks in parallel) in the reusable task
// workflow.
export const IMPLEMENT_WORKFLOW_FILE = 'speckit-implement-spec.yml';
export const IMPLEMENT_WORKFLOW_NAMES = Object.freeze({
  spec: 'Spec Kit implement spec',
  phase: 'Spec Kit implement phase',
  task: 'Spec Kit implement task',
});
// Entries of the task order per phase run: the phase workflow's tasks job is a matrix with one job per entry (a task, or
// consecutive `[P]` tasks in parallel), and GitHub runs at most 256 matrix jobs per run. A longer phase continues in
// the next run.
export const MAX_MATRIX_JOBS_PER_RUN = 256;
// Default number of specs the scheduler keeps implementing at the same time (repository variable SPECKIT_MAX_ACTIVE_SPECS).
export const DEFAULT_MAX_ACTIVE_SPECS = 3;
// Every reported outcome of an attempt carries this marker in its pull request comment; the comments are the attempt
// ledger, because check runs belong to one commit and parallel siblings move the head.
const ATTEMPT_MARKER_PATTERN = /<!-- speckit-implement:attempt (\{[^\n]*?\}) -->/;
export const ORCHESTRATE_WORKFLOW_FILE = 'speckit-orchestrate.yml';
export const PREPARE_WORKFLOW_FILE = 'speckit-prepare.yml';
// Check run external IDs tell the implementation chain what a check run means.
export const CHECK_PROGRESS = 'speckit:progress';
export const CHECK_ATTEMPT = 'speckit:attempt';
export const CHECK_LIMIT = 'speckit:limit';
export const CHECK_DONE = 'speckit:done';
export const CHECK_MERGE = 'speckit:merge';
export const CHECK_CONFLICT = 'speckit:conflict';
// Merging the base branch before a phase conflicted on this head; a conflict resolution comes before the phase.
export const CHECK_SYNC_CONFLICT = 'speckit:sync-conflict';
// Conflict resolutions before a phase are best effort: after this many failed attempts, or one that needs a person,
// the implementation continues on its current base and the merge resolves the conflicts.
export const MAX_SYNC_RESOLVE_ATTEMPTS = 2;
// A diagnosis of a stopped implementation is running, or its findings wait for a person's decision.
export const CHECK_DIAGNOSING = 'speckit:diagnosing';
export const CHECK_DIAGNOSED = 'speckit:diagnosed';
// A diagnosis that has not reported after this long belongs to a cancelled or crashed run.
export const DIAGNOSIS_STALE_MS = 60 * 60 * 1000;
// Automatic diagnoses per head; further diagnoses need a person's `/speckit diagnose`.
export const MAX_AUTO_DIAGNOSES = 2;
export const DIAGNOSE_WORKFLOW_FILE = 'speckit-diagnose.lock.yml';
export const AMEND_BRANCH_PREFIX = 'speckit-amend/';
export const DIAGNOSIS_COMMENT_MARKER = '<!-- speckit-implement:diagnosis -->';
// Amendment pull requests: the check that tracks consistency, the markers of a rework and of a presentation (both
// end the window of feedback that is already handled), and the status block in the description.
export const AMEND_CHECK_NAME = 'Spec Kit amendment';
export const CHECK_AMEND_CHECKING = 'speckit:amend-checking';
export const CHECK_AMEND_CONSISTENT = 'speckit:amend-consistent';
export const CHECK_AMEND_INCONSISTENT = 'speckit:amend-inconsistent';
export const AMEND_REWORK_MARKER = '<!-- speckit-amend:rework -->';
export const AMEND_PRESENTED_MARKER = '<!-- speckit-amend:presented -->';
export const AMEND_STATUS_START = '<!-- speckit-amend:status -->';
export const AMEND_STATUS_END = '<!-- /speckit-amend:status -->';
// Correction rounds (analyze, then fix) continue while they make progress (a previous finding gets resolved); an
// amendment is handed to a person as still inconsistent after MAX_STALLED_ROUNDS rounds without progress, or after
// MAX_CORRECTION_ROUNDS rounds in total, which bounds the agent runs.
export const MAX_STALLED_ROUNDS = 3;
export const MAX_CORRECTION_ROUNDS = 10;
export const ANALYZE_WORKFLOW_FILE = 'speckit-analyze.lock.yml';
const GUIDANCE_PATTERN = /<!-- speckit-implement:guidance ([A-Za-z0-9+/=]+) -->/;
// Marks that the base branch at a commit was not merged before a phase (refused, or its conflicts need a person), so
// the next phases do not try that commit again; a newer commit of the base branch is tried again.
const SYNC_SKIPPED_PATTERN = /<!-- speckit-implement:sync-skipped ([0-9a-f]{7,40}) -->/;
export const renderSyncSkippedMarker = (sha) => `<!-- speckit-implement:sync-skipped ${sha} -->`;

// The base branch commits that were not merged before a phase, from the workflow's comments.
export function syncSkippedShas(comments) {
  return new Set(comments
    .filter((comment) => comment.user?.login === BOT_LOGIN)
    .map((comment) => String(comment.body ?? '').match(SYNC_SKIPPED_PATTERN)?.[1])
    .filter(Boolean));
}
// Paths the agent must never change; specs/<folder>/tasks.md may only receive the target task's tick.
export const PROTECTED_PREFIXES = ['.github/', '.specify/', 'specs/'];
// The solution's environment actions. Agents may change them, but only in a standalone environment spec, whose
// pull request always waits for a person's review.
export const ENVIRONMENT_PREFIXES = ['.github/actions/environment-setup/', '.github/actions/environment-verify/'];

export const isEnvironmentPath = (file) => ENVIRONMENT_PREFIXES.some((prefix) => file.startsWith(prefix));
export const isProtectedPath = (file) => !isEnvironmentPath(file) && PROTECTED_PREFIXES.some((prefix) => file.startsWith(prefix));

// Reasons why a branch mixes changes to the environment actions with other changes. `branchPaths` are all paths
// the implementation branch changes compared to its base branch.
export function environmentExclusivityReasons({ folder, branchPaths }) {
  if (!branchPaths.some(isEnvironmentPath)) return [];
  const tasksPaths = [tasksPathOf(folder), tasksPathOf(folder, { archived: true })];
  const others = branchPaths.filter((file) => !isEnvironmentPath(file) && !tasksPaths.includes(file));
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

// tasks.md of a spec: specs/<folder>/tasks.md, or specs/archive/<folder>/tasks.md once the implementation archived it
// (on its branch, right before the merge).
export function tasksPathOf(folder, { archived = false } = {}) {
  return `${specDir(folder, { archived })}/tasks.md`;
}

// Reads tasks.md of a spec at a ref, wherever the spec folder is; null when it is in neither place.
export async function readSpecTasks(client, folder, ref) {
  const active = await client.getFileContent(tasksPathOf(folder), ref);
  if (active !== null) return { tasksMarkdown: active, archived: false };
  const archived = await client.getFileContent(tasksPathOf(folder, { archived: true }), ref);
  return { tasksMarkdown: archived, archived: archived !== null };
}

// Points the spec links of a twin or implementation pull request body to the spec folder on `branch` (archived or
// not): the `**Spec**` line and artifact links, including links to the implementation branch, which is deleted after
// the merge. Applying it again changes nothing.
export function pointSpecLinks(body, folder, { branch, archived }) {
  const escaped = folder.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const target = specDir(folder, { archived });
  return String(body ?? '')
    .replace(new RegExp(`/(tree|blob)/[^\\s()]+?/specs/(?:archive/)?${escaped}(?=[/)\\s#]|$)`, 'g'), (match, kind) => `/${kind}/${branch}/${target}`)
    .replace(new RegExp(`\`specs/(?:archive/)?${escaped}\``, 'g'), `\`${target}\``);
}

// Two different branches matter. The repository's default branch (SPECKIT_DEFAULT_BRANCH) holds the trusted tooling:
// workflows run and are dispatched there, and its specs are mirrored as twins and may be requested for implementation.
// The base branch of an implementation is the branch its implementation branch was created from and merges back into;
// the implementation pull request's base records it. Merges, comparisons, and links of an implementation use the base
// branch, never the default branch.
export function defaultBranchOf(env) {
  const branch = String(env?.SPECKIT_DEFAULT_BRANCH ?? '').trim();
  if (!branch) throw new Error('Expected SPECKIT_DEFAULT_BRANCH (the repository default branch).');
  return branch;
}

export function baseBranchOf(pull) {
  const branch = String(pull?.base?.ref ?? '').trim();
  if (!branch) throw new Error(`Implementation pull request #${pull?.number ?? '?'} has no base branch.`);
  return branch;
}

// The base branch handed to a job that works with git (SPECKIT_BASE_BRANCH, from the decide job).
export function baseBranchFromEnv(env) {
  const branch = String(env?.SPECKIT_BASE_BRANCH ?? '').trim();
  if (!branch) throw new Error('Expected SPECKIT_BASE_BRANCH (the base branch of the implementation pull request).');
  return branch;
}

// The base branch of an implementation pull request when it has commits the implementation branch lacks, else null.
// Only used to suggest /speckit sync, so errors count as not behind.
export async function behindBaseOf(client, pull, folder) {
  try {
    const base = baseBranchOf(pull);
    return (await client.aheadBy(implementationBranch(folder), base)) > 0 ? base : null;
  } catch {
    return null;
  }
}

export function amendmentBranch(folder) {
  return `${AMEND_BRANCH_PREFIX}${folder}`;
}

// The spec folder of an implementation or amendment branch, or null.
export function folderOfBranch(branch) {
  const value = String(branch ?? '');
  for (const prefix of [AMEND_BRANCH_PREFIX, BRANCH_PREFIX]) {
    if (value.startsWith(prefix) && value.length > prefix.length) return value.slice(prefix.length);
  }
  return null;
}

// Creation time of the latest commit on the implementation branch that a person (not the workflow) made.
export function latestHumanCommit(commits) {
  return commits
    .filter((commit) => commit.author?.login !== BOT_LOGIN && commit.committer?.login !== BOT_LOGIN)
    .map((commit) => commit.commit?.committer?.date ?? commit.commit?.author?.date)
    .filter(Boolean)
    .sort((a, b) => Date.parse(b) - Date.parse(a))[0];
}

// Guidance a person gave with `/speckit resume <guidance>`, carried in the workflow's resume comment.
export function renderGuidanceMarker(text) {
  return `<!-- speckit-implement:guidance ${Buffer.from(JSON.stringify({ text: String(text) }), 'utf8').toString('base64')} -->`;
}

export function parseGuidance(body) {
  const match = String(body ?? '').match(GUIDANCE_PATTERN);
  if (!match) return null;
  try {
    const text = JSON.parse(Buffer.from(match[1], 'base64').toString('utf8')).text;
    return typeof text === 'string' && text.trim() ? text.trim() : null;
  } catch {
    return null;
  }
}

// The comment that starts a new attempt window; it persists the resume, so a replaced run cannot lose
// it. Guidance applies to the stopped task, convergence, or conflict resolution until it succeeds.
export function renderResumeComment(lead, guidance = '') {
  const value = String(guidance ?? '').trim();
  return [
    RESUME_COMMENT_MARKER,
    ...(value ? [renderGuidanceMarker(value)] : []),
    lead,
    ...(value ? ['', 'Guidance for the next attempts:', '', ...neutralizeMarkers(value).split('\n').map((line) => `> ${line}`)] : []),
  ].join('\n');
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

// Tasks in file order with their `[P]` marker, the heading (any level) they are listed under, and their phase (the
// `##` heading; `###` sub-headings stay inside it).
function tasksWithSections(tasksMarkdown) {
  const tasks = [];
  let section = '';
  let sectionLine = 0;
  let phase = '';
  for (const [number, line] of String(tasksMarkdown ?? '').split(/\r?\n/).entries()) {
    const heading = line.match(HEADING_PATTERN);
    if (heading) {
      section = line;
      sectionLine = number + 1;
      if (heading[1] === '##') phase = line;
      continue;
    }
    const task = line.match(TASK_PATTERN);
    if (!task) continue;
    const tags = task[3].match(/^(?:\[[^\]]+\]\s*)+/)?.[0] ?? '';
    tasks.push({ id: task[2], text: task[3].trim(), done: task[1] !== ' ', parallel: /\[P\]/.test(tags), section, sectionLine, phase });
  }
  return tasks;
}

// Orders the unticked tasks of one phase: consecutive `[P]` tasks under one heading (compared by its text) form one
// entry and run in parallel, every other task is an entry of its own. Returns the entries in task order (at most
// `maxEntries`) and how many tasks they cover.
function orderTasks(open, maxEntries) {
  const order = [];
  let index = 0;
  while (index < open.length && order.length < maxEntries) {
    const first = open[index];
    const entry = [first];
    index += 1;
    while (first.parallel && index < open.length && open[index].parallel && open[index].section === first.section) {
      entry.push(open[index]);
      index += 1;
    }
    order.push(entry);
  }
  return { order, covered: index };
}

const headingText = (line) => line.replace(/^#+\s+/, '').trim() || null;

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

// The task order of the phase of the first unticked task: consecutive unticked `[P]` tasks under one heading form one
// entry (they run in parallel), every other unticked task is an entry of its own. At most `maxEntries` entries are
// planned; `phaseEnd` tells whether they finish the phase, `lastIsParallel` whether the phase ends with `[P]` tasks,
// whose combined result then gets the phase's full verification after it landed.
export function planTasks(tasksMarkdown, { maxEntries = MAX_MATRIX_JOBS_PER_RUN } = {}) {
  const tasks = tasksWithSections(tasksMarkdown);
  const start = tasks.findIndex((task) => !task.done);
  if (start < 0) return { order: [], phaseEnd: false, lastIsParallel: false, phase: null };
  const phase = tasks[start].phase;
  const open = [];
  for (const task of tasks.slice(start)) {
    if (task.phase !== phase) break;
    if (!task.done) open.push(task);
  }
  const ordered = orderTasks(open, maxEntries);
  const order = ordered.order.map((entry) => entry.map((task) => task.id));
  const phaseEnd = ordered.covered >= open.length;
  return { order, phaseEnd, lastIsParallel: phaseEnd && order.at(-1).length > 1, phase: headingText(phase) };
}

// Every phase of tasks.md with the order its unticked tasks will run in, exactly as planTasks orders them, for
// reviewing `[P]` groups (speckit.gha.refine). Warnings name headings that do not separate groups: a heading deeper
// than `####`, and a heading whose text repeats, so the `[P]` tasks under both copies form one group.
export function describeParallelGroups(tasksMarkdown) {
  const tasks = tasksWithSections(tasksMarkdown);
  const phases = [];
  for (const task of tasks) {
    if (phases.at(-1)?.key !== task.phase) phases.push({ key: task.phase, tasks: [] });
    phases.at(-1).tasks.push(task);
  }
  const warnings = [];
  for (const [number, line] of String(tasksMarkdown ?? '').split(/\r?\n/).entries()) {
    if (/^#{5,}\s+\S/.test(line)) warnings.push(`line ${number + 1}: \`${line.trim()}\` is deeper than \`####\`, so it does not end a group`);
  }
  const result = phases.map(({ key, tasks: list }) => {
    const open = list.filter((task) => !task.done);
    const order = orderTasks(open, Number.POSITIVE_INFINITY).order.map((entry) => {
      const lines = [...new Set(entry.map((task) => task.sectionLine))];
      if (lines.length > 1) {
        warnings.push(`${entry.map((task) => task.id).join(', ')} form one group because the heading \`${entry[0].section.trim()}\` repeats (lines ${lines.join(', ')}); give each group its own heading text`);
      }
      return { parallel: entry.length > 1, heading: entry[0].section ? headingText(entry[0].section) : null, tasks: entry.map((task) => task.id) };
    });
    return { phase: headingText(key), done: list.length - open.length, open: open.length, order };
  });
  return { phases: result, warnings };
}

// Parses SPECKIT_MAX_PARALLEL_TASKS; anything but a positive integer falls back to the default.
export function parseMaxParallel(value) {
  const number = Number(String(value ?? '').trim());
  return Number.isInteger(number) && number > 0 ? number : DEFAULT_MAX_PARALLEL_TASKS;
}

// Parses SPECKIT_MAX_ACTIVE_SPECS; anything but a positive integer falls back to the default.
export function parseMaxActiveSpecs(value) {
  const number = Number(String(value ?? '').trim());
  return Number.isInteger(number) && number > 0 ? number : DEFAULT_MAX_ACTIVE_SPECS;
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

// Title of a run or job summary of the implementation workflows: the workflow's name, the twin, and what it works on,
// for example "Spec Kit implement spec #35 · Recording Lineage and Upload", "Spec Kit implement phase #35 · Phase 3: …",
// or "Spec Kit implement task #35 · T013 [P] [US1] Create …". Job names stay shorter (see taskJobName).
export function renderImplementTitle(workflow, twin, subject = '') {
  return `${IMPLEMENT_WORKFLOW_NAMES[workflow]} #${twin}${subject ? ` · ${subject}` : ''}`;
}

// Run name of the implementation chain of a twin, with the spec title.
export function renderImplementRunName(twin, specTitle = '') {
  return renderImplementTitle('spec', twin, specTitle);
}

export function isImplementRunOf(title, twin) {
  const name = renderImplementRunName(twin);
  return title === name || String(title ?? '').startsWith(`${name} `);
}

// Diagnose and analyze runs are named "<workflow> #<twin> · <what the run does>" (run-name in their workflow sources).
const TWIN_RUN_NAMES = new Map([[DIAGNOSE_WORKFLOW_FILE, 'Spec Kit diagnose'], [ANALYZE_WORKFLOW_FILE, 'Spec Kit analyze']]);

export function isTwinRunOf(workflowFile, title, twin) {
  const name = `${TWIN_RUN_NAMES.get(workflowFile)} #${twin}`;
  return title === name || String(title ?? '').startsWith(`${name} `);
}

// The spec title of an implementation pull request, whose title is "Implement: <spec title>" (renderPullRequestTitle).
export function specTitleOfPull(pull) {
  return String(pull?.title ?? '').replace(/^Implement:\s*/, '').trim();
}

// What the phase workflow works on: the phase heading of tasks.md exactly as written (planTasks already removed the
// "## "); a tasks.md without phase headings has a single unnamed phase.
export function phaseSubject(phase) {
  return String(phase ?? '').trim() || 'Tasks';
}

// What one call of the task workflow works on: a single task by its ID and generated title (summarizeTask), `[P]`
// tasks by their IDs, or the convergence or a conflict resolution.
export function taskSubject(kind, entries) {
  if (kind === 'converge') return 'Convergence';
  if (kind === 'resolve') return 'Conflict resolution';
  if (entries.length !== 1) return taskJobName(entries);
  return `${entries[0].task} ${entries[0].summary ?? ''}`.trim();
}

// Name of the job that implements one entry of the task order in the run graph, which joins the names of nested jobs
// ("Phase 3: … / T013 / Implement T013") and truncates long ones, so it shows only the task IDs; the job summaries
// carry the full titles.
export function taskJobName(entries) {
  const ids = entries.map((entry) => entry.task).filter(Boolean);
  const tasks = ids.length <= 3 ? ids.join(', ') : `${ids[0]} … ${ids.at(-1)}`;
  return `${tasks}${ids.length > 1 ? ' in parallel' : ''}${ids.length > 3 ? ` (${ids.length} tasks)` : ''}`;
}

// Starts (or queues) the next run of the implementation chain of a twin. GitHub keeps one pending run per spec, and a
// replaced pending run is harmless, because every run decides from the state on GitHub. The run is named after the
// spec title, which comes from the implementation pull request unless `title` is given.
export async function dispatchImplementation(client, env, { twin, pull, title }) {
  let specTitle = title;
  if (!specTitle) {
    try {
      specTitle = specTitleOfPull(await client.getPullRequest(Number(pull)));
    } catch {
      specTitle = '';
    }
  }
  await client.dispatchWorkflow(IMPLEMENT_WORKFLOW_FILE, defaultBranchOf(env), { twin: String(twin), pull: String(pull), ...(specTitle ? { title: specTitle } : {}) });
}

// The attempt marker of an outcome comment. `kind` is what the attempt works on: task, converge, or resolve. `outcome`
// is success, failure, requeue (redone from a newer head; not a failure), or attention (a person is needed).
export function renderAttemptMarker({ kind, task = null, attempt, outcome }) {
  return `<!-- speckit-implement:attempt ${JSON.stringify({ kind, task: task || null, attempt: Number(attempt), outcome })} -->`;
}

// The attempt ledger of a pull request: the outcomes the workflow reported, oldest first. Only comments by the
// workflow count, so text in other comments cannot forge outcomes. Markers written before `kind` carry it as `step`.
export function parseAttempts(comments) {
  const attempts = [];
  for (const comment of comments) {
    if (comment.user?.login !== BOT_LOGIN) continue;
    const match = String(comment.body ?? '').match(ATTEMPT_MARKER_PATTERN);
    if (!match) continue;
    try {
      const value = JSON.parse(match[1]);
      const kind = value.kind ?? value.step;
      if (!['task', 'converge', 'resolve'].includes(kind) || !['success', 'failure', 'requeue', 'attention'].includes(value.outcome)) continue;
      attempts.push({ kind, task: value.task ?? null, attempt: Number(value.attempt) || 0, outcome: value.outcome, at: comment.created_at });
    } catch {
      // A malformed marker is ignored.
    }
  }
  return attempts.sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
}

// External ID of the check run that shows one attempt: speckit:attempt:<kind>:<task>:<attempt>.
export function attemptCheckId({ kind, task = null, attempt }) {
  return `${CHECK_ATTEMPT}:${kind}:${task ?? ''}:${attempt}`;
}

export function parseAttemptCheckId(externalId) {
  const match = String(externalId ?? '').match(/^speckit:attempt:(task|converge|resolve):(T\d{3,})?:(\d+)$/);
  return match ? { kind: match[1], task: match[2] ?? null, attempt: Number(match[3]) } : null;
}

// The verification after the agent runs the full test suite; the agent running it as well doubled the time of a task.
export const TASK_TEST_SCOPE = 'Build what you change and run only the tests that cover this task (the tests you add or change, for example through a test filter). Do not run the whole test suite: the full verification runs automatically after you, and its failures come back to the next attempt.';

// The task is named together with its spec folder: task IDs repeat across specs, and an agent that searched the whole
// repository for one picked another spec's task.
export function renderTaskPrompt(taskId, folder, { archived = false } = {}) {
  const where = folder ? ` of the spec in \`${specDir(folder, { archived })}/\` (its \`tasks.md\`); ignore the other specs` : '';
  return `/speckit-implement Implement only task ${taskId}${where}. Do not implement any other task. Do not commit and do not push. ${TASK_TEST_SCOPE}`;
}

export function renderConvergePrompt() {
  return '/speckit-converge Assess the implementation and append remaining work as new tasks if needed. Do not commit and do not push.';
}

export function renderResolvePrompt({ folder, files, base, archived = false }) {
  return [
    `The working tree is in the middle of merging the base branch \`${base}\` into the implementation branch of the spec \`${specDir(folder, { archived })}\`.`,
    `Git reported merge conflicts in these files: ${files.map((file) => `\`${file}\``).join(', ')}.`,
    'Resolve every conflict: edit only these files, remove all conflict markers, and combine both sides so that the',
    `intent of the spec and the changes from \`${base}\` are both kept. Use the spec, plan, and tasks in`,
    `\`${specDir(folder, { archived })}\` to understand the implementation side. Do not change any other file, and do not run git`,
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

// Decides how the implementation chain continues an open implementation pull request. `checks` are the
// `Spec Kit implementation` check runs of the current head; `attempts` the attempt ledger (parseAttempts);
// `windowStart` starts the current attempt window (pull request, resume, or a person's push); `done` tells whether
// the implementation was finalized for review; `resumedAt` is the latest resume.
// Order: a conflict resolution when merging the base branch before the next phase conflicted on this head (best effort,
// see MAX_SYNC_RESOLVE_ATTEMPTS) → the tasks of the phase of the next unticked task (planTasks) → converge (until a
// successful convergence is newer than the last successful task) → merge, or resolve after a merge found conflicts on
// this head. Only failures
// count against the attempt limit; all outcomes (including redos from a newer head) are capped separately so no-op
// attempts cannot loop forever. A stop that needs a person (attempt limit, attention, or a diagnosis that runs or waits
// for a decision) holds everything until a person pushes or resumes.
export function decideNext({ tasksMarkdown, checks = [], attempts = [], windowStart, done = false, resumedAt = null, now = Date.now(), maxEntries = MAX_MATRIX_JOBS_PER_RUN }) {
  const mergeChecks = checks.filter((check) => check.external_id === CHECK_MERGE);
  const stale = mergeChecks.filter((check) => check.status !== 'completed' && now - checkTime(check) >= MERGE_STALE_MS);
  if (mergeChecks.some((check) => check.status !== 'completed' && !stale.includes(check))) return { action: 'wait' };
  const latest = latestCheckRun(checks);
  if (done || (latest?.external_id === CHECK_DONE && latest.conclusion === 'success')) return { action: 'done' };
  const diagnosing = latest?.external_id === CHECK_DIAGNOSING && latest.status !== 'completed';
  const failed = (latest?.external_id === CHECK_LIMIT && latest.conclusion === 'failure') || diagnosing || latest?.external_id === CHECK_DIAGNOSED;
  // A resume comment newer than the stop counts as a resume.
  const resumed = failed && Boolean(resumedAt) && Date.parse(resumedAt) >= checkTime(latest);
  if (failed && !resumed) {
    if (diagnosing && now - checkTime(latest) < DIAGNOSIS_STALE_MS) return { action: 'diagnosing' };
    return {
      action: 'failed',
      diagnosis: {
        state: diagnosing ? 'stale' : latest.external_id === CHECK_DIAGNOSED ? 'reported' : 'none',
        check: latest,
        count: checks.filter((check) => [CHECK_DIAGNOSING, CHECK_DIAGNOSED].includes(check.external_id)).length,
      },
    };
  }

  const since = Date.parse(windowStart);
  const inWindow = attempts.filter((attempt) => Date.parse(attempt.at) >= since);
  const tally = (match) => {
    const list = inWindow.filter(match);
    return { failures: list.filter((attempt) => attempt.outcome === 'failure').length, total: list.length };
  };
  const exceeded = ({ failures, total }) => failures >= MAX_TASK_ATTEMPTS || total >= MAX_TASK_ATTEMPTS * 2;

  const plan = planTasks(tasksMarkdown, { maxEntries });
  let syncGivenUp = null;
  if (plan.order.length > 0) {
    // Merging the base branch before this phase conflicted on this head: resolve first, unless that was given up.
    const syncConflict = latestCheckRun(checks.filter((check) => check.external_id === CHECK_SYNC_CONFLICT));
    if (syncConflict) {
      const list = inWindow.filter((attempt) => attempt.kind === 'resolve' && Date.parse(attempt.at) >= checkTime(syncConflict));
      const failures = list.filter((attempt) => attempt.outcome === 'failure').length;
      const handedOver = list.some((attempt) => attempt.outcome === 'attention');
      if (!handedOver && failures < MAX_SYNC_RESOLVE_ATTEMPTS && list.length < MAX_SYNC_RESOLVE_ATTEMPTS * 2) {
        return { action: 'tasks', kind: 'resolve', sync: true, order: [[{ task: null, attempt: list.length + 1 }]], phaseEnd: false, lastIsParallel: false, stale };
      }
      syncGivenUp = syncConflict;
    }
  }
  if (plan.order.length > 0) {
    const ofTask = (id) => tally((attempt) => attempt.kind === 'task' && attempt.task === id);
    // Earlier attempts can only belong to the first entry: a run stops at the first entry that did not land.
    for (const id of plan.order[0]) {
      const count = ofTask(id);
      if (exceeded(count)) return { action: 'limit', kind: 'task', task: id, attempts: count.total, stale };
    }
    return {
      action: 'tasks',
      kind: 'task',
      order: plan.order.map((entry) => entry.map((id) => ({ task: id, attempt: ofTask(id).total + 1 }))),
      phaseEnd: plan.phaseEnd,
      lastIsParallel: plan.lastIsParallel,
      phase: plan.phase,
      stale,
      ...(syncGivenUp ? { syncGivenUp } : {}),
    };
  }

  const atOf = (attempt) => Date.parse(attempt.at);
  const lastTask = Math.max(0, ...attempts.filter((attempt) => attempt.kind === 'task' && attempt.outcome === 'success').map(atOf));
  const converged = attempts.some((attempt) => attempt.kind === 'converge' && attempt.outcome === 'success' && atOf(attempt) > lastTask);
  let kind;
  let count;
  if (!converged) {
    kind = 'converge';
    count = tally((attempt) => attempt.kind === 'converge' && atOf(attempt) > lastTask);
  } else if (latestCheckRun(checks.filter((check) => [CHECK_MERGE, CHECK_CONFLICT].includes(check.external_id)))?.external_id === CHECK_CONFLICT) {
    kind = 'resolve';
    // Only attempts at the current conflict count; earlier, already resolved conflicts do not.
    const conflictAt = checkTime(latestCheckRun(checks.filter((check) => check.external_id === CHECK_CONFLICT)));
    count = tally((attempt) => attempt.kind === 'resolve' && atOf(attempt) >= conflictAt);
  } else {
    const merges = mergeChecks.filter((check) => checkTime(check) >= since);
    const merge = { failures: merges.filter((check) => stale.includes(check) || check.conclusion === 'failure').length, total: merges.length };
    if (exceeded(merge)) return { action: 'limit', kind: 'merge', attempts: merge.total, stale };
    return { action: 'merge', kind: 'merge', attempt: merge.total + 1, stale };
  }
  if (exceeded(count)) return { action: 'limit', kind, attempts: count.total, stale };
  return { action: 'tasks', kind, order: [[{ task: null, attempt: count.total + 1 }]], phaseEnd: false, lastIsParallel: false, stale };
}
// Human-readable name of what an attempt works on, for comments and summaries.
export function kindLabel({ kind, task }) {
  if (kind === 'task') return task;
  return { converge: 'convergence', resolve: 'conflict resolution', merge: 'merge' }[kind] ?? kind;
}

// Reasons why a /speckit-converge change cannot be accepted. It may only append a convergence phase with new,
// unchecked tasks to specs/<folder>/tasks.md. `limit` is true when the append would exceed the round limit.
export function validateConvergeChange({ folder, archived = false, before, after, changedPaths }) {
  const reasons = [];
  const tasksPath = tasksPathOf(folder, { archived });
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
const GITHUB_BODY_LIMIT = 65536;
const TASKS_END_MARKER = '<!-- speckit-implement:tasks-end -->';
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

const taskLine = (task, short, ticks = false) => `- [${ticks && task.done ? 'x' : ' '}] ${task.id} ${short ? (task.text.match(/^(?:\[[^\]]+\]\s*)+/)?.[0] ?? '').trim() : summarizeTask(task.text)}`.trimEnd();

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
export function validateTaskChange({ folder, archived = false, taskId, before, after, changedPaths }) {
  const reasons = [];
  const tasksPath = tasksPathOf(folder, { archived });
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

export function renderPullRequestBody({ twinNumber, folder, tasks, context, ticks = false }) {
  const render = (short, limit = Infinity) => {
    let shown = 0;
    const lines = [];
    for (const item of tasks.items) {
      if (item.type === 'heading') {
        lines.push(`\n${'#'.repeat(Math.min(item.level + 1, 6))} ${item.text}\n`);
      } else if (shown < limit) {
        lines.push(taskLine(item, short, ticks));
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
      `Short form; the full task texts are in [\`tasks.md\`](${artifactUrl({ ...context, branch: implementationBranch(folder) }, folder, 'tasks.md', { archived: Boolean(context.archived) })}).`,
      ...(lines.length > 0 ? lines : ['', '_No tasks were found in `tasks.md`._']),
      ...(hidden > 0 ? ['', `_${hidden} more task(s) are not listed because the description would be too long._`] : []),
      '',
      TASKS_END_MARKER,
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

// Replaces the task section of a pull request body (from `## Tasks (N)` to its end marker, or to the end of older
// bodies) with one rendered from tasks.md: task texts, appended and corrective tasks, and checks, so amendments and
// concurrently landed tasks show. Text before and after the section stays. When the result would exceed GitHub's
// limit, only the checks are updated in place.
export function syncPullRequestTaskList(body, { twinNumber, folder, tasksMarkdown, context }) {
  const text = String(body ?? '');
  const heading = /^## Tasks \(\d+\)$/m;
  const start = text.search(heading);
  if (start < 0) return text;
  const endMarker = text.indexOf(TASKS_END_MARKER, start);
  const suffix = endMarker < 0 ? '' : text.slice(endMarker + TASKS_END_MARKER.length).replace(/^\n+/, '');
  const fresh = renderPullRequestBody({ twinNumber, folder, tasks: extractTasks(tasksMarkdown), context, ticks: true });
  const result = `${text.slice(0, start)}${fresh.slice(fresh.search(heading))}${suffix ? `\n${suffix}` : ''}`;
  return result.length > GITHUB_BODY_LIMIT ? syncPullRequestTicks(text, tasksMarkdown) : result;
}

// The commands a person can comment on an implementation pull request. An amendment pull request needs none: merging
// it applies the amendment, closing it discards it, and a comment or review asks for a rework.
export const PR_COMMANDS = [
  { name: 'diagnose', usage: '/speckit diagnose [notes]', help: 'diagnose why the implementation stopped; notes steer the analysis or answer its questions' },
  { name: 'resume', usage: '/speckit resume [guidance]', help: 'retry what stopped with a fresh attempt count; guidance goes into the next attempt\'s prompt' },
  { name: 'sync', usage: '/speckit sync', help: 'merge the base branch into the implementation branch and retry' },
  { name: 'help', usage: '/speckit help', help: 'list these commands' },
];

// An option title as one sentence: without the trailing punctuation an agent may add, ending with a period.
const bareTitle = (text) => String(text ?? '').trim().replace(/[\s.:;,]+$/, '');
const sentence = (text) => `${bareTitle(text)}.`;
const commandEntry = (text, command) => [`- ${bareTitle(text)}:`, '', '  ```text', `  ${command}`, '  ```'];

// How a person handles an amendment pull request; shown in its description and referenced from the implementation.
export function renderAmendmentHowTo(implementationPull) {
  return [
    '**How to proceed** (people with write access):',
    '',
    `- **Merge** this pull request to apply the amendment. It merges into the implementation branch, and the implementation continues on #${implementationPull} with a fresh attempt count, starting with the next unchecked task.`,
    '- **Comment**, or **submit a review** with *Comment* or *Request changes* (line comments included), to have the amendment reworked with your feedback. It returns here, checked again, for your review.',
    `- **Close** this pull request without merging to discard the amendment; #${implementationPull} stays stopped and lists what you can do next.`,
    `- Do not change the base branch or add this pull request to a stack: an amendment only merges into its implementation branch, and merging a stack would also merge the unfinished #${implementationPull}. The automation undoes both.`,
  ];
}

// Replaces the status block at the top of an amendment pull request description (or adds it), keeping the rest.
export function setAmendmentStatus(body, lines) {
  const text = String(body ?? '');
  const block = [AMEND_STATUS_START, ...lines, AMEND_STATUS_END].join('\n');
  const start = text.indexOf(AMEND_STATUS_START);
  const end = text.indexOf(AMEND_STATUS_END);
  if (start >= 0 && end > start) return `${text.slice(0, start)}${block}${text.slice(end + AMEND_STATUS_END.length)}`;
  return `${block}\n\n${text}`;
}

// The next-step block of a comment where the implementation waits for a person: exactly one recommended next step
// (an open amendment, else the diagnosis' recommended option, else waiting for the automatic diagnosis, else a
// diagnosis), then every other valid option, collapsed. `options` are a diagnosis' options ({ title, command,
// recommended }), `amendmentPull` an open amendment pull request, `behindBase` the name of the base branch when it has
// commits the implementation branch lacks (otherwise null), and `autoDiagnosis` whether a diagnosis starts
// automatically.
export function renderNextSteps({ options = null, amendmentPull = null, behindBase = null, autoDiagnosis = false, diagnosed = false } = {}) {
  const all = options ?? [];
  let recommended;
  let others = all;
  if (amendmentPull) {
    recommended = {
      title: `Review the amendment #${amendmentPull} and merge it; the implementation then continues by itself with a fresh attempt count. Comment or request changes there to have it reworked`,
      command: null,
    };
    // A diagnosis recommends its own amendment as an option without a command; the amendment is that option.
    others = all.filter((option) => !(option.recommended && !option.command));
  } else {
    const pick = all.find((option) => option.recommended) ?? null;
    if (pick) {
      recommended = pick;
      others = all.filter((option) => option !== pick);
    } else if (autoDiagnosis) {
      recommended = { title: 'Wait for the diagnosis: it starts automatically and posts its recommendation here', command: null };
    } else {
      recommended = {
        title: diagnosed ? 'Diagnose again, with notes on what to try instead' : 'Diagnose why the implementation stopped (notes steer the analysis)',
        command: '/speckit diagnose [notes]',
      };
    }
  }

  const lines = [`**Recommended next step**: ${sentence(recommended.title)}`];
  if (recommended.command) lines.push('', 'Comment this on the pull request (people with write access):', '', '```text', recommended.command, '```');

  const entries = others.map((option) => (option.command ? commandEntry(option.title, option.command) : [`- ${sentence(option.title)}`]));
  // Commands the recommendation or an option already suggests are not repeated.
  const used = new Set([recommended, ...others].map((option) => String(option.command ?? '').split(/\s+/)[1]));
  if (!autoDiagnosis && !used.has('diagnose')) {
    entries.push(commandEntry(diagnosed ? 'Diagnose again, optionally with your notes or answers' : 'Diagnose why the implementation stopped (notes steer the analysis)', '/speckit diagnose [notes]'));
  }
  if (!used.has('resume')) entries.push(commandEntry('Retry with a fresh attempt count, optionally with guidance for the agent', '/speckit resume [guidance]'));
  if (behindBase && !used.has('sync')) entries.push(commandEntry(`Merge the base branch \`${behindBase}\` (for example a fix that landed there) and retry`, '/speckit sync'));
  entries.push(['- Push a fix to the implementation branch (the implementation continues automatically), or close this pull request to abandon it.']);
  lines.push('', '<details><summary>Other options</summary>', '');
  for (const entry of entries) lines.push(...entry);
  lines.push('', '</details>');
  return lines;
}

// Reasons why a proposed amendment of the spec artifacts cannot be applied, and notes a person should see. It may
// only change files in specs/<folder>/; in tasks.md it may edit, add, or remove unchecked tasks and uncheck a task
// to redo it, but never check a task or drop a completed one.
export function validateAmendment({ folder, archived = false, changedPaths, beforeTasks, afterTasks }) {
  const reasons = [];
  const notes = [];
  const prefix = `${specDir(folder, { archived })}/`;
  const outside = changedPaths.filter((file) => !file.startsWith(prefix));
  if (outside.length > 0) reasons.push(`an amendment may only change files in \`${prefix}\`, but it changes ${outside.map((file) => `\`${file}\``).join(', ')}`);
  if (changedPaths.includes(`${prefix}spec.md`)) notes.push('it changes the requirements in `spec.md`');
  if (changedPaths.includes(`${prefix}tasks.md`)) {
    const before = new Map(listTasks(beforeTasks).map((task) => [task.id, task]));
    const after = listTasks(afterTasks);
    const seen = new Set();
    for (const task of after) {
      if (seen.has(task.id)) reasons.push(`task ${task.id} appears more than once in \`tasks.md\``);
      seen.add(task.id);
      const old = before.get(task.id);
      if (task.done && !old?.done) reasons.push(`an amendment may not check ${task.id}; only implementation checks tasks`);
      if (!old) notes.push(`it adds ${task.id}`);
      else if (old.done && !task.done) notes.push(`${task.id} is unchecked and will be implemented again`);
      else if (old.text !== task.text) notes.push(`it changes ${task.id}`);
    }
    for (const [id, task] of before) {
      if (seen.has(id)) continue;
      if (task.done) reasons.push(`an amendment may not remove the completed task ${id}`);
      else notes.push(`it removes ${id}`);
    }
  }
  return { reasons, notes };
}

export function renderStartComment({ twinNumber, folder, taskCount, base }) {
  return [
    START_COMMENT_MARKER,
    `Implementation workspace prepared for #${twinNumber} (\`specs/${folder}\`): ${taskCount} task(s) queued.`,
    '',
    'The `Spec Kit implement spec` workflow implements them phase by phase, in order; `[P]` tasks of a phase run in parallel. Each task is verified, committed, and reported here.',
    `Then a convergence checks the result against the spec, and the implementation is merged into its base branch \`${base}\` once it converged and passed the full verification.`,
  ].join('\n');
}

export function checkRunOutput(taskCount) {
  return {
    title: `0 of ${taskCount} tasks implemented`,
    summary: 'The implementation workspace is prepared. Tasks are implemented phase by phase, in order.',
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
