import { existsSync, readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { createGitHubClient } from './speckit-script-runtime.mjs';
import { ATTENTION_LABEL, defaultBranchOf } from './speckit-implement-core.mjs';

// The health of the default branch: the workflow `Main health` (main-health.yml) verifies the default branch in full
// (environment-verify, finalize, in shards) after every push that changes more than Markdown and specs/, nightly, and
// on demand. Its latest finished run is the health; a red default branch keeps one open tracking issue (label
// MAIN_HEALTH_LABEL) that recommends what to do and is closed by the next green run. The scheduler starts no new spec
// while the default branch is red (SPECKIT_HEALTH_GATE=false turns that off).

export const MAIN_HEALTH_WORKFLOW_FILE = 'main-health.yml';
export const MAIN_HEALTH_LABEL = 'main-health';
export const MAIN_HEALTH_MARKER = '<!-- speckit:main-health -->';
const HEALTH_WINDOW_MS = 14 * 24 * 60 * 60 * 1000;
const FINISHED = new Set(['success', 'failure', 'timed_out']);

// The latest finished run of the health workflow on the default branch: { state: 'green' | 'red' | 'unknown', run }.
// A cancelled or skipped run says nothing; a lookup that fails leaves the health unknown, which never holds anything.
export async function mainHealth(client, env, { now = Date.now() } = {}) {
  try {
    const branch = defaultBranchOf(env);
    const runs = await client.listWorkflowRuns(MAIN_HEALTH_WORKFLOW_FILE, new Date(now - HEALTH_WINDOW_MS).toISOString(), 2);
    const latest = runs
      .filter((run) => run.status === 'completed' && FINISHED.has(run.conclusion) && (!run.head_branch || run.head_branch === branch))
      .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at) || b.id - a.id)[0];
    if (!latest) return { state: 'unknown', run: null };
    return { state: latest.conclusion === 'success' ? 'green' : 'red', run: latest };
  } catch {
    return { state: 'unknown', run: null };
  }
}

// The failed shards of a combined verification log (speckit-verify-shards.mjs): "Shard 2/4 (failure): platform ...".
export function failedShards(log) {
  return [...String(log ?? '').matchAll(/^=== Shard (\S+) \((failure|cancelled|unknown)\): (.*) ===$/gm)].map((match) => ({ shard: match[1], outcome: match[2], checks: match[3] }));
}

export function renderHealthReport({ result, sha, runUrl, failed }) {
  const checks = failed.length > 0 ? failed.map((item) => `- Shard ${item.shard} (${item.outcome}): ${item.checks}`) : [`- The verification did not finish (${result}).`];
  const idea = `Make the default branch healthy again: the full verification of ${sha.slice(0, 12)} failed in ${failed.length > 0 ? failed.map((item) => item.checks).join('; ') : 'the run'} (Main health run ${runUrl}). Find the cause in that run's verification log and fix it without weakening any test.`;
  return [
    MAIN_HEALTH_MARKER,
    `**The default branch is unhealthy**: the full verification of \`${sha}\` failed in [this run](${runUrl}).`,
    '',
    ...checks,
    '',
    'While it is red, the Spec Kit scheduler starts no new spec; implementations in progress and the prerequisites they wait for continue.',
    '',
    '**Recommended next step**: Author a spec that repairs it. In Copilot Chat in this repository, run:',
    '',
    '```text',
    `/speckit-gha-author ${idea}`,
    '```',
    '',
    '- **Why**: Every implementation verifies against the default branch, so this failure would stop each of them in turn.',
    '- **What it does**: Writes and publishes one spec for the cause; request its implementation with `/speckit implement` on its twin. The next green run closes this issue.',
    '',
    '<details><summary>Other options</summary>',
    '',
    '- Fix it directly on the default branch when the cause is obvious; the push starts a new health run.',
    '- If the failure looks transient, rerun the failed jobs of the run.',
    '',
    '</details>',
  ].join('\n');
}

// Records a finished health run: a green run closes the open tracking issue, a red run opens it or adds the new run to
// it (once per run). A cancelled or skipped run changes nothing.
export async function recordHealth(client, env, { result, log = '', sha, runUrl }) {
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
  const body = renderHealthReport({ result, sha, runUrl, failed: failedShards(log) });
  if (open.length > 0) {
    const issue = open[0];
    const comments = await client.listIssueComments(issue.number);
    if (!String(issue.body ?? '').includes(runUrl) && !comments.some((comment) => String(comment.body ?? '').includes(runUrl))) await client.createComment(issue.number, body);
    return { outcome: 'updated', issue: issue.number };
  }
  await client.ensureLabel({ name: MAIN_HEALTH_LABEL, color: 'b60205', description: 'The default branch fails its full verification' });
  const issue = await client.createIssue({ title: 'The default branch is unhealthy', body, labels: [MAIN_HEALTH_LABEL, ATTENTION_LABEL] });
  return { outcome: 'opened', issue: issue.number };
}

export async function main(argv, { env = process.env, log = console.log, client } = {}) {
  if (argv[0] !== 'record') {
    log('Usage: speckit-main-health.mjs record (inputs: SPECKIT_HEALTH_RESULT, SPECKIT_HEALTH_LOG, GITHUB_SHA, GITHUB_RUN_ID)');
    return 2;
  }
  const runUrl = `${env.GITHUB_SERVER_URL ?? 'https://github.com'}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}`;
  const logFile = env.SPECKIT_HEALTH_LOG;
  const result = await recordHealth(client ?? createGitHubClient(env), env, {
    result: String(env.SPECKIT_HEALTH_RESULT ?? ''),
    log: logFile && existsSync(logFile) ? readFileSync(logFile, 'utf8') : '',
    sha: String(env.GITHUB_SHA ?? ''),
    runUrl,
  });
  log(`Main health ${env.SPECKIT_HEALTH_RESULT}: ${result.outcome}${result.issue ? ` (#${result.issue})` : ''}.`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (error) => { console.error(error.message); process.exitCode = 1; });
}
