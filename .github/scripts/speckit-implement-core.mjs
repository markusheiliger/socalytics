import { artifactUrl, renderSpecLine, specDir } from './speckit-prepare-core.mjs';

export const BRANCH_PREFIX = 'speckit/';
export const CHECK_RUN_NAME = 'Spec Kit implementation';
export const START_COMMENT_MARKER = '<!-- speckit-implement:start -->';
export const RESUME_COMMENT_MARKER = '<!-- speckit-implement:resume -->';
// A retry restarts what stopped like a resume, but on the branch as it is: it never merges the base branch.
export const RETRY_COMMENT_MARKER = '<!-- speckit-implement:retry -->';
export const DONE_COMMENT_MARKER = '<!-- speckit-implement:done -->';
const REPAIR_MARKER_PATTERN = /<!-- speckit-implement:repair ([A-Za-z0-9+/=]+) -->/;
export const BOT_LOGIN = 'github-actions[bot]';
export const MAX_TASK_ATTEMPTS = 2;
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
// Merging the base branch before a phase or the convergence conflicted on this head; a conflict resolution comes first.
// Before the merge, the merge's own CHECK_CONFLICT is used, whose resolution is required.
export const CHECK_SYNC_CONFLICT = 'speckit:sync-conflict';
// Conflict resolutions before a phase or the convergence are best effort: after this many failed attempts, or one that
// needs a person, the implementation continues on its current base and the merge resolves the conflicts.
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
// Fix specs (`/speckit fix`): a small spec, authored by an agent for a cause outside the stopped spec, proposed as a pull
// request from speckit-fix/<fix folder> against the default branch. Its own check run tracks the authoring on the
// implementation pull request's head, apart from the implementation's check runs.
export const FIX_BRANCH_PREFIX = 'speckit-fix/';
export const FIX_WORKFLOW_FILE = 'speckit-fix.lock.yml';
export const FIX_CHECK_NAME = 'Spec Kit fix spec';
export const CHECK_FIX_AUTHORING = 'speckit:fix-authoring';
export const CHECK_FIX_AUTHORED = 'speckit:fix-authored';
export const FIX_TITLE_PREFIX = 'Fix spec: ';
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
// GitHub's default label for "Extra attention is needed": an implementation, amendment, or fix spec pull request
// carries it while it waits for a person (stopped, a diagnosis or a fix spec outcome to decide on, or ready for review)
// and loses it once a person's action puts automation back to work or closes the pull request. It is only added together
// with a comment that recommends what to do, never while a diagnosis runs, and never on a blocked pull request.
export const ATTENTION_LABEL = 'help wanted';
// An implementation that waits for a prerequisite (a fix spec) carries BLOCKED_LABEL instead of ATTENTION_LABEL. It is
// only added together with a comment that says what it waits for and how it is unblocked (setBlocked).
export const BLOCKED_LABEL = 'blocked';
const BLOCKED_MARKER_PATTERN = /<!-- speckit-implement:blocked (\{[^\n]*?\}) -->/g;
export const renderBlockedMarker = (key) => `<!-- speckit-implement:blocked ${JSON.stringify(key)} -->`;

// Whether a workflow comment already explains the block `key` (renderBlockedMarker).
export function blockExplained(comments, key) {
  const wanted = JSON.stringify(key);
  for (const comment of comments ?? []) {
    if (comment.user?.login !== BOT_LOGIN) continue;
    for (const match of String(comment.body ?? '').matchAll(BLOCKED_MARKER_PATTERN)) {
      try {
        if (JSON.stringify(JSON.parse(match[1])) === wanted) return true;
      } catch {
        // A damaged marker explains nothing.
      }
    }
  }
  return false;
}
export const MAX_WRITER_GUIDANCE = 4000;
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
// Marks that a conflict resolution changed files that only the base branch owns, to fix a clash of the merged result.
// The land job writes it from the files it found while replaying the resolution; the final merge then waits for a
// person's review (baseOwnedChanges). Base64 JSON, so file names cannot end the comment.
const BASE_OWNED_PATTERN = /<!-- speckit-implement:base-owned ([A-Za-z0-9+/=]+) -->/g;
export const renderBaseOwnedMarker = (files) => `<!-- speckit-implement:base-owned ${Buffer.from(JSON.stringify([...files].sort())).toString('base64')} -->`;

