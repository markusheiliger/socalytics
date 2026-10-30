import { setTimeout as sleep } from 'node:timers/promises';
import { canWrite } from './speckit-prepare.mjs';
import { countOpenChecklistItems, isImplementRequested, parseTwinFolder } from './speckit-prepare-core.mjs';
import {
  ANALYZE_WORKFLOW_FILE, CHECK_PROGRESS, CHECK_RUN_NAME, DIAGNOSE_WORKFLOW_FILE,
  FIX_WORKFLOW_FILE, IMPLEMENT_CHAIN_WORKFLOW_FILES, LEGACY_IMPLEMENT_WORKFLOW_FILES,
  BOT_LOGIN, RESET_MARKER, RESET_BOUNDARY_MARKER, RESTART_FORCE_FLAG, baseBranchOf, dispatchImplementation, ensureImplementationDraft,
  implementationBranch, folderOfBranch, isImplementRunOf, isTwinRunOf, listTasks, parseFixEvents,
  pendingCopilotHandoff, parseCommandArguments, resetState, setAttention, specTitleOfPull,
  supersedeActionRequired, syncPullRequestTaskList,
} from './speckit-implement-core.mjs';

const COMMAND_WORKFLOW = 'speckit-commands.yml';
const recordBody = (state, message) => `${RESET_MARKER}${JSON.stringify(state)} -->\n${message}`;

export async function resetEligibility(client, { implementation, twin, folder, ignoreRun = null, now = Date.now(), force = false }) {
  const reasons = [];
  const base = baseBranchOf(implementation);
  const baseSha = await client.getBranchSha(base);
  if (!baseSha) return { reasons: [`the base branch \`${base}\` does not exist`] };
  const issue = await client.getIssue(twin);
  if (issue?.state !== 'open' || !isImplementRequested(issue) || parseTwinFolder(issue.body) !== folder) {
    reasons.push('the twin is not open and authorized for this implementation');
  }
  const prefix = `specs/${folder}/`;
  const files = await client.listRepositoryFiles(baseSha);
  const spec = await client.getFileContent(`${prefix}spec.md`, baseSha);
  const plan = await client.getFileContent(`${prefix}plan.md`, baseSha);
  const tasksMarkdown = await client.getFileContent(`${prefix}tasks.md`, baseSha);
  const tasks = listTasks(tasksMarkdown);
  if (spec === null || plan === null || tasksMarkdown === null || tasks.length === 0 || tasks.some((task) => task.done)) {
    reasons.push(`\`${base}\` must contain the active spec, plan, and a nonempty, fully unchecked task list`);
  }
  const checklists = await Promise.all(files.filter((file) => file.startsWith(`${prefix}checklists/`) && file.endsWith('.md'))
    .map(async (file) => {
      const content = await client.getFileContent(file, baseSha);
      if (content === null) throw new Error(`Could not read the pinned checklist ${file}.`);
      return content;
    }));
  if (countOpenChecklistItems(checklists) > 0) reasons.push('the base spec has unchecked checklist items');
  const comments = await client.listIssueComments(implementation.number);
  const children = await client.listPullRequestsForHead(`speckit-amend/${folder}`);
  for (const event of parseFixEvents(comments)) {
    if (event.pull && !children.some((pull) => pull.number === event.pull)) children.push(await client.getPullRequest(event.pull));
  }
  const amendments = children.filter((pull) => pull.head?.ref === `speckit-amend/${folder}`);
  for (const child of children.filter((pull) => pull.state === 'open')) {
    if (!force || !amendments.includes(child)) reasons.push(`related pull request #${child.number} must be merged or closed first`);
    else if (child.head.repo?.full_name !== implementation.head.repo?.full_name || child.base?.ref !== implementation.head.ref) {
      reasons.push(`amendment #${child.number} is not owned by this implementation`);
    }
  }
  if (pendingCopilotHandoff(comments, { now })) reasons.push('a Copilot handoff is still active; let it finish before resetting');
  const active = [];
  const relatedNumbers = [implementation.number, ...amendments.map((pull) => pull.number)];
  for (const file of [...IMPLEMENT_CHAIN_WORKFLOW_FILES, ...LEGACY_IMPLEMENT_WORKFLOW_FILES, DIAGNOSE_WORKFLOW_FILE, ANALYZE_WORKFLOW_FILE, FIX_WORKFLOW_FILE, COMMAND_WORKFLOW]) {
    let runs;
    try {
      runs = await client.listWorkflowRuns(file, implementation.created_at);
    } catch (error) {
      if (error.status === 404 && LEGACY_IMPLEMENT_WORKFLOW_FILES.includes(file)) continue;
      throw error;
    }
    for (const run of runs) {
      if (String(run.id) === String(ignoreRun) || run.status === 'completed') continue;
      if (isImplementRunOf(run.display_title, twin) || isTwinRunOf(file, run.display_title, twin) ||
          run.pull_requests?.some((item) => relatedNumbers.includes(item.number)) ||
          (file === COMMAND_WORKFLOW && relatedNumbers.some((number) => new RegExp(`#${number}(?:\\D|$)`).test(run.display_title ?? '')))) {
        active.push(run);
        if (!force) reasons.push(`run ${run.id} is ${run.status}; wait for it to finish or cancel it before resetting`);
      }
    }
  }
  const amendmentBranch = `speckit-amend/${folder}`;
  const amendmentHead = force ? await client.getBranchSha(amendmentBranch) : null;
  return { reasons, base, baseSha, tasksMarkdown, spec, active, amendments,
    amendmentBranch: { name: amendmentBranch, head: amendmentHead } };
}

