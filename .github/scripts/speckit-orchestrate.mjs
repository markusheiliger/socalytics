import { spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  IMPLEMENT_STAGE,
  TWIN_LABEL,
  buildSpecEntry,
  deriveStage,
  hasLabel,
  implementBlockers,
  isValidFolderName,
  requesterBlockers,
  resolveTwins,
  stageLabel,
  stageLabelChange,
} from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { discoverSpecs, readSpecFolder, resolveImplementRequester, updateIssueWithLabels } from './speckit-prepare.mjs';
import {
  BOT_LOGIN,
  CHECK_LIMIT,
  CHECK_MERGE,
  CHECK_PROGRESS,
  CHECK_RUN_NAME,
  DONE_COMMENT_MARKER,
  MAX_TASK_ATTEMPTS,
  RESUME_COMMENT_MARKER,
  START_COMMENT_MARKER,
  STEPS,
  checkRunOutput,
  decideContinuation,
  decideLifecycle,
  extractTasks,
  implementationBranch,
  latestCheckRun,
  markerTimes,
  nextTaskGroup,
  parseMaxParallel,
  parseStepRunName,
  renderPullRequestBody,
  renderPullRequestTitle,
  renderStartComment,
  renderStepRunName,
  stepLabel,
  syncPullRequestTicks,
} from './speckit-implement-core.mjs';

export class UsageError extends Error {}

function createReporter(env, log) {
  const lines = [];
  return {
    line(text = '') {
      lines.push(text);
      log(text);
    },
    flush() {
      if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, `${lines.join('\n')}\n`);
    },
  };
}

function setOutput(env, name, value) {
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `${name}=${value}\n`);
}

function openBlockers(blockers) {
  return blockers.filter((blocker) => blocker.state === 'open');
}

function blockerList(blockers) {
  return blockers.map((blocker) => `#${blocker.number}`).join(', ');
}