// The files only the base branch owns that conflict resolutions changed on this pull request, from the workflow's
// comments (sorted, without duplicates).
export function baseOwnedChanges(comments) {
  const files = new Set();
  for (const comment of comments ?? []) {
    if (comment.user?.login !== BOT_LOGIN) continue;
    for (const match of String(comment.body ?? '').matchAll(BASE_OWNED_PATTERN)) {
      try {
        const list = JSON.parse(Buffer.from(match[1], 'base64').toString('utf8'));
        if (Array.isArray(list)) for (const file of list) if (typeof file === 'string' && file) files.add(file);
      } catch {
        // A damaged marker names no files.
      }
    }
  }
  return [...files].sort();
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
// Only used to mention that a resume merges the base branch first, so errors count as not behind.
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

export function fixBranch(folder) {
  return `${FIX_BRANCH_PREFIX}${folder}`;
}

// The spec folder of an implementation, amendment, or fix spec branch, or null.
export function folderOfBranch(branch) {
  const value = String(branch ?? '');
  for (const prefix of [AMEND_BRANCH_PREFIX, FIX_BRANCH_PREFIX, BRANCH_PREFIX]) {
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

// Guidance a person gave with `/speckit resume` or `/speckit retry` (the text after the command), carried in the
// workflow's resume or retry comment.
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

export function normalizeWriterGuidance(text) {
  return String(text ?? '').trim().replace(/\s+/g, ' ').slice(0, MAX_WRITER_GUIDANCE).trim();
}

export function appendWriterGuidance(prompt, guidance, label = 'Guidance from the person who resumed this implementation') {
  const value = normalizeWriterGuidance(guidance);
  return value ? `${prompt} ${label}: ${value}` : prompt;
}

export function renderRepairMarker(repair) {
  return `<!-- speckit-implement:repair ${Buffer.from(JSON.stringify(repair), 'utf8').toString('base64')} -->`;
}

export function unresolvedRepair(comments) {
  const repairs = new Map();
  for (const comment of comments) {
    if (comment.user?.login !== BOT_LOGIN) continue;
    const match = String(comment.body ?? '').match(REPAIR_MARKER_PATTERN);
    if (!match) continue;
    try {
      const value = JSON.parse(Buffer.from(match[1], 'base64').toString('utf8'));
      if (!value?.id || !['pending', 'resolved'].includes(value.state)) continue;
      if (value.state === 'resolved') repairs.delete(value.id);
      else repairs.set(value.id, { ...value, at: value.at || comment.created_at, commentId: comment.id });
    } catch {
      // Ignore malformed workflow markers.
    }
  }
  return [...repairs.values()].sort((a, b) => Date.parse(b.at) - Date.parse(a.at))[0] ?? null;
}

export function renderRepairPrompt({ repair, guidance, folder }) {
  const tasks = Array.isArray(repair.tasks) && repair.tasks.length > 0 ? repair.tasks.join(', ') : 'the implemented scope';
  const prompt = [
    `Repair the already-landed implementation for ${tasks} in spec ${folder}.`,
    `The ${repair.scope} verification failed after the related tasks were committed and checked in tasks.md.`,
    'Do not untick, rewrite, or add tasks. Do not commit. Change only implementation or directly related tests needed to make the failed verification pass.',
    repair.run ? `Failure evidence from workflow run ${repair.run}, if downloadable, is in .speckit-repair-evidence/ inside your working directory; inspect it directly and report if unavailable.` : '',
    'If no code change is needed, leave the tree unchanged and explain why; a successful agent exit still requires separate reverification of the failed scope.',
  ].filter(Boolean).join(' ');
  return appendWriterGuidance(prompt, guidance, 'Writer-approved guidance');
}

// The comment that starts a new attempt window; it persists the resume, so a replaced run cannot lose
// it. Guidance applies to the stopped task, convergence, or conflict resolution until it succeeds. `state` records the
// implementation branch head and the base branch head at the restart (see unchangedRestarts).
export function renderResumeComment(lead, guidance = '', state = null) {
  return renderRestartComment(RESUME_COMMENT_MARKER, lead, guidance, state);
}

// Like a resume comment, but the next run does not merge the base branch first.
export function renderRetryComment(lead, guidance = '', state = null) {
  return renderRestartComment(RETRY_COMMENT_MARKER, lead, guidance, state);
}

function renderRestartComment(marker, lead, guidance, state = null) {
  const value = String(guidance ?? '').trim().slice(0, MAX_WRITER_GUIDANCE);
  return [
    marker,
    ...(state?.head ? [renderRestartState(state)] : []),
    ...(value ? [renderGuidanceMarker(value)] : []),
    lead,
    ...(value ? ['', 'Guidance for the next attempts:', '', ...neutralizeMarkers(value).split('\n').map((line) => `> ${line}`)] : []),
  ].join('\n');
}

// A person's restart (`/speckit retry` or `/speckit resume`) that leaves the implementation branch where it was stops
// the same way again unless something else changed. After this many restarts in a row on the same head, the commands
// refuse another one while the implementation is stopped, unless the person insists (RESTART_FORCE_FLAG).
export const MAX_UNCHANGED_RESTARTS = 2;
export const RESTART_FORCE_FLAG = '--force';
const RESTART_STATE_PATTERN = /^<!-- speckit-implement:restart-state (\{[^\n]*?\}) -->$/m;
const renderRestartState = ({ head, base = null }) => `<!-- speckit-implement:restart-state ${JSON.stringify({ head, base })} -->`;

// The latest restarts by a person, newest first, as long as each recorded `head` as the implementation branch head:
// the restarts since which the branch has not changed. A workflow restart without a recorded state (a merged
// amendment, a landed fix spec) ends the run of unchanged restarts.
export function unchangedRestarts(comments, head) {
  const restarts = [];
  const ordered = [...comments]
    .filter((comment) => comment.user?.login === BOT_LOGIN && [RESUME_COMMENT_MARKER, RETRY_COMMENT_MARKER].some((marker) => String(comment.body ?? '').startsWith(marker)))
    .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at));
  for (const comment of ordered) {
    let state = null;
    try {
      state = JSON.parse(String(comment.body).match(RESTART_STATE_PATTERN)?.[1] ?? 'null');
    } catch {
      // A damaged marker counts as unknown.
    }
    if (!state?.head || state.head !== head) break;
    restarts.push({ kind: String(comment.body).startsWith(RESUME_COMMENT_MARKER) ? 'resume' : 'retry', head: state.head, base: state.base ?? null, at: comment.created_at });
  }
  return restarts;
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

// Whether the next phase has not started yet: none of its tasks is ticked. Only then is the base branch merged before
// it, so retries and continuations of a phase build on the same base as its first tasks.
export function phaseNotStarted(tasksMarkdown) {
  const current = describeParallelGroups(tasksMarkdown).phases.find((phase) => phase.open > 0);
  return Boolean(current) && current.done === 0;
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

// Diagnose, analyze, and fix runs are named "<workflow> spec #<twin> · <why the run started>" like the implementation
// chain (run-name in their workflow sources). Runs started earlier are named "<workflow> · spec #<twin> · …",
// "[<workflow>] spec #<twin> · …", or "<workflow> #<twin> · …".
const TWIN_RUN_NAMES = new Map([[DIAGNOSE_WORKFLOW_FILE, 'Spec Kit diagnose'], [ANALYZE_WORKFLOW_FILE, 'Spec Kit analyze'], [FIX_WORKFLOW_FILE, 'Spec Kit fix']]);

export function isTwinRunOf(workflowFile, title, twin) {
  const workflow = TWIN_RUN_NAMES.get(workflowFile);
  const text = String(title ?? '');
  return [`${workflow} spec #${twin}`, `${workflow} · spec #${twin}`, `[${workflow}] spec #${twin}`, `${workflow} #${twin}`].some((name) => text === name || text.startsWith(`${name} `));
}

// Adds (`needed`) or removes ATTENTION_LABEL on a pull request; only an open pull request that is not blocked gets it.
// The label is a convenience, so a failure never stops the workflow.
export async function setAttention(client, number, needed) {
  try {
    const pull = await client.getPullRequest(number);
    const names = (pull.labels ?? []).map((label) => label?.name ?? label);
    const has = names.includes(ATTENTION_LABEL);
    if (needed && !has && pull.state === 'open' && !names.includes(BLOCKED_LABEL)) await client.addLabels(number, [ATTENTION_LABEL]);
    if (!needed && has) await client.removeLabel(number, ATTENTION_LABEL);
  } catch {
    // The label only signals the state; comments and check runs carry it.
  }
}

// Adds or removes BLOCKED_LABEL on an implementation pull request. Blocking needs `explain` ({ key, lines }): unless a
// workflow comment already carries the marker of `key` (the comment that caused the block usually does), a comment with
// `lines` says what the implementation waits for and how it is unblocked. A blocked pull request loses ATTENTION_LABEL:
// the person's part is described there. Removing it is silent; the comment that lifts the block explains it.
export async function setBlocked(client, number, blocked, { explain = null } = {}) {
  if (blocked && !explain?.key) throw new Error('setBlocked needs an explanation to block a pull request');
  try {
    const pull = await client.getPullRequest(number);
    const names = (pull.labels ?? []).map((label) => label?.name ?? label);
    const has = names.includes(BLOCKED_LABEL);
    if (blocked && pull.state === 'open') {
      if (!blockExplained(await client.listIssueComments(number), explain.key)) {
        await client.createComment(number, [renderBlockedMarker(explain.key), ...explain.lines].join('\n'));
      }
      if (!has) {
        await client.ensureLabel({ name: BLOCKED_LABEL, color: 'd93f0b', description: 'Implementation is on hold waiting for a prerequisite fix' });
        await client.addLabels(number, [BLOCKED_LABEL]);
      }
      if (names.includes(ATTENTION_LABEL)) await client.removeLabel(number, ATTENTION_LABEL);
    }
    if ((!blocked || pull.state !== 'open') && has) await client.removeLabel(number, BLOCKED_LABEL);
  } catch {
    // The label only signals the state; comments and check runs carry it.
  }
}

// Implementation pull requests stay draft until the verified result is either merged immediately or deliberately
// handed to a person for final review. Unlike the attention label, this controls GitHub's merge affordance.
export async function ensureImplementationDraft(client, pull) {
  if (pull.draft) return;
  await client.convertToDraft(pull.node_id);
  pull.draft = true;
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

// The attempt marker of an outcome comment. `kind` is what the attempt works on: task, repair, converge, or resolve. `outcome`
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
      if (!['task', 'repair', 'converge', 'resolve'].includes(kind) || !['success', 'failure', 'requeue', 'attention'].includes(value.outcome)) continue;
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
  const match = String(externalId ?? '').match(/^speckit:attempt:(task|repair|converge|resolve):(T\d{3,})?:(\d+)$/);
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

export function renderResolvePrompt({ folder, files, base, forkPoint = null, archived = false, guidance = '' }) {
  const spec = specDir(folder, { archived });
  const own = forkPoint ? `\`git diff --name-only ${forkPoint} HEAD\`` : `\`git diff --name-only $(git merge-base HEAD MERGE_HEAD) HEAD\``;
  const prompt = [
    `The working tree is in the middle of merging the base branch \`${base}\` into the implementation branch of the spec \`${spec}\`.`,
    `Git reported merge conflicts in these files: ${files.map((file) => `\`${file}\``).join(', ')}.`,
    'Resolve every conflict: remove all conflict markers, and combine both sides so that the',
    `intent of the spec and the changes from \`${base}\` are both kept. Use the spec, plan, and tasks in`,
    `\`${spec}\` to understand the implementation side.`,
    'The merged result must also build and pass its tests as a whole. Both sides can clash without a textual conflict,',
    'for example two migrations with the same sequence number, an identifier both sides added, or implementation code',
    `that uses something \`${base}\` renamed or removed. Fix such clashes on the implementation's side, keeping what`,
    `\`${base}\` has as it is: besides the conflicted files you may change the files this implementation branch changed`,
    `itself (${own}) and add new files, for example to renumber this implementation's migration to the next free number`,
    `(rename it with \`git mv\`) and update every reference to it. Only when the merged result cannot pass otherwise, you`,
    `may also make the smallest change to other files of \`${base}\` (for example add a setting this implementation`,
    'made required to a test that the base branch added); never delete them, and never weaken a test, assertion, or',
    'requirement on either side to make it pass. A person reviews such changes before the implementation merges. Do not',
    'change anything under `.github/`, `.specify/`, or `specs/`. Build what the merge touches and run the tests that cover it;',
    'the full verification runs after you. Do not run git commit, merge, checkout, reset, restore, stash, rebase, or push.',
  ].join(' ');
  return appendWriterGuidance(prompt, guidance);
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
// Order: a conflict resolution when merging the base branch before the next phase or the convergence conflicted on
// this head (best effort, see MAX_SYNC_RESOLVE_ATTEMPTS) → the tasks of the phase of the next unticked task (planTasks)
// → converge (until a successful convergence is newer than the last successful task) → merge, or resolve after a merge
// (or merging the base branch before it) found conflicts on this head. Only failures
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
  // Merging the base branch before a phase or the convergence conflicted on this head: resolve first, best effort.
  // After MAX_SYNC_RESOLVE_ATTEMPTS failed attempts, or one that needs a person, the work continues on its base.
  let syncGivenUp = null;
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
  const givenUp = syncGivenUp ? { syncGivenUp } : {};
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
      ...givenUp,
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
    // The flag survives into the merge decision, so a pending repair that wraps it does not merge the base branch again.
    return { action: 'merge', kind: 'merge', attempt: merge.total + 1, stale, ...givenUp };
  }
  if (exceeded(count)) return { action: 'limit', kind, attempts: count.total, stale };
  return { action: 'tasks', kind, order: [[{ task: null, attempt: count.total + 1 }]], phaseEnd: false, lastIsParallel: false, stale, ...(kind === 'converge' ? givenUp : {}) };
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
  { name: 'diagnose', usage: '/speckit diagnose [guidance]', help: 'diagnose why the implementation stopped; nothing runs meanwhile. Guidance steers the analysis or answers its questions' },
  { name: 'retry', usage: '/speckit retry [guidance]', help: 'restart what stopped with a fresh attempt count, without merging the base branch for it (a phase that has not started, the convergence, and the merge always merge it first); guidance goes into the next attempt\'s prompt. Refused after 2 restarts in a row that left the branch unchanged, unless the guidance starts with `--force`' },
  { name: 'resume', usage: '/speckit resume [guidance]', help: 'merge the base branch into the implementation branch first (Copilot resolves conflicts), then restart what stopped with a fresh attempt count; guidance goes into the next attempt\'s prompt. Refused like retry, unless the base branch has new commits or the guidance starts with `--force`' },
  { name: 'fix', usage: '/speckit fix [guidance]', help: 'for a cause outside this spec: an agent authors a fix spec and opens it against the base branch for your review; once it is merged and implemented, this implementation resumes by itself' },
  { name: 'help', usage: '/speckit help', help: 'list these commands' },
];

// What a diagnosis category means, for the person who decides how to continue.
export const CATEGORY_MEANING = Object.freeze({
  artifacts: 'The spec, plan, or tasks are wrong or incomplete, so the work cannot be done as written. The correction belongs in the spec artifacts, not in code around them.',
  retry: 'The failure was transient (runner, network, timeout, rate limit, Copilot error, a flaky test with evidence), or the agent took a poor approach while the spec artifacts are fine. Running the work again is expected to pass.',
  outside: 'The cause is not in this spec: code or tests of another feature on the base branch, the environment or tooling, permissions, quotas, or an external service. It has to be fixed where it lives.',
  decision: 'Only a person can make this trade-off: scope, a success criterion, a risk, a split, or abandoning the work.',
  unknown: 'The evidence is not enough to name a cause yet.',
});

// The verb and the text after it of a `/speckit <verb> [text]` command; null for anything else.
export function parseNextCommand(command) {
  const match = String(command ?? '').trim().match(/^\/speckit ([a-z]+)(?:[ \t]+([\s\S]*))?$/);
  return match ? { verb: match[1], argument: String(match[2] ?? '').trim() } : null;
}

// What a next step does, so a person knows the consequences before posting it. `baseBranch` is the implementation
// pull request's base; `continuation` describes what comes next after a restart (continuationOf), for example
// "continues with T013".
export function describeCommandEffect(command, { baseBranch = null, continuation = null, diagnosed = false } = {}) {
  const parsed = parseNextCommand(command);
  if (!parsed) return null;
  const base = baseBranch ? `\`${baseBranch}\`` : 'the base branch';
  const next = (typeof continuation === 'string' ? continuation : continuation?.text) ?? 'continues with what stopped';
  const mergesAnyway = typeof continuation === 'object' && continuation?.mergesBase === true;
  const attempts = `with a fresh attempt count (at most ${MAX_TASK_ATTEMPTS} failed attempts)`;
  const forced = ['retry', 'resume'].includes(parsed.verb) && /^--force(?:\s+|$)/i.test(parsed.argument);
  const argument = forced ? parsed.argument.replace(/^--force(?:\s+|$)/i, '').trim() : parsed.argument;
  const flag = forced ? ' `--force` is a flag that bypasses the unchanged-restart guard, not guidance.' : '';
  const guidance = (argument
    ? 'The text after the command goes into the next attempt\'s prompt as guidance.'
    : 'Text you add after the command goes into the next attempt\'s prompt as guidance.') + flag;
  if (continuation?.repair && ['retry', 'resume'].includes(parsed.verb)) {
    const start = parsed.verb === 'resume'
      ? `First merges ${base} into the implementation branch (Copilot resolves conflicts); then`
      : `Does not merge ${base};`;
    return `${start} ${argument ? 'runs a repair agent with the guidance, then reverifies' : 'reverifies without running a repair agent'} the pending ${continuation.repair.scope} verification. The original failed scope must pass before normal planning continues; if this repair attempt fails, the implementation stops for a person again. With guidance, a successful no-change repair also reaches reverification without an empty commit.${flag}`;
  }
  switch (parsed.verb) {
    case 'retry':
      return mergesAnyway
        ? `The implementation ${next}, ${attempts}. That work merges ${base} first anyway (a new phase, the convergence, and the merge always do), so here a retry and a resume do the same. ${guidance}`
        : `Does not merge ${base}: the implementation ${next} on the branch as it is, ${attempts}. ${guidance}`;
    case 'resume':
      return `First merges ${base} into the implementation branch (Copilot resolves conflicts), so fixes that landed there are included; then the implementation ${next}, ${attempts}. ${guidance}`;
    case 'diagnose':
      return [
        `Starts a new diagnosis${diagnosed ? ' with the previous one as context' : ''}${parsed.argument ? ' and the text after the command as guidance' : ''}; an open amendment pull request is closed as superseded.`,
        'Nothing is retried meanwhile, and the findings are posted here.',
        ...(parsed.argument ? [] : ['Guidance you add after the command steers the analysis or answers its questions.']),
      ].join(' ');
    case 'fix':
      return [
        `An agent authors a small fix spec for the cause${parsed.argument ? ', guided by the text after the command,' : ''} and opens it as a pull request against ${base}; it is checked for consistency and then waits for your review.`,
        `Once you merge it, the fix spec is implemented like any spec, this implementation waits for it, and then resumes by itself: it merges ${base} with the fix and continues, ${attempts}.`,
      ].join(' ');
    default:
      return null;
  }
}

// What comes next when a stopped implementation restarts (a retry or resume now): `text` as "continues with …", and
// `mergesBase` when that work merges the base branch first whatever restarted it (a phase that has not started, the
// convergence, and the merge); null when it cannot be told. A failed verification is not repeated by itself: the chain
// continues with the next work, whose verification (at the latest the full verification before the merge) covers it.
export function describeContinuation(decision, checks = [], tasksMarkdown = null) {
  const stop = latestCheckRun(checks.filter((check) => check.external_id === CHECK_LIMIT));
  const verificationStop = /verification failed/i.test(String(stop?.output?.title ?? ''));
  const note = verificationStop ? ' (the failed verification is not repeated by itself; the next verification, at the latest the full verification before the merge, covers it)' : '';
  if (decision?.action === 'merge') return { text: 'continues with the merge, which first merges the base branch and runs the full verification', mergesBase: true };
  if (decision?.action !== 'tasks') return null;
  if (decision.kind === 'task') {
    const ids = (decision.order?.[0] ?? []).map((entry) => entry.task).filter(Boolean);
    return ids.length > 0 ? { text: `continues with ${ids.join(', ')}${note}`, mergesBase: tasksMarkdown !== null && phaseNotStarted(tasksMarkdown) } : null;
  }
  if (decision.kind === 'converge') return { text: `continues with the convergence${note}`, mergesBase: true };
  if (decision.kind === 'resolve') return { text: 'continues with the conflict resolution', mergesBase: false };
  return null;
}

// Reads the state of an implementation pull request and describes what a restart now would continue with; null when
// that cannot be told (errors included, because it only explains a next step).
export async function continuationOf(client, { folder, pull }) {
  try {
    const comments = await client.listIssueComments(pull.number);
    const repair = unresolvedRepair(comments);
    if (repair) return { text: `continues with the pending ${repair.scope} verification repair`, mergesBase: false, repair };
    const { tasksMarkdown } = await readSpecTasks(client, folder, pull.head.sha);
    if (tasksMarkdown === null) return null;
    const checks = await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME);
    const now = Date.now();
    const at = new Date(now).toISOString();
    const decision = decideNext({ tasksMarkdown, checks, attempts: parseAttempts(comments), windowStart: at, resumedAt: at, now });
    return describeContinuation(decision, checks, tasksMarkdown);
  } catch {
    return null;
  }
}