async function authorize(client, { implementation, source, actor, folder, env, force }) {
  const comments = await client.listIssueComments(implementation.number);
  const command = comments.find((comment) => comment.id === source);
  const match = String(command?.body ?? '').trim().match(/^\/speckit[ \t]+reset\b([\s\S]*)$/i);
  const parsed = match ? parseCommandArguments('reset', match[1].trim()) : null;
  if (!command || !parsed || parsed.unknownFlags.length || parsed.guidance ||
      parsed.flags.includes(RESTART_FORCE_FLAG) !== force || command.user?.login !== actor ||
      command.user?.type === 'Bot' || actor.endsWith('[bot]') ||
      !canWrite(await client.getPermission(actor))) {
    throw new Error('Reset requires the unchanged reset command and force mode of a writer.');
  }
  const pull = await client.getPullRequest(implementation.number);
  if (pull.state !== 'open' || pull.head?.ref !== implementationBranch(folder) ||
      pull.head?.repo?.full_name !== env.GITHUB_REPOSITORY) {
    throw new Error('Reset requires an open, same-repository implementation pull request.');
  }
  return pull;
}

export async function readResetSource(client, env) {
  const number = Number(env.SPECKIT_PULL);
  const comments = await client.listIssueComments(number);
  const source = comments.find((item) => String(item.id) === env.SPECKIT_RESET_RESUME);
  const state = resetState(comments).records.find((item) => String(item.source) === env.SPECKIT_RESET_RESUME);
  if (state?.force && state.state === 'pending' && !state.newHead) {
    try {
      await authorize(client, { implementation: { number }, source: state.source, actor: state.actor,
        folder: folderOfBranch(state.branch), env, force: true });
    } catch (error) {
      const { comment, at, ...record } = state;
      await client.updateComment(comment, recordBody({ ...record, state: 'failed' },
        `**Force reset stopped**: authorization is no longer valid: ${error.message}. Partial cleanup is retained in this record and was not rolled back.`));
      await client.createComment(number, '**Force reset stopped before branch replacement**: the original writer authorization is no longer valid. Inspect the reset report and partial cleanup, then post a new `/speckit reset` command if appropriate. Add the force flag manually only when required.');
      await setAttention(client, number, true);
      throw error;
    }
  }
  if (!source) throw new Error('The authorized reset comment is missing.');
  return source;
}

