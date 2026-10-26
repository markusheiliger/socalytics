import { artifactUrl, isImplementRequested, parseTwinFolder, renderSpecLine, specDir } from './speckit-prepare-core.mjs';

export const BRANCH_PREFIX = 'speckit/';
export const CHECK_RUN_NAME = 'Spec Kit implementation';
export const START_COMMENT_MARKER = '<!-- speckit-implement:start -->';
export const RETRY_COMMENT_MARKER = '<!-- speckit-implement:retry -->';
export const SYNC_COMMENT_MARKER = '<!-- speckit-implement:sync -->';
// Leads a retry or sync comment that the diagnosis posted to continue automatically (speckit-diagnose.mjs). The comment
// still counts as the restart that follows the marker: startsWithMarker skips it.
export const AUTO_CONTINUE_MARKER = '<!-- speckit-implement:auto-continue -->';
export function startsWithMarker(body, marker) {
  const text = String(body ?? '');
  return (text.startsWith(AUTO_CONTINUE_MARKER) ? text.slice(AUTO_CONTINUE_MARKER.length).replace(/^\r?\n/, '') : text).startsWith(marker);
}
const SYNC_BARRIER_PATTERN = /<!-- speckit-implement:sync-barrier (\{[^\n]*?\}) -->/;

export function renderSyncComment(lead, guidance = '', state = null) {
  return renderRestartComment(SYNC_COMMENT_MARKER, lead, guidance, state);
}

export const renderSyncBarrierMarker = (state) => `<!-- speckit-implement:sync-barrier ${JSON.stringify(state)} -->`;

// Requests and acknowledgements live on the pull request, not in replaceable workflow inputs or head-bound checks.
export function syncBarrier(comments, { includeReleased = false } = {}) {
  const trusted = comments.filter((comment) => comment.user?.login === BOT_LOGIN);
  const request = trusted.filter((comment) => startsWithMarker(comment.body, SYNC_COMMENT_MARKER)).at(-1);
  if (!request) return null;
  const id = String(request.id ?? request.created_at);
  let completion = null;
  let completionAt = null;
  for (const comment of trusted) {
    try {
      const state = JSON.parse(String(comment.body ?? '').match(SYNC_BARRIER_PATTERN)?.[1] ?? 'null');
      if (state?.request === id && ['verified', 'released'].includes(state.state)) {
        completion = state;
        completionAt = comment.created_at;
      }
    } catch {
      // Invalid acknowledgements cannot release a barrier.
    }
  }
  return completion?.state === 'released' && !includeReleased ? null : { request: id, at: request.created_at, guidance: parseGuidance(request.body) ?? '', completion, completionAt };
}
// Existing pull requests may carry this marker from before `/speckit resume` was removed. It remains a restart marker
// for attempt accounting and guidance, but no longer causes a base-branch merge.
export const LEGACY_RESUME_COMMENT_MARKER = '<!-- speckit-implement:resume -->';
export const RESTART_COMMENT_MARKERS = Object.freeze([RETRY_COMMENT_MARKER, LEGACY_RESUME_COMMENT_MARKER]);
export const DONE_COMMENT_MARKER = '<!-- speckit-implement:done -->';
const REPAIR_MARKER_PATTERN = /<!-- speckit-implement:repair ([A-Za-z0-9+/=]+) -->/;
export const BOT_LOGIN = 'github-actions[bot]';
export const MAX_TASK_ATTEMPTS = 2;
export const MAX_CONVERGE_ROUNDS = 3;
// A merge check run still in progress after this long belongs to a cancelled or crashed run.
export const MERGE_STALE_MS = 2 * 60 * 60 * 1000;
// The per-spec implementation chain: every run decides one unit of work for one spec (a sequential or parallel group of
// tasks, the verification of a finished phase, a repair, the convergence, a conflict resolution, or the merge), runs
// it, and starts the next run. Each kind of work has its own workflow (IMPLEMENT_KIND_WORKFLOW_FILES); Spec Kit
// implement (IMPLEMENT_WORKFLOW_FILE) starts a chain and reports stops. All of them prepare the run with the reusable
// Spec Kit implement prepare, and every task, the convergence, a repair, and a conflict resolution run in the reusable
// Spec Kit implement task (work, then land).
export const IMPLEMENT_WORKFLOW_FILE = 'speckit-implement-decide.yml';
// The workflow of each kind of work, by the action decideNext chose (the convergence and a conflict resolution are
// groups of one).
export const IMPLEMENT_KIND_WORKFLOW_FILES = Object.freeze({
  tasks: 'speckit-implement-group.yml',
  verify: 'speckit-implement-verify.yml',
  repair: 'speckit-implement-repair.yml',
  merge: 'speckit-implement-merge.yml',
});
// Every workflow whose runs make up a chain, for the scheduler and the diagnosis.
export const IMPLEMENT_CHAIN_WORKFLOW_FILES = Object.freeze([IMPLEMENT_WORKFLOW_FILE, ...Object.values(IMPLEMENT_KIND_WORKFLOW_FILES)]);
// The workflow that does a decision's work; Spec Kit implement decide for a decision without work (a stop, a wait, done).
export function implementWorkflowFor(action) {
  return IMPLEMENT_KIND_WORKFLOW_FILES[action] ?? IMPLEMENT_WORKFLOW_FILE;
}
// The chains before the workflows of each kind: Spec Kit implement spec (with its reusable phase, group, and task
// workflows) and the single Spec Kit implement (speckit-implement.yml). Runs started on them finish on their own
// commit; the scheduler counts them as active, and they share the concurrency group of the workflows of each kind, so
// two runs never work on one spec at the same time.
export const LEGACY_IMPLEMENT_WORKFLOW_FILES = Object.freeze(['speckit-implement-spec.yml', 'speckit-implement.yml']);
// The verb that names the work of a Spec Kit implement task call (its input `kind` and its script
// speckit-<verb>.mjs) for the kind of an attempt. Attempt markers keep the kind, so comments already on GitHub stay
// readable.
export const WORK_VERBS = Object.freeze({ task: 'implement', converge: 'converge', repair: 'repair', resolve: 'resolve' });

export const IMPLEMENT_WORKFLOW_NAMES = Object.freeze({
  spec: 'Spec Kit implement spec',
  task: 'Spec Kit implement task',
});
// A group runs as one matrix job per task, and GitHub runs at most 256 matrix jobs per run; a longer group continues in
// the next run.
export const MAX_MATRIX_JOBS_PER_RUN = 256;
// How many tasks of a parallel group run at the same time; a sequential group runs one at a time.
export const MAX_PARALLEL_TASKS = 5;
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
export const DIAGNOSE_WORKFLOW_FILE = 'speckit-diagnose.lock.yml';
export const AMEND_BRANCH_PREFIX = 'speckit-amend/';
// Specs authored by `/speckit author`: an ordinary spec, authored by the Spec Kit workflow `author` for a cause outside
// the stopped spec, proposed as a pull request from speckit-spec/<folder> against the default branch. Pull requests
// opened earlier use speckit-fix/<folder> and are still recognized until they are settled. Its own check run tracks the
// authoring on the implementation pull request's head, apart from the implementation's check runs.
export const SPEC_BRANCH_PREFIX = 'speckit-spec/';
export const FIX_BRANCH_PREFIX = 'speckit-fix/';
export const PREREQUISITE_BRANCH_PREFIXES = Object.freeze([SPEC_BRANCH_PREFIX, FIX_BRANCH_PREFIX]);
export const FIX_WORKFLOW_FILE = 'speckit-author.yml';
export const FIX_CHECK_NAME = 'Spec Kit author';
export const CHECK_FIX_AUTHORING = 'speckit:fix-authoring';
export const CHECK_FIX_AUTHORED = 'speckit:fix-authored';
export const FIX_TITLE_PREFIX = 'Spec: ';
// The title prefix of spec pull requests opened by earlier tooling; a rework renames them to FIX_TITLE_PREFIX, which
// the rework's push requires.
export const LEGACY_FIX_TITLE_PREFIX = 'Fix spec: ';
// Architecture changes of a spec pull request may only come from the spec's design (docs/architecture/**).
export const ARCHITECTURE_PREFIX = 'docs/architecture/';
export const DIAGNOSIS_COMMENT_MARKER = '<!-- speckit-implement:diagnosis -->';