// What the next-step block of a comment on an implementation pull request needs to explain the commands.
export async function nextStepContext(client, pull, folder) {
  let baseBranch = null;
  try {
    baseBranch = baseBranchOf(pull);
  } catch {
    // Explained as "the base branch".
  }
  return {
    baseBranch,
    behindBase: folder ? await behindBaseOf(client, pull, folder) : null,
    continuation: folder ? await continuationOf(client, { folder, pull }) : null,
  };
}

// An option title as one sentence: without the trailing punctuation an agent may add, ending with a period.
const bareTitle = (text) => String(text ?? '').trim().replace(/[\s.:;,]+$/, '');
const sentence = (text) => `${bareTitle(text)}.`;
const FIXED_EFFECTS = {
  amendment: 'Merging applies the amendment to the spec artifacts on the implementation branch; the implementation then continues by itself from the next unchecked task, with a fresh attempt count. Comment or request changes there to have it reworked first.',
  push: 'A push to the implementation branch continues the implementation by itself with a fresh attempt count. Closing the pull request abandons the implementation, and the twin returns to its computed stage.',
};
const optionEffect = (option, context) => {
  if (option.effect) return option.effect;
  if (option.command) return describeCommandEffect(option.command, context);
  if (context.continuation?.repair) {
    if (option.kind === 'push') return 'After pushing a fix, request `/speckit retry` to reverify the pending repair; a push alone does not clear it. Closing the pull request abandons the implementation, and the twin returns to its computed stage.';
    if (option.kind === 'amendment') return 'Merging applies the amendment and resumes the implementation with the pending repair verification before normal planning can continue. Comment or request changes there to have it reworked first.';
  }
  return FIXED_EFFECTS[option.kind] ?? null;
};
const optionTitle = (option) => (option.link ? `[${bareTitle(option.title)}](${option.link})` : bareTitle(option.title));

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