async function prepareCleanup(client, { state, save, discover, authorizeSource, pause }) {
  state.cleanup ??= { cancellations: [], closed: [], amendments: [], branch: null };
  for (let poll = 0; poll < 60; poll += 1) {
    await authorizeSource();
    const observed = await discover();
    if (observed.reasons.length) throw new Error(observed.reasons.join('; '));
    if (observed.amendments.some((pull) => pull.state === 'open' && !state.cleanup.amendments.some((item) => item.number === pull.number))) {
      throw new Error('A new amendment appeared during force cleanup.');
    }
    if (observed.active.length) {
      for (const run of observed.active) {
        if (!state.cleanup.cancellations.includes(run.id)) {
          await client.cancelWorkflowRun(run.id);
          state.cleanup.cancellations.push(run.id);
          await save(`**Force reset cleanup**: requested cancellation of run ${run.id}; waiting for confirmed completion.`);
        }
      }
      let waiting = false;
      for (const id of state.cleanup.cancellations) {
        const run = await client.getWorkflowRun(id);
        if (!run) throw new Error(`Cannot confirm that cancelled run ${id} stopped.`);
        waiting ||= run.status !== 'completed';
      }
      if (waiting) await pause(1000);
      continue;
    }
    for (const id of state.cleanup.cancellations) {
      const run = await client.getWorkflowRun(id);
      if (!run || run.status !== 'completed') throw new Error(`Cancelled run ${id} has not been confirmed stopped.`);
    }
    for (const amendment of state.cleanup.amendments) {
      const current = await client.getPullRequest(amendment.number);
      if (current.state !== 'open') continue;
      if (current.head?.sha !== amendment.head || current.head?.repo?.full_name !== state.repository ||
          current.head?.ref !== state.cleanup.branch.name || current.base?.ref !== state.branch) {
        throw new Error(`Amendment #${amendment.number} changed during force cleanup.`);
      }
      await client.updatePullRequest(amendment.number, { state: 'closed' });
      if ((await client.getPullRequest(amendment.number)).state !== 'closed') throw new Error(`Amendment #${amendment.number} did not close.`);
      state.cleanup.closed.push(amendment.number);
      await save(`**Force reset cleanup**: closed amendment #${amendment.number} without merging.`);
    }
    const branch = state.cleanup.branch;
    const head = await client.getBranchSha(branch.name);
    if (head !== null) {
      if (head !== branch.head) throw new Error('The amendment branch changed during force cleanup; it was not deleted.');
      await client.deleteAmendmentBranch(branch.name, head);
      if (await client.getBranchSha(branch.name) !== null) throw new Error('The amendment branch deletion could not be verified.');
      state.cleanup.deleted = true;
      await save(`**Force reset cleanup**: deleted the leased amendment branch \`${branch.name}\`.`);
    }
    const final = await discover();
    if (final.reasons.length) throw new Error(final.reasons.join('; '));
    if (final.active.length) { await pause(1000); continue; }
    if (final.amendments.some((pull) => pull.state === 'open') || final.amendmentBranch.head !== null) {
      throw new Error('Amendment work appeared again during force cleanup.');
    }
    state.prepared = true;
    await save('**Force reset prepared**: related work is confirmed stopped and owned amendments are removed. Waiting for the publication lock.');
    return;
  }
  throw new Error('Timed out confirming that related workflows stopped; the implementation branch was not reset.');
}