export function diagnosisRunsUrl(env) {
  const server = String(env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '');
  return `${server}/${env.GITHUB_REPOSITORY}/actions/workflows/${DIAGNOSE_WORKFLOW_FILE}`;
}
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
// GitHub's default label for "Extra attention is needed": an implementation, amendment, or prerequisite spec pull
// request carries it while it waits for a person (stopped, a diagnosis or a prerequisite spec outcome to decide on, or
// ready for review) and loses it once a person's action puts automation back to work or closes the pull request. It is
// only added together with a comment that recommends what to do, never while a diagnosis runs, and never on a blocked
// pull request.
export const ATTENTION_LABEL = 'help wanted';
// An implementation that waits for a prerequisite (an open blocked-by dependency of its twin, or a prerequisite spec
// that is still being reviewed) carries BLOCKED_LABEL instead of ATTENTION_LABEL. It is only added together with a
// comment that says what it waits for and how it is unblocked (setBlocked), and lifted by maintainDependencyBlock.
export const BLOCKED_LABEL = 'blocked';
// The implementations whose merge unblocks the most others (Spec Kit orchestrate, maintainRoadblocks): not blocked
// themselves, ranked by how many implementations wait for them, also through chains. It is added together with a
// comment that names the waiting implementations, and moves with the queue on every scheduler run.
export const ROADBLOCK_LABEL = 'roadblock';
export const ROADBLOCK_MARKER = '<!-- speckit-implement:roadblock -->';
const BLOCKED_MARKER_PATTERN = /<!-- speckit-implement:blocked (\{[^\n]*?\}) -->/g;
export const renderBlockedMarker = (key) => `<!-- speckit-implement:blocked ${JSON.stringify(key)} -->`;
// Ends a block: the comment that lifts it carries this marker, so the lift (and its retry) happens once per block.
export const UNBLOCKED_MARKER = '<!-- speckit-implement:unblocked -->';

// The keys of the blocks explained since the block was last lifted (renderBlockedMarker, UNBLOCKED_MARKER), oldest
// first; empty when the latest block is already lifted.
export function currentBlocks(comments) {
  let keys = [];
  for (const comment of comments ?? []) {
    if (comment.user?.login !== BOT_LOGIN) continue;
    const body = String(comment.body ?? '');
    if (body.includes(UNBLOCKED_MARKER)) keys = [];
    for (const match of body.matchAll(BLOCKED_MARKER_PATTERN)) {
      try {
        keys.push(JSON.parse(match[1]));
      } catch {
        // A damaged marker explains nothing.
      }
    }
  }
  return keys;
}