// How a person handles a fix spec pull request; shown in its description and referenced from the implementation.
export function renderFixHowTo(implementationPull, baseBranch = null) {
  const base = baseBranch ? `\`${baseBranch}\`` : 'the default branch';
  return [
    '**How to proceed** (people with write access):',
    '',
    `- **Merge** this pull request to add the fix spec to ${base}. It becomes a spec twin, is requested for implementation on behalf of the person who asked for it, and is implemented like any spec. #${implementationPull} waits for it and resumes by itself once the fix has landed (it merges ${base} first).`,
    '- **Comment**, or **submit a review** with *Comment* or *Request changes* (line comments included), to have the fix spec reworked with your feedback. It returns here, checked again, for your review.',
    `- **Close** this pull request without merging to discard the fix spec; #${implementationPull} stays stopped and lists what you can do next.`,
  ];
}

// Links a fix spec pull request to the implementation pull request it unblocks. Only the workflow writes it into the
// description; agent text never carries markers (neutralizeMarkers).
const FIX_FOR_PATTERN = /<!-- speckit-fix:for (\{[^\n]*?\}) -->/;
export const renderFixFor = (data) => `<!-- speckit-fix:for ${JSON.stringify(data)} -->`;
export function parseFixFor(body) {
  try {
    const data = JSON.parse(String(body ?? '').match(FIX_FOR_PATTERN)?.[1] ?? 'null');
    return Number.isInteger(data?.pull) && data.pull > 0 ? data : null;
  } catch {
    return null;
  }
}

