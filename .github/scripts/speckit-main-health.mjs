import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { createGitHubClient } from './speckit-script-runtime.mjs';
import { ATTENTION_LABEL, defaultBranchOf } from './speckit-implement-core.mjs';

// The health of the default branch: the workflow `Main health` (main-health.yml) verifies the default branch in full
// (environment-verify, finalize, in shards) after every push that changes more than Markdown and specs/, nightly, and
// on demand. A red run keeps one open tracking issue (label MAIN_HEALTH_LABEL) and starts the healer
// (speckit-heal.mjs, main-heal.yml); a green run closes it and starts the scheduler. While the issue or a healing pull
// request is open, the scheduler blocks every implementation and starts no spec.

export const MAIN_HEALTH_WORKFLOW_FILE = 'main-health.yml';
export const MAIN_HEALTH_LABEL = 'main-health';
export const MAIN_HEALTH_MARKER = '<!-- speckit:main-health -->';
const CAUSE_PATTERN = /<!-- speckit:main-health-cause (\{[^\n]*\}) -->/;
export const renderCause = (cause) => `<!-- speckit:main-health-cause ${JSON.stringify(cause)} -->`;
const GREEN_WINDOW_MS = 30 * 24 * 60 * 60 * 1000;
const MAX_ATTRIBUTED_COMMITS = 20;

// What the healer may change: product code and tests anywhere but the repository tooling, the two environment action
// folders, and no spec artifact.
export const HEALER_FORBIDDEN = [/^\.github\/(?!actions\/environment-(setup|verify)\/)/, /^\.specify\//, /^specs\//];
export const healerMayChange = (file) => !HEALER_FORBIDDEN.some((pattern) => pattern.test(file));

export function parseCause(body) {
  const match = String(body ?? '').match(CAUSE_PATTERN);
  if (!match) return null;
  try {
    return JSON.parse(match[1]);
  } catch {
    return null;
  }
}

// The commit of the latest green Main health run on the default branch within the last 30 days.
export async function lastGreenSha(client, env, { now = Date.now() } = {}) {
  const branch = defaultBranchOf(env);
  const runs = await client.listWorkflowRuns(MAIN_HEALTH_WORKFLOW_FILE, new Date(now - GREEN_WINDOW_MS).toISOString(), 10);
  const green = runs
    .filter((run) => run.status === 'completed' && run.conclusion === 'success' && run.head_sha && (!run.head_branch || run.head_branch === branch))
    .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at) || b.id - a.id)[0];
  return green?.head_sha ?? null;
}

// What made the default branch red: the changes between the last green verification and the failing commit, each
// attributed to its pull request. kind is spec (one Spec Kit implementation merge), pull (one other pull request),
// direct (one direct push), combined (several changes), drift (no change: SDKs, runner images, downloads, services, or
// a flaky test), or unknown (nothing to compare with). healable is false when every changed file is in a path the
// healer may not change, so a person has to fix it.
export async function attributeCause(client, env, { git, sha, now = Date.now() }) {
  let green = null;
  try {
    green = await lastGreenSha(client, env, { now });
  } catch {
    green = null;
  }
  if (!green) return { kind: 'unknown', red: sha, healable: true };
  if (green === sha) return { kind: 'drift', green, red: sha, commits: [], healable: true };
  const log = git(['log', '--first-parent', '--format=%H%x09%s', `${green}..${sha}`]);
  const diff = git(['diff', '--name-only', green, sha]);
  if (log.status !== 0 || diff.status !== 0) return { kind: 'unknown', green, red: sha, healable: true };
  const lines = log.stdout.split('\n').filter(Boolean);
  if (lines.length === 0) return { kind: 'drift', green, red: sha, commits: [], healable: true };
  const branch = defaultBranchOf(env);
  const commits = [];
  for (const line of lines.slice(0, MAX_ATTRIBUTED_COMMITS)) {
    const [commit, ...subject] = line.split('\t');
    let pull = null;
    try {
      pull = (await client.listPullRequestsForCommit(commit)).find((item) => item.merged_at && (!item.base?.ref || item.base.ref === branch)) ?? null;
    } catch {
      pull = null;
    }
    const head = String(pull?.head?.ref ?? '');
    const kind = !pull ? 'direct' : head.startsWith('speckit/') ? 'spec' : 'pull';
    commits.push({ sha: commit, subject: subject.join('\t').slice(0, 120), kind, ...(pull ? { pull: pull.number } : {}) });
  }
  const files = diff.stdout.split('\n').filter(Boolean);
  const blocked = files.filter((file) => !healerMayChange(file));
  return {
    kind: lines.length === 1 ? commits[0].kind : 'combined',
    green,
    red: sha,
    commits,
    ...(lines.length > commits.length ? { more: lines.length - commits.length } : {}),
    healable: files.length === 0 || blocked.length < files.length,
    ...(blocked.length > 0 ? { protected: blocked.slice(0, 10) } : {}),
  };
}