// Server side: lists flagged twins that are consistent with the default branch and have no open blockers,
// and resolves what their implementation PRs mean (in progress, closed without merge, or ready to start).
export async function runSelect({ client, rootDir, env, log, now = Date.now }) {
  const report = createReporter(env, log);
  const specs = discoverSpecs(rootDir);
  const { byFolder } = resolveTwins(await client.listTwinIssues(TWIN_LABEL));
  const ready = [];
  const blocked = [];
  const inconsistent = [];
  const inProgress = [];
  const fallback = [];
  const merged = [];
  for (const [folder, issue] of [...byFolder.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    if (!hasLabel(issue, stageLabel(IMPLEMENT_STAGE))) continue;
    const open = issue.state === 'open';
    // A person who merges a pull request held for review closes the twin through "Closes #<twin>", which leaves
    // the flag and the branch behind; such twins are finalized here like an automatic merge.
    if (!open && issue.state_reason !== 'completed') continue;
    const entry = specs.get(folder);
    const requester = entry || !open ? await resolveImplementRequester(client, issue.number) : null;
    const lifecycle = requester
      ? decideLifecycle({ pulls: await client.listPullRequestsForHead(implementationBranch(folder)), flaggedAt: requester.labeledAt })
      : null;
    if (lifecycle?.state === 'merged') {
      merged.push({ number: issue.number, folder, pull: lifecycle.pull.number, ...(await finalizeMerged(client, issue, folder, lifecycle.pull.number)) });
      continue;
    }
    if (!open) continue;
    const computedStage = entry ? deriveStage(entry.artifacts, entry.tasks) : null;
    const reasons = entry
      ? [
        ...requesterBlockers(requester),
        ...implementBlockers({ folder, computedStage, openChecklistItems: entry.openChecklistItems }),
      ]
      : [`\`specs/${folder}\` does not exist on the default branch`];
    if (reasons.length > 0) {
      inconsistent.push({ number: issue.number, folder, reasons });
      continue;
    }

    if (lifecycle.state === 'in-progress') {
      const decision = await decideForPull(client, {
        twin: issue.number,
        folder,
        pullNumber: lifecycle.pull.number,
        resume: String(env.SPECKIT_RESUME_TWIN ?? '') === String(issue.number),
        now: now(),
        maxParallel: parseMaxParallel(env.SPECKIT_MAX_PARALLEL_TASKS),
      });
      inProgress.push({ number: issue.number, folder, pull: lifecycle.pull.number, requester: requester.login, decision });
      continue;
    }
    if (lifecycle.state === 'fallback') {
      await updateIssueWithLabels(client, issue, stageLabelChange(issue, computedStage));
      await client.createComment(issue.number, [
        `Implementation pull request #${lifecycle.pull.number} was closed without merging, so the \`${stageLabel(IMPLEMENT_STAGE)}\` flag was removed.`,
        '',
        `The twin is back at its computed stage \`${computedStage}\`. Flag it again to restart; the branch \`${implementationBranch(folder)}\` is then reset.`,
      ].join('\n'));
      fallback.push({ number: issue.number, folder, pull: lifecycle.pull.number });
      continue;
    }

    const openBlocking = openBlockers(await client.listBlockedBy(issue.number));
    if (openBlocking.length > 0) blocked.push({ number: issue.number, folder, blockers: openBlocking });
    else ready.push({ number: issue.number, folder, requester: requester.login, reset: lifecycle.reset });
  }

  report.line('## Spec implementation selection');
  report.line();
  for (const twin of ready) report.line(`- Ready: #${twin.number} \`${twin.folder}\`${twin.reset ? ' (the branch is reset)' : ''}`);
  for (const twin of inProgress) {
    report.line(`- In progress: #${twin.number} \`${twin.folder}\` in pull request #${twin.pull}: ${describeDecision(twin.decision)}`);
  }
  for (const twin of blocked) report.line(`- Blocked: #${twin.number} \`${twin.folder}\` by ${blockerList(twin.blockers)}`);
  for (const twin of fallback) {
    report.line(`- Flag removed: #${twin.number} \`${twin.folder}\` because pull request #${twin.pull} was closed without merging`);
  }
  for (const twin of merged) {
    report.line(`- Finalized: #${twin.number} \`${twin.folder}\` merged in pull request #${twin.pull}; the twin is \`${stageLabel('implemented')}\`${twin.branchDeleted ? ' and the branch was deleted' : ''}${twin.note ? ` (${twin.note})` : ''}`);
  }
  for (const twin of inconsistent) {
    report.line(`- Inconsistent (the next sync revokes the flag): #${twin.number} \`${twin.folder}\`: ${twin.reasons.join('; ')}`);
  }
  const total = ready.length + inProgress.length + blocked.length + fallback.length + merged.length + inconsistent.length;
  if (total === 0) report.line('- No spec twin is flagged for implementation.');
  report.flush();
  setOutput(env, 'count', String(ready.length));
  setOutput(env, 'matrix', JSON.stringify({ include: ready }));
  return { exitCode: 0, ready, blocked, inconsistent, inProgress, fallback, merged };
}

async function findOpenPull(client, branch) {
  return (await client.listPullRequestsForHead(branch)).find((pull) => pull.state === 'open') ?? null;
}

// Completes what the merge step does after an automatic merge for a pull request that a person merged:
// marks the twin implemented (closing it if needed) and deletes the implementation branch.
async function finalizeMerged(client, issue, folder, pullNumber) {
  await updateIssueWithLabels(client, issue, stageLabelChange(issue, 'implemented'), issue.state === 'open' ? { state: 'closed', state_reason: 'completed' } : {});
  const branch = implementationBranch(folder);
  let branchDeleted = false;
  let note = null;
  try {
    if (await client.getBranchSha(branch)) {
      await client.deleteBranch(branch);
      branchDeleted = true;
    }
  } catch (error) {
    note = `the branch \`${branch}\` could not be deleted: ${error.message}`;
  }
  await client.createComment(issue.number, [
    `Implementation pull request #${pullNumber} was merged, so the twin is now \`${stageLabel('implemented')}\`${branchDeleted ? ` and the branch \`${branch}\` was deleted` : ''}.`,
    ...(note ? ['', `Note: ${note}.`] : []),
  ].join('\n'));
  return { branchDeleted, note };
}

function describeDecision(decision) {
  switch (decision.action) {
    case 'wait': return 'a step is running';
    case 'done': return 'implemented and waiting for review';
    case 'failed': return 'stopped and waiting for a person (push a fix or resume with a manual run)';
    case 'resume': return `resuming with ${stepLabel(decision)}`;
    case 'limit': return `${stepLabel(decision)} reached the attempt limit`;
    case 'dispatch': return `next is ${stepLabel(decision)} attempt ${decision.attempt}`;
    case 'dispatch-tasks': return `next are ${decision.tasks.map(({ task, attempt }) => `${task} attempt ${attempt}`).join(', ')} in parallel`;
    case 'merge': return `merging, attempt ${decision.attempt}`;
    default: return decision.action;
  }
}

// Creation time of the latest commit on the implementation branch that a person (not the workflow) made.
function latestHumanCommit(commits) {
  return commits
    .filter((commit) => commit.author?.login !== BOT_LOGIN && commit.committer?.login !== BOT_LOGIN)
    .map((commit) => commit.commit?.committer?.date ?? commit.commit?.author?.date)
    .filter(Boolean)
    .sort((a, b) => Date.parse(b) - Date.parse(a))[0];
}

// Reads the state of an open implementation pull request and decides how to continue it. It also checks the tasks of
// the pull request body that are checked on the branch, because parallel tasks land concurrently.
async function decideForPull(client, { twin, folder, pullNumber, resume, now, maxParallel }) {
  const pull = await client.getPullRequest(pullNumber);
  const tasksMarkdown = await client.getFileContent(`specs/${folder}/tasks.md`, pull.head.sha);
  if (tasksMarkdown === null) return { action: 'missing-tasks', pull };
  const body = syncPullRequestTicks(pull.body, tasksMarkdown);
  if (body !== String(pull.body ?? '')) await client.updatePullRequest(pullNumber, { body });
  const checks = await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME);
  const comments = await client.listIssueComments(pullNumber);
  const resumedAt = markerTimes(comments, RESUME_COMMENT_MARKER);
  const pushedAt = latestHumanCommit(await client.listPullRequestCommits(pullNumber));
  const done = markerTimes(comments, DONE_COMMENT_MARKER).length > 0;
  const windowStart = [pull.created_at, ...resumedAt, ...(pushedAt ? [pushedAt] : [])].sort((a, b) => Date.parse(b) - Date.parse(a))[0];
  const runs = {};
  for (const [step, { file }] of Object.entries(STEPS)) {
    runs[step] = (await client.listWorkflowRuns(file, pull.created_at))
      .map((run) => ({ ...parseStepRunName(step, run.display_title), status: run.status, conclusion: run.conclusion, created_at: run.created_at }))
      .filter((run) => run.twin === twin);
  }
  return { ...decideContinuation({ tasksMarkdown, checks, runs, windowStart, done, resume, now, maxParallel }), pull };
}

const defaultSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// Starts one worker run and waits until it is visible, so the next orchestrator run sees it as active.
async function dispatchStep(client, env, { step, twin, pull, task, attempt }, { report, sleep, now }) {
  const since = new Date(now() - 60_000).toISOString();
  const title = renderStepRunName({ step, twin, task, attempt });
  const { file } = STEPS[step];
  await client.dispatchWorkflow(file, env.SPECKIT_BRANCH || 'main', {
    twin: String(twin),
    pull: String(pull),
    ...(step === 'task' ? { task } : {}),
    attempt: String(attempt),
  });
  for (let poll = 0; poll < 18; poll += 1) {
    if ((await client.listWorkflowRuns(file, since, 1)).some((run) => run.display_title === title)) {
      report.line(`- Started "${title}".`);
      return;
    }
    await sleep(5000);
  }
  report.line(`- Warning: started "${title}", but the run was not visible after 90 seconds.`);
}

// Waits until the run that handed control back has completed, so its outcome counts and it no longer looks
// active. The hand-back job is the run's last job, so this normally takes seconds.
async function awaitHandBack(client, runId, { report, sleep }) {
  for (let poll = 0; poll < 60; poll += 1) {
    const run = await client.getWorkflowRun(runId);
    if (!run || run.status === 'completed') return;
    await sleep(5000);
  }
  report.line(`- Warning: run ${runId} was still active after 5 minutes.`);
}

// Opens the merge check run that marks a merge as running, taking over a queued progress check run of the head.
async function startMerge(client, { pull, attempt }) {
  const fields = {
    status: 'in_progress',
    external_id: CHECK_MERGE,
    output: { title: `Merge attempt ${attempt} in progress`, summary: 'Merges the default branch, runs the full verification, and merges the implementation.' },
  };
  const latest = latestCheckRun(await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME));
  if (latest?.status === 'queued' && latest.external_id === CHECK_PROGRESS) {
    await client.updateCheckRun(latest.id, fields);
    return latest.id;
  }
  return (await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: pull.head.sha, ...fields })).id;
}

async function stopAtLimit(client, twin, decision) {
  const label = stepLabel(decision);
  await client.createCheckRun({
    name: CHECK_RUN_NAME,
    head_sha: decision.pull.head.sha,
    status: 'completed',
    conclusion: 'failure',
    external_id: CHECK_LIMIT,
    output: { title: `${label} reached the attempt limit`, summary: `The implementation stopped at ${label} after ${decision.attempts} attempts.` },
  });
  await client.createComment(twin.pull, [
    `**Implementation needs attention**${twin.requester ? ` @${twin.requester}` : ''}: ${label} did not succeed in ${decision.attempts} attempts (at most ${MAX_TASK_ATTEMPTS} failed attempts are allowed).`,
    '',
    'See the comments above for the reasons. Push a fix to the implementation branch, which continues automatically, or run the',
    `\`Spec Kit orchestrate\` workflow manually with \`twin\` set to ${twin.number}, which starts a new attempt count. To abandon this`,
    'implementation, close this pull request.',
  ].join('\n'));
}