export async function runReset({ client, env, actor, number, commentId, folder, implementation, twin, flags = [], prepareOnly = false, now = Date.now, pause = sleep }) {
  if (number !== implementation.number) return { rejected: 'post /speckit reset on the implementation pull request itself' };
  const force = flags.includes(RESTART_FORCE_FLAG);
  if (prepareOnly && !force) return { outcome: 'not forced' };
  const authorizeSource = () => authorize(client, { implementation, source: commentId, actor, folder, env, force });
  let pull = await authorizeSource();
  const history = resetState(await client.listIssueComments(number));
  let state = history.records.find((record) => record.source === commentId);
  if (state && Boolean(state.force) !== force) throw new Error('The reset force authorization changed.');
  if (state?.state === 'completed' && state.dispatched) return { outcome: 'already reset' };
  if (state?.state === 'failed') return { rejected: 'this reset was refused; inspect its report and post a new command after correcting the obstacle' };
  if (history.pending && history.pending.source !== commentId) return { rejected: 'another reset is pending; wait for its reconciliation' };
  const reply = (message) => client.createComment(number, message);
  if (!state) {
    const eligibility = await resetEligibility(client, { implementation: pull, twin, folder, ignoreRun: env.GITHUB_RUN_ID, now: now(), force });
    if (eligibility.reasons.length) return { rejected: eligibility.reasons.join('; ') };
    const operation = {
      source: commentId, actor, state: 'pending', branch: pull.head.ref, base: eligibility.base,
      baseSha: eligibility.baseSha, oldHead: pull.head.sha,
      recovery: `refs/tags/speckit-reset/pr-${number}/${commentId}`, run: env.GITHUB_RUN_ID ?? null,
      force, repository: env.GITHUB_REPOSITORY,
      ...(force ? { cleanup: { cancellations: [], closed: [],
        amendments: eligibility.amendments.filter((item) => item.state === 'open').map((item) => ({ number: item.number, head: item.head.sha })),
        branch: eligibility.amendmentBranch } } : {}),
    };
    const comment = await reply(recordBody(operation,
      `**Reset requested on #${number}** by @${actor}: discard all branch-only changes and progress, including amendments. The old head will be retained in \`${operation.recovery}\`. No implementation work may publish while reset is pending.`));
    state = { ...operation, comment: comment.id, at: comment.created_at };
  }
  const save = async (message) => {
    const { comment, at, ...record } = state;
    await client.updateComment(comment, recordBody(record, message));
  };
  try {
    if (force && prepareOnly && state.state !== 'completed') {
      state.run = env.GITHUB_RUN_ID ?? null;
      await save('**Force reset preparation**: fenced this implementation before cancelling related work.');
      await prepareCleanup(client, { state, save, authorizeSource, pause,
        discover: async () => resetEligibility(client, { implementation: await authorizeSource(), twin, folder, ignoreRun: env.GITHUB_RUN_ID, now: now(), force: true }) });
      return { outcome: 'prepared' };
    }
    if (prepareOnly) return { outcome: 'prepared' };
    if (force && !state.prepared) throw new Error('Force reset requires successful trusted preparation before the publication lock.');
    if (state.state !== 'completed') {
      const recovery = await client.getRefSha(state.recovery);
      if (!(state.newHead && pull.head.sha === state.newHead && recovery === state.oldHead)) {
        const eligibility = await resetEligibility(client, { implementation: pull, twin, folder, ignoreRun: env.GITHUB_RUN_ID, now: now() });
        if (eligibility.reasons.length) throw new Error(eligibility.reasons.join('; '));
        if (baseBranchOf(pull) !== state.base || pull.head.sha !== state.oldHead) throw new Error('The PR head or base changed after reset authorization.');
        if (!state.newHead) {
          state.newHead = await client.createEmptyCommit(state.baseSha, `chore(speckit): reset #${number} from ${state.base} (request ${commentId})`);
          state.run = env.GITHUB_RUN_ID ?? null;
          await save(`**Reset pending on #${number}**: prepared a fresh start; the previous head remains protected by an exact-head lease.`);
        }
        pull = await authorizeSource();
        if (baseBranchOf(pull) !== state.base || pull.head.sha !== state.oldHead) throw new Error('The PR head or base changed before reset publication.');
        await client.resetImplementationRef(state);
      }
      const [head, recoveryHead, commit, baseCommit] = await Promise.all([
        client.getBranchSha(state.branch), client.getRefSha(state.recovery),
        client.getGitCommit(state.newHead), client.getGitCommit(state.baseSha),
      ]);
      if (head !== state.newHead || recoveryHead !== state.oldHead || commit.tree.sha !== baseCommit.tree.sha ||
          commit.parents.length !== 1 || commit.parents[0].sha !== state.baseSha) {
        throw new Error('The reset references or base-equivalent start commit could not be verified.');
      }
      pull = await client.getPullRequest(number);
      await ensureImplementationDraft(client, pull);
      const tasksMarkdown = await client.getFileContent(`specs/${folder}/tasks.md`, state.baseSha);
      const body = syncPullRequestTaskList(pull.body, { twinNumber: twin, folder, tasksMarkdown, context: {
        repository: env.GITHUB_REPOSITORY, serverUrl: env.GITHUB_SERVER_URL || 'https://github.com', branch: state.base, archived: false,
      } });
      if (body !== pull.body) await client.updatePullRequest(number, { body });
      const checks = await client.listCheckRuns(state.newHead, CHECK_RUN_NAME);
      if (!checks.some((check) => check.external_id === CHECK_PROGRESS)) {
        await client.createCheckRun({ name: CHECK_RUN_NAME, head_sha: state.newHead, status: 'queued', external_id: CHECK_PROGRESS,
          output: { title: 'Implementation reset', summary: 'All implementation progress is discarded; start again at the first task.' } });
      }
      await setAttention(client, number, false);
      await supersedeActionRequired(client, number);
      const boundaryBody = `${RESET_BOUNDARY_MARKER}${JSON.stringify({ source: commentId })} -->`;
      const existing = (await client.listIssueComments(number)).find((item) => item.user?.login === BOT_LOGIN && item.body === boundaryBody);
      const boundary = existing ?? await reply(boundaryBody);
      state.boundary = boundary.id;
      state.at = boundary.created_at;
      state.state = 'completed';
      await save(`**Reset complete on #${number}**: restarted from \`${state.base}\` at \`${state.baseSha}\`. All branch-only changes and progress were discarded. Recovery: \`${state.recovery}\` points to \`${state.oldHead}\`. Historical checks are not verification of this new implementation. The normal implementation loop starts again at the first task; native dependencies and protected-path rules still apply.`);
    }
    // GitHub timestamps have second precision; new runs must be strictly newer than the completion boundary.
    const delay = Date.parse(state.at) + 1000 - now();
    if (delay > 0) await pause(delay);
    await dispatchImplementation(client, env, { twin, pull: number, title: specTitleOfPull(pull) });
    state.dispatched = true;
    await save(`**Reset complete on #${number}**: the normal implementation loop was requested from \`${state.base}\` at \`${state.baseSha}\`. All branch-only work, including amendments and task progress, was discarded. Recovery: \`${state.recovery}\` retains \`${state.oldHead}\`. Existing prerequisites still apply.`);
    return { outcome: 'reset', head: state.newHead };
  } catch (error) {
    const current = await client.getBranchSha(state.branch);
    if (current === state.oldHead || await client.getRefSha(state.recovery) !== state.oldHead) {
      state.state = 'failed';
      await save(`**Reset did not replace the implementation branch of #${number}**: ${error.message}\n\nPartial cleanup (not rolled back): ${JSON.stringify(state.cleanup ?? {})}.\n\n**Recommended next step**: Inspect the reported obstacle and partial cleanup, then post a new \`/speckit reset\` command. No permissions or protections were bypassed.`);
      await reply(`**Reset stopped on #${number}**: ${error.message}. The implementation branch was not replaced. Completed cleanup is recorded in the reset report and was not rolled back.\n\n**Recommended next step**: Inspect that report and resolve the blocker before posting \`/speckit reset\` again. Add the force flag manually only if cleanup is still required.`);
      await setAttention(client, number, true);
    } else {
      await reply(`**Reset on #${number} requires reconciliation**: ${error.message}\n\nThe recorded operation and recovery reference are retained. The scheduler will reconcile the interrupted reset; do not repeat it or bypass verification.`);
    }
    throw error;
  }
}

export async function maintainReset(client, env, { implementation, twin, folder }) {
  const state = resetState(await client.listIssueComments(implementation.number));
  const operation = state.pending ?? (state.completed && !state.completed.dispatched ? state.completed : null);
  if (!operation) return false;
  const run = operation.run ? await client.getWorkflowRun(operation.run) : null;
  if (!run || run.status === 'completed') {
    const commands = await client.listWorkflowRuns(COMMAND_WORKFLOW, operation.at);
    if (!commands.some((item) => item.status !== 'completed' &&
        new RegExp(`#${implementation.number}(?:\\D|$)`).test(item.display_title ?? ''))) {
      await client.dispatchWorkflow(COMMAND_WORKFLOW, env.SPECKIT_DEFAULT_BRANCH, {
        pull: String(implementation.number), reset_comment: String(operation.source),
      });
    }
  }
  return true;
}