// Whether a workflow comment already explains the block `key` (renderBlockedMarker) since the block was last lifted.
export function blockExplained(comments, key) {
  const wanted = JSON.stringify(key);
  return currentBlocks(comments).some((item) => JSON.stringify(item) === wanted);
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

// Tests that failed and passed on their one rerun in a verification (environment-verify's `flaky` output). Every
// comment that reports a verification lists them and records them in a marker; the scheduler's flaky-test ledger
// reads the markers of the workflow's comments only (parseFlakyMarkers). `<` and `>` are escaped in the JSON, so a test
// name cannot end the comment.
const FLAKY_MARKER_PATTERN = /<!-- speckit-implement:flaky (\{[^\n]*?\}) -->/g;
const FLAKY_SPEC_PATTERN = /<!-- speckit-implement:flaky-spec (\{[^\n]*?\}) -->/g;
const markerJson = (data) => JSON.stringify(data).replace(/</g, '\\u003c').replace(/>/g, '\\u003e');

// The tests of a comma-separated list or an array, trimmed and without duplicates.
export function parseFlakyList(value) {
  const items = Array.isArray(value) ? value : String(value ?? '').split(',');
  return [...new Set(items.map((item) => String(item ?? '').trim()).filter(Boolean))];
}

// The lines a verification comment adds for its flaky tests: the visible list and the marker; none without flaky tests.
export function renderFlakyLines(value, { bullet = true } = {}) {
  const tests = parseFlakyList(value);
  if (tests.length === 0) return [];
  const shown = tests.map((test) => `\`${neutralizeMarkers(test).replaceAll('`', "'")}\``).join(', ');
  return [`${bullet ? '- ' : ''}Flaky (passed on rerun): ${shown}`, `<!-- speckit-implement:flaky ${markerJson({ tests })} -->`];
}

function workflowMarkers(comments, pattern) {
  const found = [];
  for (const comment of comments ?? []) {
    if (comment.user?.login !== BOT_LOGIN) continue;
    for (const match of String(comment.body ?? '').matchAll(pattern)) {
      try {
        found.push({ data: JSON.parse(match[1]), at: comment.created_at });
      } catch {
        // A damaged marker records nothing.
      }
    }
  }
  return found;
}

// Every flaky test the workflow's comments recorded, with the time of the comment (one record per occurrence).
export function parseFlakyMarkers(comments) {
  return workflowMarkers(comments, FLAKY_MARKER_PATTERN).flatMap(({ data, at }) => parseFlakyList(Array.isArray(data?.tests) ? data.tests.filter((test) => typeof test === 'string') : []).map((test) => ({ test, at })));
}

// A flaky test the scheduler started a spec for (on the implementation pull request it was flaky on most recently).
export const renderFlakySpecMarker = (test) => `<!-- speckit-implement:flaky-spec ${markerJson({ test })} -->`;
export function parseFlakySpecMarkers(comments) {
  return workflowMarkers(comments, FLAKY_SPEC_PATTERN).map(({ data }) => data?.test).filter((test) => typeof test === 'string' && test);
}
// Paths the agent must never change; specs/<folder>/tasks.md may only receive the target task's tick.
export const PROTECTED_PREFIXES = ['.github/', '.specify/', 'specs/'];
// The solution's environment actions. Agents may change them, but only in a standalone environment spec, whose
// pull request always waits for a person's review.
export const ENVIRONMENT_PREFIXES = ['.github/actions/environment-setup/', '.github/actions/environment-verify/'];

export const isEnvironmentPath = (file) => ENVIRONMENT_PREFIXES.some((prefix) => file.startsWith(prefix));
export const isProtectedPath = (file) => !isEnvironmentPath(file) && PROTECTED_PREFIXES.some((prefix) => file.startsWith(prefix));

// Reasons why a branch mixes changes to the environment actions with other changes. Archive reference rewrites
// outside the spec's folder do not count as other changes.
export function environmentExclusivityReasons({ folder, branchPaths, referenceRewrites = [] }) {
  if (!branchPaths.some(isEnvironmentPath)) return [];
  const tasksPaths = [tasksPathOf(folder), tasksPathOf(folder, { archived: true })];
  const rewritten = new Set(referenceRewrites);
  const others = branchPaths.filter((file) => !isEnvironmentPath(file) && !tasksPaths.includes(file) && !rewritten.has(file));
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

// The branch of a new prerequisite spec pull request (`prefix` FIX_BRANCH_PREFIX for one opened earlier).
export function fixBranch(folder, prefix = SPEC_BRANCH_PREFIX) {
  return `${prefix}${folder}`;
}

// Whether a branch carries a prerequisite spec pull request authored by `/speckit author` (earlier `/speckit fix`), new or legacy.
export function isPrerequisiteBranch(branch) {
  const value = String(branch ?? '');
  return PREREQUISITE_BRANCH_PREFIXES.some((prefix) => value.startsWith(prefix) && value.length > prefix.length);
}

// The spec folder of an implementation, amendment, or prerequisite spec branch, or null.
export function folderOfBranch(branch) {
  const value = String(branch ?? '');
  for (const prefix of [AMEND_BRANCH_PREFIX, ...PREREQUISITE_BRANCH_PREFIXES, BRANCH_PREFIX]) {
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

// Guidance a person gave with `/speckit retry` or `/speckit sync`, carried in the workflow comment.
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

// The comment that starts a new attempt window; it persists the retry, so a replaced run cannot lose
// it. Guidance applies to the stopped task, convergence, or conflict resolution until it succeeds. `state` records the
// implementation branch head and the base branch head at the restart (see unchangedRestarts).
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

// A person's `/speckit retry` that leaves the implementation branch where it was stops
// the same way again unless something else changed. After this many restarts in a row on the same head, the commands
// refuse another one while the implementation is stopped, unless the person insists (RESTART_FORCE_FLAG).
export const MAX_UNCHANGED_RESTARTS = 2;
export const RESTART_FORCE_FLAG = '--force';
const RESTART_STATE_PATTERN = /^<!-- speckit-implement:restart-state (\{[^\n]*?\}) -->$/m;
const renderRestartState = ({ head, base = null }) => `<!-- speckit-implement:restart-state ${JSON.stringify({ head, base })} -->`;

// The latest restarts by a person, newest first, as long as each recorded `head` as the implementation branch head:
// the restarts since which the branch has not changed. A workflow restart without a recorded state (a merged
// amendment, an implemented prerequisite) ends the run of unchanged restarts.
export function unchangedRestarts(comments, head) {
  const restarts = [];
  const ordered = [...comments]
    .filter((comment) => comment.user?.login === BOT_LOGIN && RESTART_COMMENT_MARKERS.some((marker) => startsWithMarker(comment.body, marker)))
    .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at));
  for (const comment of ordered) {
    let state = null;
    try {
      state = JSON.parse(String(comment.body).match(RESTART_STATE_PATTERN)?.[1] ?? 'null');
    } catch {
      // A damaged marker counts as unknown.
    }
    if (!state?.head || state.head !== head) break;
    restarts.push({ kind: 'retry', head: state.head, base: state.base ?? null, at: comment.created_at });
  }
  return restarts;
}

const STOP_PATTERN = /\*\*Implementation needs attention\*\*(?: @[A-Za-z0-9-]+)?: ([^\n]*)/;
// Workflow and command links in a stop reason change with every run; they do not make two stops different.
const stopReason = (body) => String(body ?? '').match(STOP_PATTERN)?.[1].replace(/\]\([^)]*\)/g, ']').replace(/https?:\/\/\S+/g, '').trim() ?? null;

// The comment command that already failed the same way: the latest `/speckit retry` or `/speckit sync` recorded the
// implementation branch head `head` (so nothing changed since), and the stop after it gave the same reason as the stop
// before it. Recommending that command again would only repeat the failure. Returns { verb, reason } or null.
export function repeatedFailure(comments, head) {
  const ordered = [...comments]
    .filter((comment) => comment.user?.login === BOT_LOGIN)
    .sort((a, b) => Date.parse(a.created_at) - Date.parse(b.created_at));
  const verbOf = (body) => (startsWithMarker(body, SYNC_COMMENT_MARKER) ? 'sync' : RESTART_COMMENT_MARKERS.some((marker) => startsWithMarker(body, marker)) ? 'retry' : null);
  const restartAt = ordered.findLastIndex((comment) => verbOf(comment.body) && RESTART_STATE_PATTERN.test(String(comment.body)));
  if (restartAt < 0) return null;
  let state = null;
  try {
    state = JSON.parse(String(ordered[restartAt].body).match(RESTART_STATE_PATTERN)[1]);
  } catch {
    return null;
  }
  if (!state?.head || state.head !== head) return null;
  const before = ordered.slice(0, restartAt).map((comment) => stopReason(comment.body)).filter(Boolean).at(-1);
  const after = ordered.slice(restartAt + 1).map((comment) => stopReason(comment.body)).filter(Boolean)[0];
  return before && after && before === after ? { verb: verbOf(ordered[restartAt].body), reason: after } : null;
}

// The reason of the latest stop for a person, or null.
export function latestStopReason(comments) {
  return [...comments]
    .filter((comment) => comment.user?.login === BOT_LOGIN)
    .sort((a, b) => Date.parse(a.created_at) - Date.parse(b.created_at))
    .map((comment) => stopReason(comment.body))
    .filter(Boolean)
    .at(-1) ?? null;
}

// Where the guidance of a `/speckit retry` or `/speckit sync` goes next, for the reply that acknowledges the command.
// Guidance only steers an agent's prompt; it never changes which work runs, the checks, the limits, or verification.
export function describeGuidanceReach(verb, { continuation = null, lastStop = null } = {}) {
  if (verb === 'retry') {
    if (continuation?.repair) return 'The guidance goes to the repair agent, which runs before the reverification (without guidance, the retry only reverifies).';
    const next = continuation?.text ? ` (the implementation ${continuation.text})` : '';
    return `The guidance is added to the prompt of the first work that runs${next} and ends after its first success or a person's push; it changes no check, limit, or verification.`;
  }
  if (verb === 'sync') {
    const early = lastStop && /conflicts in protected paths/.test(lastStop)
      ? ` The last stop came before any agent ran (${lastStop}), so the guidance alone cannot change that outcome.`
      : '';
    return `The guidance reaches Copilot only if conflicts remain after the merge (conflicts that archive reference rewrites cause under \`specs/\` are resolved by script); after a clean merge it goes to the first work that continues.${early}`;
  }
  return '';
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

// The next group of tasks: the first unticked task and the unticked tasks after it in the same phase that run with it.
// A `[P]` task starts a parallel group of the consecutive unticked `[P]` tasks under its heading (also a single one);
// any other task starts a sequential group of the consecutive unticked tasks without `[P]`, up to the next `[P]` task
// or the end of the phase. Ticked tasks in between are skipped. At most `maxTasks` (GitHub's matrix limit); the rest
// follows in the next group. `completesPhase` tells whether the group finishes its phase, which is then verified in
// a run of its own.
export function planGroup(tasksMarkdown, { maxTasks = MAX_MATRIX_JOBS_PER_RUN } = {}) {
  const tasks = tasksWithSections(tasksMarkdown);
  const start = tasks.findIndex((task) => !task.done);
  if (start < 0) return null;
  const first = tasks[start];
  const open = tasks.slice(start).filter((task) => task.phase === first.phase && !task.done);
  const belongs = (task) => (first.parallel ? task.parallel && task.section === first.section : !task.parallel);
  const group = [];
  for (const task of open) {
    if (group.length >= maxTasks || !belongs(task)) break;
    group.push(task);
  }
  return {
    mode: first.parallel ? 'parallel' : 'sequential',
    tasks: group.map((task) => task.id),
    phase: headingText(first.phase),
    completesPhase: group.length === open.length,
  };
}

// The phases of tasks.md in order, each with its name (the heading without "## ", null without phase headings), its
// task IDs, and whether every task is ticked.
export function listPhases(tasksMarkdown) {
  const phases = [];
  for (const task of tasksWithSections(tasksMarkdown)) {
    if (phases.at(-1)?.key !== task.phase) phases.push({ key: task.phase, name: headingText(task.phase), tasks: [], done: true });
    phases.at(-1).tasks.push(task.id);
    if (!task.done) phases.at(-1).done = false;
  }
  return phases.map(({ name, tasks, done }) => ({ name, tasks, done }));
}

// Whether the next phase has not started yet: none of its tasks is ticked. Only then is the base branch merged before
// it, so retries and continuations of a phase build on the same base as its first tasks.
export function phaseNotStarted(tasksMarkdown) {
  const current = describeParallelGroups(tasksMarkdown).phases.find((phase) => phase.open > 0);
  return Boolean(current) && current.done === 0;
}

// Every phase of tasks.md with the order its unticked tasks will run in (consecutive `[P]` tasks under one heading together, as planGroup groups them), for
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
// for example "Spec Kit implement spec #35 · Recording Lineage and Upload" or "Spec Kit implement task #35 · T013 [P] [US1]
// Create …". Job names stay shorter (see taskJobName).
export function renderImplementTitle(workflow, twin, subject = '') {
  return `${IMPLEMENT_WORKFLOW_NAMES[workflow]} #${twin}${subject ? ` · ${subject}` : ''}`;
}

// Run name of the implementation chain of a twin: the spec title and what the run works on (renderRunSubject), for
// example "Spec Kit implement spec #35 · Recording Lineage and Upload · Phase 3: User Story 1 · T013 to T016 one after
// another". The workflow's run-name renders the same from its inputs.
export function renderImplementRunName(twin, specTitle = '', subject = '') {
  return renderImplementTitle('spec', twin, [specTitle, subject].filter(Boolean).join(' · '));
}

export function isImplementRunOf(title, twin) {
  const name = renderImplementRunName(twin);
  return title === name || String(title ?? '').startsWith(`${name} `);
}

// Diagnose, analyze, and fix runs are named "<workflow> spec #<twin> · <why the run started>" like the implementation
// chain (run-name in their workflow sources). Runs started earlier are named "<workflow> · spec #<twin> · …",
// "[<workflow>] spec #<twin> · …", or "<workflow> #<twin> · …".
const TWIN_RUN_NAMES = new Map([[DIAGNOSE_WORKFLOW_FILE, 'Spec Kit diagnose'], [ANALYZE_WORKFLOW_FILE, 'Spec Kit analyze'], [FIX_WORKFLOW_FILE, 'Spec Kit author']]);

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
        await client.ensureLabel({ name: BLOCKED_LABEL, color: 'd93f0b', description: 'Implementation is on hold waiting for a prerequisite' });
        await client.addLabels(number, [BLOCKED_LABEL]);
      }
      if (names.includes(ATTENTION_LABEL)) await client.removeLabel(number, ATTENTION_LABEL);
    }
    if ((!blocked || pull.state !== 'open') && has) await client.removeLabel(number, BLOCKED_LABEL);
  } catch {
    // The label only signals the state; comments and check runs carry it.
  }
}