// Orchestrator: selects flagged twins, prepares new implementation workspaces, starts, retries, resumes, or stops
// worker runs, and lists the twins whose implementation is merged by the merge jobs of this workflow run. Worker
// runs and merge jobs hand control back by dispatching this workflow with their run id.
export async function runOrchestrate({ client, rootDir, env, log, sleep = defaultSleep, now = Date.now }) {
  const pending = [];
  const afterRun = String(env.SPECKIT_AFTER_RUN ?? '').trim();
  if (/^\d+$/.test(afterRun)) await awaitHandBack(client, afterRun, { report: { line: (text) => pending.push(text) }, sleep });
  const selection = await runSelect({ client, rootDir, env, log, now });
  const report = createReporter(env, log);
  report.line();
  report.line('### Actions');
  report.line();
  for (const line of pending) report.line(line);
  let actions = 0;
  const merges = [];
  for (const twin of selection.ready) {
    const started = await runStart({ client, rootDir, env, issueNumber: twin.number, folder: twin.folder, requester: twin.requester, reset: twin.reset, log });
    if (!started.pull) continue;
    const entry = readSpecFolder(rootDir, twin.folder);
    const group = nextTaskGroup(entry?.tasks, parseMaxParallel(env.SPECKIT_MAX_PARALLEL_TASKS));
    const steps = group.length > 0 ? group.map((task) => ({ step: 'task', task: task.id })) : [{ step: 'converge' }];
    for (const first of steps) {
      await dispatchStep(client, env, { ...first, twin: twin.number, pull: started.pull.number, attempt: 1 }, { report, sleep, now });
    }
    actions += 1;
  }
  for (const twin of selection.inProgress) {
    const { decision } = twin;
    for (const stale of decision.stale ?? []) {
      await client.updateCheckRun(stale.id, { status: 'completed', conclusion: 'failure', output: { title: 'Merge attempt abandoned', summary: 'The merge did not finish within 2 hours.' } });
    }
    if (decision.action === 'resume') {
      await client.createComment(twin.pull, `${RESUME_COMMENT_MARKER}\nImplementation resumed by a manual run; the attempt count starts over with ${stepLabel(decision)}.`);
    }
    if (decision.action === 'dispatch-tasks') {
      for (const { task, attempt } of decision.tasks) {
        await dispatchStep(client, env, { step: 'task', task, twin: twin.number, pull: twin.pull, attempt }, { report, sleep, now });
      }
    } else if (decision.action === 'dispatch' || (decision.action === 'resume' && decision.step !== 'merge')) {
      await dispatchStep(client, env, { step: decision.step, task: decision.task, twin: twin.number, pull: twin.pull, attempt: decision.attempt }, { report, sleep, now });
    } else if (decision.action === 'merge' || (decision.action === 'resume' && decision.step === 'merge')) {
      const checkRun = await startMerge(client, { pull: decision.pull, attempt: decision.attempt });
      merges.push({ twin: twin.number, pull: twin.pull, folder: twin.folder, head: decision.pull.head.sha, attempt: decision.attempt, check_run: checkRun });
      report.line(`- Merging #${twin.number} (pull request #${twin.pull}), attempt ${decision.attempt}.`);
    } else if (decision.action === 'limit') {
      await stopAtLimit(client, twin, decision);
      report.line(`- Stopped #${twin.number}: ${stepLabel(decision)} reached the attempt limit.`);
    } else {
      continue;
    }
    actions += 1;
  }
  if (actions === 0) report.line('- Nothing to do.');
  report.flush();
  setOutput(env, 'merge_count', String(merges.length));
  setOutput(env, 'merges', JSON.stringify({ include: merges }));
  return { exitCode: 0, selection, merges };
}