// The lifecycle of a fix spec, recorded on the implementation pull request it unblocks by the workflow's comments:
// requested, created, merged or discarded, linked (twin, dependency, and implementation request), landed (resumed), or
// stopped (the fix spec was not implemented).
const FIX_EVENT_PATTERN = /<!-- speckit-fix:(requested|created|merged|discarded|linked|landed|stopped) (\{[^\n]*?\}) -->/g;
export const renderFixEvent = (event, data = {}) => `<!-- speckit-fix:${event} ${JSON.stringify(data)} -->`;
export function parseFixEvents(comments) {
  const events = [];
  for (const comment of comments ?? []) {
    if (comment.user?.login !== BOT_LOGIN) continue;
    for (const match of String(comment.body ?? '').matchAll(FIX_EVENT_PATTERN)) {
      try {
        events.push({ event: match[1], ...JSON.parse(match[2]), at: comment.created_at, commentId: comment.id });
      } catch {
        // A damaged marker is ignored.
      }
    }
  }
  return events;
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
// (an open amendment, else the recommended option, else waiting for the automatic diagnosis, else a diagnosis), then
// every other valid option, collapsed. Every option says why it helps and what it does, and every command can be
// posted as it is: no placeholders.
// - `options`: { title, why, command, recommended, link, kind } from a diagnosis (`kind` names a fixed effect for an
//   option without a command: `amendment` or `push`).
// - `amendment`: an open amendment pull request ({ number, title, html_url, status }), or `amendmentPull` its number.
// - `baseBranch`: the implementation pull request's base; `behindBase`: its name when it has commits the
//   implementation branch lacks, else null.
// - `continuation`: what a restart continues with (continuationOf).
// - `autoDiagnosis`: whether a diagnosis starts by itself; `diagnosed`: whether a diagnosis already reported.
export function renderNextSteps({ options = null, amendment = null, amendmentPull = null, baseBranch = null, behindBase = null, continuation = null, autoDiagnosis = false, diagnosed = false } = {}) {
  const all = options ?? [];
  const context = { baseBranch: baseBranch ?? behindBase, continuation, diagnosed };
  const pending = amendment ?? (amendmentPull ? { number: amendmentPull } : null);
  let recommended;
  let others = all;
  if (pending) {
    const status = pending.status ? ` (${bareTitle(pending.status)})` : '';
    recommended = {
      title: `Review and merge the amendment #${pending.number}${pending.title ? `: ${bareTitle(pending.title)}` : ''}${status}`,
      link: pending.html_url ?? null,
      why: 'The diagnosis found the cause in the spec artifacts; the amendment corrects them, so the implementation can do the work as written.',
      kind: 'amendment',
    };
    // A diagnosis recommends its own amendment as an option without a command; the amendment is that option.
    others = all.filter((option) => !(option.recommended && !option.command));
  } else {
    const pick = all.find((option) => option.recommended) ?? null;
    if (pick) {
      recommended = pick;
      others = all.filter((option) => option !== pick);
    } else if (autoDiagnosis) {
      recommended = {
        title: 'Wait for the diagnosis',
        why: 'It starts automatically, finds the cause, and posts a recommendation with a ready-to-post command here.',
        kind: 'wait',
      };
    } else {
      recommended = {
        title: diagnosed ? 'Diagnose again' : 'Diagnose why the implementation stopped',
        why: diagnosed
          ? 'No other step is known to solve the problem; a new diagnosis looks at the current state with the previous findings as context.'
          : 'The cause is not known yet; a diagnosis finds it and recommends how to continue before anything runs again.',
        command: '/speckit diagnose',
      };
    }
  }

  const lines = [`**Recommended next step**: ${sentence(optionTitle(recommended))}`];
  const details = optionDetails(recommended, context);
  if (details.length > 0) lines.push('', ...details.map(([label, text]) => `- **${label}**: ${text}`));
  if (recommended.command) lines.push('', 'Comment this on the pull request (people with write access):', '', '```text', recommended.command, '```');

  // Commands the recommendation or an option already suggests are not repeated.
  const used = new Set([recommended, ...others].map((option) => parseNextCommand(option.command)?.verb).filter(Boolean));
  const generic = [];
  if (!autoDiagnosis && !used.has('diagnose')) {
    generic.push({
      title: diagnosed ? 'Diagnose again' : 'Diagnose why the implementation stopped',
      why: diagnosed ? 'If you doubt the diagnosis, or know something it did not consider.' : 'If the cause is not clear from the comments above.',
      command: '/speckit diagnose',
    });
  }
  if (!used.has('retry')) {
    generic.push({
      title: 'Retry what stopped',
      why: 'If the failure looks transient (runner, network, timeout, a flaky test) and nothing needs to change.',
      command: '/speckit retry',
    });
  }
  if (!used.has('resume')) {
    generic.push({
      title: behindBase ? `Merge \`${behindBase}\` first, then continue` : 'Merge the base branch first, then continue',
      why: behindBase
        ? `\`${behindBase}\` has commits the implementation branch lacks, possibly a fix for the cause.`
        : 'Once a fix for the cause has landed on the base branch.',
      command: '/speckit resume',
    });
  }
  generic.push({
    title: 'Push a fix yourself, or close the pull request',
    why: 'If you know the fix, or the spec should not be implemented as it is.',
    kind: 'push',
  });
  lines.push('', '<details><summary>Other options</summary>', '');
  for (const option of [...others, ...generic]) {
    lines.push(`- **${optionTitle(option)}**`);
    for (const [label, text] of optionDetails(option, context)) lines.push(`  - ${label}: ${text}`);
    if (option.command) lines.push('', '  ```text', `  ${option.command}`, '  ```');
  }
  lines.push('', '</details>');
  return lines;
}

function optionDetails(option, context) {
  const effect = optionEffect(option, context);
  return [
    ...(option.why ? [['Why', String(option.why).trim()]] : []),
    ...(effect ? [['What it does', effect]] : []),
  ];
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