const short = (sha) => `\`${String(sha).slice(0, 12)}\``;
const describeCommit = (commit) => `${commit.pull ? `#${commit.pull} (${short(commit.sha)})` : `the direct push ${short(commit.sha)}`}: ${commit.subject}`;

// The cause section of the health issue, and what the healer does about it.
export function renderCauseLines(cause) {
  if (!cause) return [];
  const lines = ['**Cause**:'];
  if (cause.kind === 'unknown') lines.push('No green verification to compare with, so the healer searches the whole default branch.');
  else if (cause.kind === 'drift') lines.push(`Nothing changed since the last green verification of ${short(cause.green)}: the cause is outside the repository (an SDK, runner image, download, or service) or a flaky test. The healer pins, hardens, or makes it deterministic.`);
  else if (cause.kind === 'spec') lines.push(`Introduced by the merge of the Spec Kit implementation ${describeCommit(cause.commits[0])}. The healer fixes it forward; the implementation is not reverted.`);
  else if (cause.kind === 'pull' || cause.kind === 'direct') lines.push(`Introduced by ${describeCommit(cause.commits[0])}. The healer may revert it when that is the smallest correct fix.`);
  else {
    lines.push(`${cause.commits.length + (cause.more ?? 0)} changes since the last green verification of ${short(cause.green)}; the healer looks at each and at how they interact:`, '');
    lines.push(...cause.commits.map((commit) => `- ${commit.kind === 'spec' ? 'Spec Kit implementation ' : ''}${describeCommit(commit)}`));
    if (cause.more) lines.push(`- and ${cause.more} more`);
  }
  return lines;
}

// The failed shards of a combined verification log (speckit-verify-shards.mjs): "Shard 2/4 (failure): platform ...".
export function failedShards(log) {
  return [...String(log ?? '').matchAll(/^=== Shard (\S+) \((failure|cancelled|unknown)\): (.*) ===$/gm)].map((match) => ({ shard: match[1], outcome: match[2], checks: match[3] }));
}