// Server side: prepares the implementation workspace of one ready twin. Every step checks what already exists,
// so a rerun after a partial failure completes the work instead of duplicating it.
export async function runStart({ client, rootDir, env, issueNumber, folder, requester, reset, log }) {
  const report = createReporter(env, log);
  report.line(`## Implementation workspace for #${issueNumber}`);
  report.line();
  const branch = implementationBranch(folder);
  const defaultBranch = env.SPECKIT_BRANCH || 'main';
  const issue = await client.getIssue(issueNumber);
  if (issue.state !== 'open' || !hasLabel(issue, stageLabel(IMPLEMENT_STAGE))) {
    report.line(`#${issueNumber} is no longer open and flagged; nothing to start.`);
    report.flush();
    return { exitCode: 0 };
  }
  const entry = readSpecFolder(rootDir, folder);
  if (!entry || entry.tasks === null) throw new Error(`specs/${folder} has no tasks.md on the default branch`);
  const tasks = extractTasks(entry.tasks);

  let pull = await findOpenPull(client, branch);
  if (pull) {
    report.line(`- Pull request #${pull.number} is already open for \`${branch}\`.`);
  } else {
    const baseSha = await client.getBranchSha(defaultBranch);
    if (reset && (await client.getBranchSha(branch))) {
      await client.deleteBranch(branch);
      report.line(`- Deleted the stale branch \`${branch}\`.`);
    }
    if (!(await client.getBranchSha(branch))) {
      try {
        await client.createLinkedBranch(issue.node_id, baseSha, branch);
        report.line(`- Created \`${branch}\`, linked to #${issueNumber}.`);
      } catch (error) {
        await client.createBranch(branch, baseSha);
        report.line(`- Created \`${branch}\` without an issue link (${error.message}).`);
      }
    }
    if ((await client.aheadBy(defaultBranch, branch)) === 0) {
      const head = await client.getBranchSha(branch);
      const commit = await client.createEmptyCommit(head, `chore(speckit): start implementation of ${folder}`);
      await client.updateBranch(branch, commit);
      report.line('- Added the start commit.');
    }
    pull = await client.createPullRequest({
      title: renderPullRequestTitle(entry.spec),
      head: branch,
      base: defaultBranch,
      body: renderPullRequestBody({ twinNumber: issueNumber, folder, tasks, context: contextFromEnv(env) }),
      draft: true,
    }) ?? await findOpenPull(client, branch);
    if (!pull) throw new Error(`Could not create or find the pull request for ${branch}`);
    report.line(`- Opened draft pull request #${pull.number}.`);
  }

  try {
    await client.addAssignees(pull.number, [requester]);
  } catch (error) {
    report.line(`- Warning: could not assign @${requester}: ${error.message}`);
  }
  if ((await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME)).length === 0) {
    await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: pull.head.sha, status: 'queued', external_id: CHECK_PROGRESS, output: checkRunOutput(tasks.count) });
    report.line(`- Created the \`${CHECK_RUN_NAME}\` check run.`);
  }
  const comments = await client.listIssueComments(pull.number);
  if (!comments.some((comment) => comment.body?.includes(START_COMMENT_MARKER))) {
    await client.createComment(pull.number, renderStartComment({ twinNumber: issueNumber, folder, taskCount: tasks.count }));
    report.line('- Posted the start comment.');
  }
  report.flush();
  return { exitCode: 0, pull };
}

