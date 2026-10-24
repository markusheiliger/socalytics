import { spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { TWIN_LABEL, deriveStage, isImplementRequested, isValidFolderName, resolveTwins } from './speckit-prepare-core.mjs';
import { discoverSpecs } from './speckit-prepare.mjs';
import { createGitHubClient, createReporter } from './speckit-script-runtime.mjs';
import { ARCHITECTURE_PREFIX, PREPARE_WORKFLOW_FILE, defaultBranchOf, neutralizeMarkers } from './speckit-implement-core.mjs';

// The spec backlog: once no spec is left that a person can request with `/speckit implement` (an open twin, or a
// folder whose twin Spec Kit prepare is about to create, at the computed stage `tasked` with every checklist item
// checked and no implementation request), Spec Kit orchestrate starts the workflow Spec Kit backlog
// (speckit-backlog.yml), which authors the next logical specs and publishes them straight to the default branch. It
// does what `/speckit-gha-author --repeat <n>` does in autopilot: n runs of the Spec Kit workflow `author`
// (.specify/extension-src/gha/workflows/author/) one after another, each without a feature, unattended (every
// recommended choice), and each committing its spec, so the next run proposes the next spec from the specs that exist
// by then; it stops at the first run that fails. Its job `author` (read-only) runs them and packages the commits
// (runAuthor); its job `land` (write token, never runs agent-written code) checks that they only add new spec folders
// and change docs/architecture/, pushes them to the default branch, and dispatches Spec Kit prepare, which creates
// their twins (runLand). Repository variable SPECKIT_BACKLOG_SPECS sets how many specs one refill authors (default 5
// in the workflow, 0 turns the refill off).

export class BacklogUsageError extends Error {}

export const BACKLOG_WORKFLOW_FILE = 'speckit-backlog.yml';
export const MAX_BACKLOG_SPECS = 10;
// A failed refill is not started again automatically before this time has passed (a person can start one by hand).
export const BACKLOG_RETRY_MS = 24 * 60 * 60 * 1000;
// The author job starts another run only while this much of its time budget is left.
export const DEFAULT_BUDGET_MINUTES = 300;
export const DEFAULT_RUN_MINUTES = 60;

const NEW_FOLDER = /^\d{8}-\d{6}-[a-z0-9][a-z0-9-]*$/;
const BOT_IDENTITY = ['-c', 'user.name=github-actions[bot]', '-c', 'user.email=41898282+github-actions[bot]@users.noreply.github.com'];
const clip = (text, max) => (text.length <= max ? text : `${text.slice(0, max - 1)}…`);

// SPECKIT_BACKLOG_SPECS: how many specs one refill authors; empty or invalid turns the refill off here (the workflow
// passes its default), capped at MAX_BACKLOG_SPECS.
export function parseBacklogSize(value) {
  const number = Number(String(value ?? '').trim());
  if (!Number.isInteger(number) || number <= 0) return 0;
  return Math.min(number, MAX_BACKLOG_SPECS);
}

// The spec folders on the default branch that a person can request with `/speckit implement` now or once Spec Kit
// prepare created their twin: not archived, at the computed stage `tasked`, every checklist item checked, and their
// twin (if any) open and not requested yet.
export function requestableSpecs(specs, twinsByFolder) {
  const folders = [];
  for (const [folder, entry] of specs) {
    if (entry.archived) continue;
    const stage = deriveStage(entry.artifacts, entry.tasks);
    if (stage !== 'tasked' || entry.openChecklistItems > 0) continue;
    const twin = twinsByFolder.get(folder);
    if (twin && (twin.state !== 'open' || isImplementRequested(twin, stage))) continue;
    folders.push(folder);
  }
  return folders;
}

const runLink = (run) => (run.html_url ? `[run ${run.id}](${run.html_url})` : `run ${run.id}`);

// Spec Kit orchestrate: starts one refill when no spec is left to request, none is running, and the last one did not
// fail within BACKLOG_RETRY_MS. Returns the summary lines and whether it started one.
export async function maintainBacklog(client, env, { rootDir, now = Date.now() } = {}) {
  const size = parseBacklogSize(env.SPECKIT_BACKLOG_SPECS);
  if (size === 0) return { lines: [], started: false };
  let requestable;
  try {
    const { byFolder } = resolveTwins(await client.listTwinIssues(TWIN_LABEL));
    requestable = requestableSpecs(discoverSpecs(rootDir), byFolder);
  } catch (error) {
    return { lines: [`- Warning: could not count the specs that can be requested: ${error.message}`], started: false };
  }
  if (requestable.length > 0) {
    return { lines: [`- ${requestable.length} spec(s) can be requested with \`/speckit implement\`; no new specs are authored.`], started: false, requestable };
  }
  let runs;
  try {
    runs = await client.listWorkflowRuns(BACKLOG_WORKFLOW_FILE, new Date(now - BACKLOG_RETRY_MS).toISOString());
  } catch (error) {
    return { lines: [`- Warning: could not read the runs of Spec Kit backlog: ${error.message}`], started: false, requestable };
  }
  const active = runs.find((run) => run.status !== 'completed');
  if (active) return { lines: [`- No spec is left to request; Spec Kit backlog is authoring new specs in ${runLink(active)}.`], started: false, requestable };
  const latest = [...runs].sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at))[0];
  if (latest && latest.conclusion !== 'success') {
    const retry = new Date(Date.parse(latest.created_at) + BACKLOG_RETRY_MS).toISOString();
    return {
      lines: [`- No spec is left to request, but the last Spec Kit backlog ${runLink(latest)} ended \`${latest.conclusion}\`; the next refill starts after ${retry}, or start Spec Kit backlog by hand.`],
      started: false,
      requestable,
    };
  }
  await client.dispatchWorkflow(BACKLOG_WORKFLOW_FILE, defaultBranchOf(env), { count: String(size) });
  return { lines: [`- No spec is left to request with \`/speckit implement\`, so Spec Kit backlog was started to author the next ${size} spec(s) on the default branch.`], started: true, requestable };
}