// Why an implementation waits for the open blocked-by dependencies of its twin, for setBlocked. `blockers` are twin
// numbers or the twin issues; `pulls` maps a blocker twin number to the number of its open implementation pull request
// (explainDependencyBlock). The explanation sorts every blocker into what it needs:
// - an open implementation pull request: the pull request to finish first; nothing to do here;
// - no implementation request (isImplementRequested): it never starts by itself, so the explanation recommends
//   requesting it with `/speckit implement` on its spec twin, for every such blocker;
// - requested, but its implementation pull request is not open yet: the scheduler starts it; nothing to do;
// - a bare number (the twin was not read): it waits until the blocker is implemented.
// The key holds the blockers, the unrequested ones, and the blocking pull requests, so the explanation is posted again
// when a blocker is requested or its implementation pull request opens.
export function dependencyBlock(blockers, { pulls = new Map() } = {}) {
  const issues = new Map();
  for (const blocker of blockers) {
    const number = Number(typeof blocker === 'object' && blocker !== null ? blocker.number : blocker);
    if (!issues.has(number) || typeof blocker === 'object') issues.set(number, typeof blocker === 'object' ? blocker : null);
  }
  const numbers = [...issues.keys()].sort((a, b) => a - b);
  const pullOf = (number) => (Number.isInteger(pulls.get(number)) ? pulls.get(number) : null);
  const withPull = numbers.filter((number) => pullOf(number) !== null);
  const unrequested = numbers.filter((number) => pullOf(number) === null && issues.get(number) && !isImplementRequested(issues.get(number)));
  const starting = numbers.filter((number) => pullOf(number) === null && issues.get(number) && isImplementRequested(issues.get(number)));
  const unknown = numbers.filter((number) => pullOf(number) === null && !issues.get(number));
  const refs = (list) => list.map((number) => `#${number}`).join(', ');
  const list = refs(numbers);
  const one = numbers.length === 1;
  const request = unrequested.length > 0
    ? [
      '',
      `**Recommended next step**: Comment the following on spec twin ${unrequested.length === 1 ? refs(unrequested) : `${refs(unrequested)} (on each)`} to request ${unrequested.length === 1 ? 'its' : 'their'} implementation.`,
      '',
      '```text',
      '/speckit implement',
      '```',
      '',
      `- **Why**: ${refs(unrequested)} ${unrequested.length === 1 ? 'is' : 'are'} not requested for implementation and ${unrequested.length === 1 ? 'has' : 'have'} no implementation pull request, so ${unrequested.length === 1 ? 'it never starts' : 'they never start'} by ${unrequested.length === 1 ? 'itself' : 'themselves'}, and this implementation waits for ${unrequested.length === 1 ? 'it' : 'them'}.`,
      `- **What it does**: Checks that the spec is ready (\`tasked\`, checklists checked), records who requested it, and starts its implementation ahead of other specs. Running \`/speckit-implement <folder>\` locally for its folder does the same.`,
    ]
    : [];
  const section = (title, items) => (items.length > 0 ? ['', title, '', ...items] : []);
  return {
    key: { blockers: numbers, ...(unrequested.length > 0 ? { unrequested } : {}), ...(withPull.length > 0 ? { pulls: withPull.map(pullOf) } : {}) },
    lines: [
      `**Blocked** by prerequisite ${list}: the spec twin of this implementation depends on ${one ? 'it' : 'them'}, so this implementation waits instead of continuing.${unrequested.length > 0 ? '' : ' Nothing is needed on this pull request.'}`,
      ...request,
      ...section(`**Spec twin${unrequested.length === 1 ? '' : 's'} to request first** (no implementation pull request yet):`,
        unrequested.map((number) => `- #${number}: not requested; comment \`/speckit implement\` on #${number} to start its implementation.`)),
      ...section(`**Implementation pull request${withPull.length === 1 ? '' : 's'} to finish first**:`,
        withPull.map((number) => `- #${pullOf(number)} implements #${number}; this implementation resumes once it is merged.`)),
      ...section(`**Requested, starting by ${starting.length === 1 ? 'itself' : 'themselves'}**:`,
        starting.map((number) => `- #${number}: requested for implementation; its implementation pull request opens when the scheduler starts it, and it is listed here then. Nothing to do.`)),
      ...section('**Other prerequisites**:', unknown.map((number) => `- #${number}: this implementation waits until it is implemented.`)),
      '',
      `- **Unblocks by itself** once ${list} ${one ? 'is' : 'are'} implemented: this implementation then continues with a fresh attempt count on its current phase base.`,
      `- **To follow the prerequisite**: watch ${list} and ${one ? 'its' : 'their'} implementation pull request; if that implementation stops, it asks for help there.`,
      '- **To stop waiting**: remove the dependency from the spec twin; the block is then lifted.',
      '- If a prerequisite is closed without being implemented, the block is lifted and this pull request lists what you can do next.',
    ],
  };
}

// dependencyBlock for blocker twin issues, with the open implementation pull request of each (its `speckit/<folder>`
// branch, from the twin's spec link line). A lookup that fails leaves that blocker without a pull request.
export async function explainDependencyBlock(client, blockers) {
  const pulls = new Map();
  for (const blocker of blockers) {
    if (typeof blocker !== 'object' || blocker === null) continue;
    const folder = parseTwinFolder(blocker.body);
    if (!folder) continue;
    try {
      const pull = (await client.listPullRequestsForHead(implementationBranch(folder))).find((item) => item.state === 'open');
      if (pull) pulls.set(Number(blocker.number), pull.number);
    } catch {
      // The explanation still names the blocker twin.
    }
  }
  return dependencyBlock(blockers, { pulls });
}

// The open blocked-by twins of a spec twin.
export async function openPrerequisites(client, twin) {
  return (await client.listBlockedBy(twin)).filter((blocker) => blocker.state === 'open');
}