function contextFromEnv(env) {
  return {
    serverUrl: (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, ''),
    repository: env.GITHUB_REPOSITORY,
    branch: env.SPECKIT_BRANCH || 'main',
  };
}

export function defaultGit(cwd) {
  return (args) => {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 20 * 1024 * 1024 });
    if (result.error) throw new UsageError(`git is required: ${result.error.message}`);
    return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  };
}

export function resolveFolder({ folder, env, rootDir }) {
  let candidate = folder || env.SPECIFY_FEATURE_DIRECTORY;
  if (!candidate) {
    const featureFile = path.join(rootDir, '.specify', 'feature.json');
    if (existsSync(featureFile)) {
      try {
        candidate = JSON.parse(readFileSync(featureFile, 'utf8')).feature_directory;
      } catch {
        throw new UsageError('.specify/feature.json is not valid JSON.');
      }
    }
  }
  if (!candidate) throw new UsageError('No feature folder given. Pass --folder <folder> or select a feature with /speckit-specify.');
  const name = path.basename(String(candidate).replaceAll('\\', '/').replace(/\/+$/, ''));
  if (!isValidFolderName(name)) throw new UsageError(`"${candidate}" is not a valid spec folder name.`);
  return name;
}

// Reads one spec folder from the remote default branch, or returns null when it is not merged there.
export function readSpecFromRef(git, ref, folder) {
  const show = (file) => {
    const result = git(['show', `${ref}:specs/${folder}/${file}`]);
    return result.status === 0 ? result.stdout : null;
  };
  const specMarkdown = show('spec.md');
  if (specMarkdown === null) return null;
  const listing = git(['ls-tree', '-z', '--full-tree', '--name-only', ref, '--', `specs/${folder}/checklists/`]);
  const checklistFiles = listing.status === 0
    ? listing.stdout.split('\0').filter((file) => file.endsWith('.md'))
    : [];
  return buildSpecEntry(folder, {
    specMarkdown,
    hasPlan: show('plan.md') !== null,
    tasksMarkdown: show('tasks.md'),
    checklistMarkdowns: checklistFiles.map((file) => {
      const result = git(['show', `${ref}:${file}`]);
      return result.status === 0 ? result.stdout : '';
    }),
  });
}