// ── Job `author` of Spec Kit backlog (read-only token) ─────────────────────────────────────────────────────────────

function resultDir(env) {
  return path.resolve(String(env.SPECKIT_RESULT_DIR || '.speckit-backlog'));
}

function defaultSpecify(rootDir) {
  return (args) => {
    // An empty standard input: an unattended run never waits for a person.
    const result = spawnSync('specify', args, { cwd: rootDir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'], windowsHide: true, maxBuffer: 50 * 1024 * 1024 });
    return { status: result.error ? 1 : result.status, stdout: result.stdout ?? '', error: result.error?.message ?? '' };
  };
}

function writeAuthorResult(dir, git, start, result) {
  writeFileSync(path.join(dir, 'result.json'), `${JSON.stringify(result, null, 2)}\n`);
  if (result.specs.length > 0) {
    writeFileSync(path.join(dir, 'specs.patch'), git(['format-patch', '--stdout', '--binary', '--no-renames', `${start}..HEAD`]).stdout);
  }
}

// Runs the Spec Kit workflow `author` up to SPECKIT_BACKLOG_COUNT times, like `/speckit-gha-author --repeat <n>` in
// autopilot: each run proposes the next logical spec, takes every recommended choice, and commits it (the land job
// pushes them all). Stops at the first run that does not complete with a committed spec, and before a run that would
// not fit into the job's time budget. Records the result after every run, so a job that ends early keeps the specs
// authored so far.
export async function runAuthor({ env, log, git, rootDir, specify = defaultSpecify(rootDir), now = Date.now }) {
  const count = parseBacklogSize(env.SPECKIT_BACKLOG_COUNT);
  if (count === 0) throw new BacklogUsageError(`SPECKIT_BACKLOG_COUNT must be a whole number from 1 to ${MAX_BACKLOG_SPECS}.`);
  const tooling = path.resolve(String(env.SPECKIT_TOOLING || '.'));
  const dir = resultDir(env);
  mkdirSync(dir, { recursive: true });
  const minutes = (value, fallback) => (Number(value) > 0 ? Number(value) : fallback) * 60 * 1000;
  const deadline = now() + minutes(env.SPECKIT_BACKLOG_BUDGET_MINUTES, DEFAULT_BUDGET_MINUTES);
  const reserve = minutes(env.SPECKIT_BACKLOG_RUN_MINUTES, DEFAULT_RUN_MINUTES);
  const start = git(['rev-parse', 'HEAD']).stdout.trim();
  const { pending, readState } = await import('./speckit-author-flow.mjs');
  const result = { count, specs: [], runs: [], error: null };
  writeAuthorResult(dir, git, start, result);
  for (let index = 1; index <= count; index += 1) {
    if (now() + reserve > deadline) {
      result.stopped = `the time budget of the job leaves no room for spec ${index} of ${count}`;
      break;
    }
    log(`Authoring spec ${index} of ${count}.`);
    const ran = specify(['workflow', 'run', path.join(tooling, '.specify', 'workflows', 'author', 'workflow.yml'), '--json',
      '-i', 'mode=unattended', '-i', 'publish=commit', '-i', `tooling=${tooling}`]);
    const run = ran.stdout.match(/"run_id"\s*:\s*"([\w-]+)"/)?.[1] ?? '';
    const state = run ? pending({ rootDir, run }) : { status: 'failed', error: `the Spec Kit workflow \`author\` did not start${ran.error ? `: ${ran.error}` : ''}` };
    result.runs.push({ run: run || null, status: state.status, folder: state.folder ?? null });
    if (state.status === 'paused') state.error = `a gate (${state.step}) paused the unattended run, which must never ask; the workflow \`author\` has a defect`;
    if (state.status === 'completed' && state.publish?.committed !== true) state.error = 'the run completed without committing a spec';
    if (state.status !== 'completed' || state.error) {
      result.error = clip(`Spec ${index} of ${count}${state.folder ? ` (specs/${state.folder})` : ''}: ${state.error || `the Spec Kit workflow \`author\` ended ${state.status}`}`, 3000);
      writeAuthorResult(dir, git, start, result);
      break;
    }
    const { title = null, architecture = [] } = readState(rootDir, run);
    result.specs.push({ folder: state.folder, title, architecture });
    writeAuthorResult(dir, git, start, result);
    log(`Committed specs/${state.folder}.`);
  }
  writeAuthorResult(dir, git, start, result);
  log(`Authored ${result.specs.length} of ${count} spec(s)${result.error ? `; stopped: ${result.error}` : result.stopped ? `; stopped: ${result.stopped}` : ''}.`);
  return { exitCode: 0 };
}

// ── Job `land` of Spec Kit backlog (write token, never runs agent-written code) ────────────────────────────────────

function remoteArgs(env, ...args) {
  const server = (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '');
  const basic = Buffer.from(`x-access-token:${env.GITHUB_TOKEN}`).toString('base64');
  return ['-c', `http.${server}/.extraheader=AUTHORIZATION: basic ${basic}`, ...args.map((arg) => (arg === '<remote>' ? `${server}/${env.GITHUB_REPOSITORY}.git` : arg))];
}

export function readAuthorResult(env) {
  const file = path.join(resultDir(env), 'result.json');
  const fallback = { specs: [], error: `the author job did not finish (${String(env.SPECKIT_AUTHOR_JOB || 'no result')})` };
  if (!existsSync(file)) return fallback;
  try {
    const data = JSON.parse(readFileSync(file, 'utf8'));
    return { ...data, specs: Array.isArray(data.specs) ? data.specs : [] };
  } catch {
    return fallback;
  }
}

// The commits base..HEAD may only add the reported spec folders (new, with spec.md and tasks.md) and change
// docs/architecture/.
export function checkLanded(git, base, folders) {
  const changed = git(['diff', '--name-only', '--no-renames', base, 'HEAD']).stdout.split(/\r?\n/).filter(Boolean);
  const allowed = new Set(folders);
  const outside = changed.filter((file) => {
    if (file.startsWith(ARCHITECTURE_PREFIX)) return false;
    const folder = file.match(/^specs\/([^/]+)\//)?.[1];
    return !folder || !allowed.has(folder);
  });
  if (outside.length > 0) return `the specs change files outside their new folders and \`${ARCHITECTURE_PREFIX}\`: ${outside.slice(0, 20).map((file) => `\`${file}\``).join(', ')}`;
  for (const folder of folders) {
    if (git(['cat-file', '-e', `${base}:specs/${folder}`]).status === 0) return `\`specs/${folder}\` already exists on the default branch`;
    if (git(['cat-file', '-e', `${base}:specs/archive/${folder}`]).status === 0) return `\`specs/archive/${folder}\` already exists on the default branch`;
    for (const file of ['spec.md', 'tasks.md']) {
      if (!changed.includes(`specs/${folder}/${file}`)) return `\`specs/${folder}\` has no \`${file}\``;
    }
  }
  return null;
}

// Applies specs.patch on the latest default branch, checks it, and pushes it; a push refused because the branch moved
// meanwhile applies it again on the new head (at most three attempts).
export function landSpecs({ env, git, dir, folders }) {
  const branch = defaultBranchOf(env);
  const patch = path.join(dir, 'specs.patch');
  if (!existsSync(patch)) return { error: 'the author job packaged no specs' };
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    if (attempt > 1) {
      const fetched = git(remoteArgs(env, 'fetch', '--quiet', '<remote>', `refs/heads/${branch}`));
      if (fetched.status !== 0) return { error: `fetching \`${branch}\` failed: ${(fetched.stderr || fetched.stdout).trim().slice(0, 500)}` };
      git(['reset', '--quiet', '--hard', 'FETCH_HEAD']);
    }
    const base = git(['rev-parse', 'HEAD']).stdout.trim();
    const applied = git([...BOT_IDENTITY, 'am', '--quiet', '--3way', patch]);
    if (applied.status !== 0) {
      git(['am', '--abort']);
      return { error: `the specs do not apply to \`${branch}\`: ${(applied.stderr || applied.stdout).trim().slice(0, 500)}` };
    }
    const refused = checkLanded(git, base, folders);
    if (refused) return { error: refused };
    const pushed = git(remoteArgs(env, 'push', '--quiet', '<remote>', `HEAD:refs/heads/${branch}`));
    if (pushed.status === 0) return { sha: git(['rev-parse', 'HEAD']).stdout.trim() };
    if (attempt === 3) return { error: `the push to \`${branch}\` failed: ${(pushed.stderr || pushed.stdout).trim().slice(0, 500)}` };
  }
  return { error: 'unreachable' };
}