// Scheduler side, for an open implementation pull request of `twin`: blocks it while its twin has open blocked-by
// dependencies, whoever added them. Once none is open (and `hold`, a prerequisite spec still under review, is false), a
// block that is not lifted yet is lifted once (UNBLOCKED_MARKER): the implementation retries with a fresh attempt count
// on its current phase base, or, when a prerequisite was closed without being implemented, asks for a person.
export async function maintainDependencyBlock(client, env, { implementation, twin, hold = false }, { report = () => {} } = {}) {
  const blockers = await client.listBlockedBy(twin);
  const open = blockers.filter((blocker) => blocker.state === 'open');
  if (open.length > 0) {
    await setBlocked(client, implementation.number, true, { explain: await explainDependencyBlock(client, open) });
    return { outcome: 'blocked', blocked: true, blockers: open.map((blocker) => blocker.number) };
  }
  if (hold) return { outcome: 'held', blocked: true, blockers: [] };
  const pull = await client.getPullRequest(implementation.number);
  if (pull.state !== 'open' || !(pull.labels ?? []).some((label) => (label?.name ?? label) === BLOCKED_LABEL)) return { outcome: 'none', blocked: false, blockers: [] };
  const comments = await client.listIssueComments(pull.number);
  const blocks = currentBlocks(comments);
  if (blocks.length === 0) {
    await setBlocked(client, pull.number, false);
    return { outcome: 'cleared', blocked: false, blockers: [] };
  }
  // The prerequisites of this block: those its explanations name, and the twins of linked prerequisite specs.
  const named = new Set(blocks.flatMap((key) => (Array.isArray(key.blockers) ? key.blockers : [])));
  for (const event of parseFixEvents(comments)) if (event.event === 'linked' && Number.isInteger(event.twin)) named.add(event.twin);
  const closed = blockers.filter((blocker) => named.has(blocker.number));
  const list = (items) => items.map((item) => `#${item.number}`).join(', ');
  const notImplemented = closed.filter((blocker) => blocker.state_reason !== 'completed');
  const implemented = closed.filter((blocker) => blocker.state_reason === 'completed');
  const folder = folderOfBranch(pull.head?.ref);
  if (notImplemented.length > 0) {
    await client.createComment(pull.number, [
      UNBLOCKED_MARKER,
      `**A prerequisite was not implemented**: ${list(notImplemented)} was closed without being implemented. The block is lifted, and this implementation stays stopped.`,
      '',
      ...renderNextSteps({ diagnosed: true, ...(await nextStepContext(client, pull, folder)) }),
    ].join('\n'));
    await setBlocked(client, pull.number, false);
    await setAttention(client, pull.number, true);
    report(`- A prerequisite of #${pull.number} was not implemented (${list(notImplemented)} closed); it needs a person.`);
    return { outcome: 'stopped', blocked: false, blockers: [] };
  }
  const lead = [
    UNBLOCKED_MARKER,
    implemented.length > 0
      ? `**The block is lifted**: prerequisite ${list(implemented)} ${implemented.length === 1 ? 'is' : 'are'} implemented. The implementation retries with a fresh attempt count on its current phase base.`
      : '**The block is lifted**: the spec twin has no open prerequisites anymore. The implementation retries with a fresh attempt count on its current phase base.',
  ].join('\n');
  const guidance = implemented.length > 0
    ? `This implementation waited for the spec twin(s) ${list(implemented)}, which are now implemented on the base branch. Continue as planned; do not work around what they provide in this spec.`
    : '';
  await client.createComment(pull.number, renderRetryComment(lead, guidance));
  await setBlocked(client, pull.number, false);
  try {
    await dispatchImplementation(client, env, { twin, pull: pull.number, title: specTitleOfPull(pull) });
  } catch {
    // The retry comment persists the request; the next scheduler run continues the implementation.
  }
  report(`- The prerequisites of #${pull.number} are implemented${implemented.length > 0 ? ` (${list(implemented)})` : ''}; lifted its block and retried it.`);
  return { outcome: 'retried', blocked: false, blockers: [] };
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

// The name of a phase in run names and reports: the phase heading of tasks.md exactly as written (planGroup already removed the
// "## "); a tasks.md without phase headings has a single unnamed phase.
export function phaseSubject(phase) {
  return String(phase ?? '').trim() || 'Tasks';
}

// The short name of a phase in run names, so the run's tasks stay visible: the heading up to an explanation after " - "
// or " (" ("Phase 3: User Story 1" for "Phase 3: User Story 1 - A real … (Priority: P1)"), and only "Phase 3" when that
// is still longer than 40 characters.
export function shortPhaseSubject(phase) {
  const full = phaseSubject(phase);
  const short = full.split(/\s+[-–—]\s+|\s+\(/)[0].trim();
  if (short.length <= 40) return short;
  return full.match(/^Phase\s+\d+/i)?.[0] ?? `${short.slice(0, 39).trimEnd()}…`;
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
// spec title, which comes from the implementation pull request unless `title` is given, and what it will work on,
// decided now without changing anything (predictNextRun), which also selects the workflow of its kind; the run decides
// again when it starts and hands over to the right workflow when the state changed in the meantime.
export async function dispatchImplementation(client, env, { twin, pull, title, subject = undefined }) {
  let specTitle = title;
  if (!specTitle) {
    try {
      specTitle = specTitleOfPull(await client.getPullRequest(Number(pull)));
    } catch {
      specTitle = '';
    }
  }
  let next = subject;
  let workflow = IMPLEMENT_WORKFLOW_FILE;
  if (next === undefined) {
    try {
      ({ subject: next, workflow } = await (await import('./speckit-spec.mjs')).predictNextRun(client, env, { twin, pull }));
    } catch {
      next = '';
    }
  }
  await client.dispatchWorkflow(workflow, defaultBranchOf(env), { issue: String(twin), pull: String(pull), ...(specTitle ? { title: specTitle } : {}), ...(next ? { subject: next } : {}) });
}

// The attempt marker of an outcome comment. `kind` is what the attempt works on: task, repair, converge, resolve, or verify
// (the verification of a finished phase, named by `phase`). `outcome` is success, failure, requeue (redone from a
// newer head; not a failure), or attention (a person is needed).
export function renderAttemptMarker({ kind, task = null, attempt, outcome, phase = undefined }) {
  return `<!-- speckit-implement:attempt ${JSON.stringify({ kind, task: task || null, attempt: Number(attempt), outcome, ...(phase !== undefined ? { phase } : {}) })} -->`;
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
      if (!['task', 'repair', 'converge', 'resolve', 'verify'].includes(kind) || !['success', 'failure', 'requeue', 'attention'].includes(value.outcome)) continue;
      attempts.push({ kind, task: value.task ?? null, attempt: Number(value.attempt) || 0, outcome: value.outcome, at: comment.created_at, ...(kind === 'verify' ? { phase: value.phase ?? null } : {}) });
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
  const match = String(externalId ?? '').match(/^speckit:attempt:(task|repair|converge|resolve|verify):(T\d{3,})?:(\d+)$/);
  return match ? { kind: match[1], task: match[2] ?? null, attempt: Number(match[3]) } : null;
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
    .filter((comment) => comment.user?.login === BOT_LOGIN && startsWithMarker(comment.body, marker))
    .map((comment) => comment.created_at);
}

// Neutralizes HTML comments in untrusted text before it is posted, so it cannot carry workflow markers.
export function neutralizeMarkers(text) {
  return String(text ?? '').replaceAll('<!--', '&lt;!--');
}

const checkTime = (check) => Date.parse(check.started_at ?? check.created_at ?? 0);

// Decides how the implementation chain continues an open implementation pull request. `checks` are the
// `Spec Kit implementation` check runs of the current head; `attempts` the attempt ledger (parseAttempts);
// `windowStart` starts the current attempt window (pull request, retry, or a person's push); `done` tells whether
// the implementation was finalized for review; `resumedAt` is the latest restart time.
// Order: a conflict resolution when merging the base branch before the next phase or the convergence conflicted on
// this head (best effort, see MAX_SYNC_RESOLVE_ATTEMPTS) → the verification of a finished phase whose tasks this chain
// landed after its last successful verification (verifyNeeded) → the next group of tasks (planGroup) → converge (until a successful convergence is newer than the last successful task) → merge, or resolve after a merge
// (or merging the base branch before it) found conflicts on this head. Only failures
// count against the attempt limit; all outcomes (including redos from a newer head) are capped separately so no-op
// attempts cannot loop forever. A stop that needs a person (attempt limit, attention, or a diagnosis that runs or waits
// for a decision) holds everything until a person pushes or retries.
export function decideNext({ tasksMarkdown, checks = [], attempts = [], windowStart, done = false, resumedAt = null, now = Date.now(), maxEntries = MAX_MATRIX_JOBS_PER_RUN }) {
  const mergeChecks = checks.filter((check) => check.external_id === CHECK_MERGE);
  const stale = mergeChecks.filter((check) => check.status !== 'completed' && now - checkTime(check) >= MERGE_STALE_MS);
  if (mergeChecks.some((check) => check.status !== 'completed' && !stale.includes(check))) return { action: 'wait' };
  const latest = latestCheckRun(checks);
  if (done || (latest?.external_id === CHECK_DONE && latest.conclusion === 'success')) return { action: 'done' };
  const diagnosing = latest?.external_id === CHECK_DIAGNOSING && latest.status !== 'completed';
  const failed = (latest?.external_id === CHECK_LIMIT && latest.conclusion === 'failure') || diagnosing || latest?.external_id === CHECK_DIAGNOSED;
  // A retry comment newer than the stop starts a fresh attempt window.
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

  // An already-current required sync can succeed without moving the head, leaving old conflict checks on it.
  const unresolved = (check) => !attempts.some((attempt) => attempt.kind === 'resolve' && attempt.outcome === 'success' && Date.parse(attempt.at) > checkTime(check));
  // Merging the base branch before a phase or the convergence conflicted on this head: resolve first, best effort.
  // After MAX_SYNC_RESOLVE_ATTEMPTS failed attempts, or one that needs a person, the work continues on its base.
  let syncGivenUp = null;
  const syncConflict = latestCheckRun(checks.filter((check) => check.external_id === CHECK_SYNC_CONFLICT && unresolved(check)));
  if (syncConflict) {
    const list = inWindow.filter((attempt) => attempt.kind === 'resolve' && Date.parse(attempt.at) >= checkTime(syncConflict));
    const failures = list.filter((attempt) => attempt.outcome === 'failure').length;
    const handedOver = list.some((attempt) => attempt.outcome === 'attention');
    if (!handedOver && failures < MAX_SYNC_RESOLVE_ATTEMPTS && list.length < MAX_SYNC_RESOLVE_ATTEMPTS * 2) {
      return { action: 'tasks', kind: 'resolve', sync: true, order: [[{ task: null, attempt: list.length + 1 }]], stale };
    }
    syncGivenUp = syncConflict;
  }
  const givenUp = syncGivenUp ? { syncGivenUp } : {};
  const verify = verifyNeeded(tasksMarkdown, attempts);
  if (verify) {
    const count = tally((attempt) => attempt.kind === 'verify' && attempt.phase === verify.name);
    if (exceeded(count)) return { action: 'limit', kind: 'verify', phase: verify.name, attempts: count.total, stale };
    return { action: 'verify', kind: 'verify', phase: verify.name, tasks: verify.tasks, attempt: count.total + 1, stale, ...givenUp };
  }
  const group = planGroup(tasksMarkdown, { maxTasks: maxEntries });
  if (group) {
    const ofTask = (id) => tally((attempt) => attempt.kind === 'task' && attempt.task === id);
    for (const id of group.tasks) {
      const count = ofTask(id);
      if (exceeded(count)) return { action: 'limit', kind: 'task', task: id, attempts: count.total, stale };
    }
    const entries = group.tasks.map((id) => ({ task: id, attempt: ofTask(id).total + 1 }));
    // The order of the run: a sequential group has one entry per task (run one after another), a parallel group one
    // entry with all its tasks (run at the same time).
    return {
      action: 'tasks',
      kind: 'task',
      mode: group.mode,
      order: group.mode === 'parallel' ? [entries] : entries.map((entry) => [entry]),
      phase: group.phase,
      completesPhase: group.completesPhase,
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
  } else if (latestCheckRun(checks.filter((check) => [CHECK_MERGE, CHECK_CONFLICT].includes(check.external_id) && (check.external_id !== CHECK_CONFLICT || unresolved(check))))?.external_id === CHECK_CONFLICT) {
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
  return { action: 'tasks', kind, order: [[{ task: null, attempt: count.total + 1 }]], stale, ...(kind === 'converge' ? givenUp : {}) };
}

// The first finished phase whose tasks this chain landed after the phase's last successful verification (attempt kind
// verify), or null. Phases ticked before the chain ran (no successful task attempt) need no verification here; the
// merge verifies everything.
export function verifyNeeded(tasksMarkdown, attempts) {
  const at = (attempt) => Date.parse(attempt.at);
  for (const phase of listPhases(tasksMarkdown)) {
    if (!phase.done) return null;
    const ids = new Set(phase.tasks);
    const landed = Math.max(0, ...attempts.filter((attempt) => attempt.kind === 'task' && attempt.outcome === 'success' && ids.has(attempt.task)).map(at));
    const verified = Math.max(0, ...attempts.filter((attempt) => attempt.kind === 'verify' && attempt.outcome === 'success' && attempt.phase === phase.name).map(at));
    if (landed > verified) return phase;
  }
  return null;
}

// What one run of the chain works on, for its name: the short phase name and the group's tasks ("Phase 3: User Story 1 ·
// T010 to T014 one after another", "… · T015, T016 in parallel", "… · T010 Create the model" for a single task), the verification of a phase,
// a repair, the convergence, a conflict resolution, the merge, or the stop the run reports; empty when the run has
// nothing to do. `summaries` maps task IDs to their summarized text.
export function renderRunSubject(decision, { summaries = new Map() } = {}) {
  const clip = (text) => (text.length > 120 ? `${text.slice(0, 119).trimEnd()}…` : text);
  if (decision.action === 'verify') return clip(`Verify ${shortPhaseSubject(decision.phase)}`);
  if (decision.action === 'repair') return `Repair after the failed ${decision.repair?.scope ? `${decision.repair.scope} ` : ''}verification`;
  if (decision.action === 'merge') return 'Merge';
  if (decision.action === 'limit') return `Stop at the attempt limit of ${kindLabel(decision)}`;
  if (decision.action === 'failed') return 'Diagnose the stop';
  if (decision.action !== 'tasks') return '';
  if (decision.requiredSync) return 'Required base synchronization';
  if (decision.kind !== 'task') return kindLabel(decision).replace(/^./, (char) => char.toUpperCase());
  const ids = decision.order.flat().map((entry) => entry.task);
  const tasks = ids.length === 1
    ? `${ids[0]} ${summaries.get(ids[0]) ?? ''}`.trim()
    : `${ids.length <= 3 ? ids.join(', ') : `${ids[0]} to ${ids.at(-1)}`} ${decision.mode === 'parallel' ? 'in parallel' : 'one after another'}`;
  return clip(`${shortPhaseSubject(decision.phase)} · ${tasks}`);
}
// Human-readable name of what an attempt works on, for comments and summaries.
export function kindLabel({ kind, task }) {
  if (kind === 'task') return task;
  return { converge: 'convergence', resolve: 'conflict resolution', merge: 'merge', verify: 'phase verification' }[kind] ?? kind;
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
  // An unchanged tasks.md is expected: the land job checks the task after verification (runLand).
  if (differing === null || differing.length > 1 || (differing.length === 1 && !ticked(differing[0]))) {
    reasons.push(`\`${tasksPath}\` may only change by checking ${taskId}`);
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

// The comment commands, all in the form `/speckit <command> [flags] [guidance]`; `flags` lists the flags each command
// supports, and `scope` where it works: `pr` on an implementation pull request (or an amendment pull request of it),
// `twin` on a spec twin issue, `any` on both. An amendment pull request needs none: merging it applies the amendment,
// closing it discards it, and a comment or review asks for a rework.
// This list is the single source for everything that names the commands: the command router and its help, and the
// diagnosis (which commands it may recommend, for which categories, and the command reference in its evidence).
// `recommend` marks commands a diagnosis may recommend; `categories` the diagnosis categories they fit;
// `whenToRecommend` and `guidanceScope` brief the diagnosis agent.
export const PR_COMMANDS = [
  {
    name: 'retry',
    usage: '/speckit retry [--force] [guidance]',
    scope: 'pr',
    flags: { [RESTART_FORCE_FLAG]: 'restart even after 2 restarts in a row that left the branch unchanged' },
    help: 'restart what stopped with a fresh attempt count while preserving the current phase base; guidance goes into the next attempt\'s prompt. Refused after 2 restarts in a row that left the branch unchanged, unless `--force` is given',
    recommend: true,
    categories: ['retry', 'decision'],
    whenToRecommend: 'The artifacts are adequate and running the work again with concrete guidance is expected to pass (`retry`), or a person answers the questions of a trade-off (`decision`, the recommended answers as guidance). With a pending repair, add guidance only when a code change is needed: guidance runs a repair agent on the landed code, a bare command only reverifies. Use `--force` only when guidance that differs materially from what was tried can help after 2 unchanged restarts.',
    guidanceScope: 'the first work that continues: the retried task group, the convergence, the repair, or the conflict resolution',
  },
  {
    name: 'sync',
    usage: '/speckit sync [guidance]',
    scope: 'pr',
    flags: {},
    help: 'require merging the current pull request base and successful verification before any work continues, even when automatic sync is disabled or was skipped; Copilot resolves conflicts. Pending repair scope and attempt limits are preserved',
    recommend: true,
    categories: ['outside'],
    whenToRecommend: 'The fix of a cause outside this spec is already on the base branch, or tooling under `.github/` or a non-code cause was fixed there: the merge and its verification must happen before work continues.',
    guidanceScope: 'the conflict resolution when the merge conflicts, otherwise the first prompted work after the clean merge (it is kept until that work succeeds)',
  },
  {
    name: 'author',
    usage: '/speckit author [guidance]',
    scope: 'pr',
    flags: {},
    help: 'author a new spec for a cause outside this spec with the Spec Kit workflow `author`; it is checked and merged to the default branch automatically, and this implementation waits until that spec is signed off with `/speckit implement` on its twin and implemented',
    recommend: true,
    categories: ['outside'],
    whenToRecommend: 'The cause lies in code, tests, or environment actions owned by another feature on the base branch and is not fixed there yet: the guidance says what to fix, where, and how.',
    guidanceScope: 'the decision step that turns the cause into the feature description of the new spec',
  },
  {
    name: 'implement',
    usage: '/speckit implement',
    scope: 'twin',
    flags: {},
    help: 'request the implementation of this spec: checks that it is `tasked` with all checklists checked, records who requested it, and starts it on GitHub (it waits for open prerequisites)',
    recommend: false,
    categories: [],
  },
  { name: 'help', usage: '/speckit help', scope: 'any', flags: {}, help: 'list these commands', recommend: false, categories: [] },
];
export const COMMAND_SCOPES = Object.freeze({ pr: 'implementation pull request', twin: 'spec twin issue', any: 'anywhere' });

// The commands a diagnosis may recommend, and those that fit a category.
export const recommendableCommands = (commands = PR_COMMANDS) => commands.filter((command) => command.recommend).map((command) => command.name);
export function commandsForCategory(category, commands = PR_COMMANDS) {
  return commands.filter((command) => command.recommend && command.categories.includes(category)).map((command) => command.name);
}
export const RECOMMENDABLE_COMMANDS = Object.freeze(recommendableCommands());

// Splits the text after `/speckit <command>` into flags and guidance. Flags are the leading words that start with
// `--` (case-insensitive, trailing punctuation such as `--force;` or `--force,` tolerated); a bare `--` ends them, so
// guidance can itself start with `--`. Everything after the flags is the guidance. `flags` holds the supported flags
// of the command (lowercased, without duplicates), `unknownFlags` the others as given.
const FLAG_PATTERN = /^(--[a-z][a-z0-9-]*)[.,;:!]*$/i;
export function parseCommandArguments(name, argument) {
  const supported = Object.keys(PR_COMMANDS.find((command) => command.name === name)?.flags ?? {});
  const flags = [];
  const unknownFlags = [];
  let rest = String(argument ?? '').trim();
  for (;;) {
    const word = rest.match(/^\S+/)?.[0];
    if (!word) break;
    if (word === '--') {
      rest = rest.slice(word.length).trim();
      break;
    }
    const flag = word.match(FLAG_PATTERN)?.[1];
    if (!flag) break;
    const normalized = flag.toLowerCase();
    if (!supported.includes(normalized)) unknownFlags.push(flag);
    else if (!flags.includes(normalized)) flags.push(normalized);
    rest = rest.slice(word.length).trim();
  }
  return { flags, unknownFlags, guidance: rest };
}

// What a diagnosis category means, for the person who decides how to continue.
export const CATEGORY_MEANING = Object.freeze({
  artifacts: 'The spec, plan, or tasks are wrong or incomplete, so the work cannot be done as written. The correction belongs in the spec artifacts, not in code around them.',
  retry: 'The failure was transient (runner, network, timeout, rate limit, Copilot error, a flaky test with evidence), or the agent took a poor approach while the spec artifacts are fine. Running the work again is expected to pass.',
  outside: 'The cause is not in this spec: code or tests of another feature on the base branch, the environment or tooling, permissions, quotas, or an external service. It has to be fixed where it lives.',
  decision: 'Only a person can make this trade-off: scope, a success criterion, a risk, a split, or abandoning the work.',
  unknown: 'The evidence is not enough to name a cause yet.',
});

// The verb, the text after it, and its flags and guidance (parseCommandArguments) of a
// `/speckit <verb> [flags] [guidance]` command; null for anything else.
export function parseNextCommand(command) {
  const match = String(command ?? '').trim().match(/^\/speckit ([a-z]+)(?:[ \t]+([\s\S]*))?$/);
  if (!match) return null;
  const argument = String(match[2] ?? '').trim();
  return { verb: match[1], argument, ...parseCommandArguments(match[1], argument) };
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
  const forced = parsed.verb === 'retry' && parsed.flags.includes(RESTART_FORCE_FLAG);
  const argument = parsed.guidance;
  const flag = forced ? ` \`${RESTART_FORCE_FLAG}\` is a flag that bypasses the unchanged-restart guard, not guidance.` : '';
  const guidance = (argument
    ? 'The text after the command goes into the next attempt\'s prompt as guidance.'
    : 'Text you add after the command goes into the next attempt\'s prompt as guidance.') + flag;
  if (continuation?.repair && parsed.verb === 'retry') {
    return `Does not merge ${base}; ${argument ? 'runs a repair agent with the guidance, then reverifies' : 'reverifies without running a repair agent'} the pending ${continuation.repair.scope} verification. The original failed scope must pass before normal planning continues; if this repair attempt fails, the implementation stops for a person again. With guidance, a successful no-change repair also reaches reverification without an empty commit.${flag}`;
  }
  switch (parsed.verb) {
    case 'sync':
      return `Requires merging ${base} into the implementation branch and successful full verification before any work continues (Copilot resolves conflicts). A pending repair retains its original scope and attempt limit; full verification can clear it only when the original failed scope is covered and passes. ${guidance}`;
    case 'retry':
      return mergesAnyway
        ? `The implementation ${next}, ${attempts}. That work merges ${base} first at its normal synchronization boundary. ${guidance}`
        : `Does not merge ${base}: the implementation ${next} on the branch as it is, ${attempts}. ${guidance}`;
    case 'author':
      return [
        `The Spec Kit workflow \`author\` writes a new spec for the cause${argument ? ', guided by the text after the command,' : ''} and opens it as a pull request against ${base}; it is checked for consistency and merged automatically (a change of \`docs/architecture/\` or a person's push waits for your review instead).`,
        `This spec then depends on it; it starts once someone with write access signs it off with \`/speckit implement\` on its spec twin, this implementation waits for it, and then retries by itself on its current phase base, ${attempts}.`,
      ].join(' ');
    default:
      return null;
  }
}

// The command reference a diagnosis agent reads (evidence section "Commands you can recommend", and
// `speckit-diagnose.mjs commands`). Generated from PR_COMMANDS and describeCommandEffect, so it never falls behind the
// commands. With a pending `repair`, `retry` is described both with and without guidance.
export const COMMAND_REFERENCE_TITLE = 'Commands you can recommend';
export function renderCommandReference({ baseBranch = null, repair = null, commands = PR_COMMANDS } = {}) {
  const lines = [
    `## ${COMMAND_REFERENCE_TITLE}`,
    '',
    'Recommend exactly one of these, written as one line a person can post as it is: `/speckit <command> [flags] [guidance]`.',
    `Flags come right after the command; everything after them is guidance (at most ${MAX_WRITER_GUIDANCE} characters). Put \`--\` before guidance that itself starts with \`--\`.`,
    repair ? `A **${repair.scope} repair is pending** on this pull request${Array.isArray(repair.tasks) && repair.tasks.length > 0 ? ` (${repair.tasks.join(', ')})` : ''}.` : 'No repair is pending on this pull request.',
    '',
  ];
  for (const command of commands.filter((item) => item.recommend)) {
    const flags = Object.entries(command.flags ?? {});
    lines.push(
      `### \`${command.usage}\``,
      '',
      `- Fits the categories: ${command.categories.map((category) => `\`${category}\``).join(', ')}`,
      `- When: ${command.whenToRecommend}`,
      `- Flags: ${flags.length > 0 ? flags.map(([flag, help]) => `\`${flag}\` (${help})`).join('; ') : 'none'}`,
      `- Guidance reaches: ${command.guidanceScope}`,
    );
    const effect = (text) => describeCommandEffect(text, { baseBranch, continuation: repair ? { repair } : null }) ?? command.help;
    if (repair && command.name === 'retry') {
      lines.push(
        `- What it does with guidance: ${effect(`/speckit ${command.name} guidance`)}`,
        `- What it does without guidance: ${effect(`/speckit ${command.name}`)}`,
      );
    } else {
      lines.push(`- What it does: ${effect(`/speckit ${command.name} guidance`)}`);
    }
    lines.push('');
  }
  return lines.join('\n');
}

// What comes next when a stopped implementation retries: `text` as "continues with …", and
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
// A change only a push to the implementation branch can make (another spec's folder, a protected path, a decision a
// person took) is recommended as a prompt for GitHub Copilot's cloud agent: a person posts it as a comment, Copilot
// pushes the change to the pull request's branch, and that push continues the implementation like a person's push.
export const MAX_COPILOT_PROMPT = 3000;
export const COPILOT_SCOPE = 'Make only this change, keep it as small as possible, and push it to this pull request\'s branch. Do not edit or tick tasks in tasks.md, do not change files the request does not name, and do not open another pull request.';
const COPILOT_EFFECT = 'GitHub Copilot\'s cloud agent (it must be enabled for the repository) makes the change and pushes it to the implementation branch; that push continues the implementation by itself with a fresh attempt count. Check its commit on this pull request.';

// The comment that asks Copilot for the change: `@copilot`, the prompt, and the scope every such request keeps.
export function renderCopilotRequest(prompt) {
  const text = String(prompt ?? '').trim().replace(/^@copilot\b[\s,:]*/i, '').trim();
  return `@copilot ${text}\n\n${COPILOT_SCOPE}`;
}

// The `@copilot` request in the recommended next step of a bot comment, or null: the recommended block first, else
// the first one under the other options.
export function copilotRequestOf(body) {
  const text = String(body ?? '').replace(/\r\n/g, '\n');
  const blocks = [...text.matchAll(/^( *)```text\n((?:\1@copilot[^\n]*\n)(?:\1[^\n]*\n)*?)\1```$/gm)];
  if (blocks.length === 0) return null;
  const [, indent, content] = blocks[0];
  return content.split('\n').map((line) => (line.startsWith(indent) ? line.slice(indent.length) : line)).join('\n').trim();
}

const optionEffect = (option, context) => {
  if (option.effect) return option.effect;
  if (option.command) return describeCommandEffect(option.command, context);
  if (option.copilot) {
    return context.continuation?.repair
      ? `${COPILOT_EFFECT} After Copilot pushed, request \`/speckit retry\` to reverify the pending repair; a push alone does not clear it.`
      : COPILOT_EFFECT;
  }
  if (context.continuation?.repair) {
    if (option.kind === 'push') return 'After pushing a fix, request `/speckit retry` to reverify the pending repair; a push alone does not clear it. Closing the pull request abandons the implementation, and the twin returns to its computed stage.';
    if (option.kind === 'amendment') return 'Merging applies the amendment and retries the implementation with the pending repair verification before normal planning can continue. Comment or request changes there to have it reworked first.';
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

// How a person handles a spec pull request authored by `/speckit author`; shown in its description and referenced
// from the implementation.
export function renderFixHowTo(implementationPull, baseBranch = null) {
  const base = baseBranch ? `\`${baseBranch}\`` : 'the default branch';
  return [
    '**How to proceed** (people with write access):',
    '',
    `- **Merge** this pull request to add the spec to ${base}. It becomes a spec twin that the stopped spec depends on; it is implemented once someone signs it off with \`/speckit implement\` on its twin. #${implementationPull} waits for it and retries by itself once it is implemented.`,
    '- **Comment**, or **submit a review** with *Comment* or *Request changes* (line comments included), to have the spec reworked with your feedback. It returns here, checked again.',
    `- **Close** this pull request without merging to discard the spec; #${implementationPull} stays stopped and lists what you can do next.`,
  ];
}

// Links a prerequisite spec pull request opened by earlier tooling to the implementation pull request it unblocks
// (legacy, read only; pull requests are now found through the `created` event on the implementation pull request).
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

// The lifecycle of a prerequisite spec, recorded on the implementation pull request it unblocks by the workflow's
// comments: requested, created, merged or discarded, and linked (twin dependency and implementation request). landed
// and stopped were recorded by earlier tooling; lifting the block is now generic (maintainDependencyBlock).
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
// (an open amendment, else the recommended option, else waiting for the automatic diagnosis), then
// every other valid option, collapsed. Every option says why it helps and what it does, and every command can be
// posted as it is: no placeholders.
// - `options`: { title, why, command, copilot, recommended, link, kind } from a diagnosis (`copilot` is a prompt for
//   GitHub Copilot's cloud agent, rendered as a ready-to-post `@copilot` comment; `kind` names a fixed effect for an
//   option without a command or prompt: `amendment` or `push`).
// - `amendment`: an open amendment pull request ({ number, title, html_url, status }), or `amendmentPull` its number.
// - `baseBranch`: the implementation pull request's base; `behindBase`: its name when it has commits the
//   implementation branch lacks, else null.
// - `continuation`: what a restart continues with (continuationOf).
// - `diagnosed`: whether a diagnosis already reported.
export function renderNextSteps({ options = null, amendment = null, amendmentPull = null, baseBranch = null, behindBase = null, continuation = null, diagnosed = false, diagnosisUrl = null } = {}) {
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
    } else if (!diagnosed) {
      recommended = {
        title: 'Wait for the diagnosis',
        link: diagnosisUrl,
        why: 'It starts automatically, finds the cause, and posts a recommendation with a ready-to-post command here.',
        kind: 'wait',
      };
    } else {
      recommended = {
        title: 'Retry what stopped',
        why: 'The diagnosis did not identify another action that can be requested from this pull request.',
        command: '/speckit retry',
      };
    }
  }

  if (recommended.kind === 'wait' && diagnosisUrl && !recommended.link) recommended = { ...recommended, link: diagnosisUrl };
  const lines = [`**Recommended next step**: ${sentence(optionTitle(recommended))}`];
  const details = optionDetails(recommended, context);
  if (details.length > 0) lines.push('', ...details.map(([label, text]) => `- **${label}**: ${text}`));
  if (recommended.command) lines.push('', 'Comment this on the pull request (people with write access):', '', '```text', recommended.command, '```');
  else if (recommended.copilot) lines.push('', 'Comment this on the pull request (people with write access):', '', '```text', ...renderCopilotRequest(recommended.copilot).split('\n'), '```');

  // Commands the recommendation or an option already suggests are not repeated.
  const used = new Set([recommended, ...others].map((option) => parseNextCommand(option.command)?.verb).filter(Boolean));
  const generic = [];
  if (!used.has('retry')) {
    generic.push({
      title: 'Retry what stopped',
      why: 'If the failure looks transient (runner, network, timeout, a flaky test) and nothing needs to change.',
      command: '/speckit retry',
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
    else if (option.copilot) lines.push('', '  ```text', ...renderCopilotRequest(option.copilot).split('\n').map((line) => `  ${line}`), '  ```');
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
    'The Spec Kit implement workflows implement them group by group, in order; consecutive `[P]` tasks run in parallel, and every finished phase is verified. Each task is verified, committed, and reported here.',
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