// Client side: checks a spec against the remote default branch and flags its twin for implementation.
export async function runRequest({ client, git, rootDir, env, folder, log }) {
  const name = resolveFolder({ folder, env, rootDir });
  const branch = env.SPECKIT_BRANCH || 'main';
  const fetch = git(['fetch', '--quiet', 'origin', branch]);
  if (fetch.status !== 0) throw new UsageError(`git fetch origin ${branch} failed: ${fetch.stderr.trim()}`);
  const ref = `origin/${branch}`;
  log(`Spec folder: specs/${name} (checked against ${ref})`);

  const entry = readSpecFromRef(git, ref, name);
  if (!entry) {
    log(`Not requested: specs/${name} is not merged to ${branch} yet. Merge the spec, plan, and tasks first.`);
    return { exitCode: 1 };
  }
  const computedStage = deriveStage(entry.artifacts, entry.tasks);
  const twin = resolveTwins(await client.listTwinIssues(TWIN_LABEL)).byFolder.get(name);
  if (!twin || twin.state !== 'open') {
    log(`Not requested: no open spec twin exists for specs/${name} yet. The Spec Kit prepare workflow creates it after the merge.`);
    return { exitCode: 1 };
  }
  const url = twin.html_url ?? `#${twin.number}`;
  const implementLabel = stageLabel(IMPLEMENT_STAGE);
  const reportBlockers = async () => {
    const open = openBlockers(await client.listBlockedBy(twin.number));
    log(open.length > 0
      ? `Open blockers: ${blockerList(open)}. The speckit-orchestrate workflow starts after they are closed.`
      : 'No open blockers: the speckit-orchestrate workflow can pick it up.');
  };

  if (hasLabel(twin, implementLabel)) {
    log(`Already requested: ${url} carries ${implementLabel}.`);
    await reportBlockers();
    return { exitCode: 0 };
  }
  const reasons = implementBlockers({ folder: name, computedStage, openChecklistItems: entry.openChecklistItems });
  if (reasons.length > 0) {
    log(`Not requested for ${url}:`);
    for (const reason of reasons) log(`- ${reason}`);
    return { exitCode: 1 };
  }
  await updateIssueWithLabels(client, twin, stageLabelChange(twin, IMPLEMENT_STAGE));
  log(`Requested: ${url} is labelled ${implementLabel}. The Spec Kit prepare workflow validates the request on GitHub.`);
  await reportBlockers();
  return { exitCode: 0 };
}