// Job `land`: publishes the specs the author job committed to the default branch, dispatches Spec Kit prepare (a push
// with the workflow token starts no workflow), and reports. Fails when a run failed or the specs could not be
// published, so the refill is not started again automatically before BACKLOG_RETRY_MS.
export async function runLand({ client, env, log, git }) {
  const report = createReporter(env, log);
  const result = readAuthorResult(env);
  const branch = defaultBranchOf(env);
  const server = (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '');
  const specs = result.specs.filter((spec) => isValidFolderName(String(spec?.folder ?? '')) && NEW_FOLDER.test(spec.folder));
  let error = result.error ? neutralizeMarkers(String(result.error)) : null;
  report.line('## Spec Kit backlog');
  report.line();
  if (specs.length !== result.specs.length) error = 'the author job reported an invalid spec folder';
  let landed = null;
  if (specs.length > 0 && specs.length === result.specs.length) {
    landed = landSpecs({ env, git, dir: resultDir(env), folders: specs.map((spec) => spec.folder) });
    if (landed.error) {
      error = `The authored specs could not be published: ${landed.error}.`;
      landed = null;
    }
  }
  if (landed) {
    report.line(`Published ${specs.length} new spec(s) on \`${branch}\` (${landed.sha.slice(0, 7)}):`);
    report.line();
    for (const spec of specs) {
      const title = clip(String(spec.title || spec.folder).replace(/\s+/g, ' ').trim(), 200);
      report.line(`- [${title}](${server}/${env.GITHUB_REPOSITORY}/tree/${branch}/specs/${spec.folder})${spec.architecture?.length ? ` with ${spec.architecture.length} change(s) to \`${ARCHITECTURE_PREFIX}\`` : ''}`);
    }
    await client.dispatchWorkflow(PREPARE_WORKFLOW_FILE, branch, { dry_run: 'false' });
    report.line();
    report.line('Spec Kit prepare was started to create their twins; request an implementation with `/speckit implement` on a twin.');
  } else if (specs.length === 0 && !error) {
    error = result.stopped ? `No spec was authored: ${result.stopped}.` : 'No spec was authored.';
  }
  if (result.stopped && specs.length > 0) {
    report.line();
    report.line(`Stopped early: ${result.stopped}.`);
  }
  if (error) {
    report.line();
    report.line(`**Stopped**: ${error}`);
    report.line();
    report.line('Spec Kit orchestrate starts the next refill automatically after a day; to retry now, start Spec Kit backlog by hand once the cause is fixed.');
  }
  report.flush();
  return { exitCode: error ? 1 : 0 };
}

function defaultGit(cwd) {
  return (args) => {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 50 * 1024 * 1024 });
    if (result.error) throw new BacklogUsageError(`git is required: ${result.error.message}`);
    return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  };
}

export async function main(argv, { env = process.env, log = console.log, client, git, specify } = {}) {
  const [command] = argv;
  const handlers = { author: runAuthor, land: runLand };
  if (!handlers[command]) throw new BacklogUsageError(`Usage: speckit-backlog.mjs <${Object.keys(handlers).join(' | ')}>`);
  const workspace = path.resolve(String(env.SPECKIT_WORKSPACE || '.'));
  const run = {
    env,
    log,
    rootDir: workspace,
    git: git ?? defaultGit(workspace),
    ...(specify ? { specify } : {}),
    // The author job runs without a GitHub token for the tooling.
    client: client ?? (command === 'land' ? createGitHubClient(env) : null),
  };
  const result = await handlers[command](run);
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `exit=${result.exitCode}\n`);
  return result.exitCode;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof BacklogUsageError ? 2 : 1;
    },
  );
}
