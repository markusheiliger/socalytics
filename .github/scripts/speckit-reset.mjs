import { canWrite } from './speckit-prepare.mjs';
import { countOpenChecklistItems, isImplementRequested, parseTwinFolder } from './speckit-prepare-core.mjs';
import {
  ANALYZE_WORKFLOW_FILE, CHECK_PROGRESS, CHECK_RUN_NAME, DIAGNOSE_WORKFLOW_FILE,
  FIX_WORKFLOW_FILE, IMPLEMENT_CHAIN_WORKFLOW_FILES, LEGACY_IMPLEMENT_WORKFLOW_FILES,
  RESET_MARKER, baseBranchOf, dispatchImplementation, ensureImplementationDraft,
  implementationBranch, isImplementRunOf, isTwinRunOf, listTasks, parseFixEvents,
  pendingCopilotHandoff, resetState, setAttention, specTitleOfPull,
  supersedeActionRequired, syncPullRequestTaskList,
} from './speckit-implement-core.mjs';

const COMMAND_WORKFLOW = 'speckit-commands.yml';
const recordBody = (state, message) => `${RESET_MARKER}${JSON.stringify(state)} -->\n${message}`;

export async function resetEligibility(client, { implementation, twin, folder, ignoreRun = null, now = Date.now() }) {
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
  for (const child of children.filter((pull) => pull.state === 'open')) reasons.push(`related pull request #${child.number} must be merged or closed first`);
  if (pendingCopilotHandoff(comments, { now })) reasons.push('a Copilot handoff is still active; let it finish before resetting');
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
          (file === COMMAND_WORKFLOW && new RegExp(`#${implementation.number}(?:\\D|$)`).test(run.display_title ?? ''))) {
        reasons.push(`run ${run.id} is ${run.status}; wait for it to finish or cancel it before resetting`);
      }
    }
  }
  return { reasons, base, baseSha, tasksMarkdown, spec };
}

async function authorize(client, { implementation, source, actor, folder, env }) {
  const comments = await client.listIssueComments(implementation.number);
  const command = comments.find((comment) => comment.id === source);
  if (!command || !/^\/speckit[ \t]+reset$/i.test(command.body.trim()) || command.user?.login !== actor ||
      command.user?.type === 'Bot' || actor.endsWith('[bot]') ||
      !canWrite(await client.getPermission(actor))) {
    throw new Error('Reset requires the unchanged /speckit reset comment of a writer.');
  }
  const pull = await client.getPullRequest(implementation.number);
  if (pull.state !== 'open' || pull.head?.ref !== implementationBranch(folder) ||
      pull.head?.repo?.full_name !== env.GITHUB_REPOSITORY) {
    throw new Error('Reset requires an open, same-repository implementation pull request.');
  }
  return pull;
}

export async function runReset({ client, env, actor, number, commentId, folder, implementation, twin, now = Date.now }) {
  if (number !== implementation.number) return { rejected: 'post /speckit reset on the implementation pull request itself' };
  let pull = await authorize(client, { implementation, source: commentId, actor, folder, env });
  const history = resetState(await client.listIssueComments(number));
  let state = history.records.find((record) => record.source === commentId);
  if (state?.state === 'completed' && state.dispatched) return { outcome: 'already reset' };
  if (state?.state === 'failed') return { rejected: 'this reset was refused; inspect its report and post a new command after correcting the obstacle' };
  if (history.pending && history.pending.source !== commentId) return { rejected: 'another reset is pending; wait for its reconciliation' };
  const reply = (message) => client.createComment(number, message);
  if (!state) {
    const eligibility = await resetEligibility(client, { implementation: pull, twin, folder, ignoreRun: env.GITHUB_RUN_ID, now: now() });
    if (eligibility.reasons.length) return { rejected: eligibility.reasons.join('; ') };
    const operation = {
      source: commentId, actor, state: 'pending', branch: pull.head.ref, base: eligibility.base,
      baseSha: eligibility.baseSha, oldHead: pull.head.sha,
      recovery: `refs/tags/speckit-reset/pr-${number}/${commentId}`, run: env.GITHUB_RUN_ID ?? null,
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
        pull = await authorize(client, { implementation, source: commentId, actor, folder, env });
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
      state.state = 'completed';
      await save(`**Reset complete on #${number}**: restarted from \`${state.base}\` at \`${state.baseSha}\`. All branch-only changes and progress were discarded. Recovery: \`${state.recovery}\` points to \`${state.oldHead}\`. Historical checks are not verification of this new implementation. The normal implementation loop starts again at the first task; native dependencies and protected-path rules still apply.`);
    }
    await dispatchImplementation(client, env, { twin, pull: number, title: specTitleOfPull(pull) });
    state.dispatched = true;
    await save(`**Reset complete on #${number}**: the normal implementation loop was requested from \`${state.base}\` at \`${state.baseSha}\`. All branch-only work, including amendments and task progress, was discarded. Recovery: \`${state.recovery}\` retains \`${state.oldHead}\`. Existing prerequisites still apply.`);
    return { outcome: 'reset', head: state.newHead };
  } catch (error) {
    const current = await client.getBranchSha(state.branch);
    if (current === state.oldHead || await client.getRefSha(state.recovery) !== state.oldHead) {
      state.state = 'failed';
      await save(`**Reset did not change #${number}**: ${error.message}\n\n**Recommended next step**: Correct the reported obstacle, then post a new \`/speckit reset\` command. No permissions or protections were bypassed.`);
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