function parseArgs(argv) {
  const [command, ...rest] = argv;
  const options = { command };
  for (let index = 0; index < rest.length; index += 1) {
    const arg = rest[index];
    if (arg === '--folder') options.folder = rest[++index];
    else if (arg === '--issue') options.issue = Number(rest[++index]);
    else if (arg === '--requester') options.requester = rest[++index];
    else if (arg === '--reset') options.reset = rest[index + 1] === 'true' || rest[index + 1] === 'false' ? rest[++index] === 'true' : true;
    else throw new UsageError(`Unknown argument: ${arg}`);
  }
  return options;
}

function repositoryFromGit(git) {
  const result = git(['remote', 'get-url', 'origin']);
  const match = result.stdout.trim().match(/github\.com[:/]([^/]+\/[^/]+?)(?:\.git)?$/);
  if (!match) throw new UsageError('Could not determine the GitHub repository from the origin remote; set GITHUB_REPOSITORY.');
  return match[1];
}

function tokenFromGh() {
  const result = spawnSync('gh', ['auth', 'token'], { encoding: 'utf8', windowsHide: true });
  if (result.error || result.status !== 0 || !result.stdout.trim()) {
    throw new UsageError('No GitHub token available. Run `gh auth login` or set GH_TOKEN.');
  }
  return result.stdout.trim();
}

export async function main(argv, { env = process.env, rootDir = process.cwd(), log = console.log, client, git } = {}) {
  const options = parseArgs(argv);
  const actionsClient = () => client ?? new GitHubClient({
    token: env.GITHUB_TOKEN || env.GH_TOKEN,
    repository: env.GITHUB_REPOSITORY,
    apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
    graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
  });
  if (options.command === 'select') {
    return (await runSelect({ client: actionsClient(), rootDir, env, log })).exitCode;
  }
  if (options.command === 'orchestrate') {
    return (await runOrchestrate({ client: actionsClient(), rootDir, env, log })).exitCode;
  }
  if (options.command === 'start') {
    if (!Number.isInteger(options.issue) || options.issue <= 0 || !isValidFolderName(options.folder ?? '') || !options.requester) {
      throw new UsageError('start requires --issue <number>, --folder <folder>, and --requester <login>');
    }
    return (await runStart({
      client: actionsClient(),
      rootDir,
      env,
      issueNumber: options.issue,
      folder: options.folder,
      requester: options.requester,
      reset: Boolean(options.reset),
      log,
    })).exitCode;
  }
  if (options.command === 'request') {
    const gitRunner = git ?? defaultGit(rootDir);
    const githubClient = client ?? new GitHubClient({
      token: env.GH_TOKEN || env.GITHUB_TOKEN || tokenFromGh(),
      repository: env.GITHUB_REPOSITORY || repositoryFromGit(gitRunner),
      apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
      graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
    });
    return (await runRequest({ client: githubClient, git: gitRunner, rootDir, env, folder: options.folder, log })).exitCode;
  }
  throw new UsageError('Usage: speckit-orchestrate.mjs <select | orchestrate | start --issue <number> --folder <folder> --requester <login> [--reset [true|false]] | request [--folder <folder>]>');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof UsageError ? 2 : 1;
    },
  );
}