export function renderHealthReport({ result, sha, runUrl, failed, cause = null }) {
  const checks = failed.length > 0 ? failed.map((item) => `- Shard ${item.shard} (${item.outcome}): ${item.checks}`) : [`- The verification did not finish (${result}).`];
  return [
    MAIN_HEALTH_MARKER,
    `**The default branch is unhealthy**: the full verification of \`${sha}\` failed in [this run](${runUrl}).`,
    '',
    ...checks,
    '',
    ...(cause ? [...renderCauseLines(cause), ''] : []),
    '**Every Spec Kit implementation is blocked** and no spec starts until the default branch is healthy again, so none builds on a broken foundation.',
    '',
    ...(cause && !cause.healable
      ? [
        `**Recommended next step**: Fix or revert the cause in a pull request against the default branch: every change since the last green verification is in paths the healer may not change (${cause.protected.map((file) => `\`${file}\``).join(', ')}).`,
        '',
        '- **Why**: Every Spec Kit implementation waits for a healthy default branch, and the healer only changes product code, tests, and the environment actions.',
        '- **What it does**: Once merged, Main health runs again; a green run closes this issue and lifts the blocks.',
      ]
      : ['**Nothing is needed yet**: the healer (workflow Main heal) lets an agent fix it, verifies the fix in full, and opens a healing pull request for a person to review and merge. Its attempts are listed below.']),
    ...(cause ? ['', renderCause(cause)] : []),
  ].join('\n');
}

// Records a finished health run: a green run closes the open tracking issue, a red run opens it or adds the new run to
// it (once per run). A cancelled or skipped run changes nothing.
export async function recordHealth(client, env, { result, log = '', sha, runUrl, attribute = async () => null }) {
  const open = (await client.listTwinIssues(MAIN_HEALTH_LABEL)).filter((issue) => issue.state === 'open');
  if (result === 'success') {
    for (const issue of open) {
      await client.createComment(issue.number, `${MAIN_HEALTH_MARKER}\n**The default branch is healthy again**: the full verification of \`${sha}\` passed in [this run](${runUrl}). New specs start again.`);
      await client.updateIssue(issue.number, { state: 'closed', state_reason: 'completed' });
      try {
        await client.removeLabel(issue.number, ATTENTION_LABEL);
      } catch {
        // The closed issue no longer asks for anything.
      }
    }
    return { outcome: open.length > 0 ? 'closed' : 'healthy', issue: open[0]?.number ?? null };
  }
  if (!['failure', 'timed_out'].includes(result)) return { outcome: 'ignored', issue: open[0]?.number ?? null };
  const failed = failedShards(log);
  if (open.length > 0) {
    const body = renderHealthReport({ result, sha, runUrl, failed });
    const issue = open[0];
    const comments = await client.listIssueComments(issue.number);
    if (!String(issue.body ?? '').includes(runUrl) && !comments.some((comment) => String(comment.body ?? '').includes(runUrl))) await client.createComment(issue.number, body);
    return { outcome: 'updated', issue: issue.number };
  }
  await client.ensureLabel({ name: MAIN_HEALTH_LABEL, color: 'b60205', description: 'The default branch fails its full verification' });
  // The cause is attributed once, when the default branch turns red; later red runs keep it.
  let cause = null;
  try {
    cause = await attribute();
  } catch {
    cause = null;
  }
  const body = renderHealthReport({ result, sha, runUrl, failed, cause });
  const issue = await client.createIssue({ title: 'The default branch is unhealthy', body, labels: cause && !cause.healable ? [MAIN_HEALTH_LABEL, ATTENTION_LABEL] : [MAIN_HEALTH_LABEL] });
  // The pull requests that made it red learn about it: their merge verification missed something.
  for (const commit of (cause?.commits ?? []).filter((item) => item.pull)) {
    try {
      await client.createComment(commit.pull, `**Merging this pull request made the default branch unhealthy** (#${issue.number}): the full verification of \`${sha}\` fails in [this run](${runUrl})${cause.commits.length > 1 ? ', together with other changes' : ''}. ${cause.healable ? 'The healer fixes it forward in a healing pull request; nothing is needed here.' : 'A person fixes it; see the issue.'}`);
    } catch {
      // The issue names it either way.
    }
  }
  return { outcome: 'opened', issue: issue.number, cause };
}

// After recording: a red run starts the healer, a green run starts the scheduler, which lifts the health blocks.
export async function dispatchAfterHealth(client, env, result) {
  const workflow = result === 'success' ? 'speckit-orchestrate.yml' : ['failure', 'timed_out'].includes(result) ? 'main-heal.yml' : null;
  if (!workflow) return null;
  try {
    await client.dispatchWorkflow(workflow, defaultBranchOf(env), {});
    return workflow;
  } catch {
    // The scheduler's fallback runs and the next health run catch up.
    return null;
  }
}

function defaultGit(args) {
  const result = spawnSync('git', args, { encoding: 'utf8', maxBuffer: 50 * 1024 * 1024 });
  return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

export async function main(argv, { env = process.env, log = console.log, client, git } = {}) {
  if (argv[0] !== 'record') {
    log('Usage: speckit-main-health.mjs record (inputs: SPECKIT_HEALTH_RESULT, SPECKIT_HEALTH_LOG, GITHUB_SHA, GITHUB_RUN_ID)');
    return 2;
  }
  const runUrl = `${env.GITHUB_SERVER_URL ?? 'https://github.com'}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}`;
  const logFile = env.SPECKIT_HEALTH_LOG;
  const github = client ?? createGitHubClient(env);
  const result = await recordHealth(github, env, {
    result: String(env.SPECKIT_HEALTH_RESULT ?? ''),
    log: logFile && existsSync(logFile) ? readFileSync(logFile, 'utf8') : '',
    sha: String(env.GITHUB_SHA ?? ''),
    runUrl,
    attribute: () => attributeCause(github, env, { git: git ?? defaultGit, sha: String(env.GITHUB_SHA ?? '') }),
  });
  const started = await dispatchAfterHealth(github, env, String(env.SPECKIT_HEALTH_RESULT ?? ''));
  log(`Main health ${env.SPECKIT_HEALTH_RESULT}: ${result.outcome}${result.issue ? ` (#${result.issue})` : ''}${started ? `; started ${started}` : ''}.`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (error) => { console.error(error.message); process.exitCode = 1; });
}
