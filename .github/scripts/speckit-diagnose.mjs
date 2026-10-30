import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { specDir } from './speckit-prepare-core.mjs';
import { createGitHubClient } from './speckit-script-runtime.mjs';
import { AmendUsageError, beginAmendment, checkAmendment, decodeTracked, findAmendment, finishAmendmentRequest, pendingFeedback, presentAmendment, readAmendmentAuthorization, reportSpecPullFailure, resolveAmendment, startAnalysis, startRework } from './speckit-amend.mjs';
import { RETRY_EFFECT, requestSync, restartState, retryImplementation } from './speckit-commands.mjs';
import {
  AUTO_CONTINUE_MARKER,
  AMEND_AUTHORIZATION_MARKER,
  AMEND_REQUEST_STATE_MARKER,
  BOT_LOGIN,
  CATEGORY_MEANING,
  CHECK_AMEND_CHECKING,
  CHECK_AMEND_INCONSISTENT,
  CHECK_DIAGNOSED,
  CHECK_DIAGNOSING,
  CHECK_LIMIT,
  CHECK_RUN_NAME,
  DIAGNOSE_WORKFLOW_FILE,
  DIAGNOSIS_COMMENT_MARKER,
  MAX_CORRECTION_ROUNDS,
  IMPLEMENT_CHAIN_WORKFLOW_FILES,
  LEGACY_IMPLEMENT_WORKFLOW_FILES,
  MAX_STALLED_ROUNDS,
  MAX_WRITER_GUIDANCE,
  PR_COMMANDS,
  MAX_COPILOT_PROMPT,
  RECOMMENDABLE_COMMANDS,
  amendmentBranch,
  baseBranchOf,
  commandsForCategory,
  defaultBranchOf,
  diagnosisRunsUrl,
  implementationBranch,
  isImplementRunOf,
  isTwinRunOf,
  latestHumanCommit,
  latestCheckRun,
  neutralizeMarkers,
  nextStepContext,
  nextTaskGroup,
  failedCopilotRecoveries,
  failedRecoveryRoutes,
  failedSynchronization,
  recoveryReassessment,
  reassessRecovery,
  normalizeRecovery,
  trustedMarker,
  pendingAmendmentRequest,
  parseNextCommand,
  readSpecTasks,
  renderCommandReference,
  renderNextSteps,
  repeatedFailure,
  setAttention,
  unresolvedRepair,
} from './speckit-implement-core.mjs';

export { checkAmendment, findAmendment };

export const CATEGORIES = ['artifacts', 'retry', 'outside', 'decision', 'unknown'];
export const CONFIDENCE = ['high', 'medium', 'low'];
export const MODES = ['diagnose', 'amend', 'fix', 'revise'];
// Every command list of the diagnosis is derived from PR_COMMANDS, so a new, changed, or removed command needs no edit
// here (see commandPatterns and categoryCommands).
const escapeRegExp = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
export function commandPatterns(names = RECOMMENDABLE_COMMANDS) {
  const verbs = names.map(escapeRegExp).join('|');
  return {
    command: new RegExp(`^\\/speckit (${verbs})( [^\\n]*)?$`),
    inText: new RegExp(`[\`'"](\\/speckit (?:${verbs})(?: [^\`'"\\n]*)?)[\`'"]|(\\/speckit (?:${verbs}))\\b`),
  };
}
const { command: COMMAND_PATTERN, inText: COMMAND_IN_TEXT } = commandPatterns();
// A recommended command: the longest `/speckit <verb>` with all its flags, a space, and the guidance limit the
// commands accept.
export const MAX_RECOMMENDED_COMMAND = Math.max(...PR_COMMANDS.filter((command) => command.recommend)
  .map((command) => `/speckit ${command.name} ${Object.keys(command.flags ?? {}).join(' ')} `.length)) + MAX_WRITER_GUIDANCE;
// A placeholder an agent may copy from a usage line instead of writing the text: `<guidance>` or `<answers>`.
// Only these words count, so code such as `IReadOnlyList<Recording>` or task tags such as `[P]` stay intact.
const PLACEHOLDER_PATTERN = /<(?:concrete |your )?(?:guidance|answers?|guidance or answers|choice|first choice|what to examine next|what to fix[^<>\n]{0,60})>|\[(?:concrete |your )?(?:guidance|answers?|guidance or answers)\]/gi;
const MAX_EVIDENCE_BYTES = 200_000;
const LOG_TAIL_LINES = 150;

const clip = (text, max) => {
  const value = String(text ?? '');
  return value.length > max ? `${value.slice(0, max)}…` : value;
};

export class DiagnoseUsageError extends Error {}

// Longest stop description in a diagnose run name.
export const MAX_STOP_SUBJECT = 120;

// What stopped the implementation, for the diagnose run name: the title of the latest stop check run (CHECK_LIMIT) on
// `head`, such as "T013 reached the attempt limit" or "The phase verification failed"; empty when there is none.
export async function stopSubject(client, head) {
  try {
    const stop = latestCheckRun((await client.listCheckRuns(head, CHECK_RUN_NAME)).filter((check) => check.external_id === CHECK_LIMIT));
    return clip(String(stop?.output?.title ?? '').replace(/\s+/g, ' ').trim(), MAX_STOP_SUBJECT);
  } catch {
    return '';
  }
}

// Starts a diagnosis of the implementation pull request `pull`: a check run on the head marks the implementation as
// waiting for the diagnosis, and the agentic diagnose workflow is dispatched (its run is named after the stop). A
// dispatch failure leaves an unfinished diagnosis check so the implementation controller starts it again.
export async function startDiagnosis(client, env, { twin, pull, folder, head, guidance = '', previous = '', actor = null, auto = false, mode = 'diagnose', authorization = null, preserveAmendment = false }) {
  // A new diagnosis supersedes an earlier amendment; its branch must not exist when the new run proposes one.
  const superseded = await findAmendment(client, folder);
  if (superseded && preserveAmendment) return { started: false, error: `Amendment #${superseded.number} is already open; review or rework it.` };
  if (mode === 'diagnose' && pendingAmendmentRequest(await client.listIssueComments(pull))) {
    return { started: false, error: 'An authorized amendment request is pending.' };
  }
  if (superseded) {
    await client.updatePullRequest(superseded.number, { state: 'closed' });
    await client.createComment(superseded.number, 'Superseded by a new diagnosis; a new amendment, if any, follows in its own pull request.');
    await setAttention(client, superseded.number, false);
  }
  try {
    await client.deleteBranch(amendmentBranch(folder));
  } catch {
    // The branch usually does not exist.
  }
  const summary = [
    auto ? 'Started automatically because the implementation stopped.' : `Started by @${actor}.`,
    ...(guidance ? ['', `Guidance: ${clip(guidance, 2000)}`] : []),
  ].join('\n');
  const stop = await stopSubject(client, head);
  const check = await client.createCheckRun({
    name: CHECK_RUN_NAME,
    head_sha: head,
    status: 'in_progress',
    external_id: CHECK_DIAGNOSING,
    details_url: diagnosisRunsUrl(env),
    output: { title: mode === 'amend' ? 'Authorized amendment in progress' : 'Diagnosis in progress', summary },
  });
  try {
    await client.dispatchWorkflow(DIAGNOSE_WORKFLOW_FILE, defaultBranchOf(env), {
      issue: String(twin),
      pull: String(pull),
      folder,
      guidance: clip(guidance, 4000),
      previous: String(previous ?? ''),
      check_run: String(check.id),
      mode,
      ...(authorization ? { authorization: String(authorization) } : {}),
      amendment: '',
      round: '0',
      stop,
    });
    await setAttention(client, pull, false);
    return { started: true, check };
  } catch (error) {
    await client.updateCheckRun(check.id, {
      status: 'completed',
      conclusion: 'failure',
      external_id: CHECK_DIAGNOSING,
      output: { title: 'Diagnosis could not start', summary: clip(error.message, 2000) },
    });
    await setAttention(client, pull, false);
    return { started: false, check, error: error.message };
  }
}

function inputsFrom(env) {
  const twin = Number(env.SPECKIT_TWIN);
  const pull = Number(env.SPECKIT_PULL);
  const folder = String(env.SPECKIT_FOLDER ?? '');
  if (!Number.isInteger(twin) || twin <= 0 || !Number.isInteger(pull) || pull <= 0 || !/^[\w.-]+$/.test(folder)) {
    throw new DiagnoseUsageError('Expected SPECKIT_TWIN and SPECKIT_PULL as positive integers and SPECKIT_FOLDER as a spec folder name.');
  }
  const mode = MODES.includes(env.SPECKIT_MODE) ? env.SPECKIT_MODE : 'diagnose';
  const amendment = Number(env.SPECKIT_AMENDMENT);
  if (['fix', 'revise'].includes(mode) && (!Number.isInteger(amendment) || amendment <= 0)) throw new DiagnoseUsageError(`Mode ${mode} needs SPECKIT_AMENDMENT.`);
  const authorization = Number(env.SPECKIT_AUTHORIZATION);
  if (mode === 'amend' && (!Number.isInteger(authorization) || authorization <= 0)) throw new DiagnoseUsageError('Mode amend needs SPECKIT_AUTHORIZATION.');
  return {
    twin,
    pull,
    folder,
    mode,
    amendment: ['fix', 'revise'].includes(mode) ? amendment : null,
    authorization: mode === 'amend' ? authorization : null,
    round: Math.max(0, Number(env.SPECKIT_ROUND) || 0),
    stalls: Math.max(0, Number(env.SPECKIT_STALLS) || 0),
    findings: decodeTracked(env.SPECKIT_FINDINGS),
  };
}

function logTail(text) {
  return String(text ?? '')
    .split(/\r?\n/)
    .map((line) => line.replace(/^\d{4}-\d\d-\d\dT[\d:.]+Z /, ''))
    .slice(-LOG_TAIL_LINES)
    .join('\n');
}

// Agent job, before the agent: collects what the agent needs to diagnose the stop and cannot fetch itself, and writes
// evidence.md and context.json to SPECKIT_EVIDENCE_DIR.
export async function runEvidence({ client, env, log }) {
  const { twin, pull: pullNumber, folder, mode, amendment: amendmentNumber, authorization, round, stalls } = inputsFrom(env);
  const outDir = path.resolve(env.SPECKIT_EVIDENCE_DIR || 'evidence');
  mkdirSync(outDir, { recursive: true });
  const branch = implementationBranch(folder);
  const pull = await client.getPullRequest(pullNumber);
  if (mode === 'amend' && pull.head.ref !== implementationBranch(folder)) throw new DiagnoseUsageError('The authorized amendment folder does not match the implementation branch.');
  const authorized = mode === 'amend' ? await readAmendmentAuthorization(client, pull, authorization) : null;
  const baseBranch = baseBranchOf(pull);
  const { tasksMarkdown, archived } = await readSpecTasks(client, folder, pull.head.sha);
  const next = nextTaskGroup(tasksMarkdown, 1)[0] ?? null;
  const checks = [...await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME)].sort((a, b) => a.id - b.id);
  const comments = await client.listIssueComments(pullNumber);
  const previousId = Number(env.SPECKIT_PREVIOUS);
  const previous = Number.isInteger(previousId) && previousId > 0 ? comments.find((comment) => comment.id === previousId) : null;
  let behind = null;
  let ahead = null;
  try {
    behind = await client.aheadBy(branch, baseBranch);
    ahead = await client.aheadBy(baseBranch, branch);
  } catch {
    // The comparison is only context.
  }

  const sections = [
    `# Evidence for the diagnosis of #${twin} (pull request #${pullNumber})`,
    '',
    `- Mode: \`${mode}\`${mode === 'fix' ? ` (correction round ${round}; rounds without progress so far: ${stalls} of ${MAX_STALLED_ROUNDS})` : ''}${amendmentNumber ? `; amendment pull request #${amendmentNumber} on \`${amendmentBranch(folder)}\`` : ''}`,
    `- Spec folder: \`${specDir(folder, { archived })}\`${archived ? ' (archived on the implementation branch for the merge)' : ''}`,
    `- Implementation branch: \`${branch}\` at ${pull.head.sha}`,
    `- Base branch \`${baseBranch}\` (the pull request's base, which the implementation merges into): ${behind ?? '?'} commit(s) the implementation branch lacks; the branch is ${ahead ?? '?'} commit(s) ahead`,
    `- Next unchecked task: ${next ? `${next.id} ${next.text}` : 'none (all tasks are checked)'}`,
    '',
  ];
  if (authorized) {
    sections.push('## Writer-authorized artifact amendment', '',
      `Writer @${authorized.actor} authorized request ${authorized.comment} at ${authorized.head}.`,
      '', authorized.guidance, '',
      'Change only this feature folder. Preserve completed tasks verbatim and never tick a task. Propagate the authorized change through every affected artifact. Do not implement tasks or claim unverified manual evidence. Create an amendment for independent analysis and human review/merge; never push directly to the implementation.', '');
  } else if (mode === 'fix') {
    sections.push('## Findings of the consistency check to fix', '', env.SPECKIT_GUIDANCE?.trim() || '_None._', '');
  } else if (mode === 'revise') {
    // The feedback was collected when the rework started (as guidance); its marker already moved the feedback window.
    sections.push('## Feedback on the amendment to address', '', env.SPECKIT_GUIDANCE?.trim() || '_None._', '');
    const comments = await client.listIssueComments(amendmentNumber);
    sections.push('## Earlier comments on the amendment (oldest first)', '');
    for (const comment of comments.slice(-10)) sections.push(`### ${comment.user?.login ?? 'unknown'} at ${comment.created_at}`, '', clip(comment.body, 4000), '');
  } else {
    sections.push('## Guidance from the person who started this diagnosis', '', env.SPECKIT_GUIDANCE?.trim() ? env.SPECKIT_GUIDANCE.trim() : '_None._', '');
    // The live command reference: what each command does on this pull request, generated from PR_COMMANDS.
    sections.push(renderCommandReference({ baseBranch, repair: unresolvedRepair(comments) }));
    const failedSync = failedSynchronization(comments, pull.head.sha);
    if (failedSync) sections.push('## Earlier required synchronization failed', '',
      `The required \`/speckit sync\` requested at ${failedSync.at}${failedSync.url ? ` (${failedSync.url})` : ''} stopped without moving this branch: ${neutralizeMarkers(failedSync.reason)}`,
      'State this failed sync prominently in the diagnosis, outside collapsed evidence. Explain why another sync would or would not help. A permission cause is a hypothesis unless the remote rejection proves it. If a trusted-tooling fix can unblock it, provide a self-contained Copilot Chat prompt targeting the default branch, not a PR-comment handoff.', '');
    const repeated = repeatedFailure(comments, pull.head.sha);
    if (repeated) {
      sections.push(
        '## A command that already failed the same way',
        '',
        `The last \`/speckit ${repeated.verb}\` left the implementation branch at ${pull.head.sha} and stopped with the same reason as the stop before it: ${neutralizeMarkers(repeated.reason)}`,
        '',
        `Do not recommend \`/speckit ${repeated.verb}\` again: it would repeat the failure. Recommend a command that changes something, such as \`/speckit author\` for a cause outside this spec. A recommendation of \`/speckit ${repeated.verb}\` is replaced at publication.`,
        '',
      );
    }
    if (amendmentNumber) {
      const authorization = trustedMarker(await client.listIssueComments(amendmentNumber), AMEND_AUTHORIZATION_MARKER).at(-1);
      if (authorization) sections.push('## Authorized amendment preservation rule', '',
        `This amendment has writer authorization ${authorization.request}. Preserve every completed task verbatim from ${authorization.head}, including checkbox states. Never tick tasks, implement code, or claim unverified manual evidence.`, '');
    }
  }
  sections.push(
    '## Check runs on the head (oldest first)',
    '',
    ...checks.map((check) => `- ${check.external_id ?? '-'} ${check.status}/${check.conclusion ?? '-'}: ${check.output?.title ?? ''}${check.output?.summary ? ` — ${clip(check.output.summary, 600).replace(/\s+/g, ' ')}` : ''}`),
    '',
  );
  if (previous) sections.push('## Previous diagnosis', '', clip(previous.body, 20_000), '');
  const failedRecoveries = failedCopilotRecoveries(comments, pull.head.sha);
  const failedRoutes = failedRecoveryRoutes(comments, pull.head.sha);
  if (failedRoutes.length) sections.push('## Refused or failed recovery routes for this unchanged blocker', '',
    ...failedRoutes.map((item) => `- ${item.route}: ${neutralizeMarkers(item.reason)}${item.guidance ? `\n  Requested guidance: ${neutralizeMarkers(item.guidance)}` : ''}`),
    'Do not repeat or paraphrase these unsupported routes. The single reassessment must identify a supported remedy or a precise external action/missing evidence.', '');
  if (failedRecoveries.length) {
    sections.push('## Failed Copilot recoveries on this unchanged revision', '',
      'Do not repeat these requests or paraphrase the same ineffective action. Address the refusal. If this feature needs a writer-approved artifact change, recommend `/speckit amend` with concrete guidance instead.', '');
    for (const failed of failedRecoveries) sections.push(`### Handoff ${failed.handoff}`, '', clip(failed.request, 6000), '');
  }
  sections.push('## Recent pull request comments (oldest first)', '');
  for (const comment of comments.slice(-15)) {
    sections.push(`### ${comment.user?.login ?? 'unknown'} at ${comment.created_at}`, '', clip(comment.body, 8000), '');
  }
  // A task that fails its verification is reported in the pull request comments above; failed jobs are crashes,
  // timeouts, and infrastructure problems.
  sections.push('## Failed jobs of recent implementation runs', '');
  // Runs of the chain before groups count too while implementations started on it are still open.
  const listed = [];
  for (const file of [...IMPLEMENT_CHAIN_WORKFLOW_FILES, ...LEGACY_IMPLEMENT_WORKFLOW_FILES]) {
    try {
      listed.push(...await client.listWorkflowRuns(file, pull.created_at, 2));
    } catch {
      // A removed workflow file may not be listed any more.
    }
  }
  const runs = listed
    .filter((run) => isImplementRunOf(run.display_title, twin) && run.status === 'completed')
    .sort((a, b) => Date.parse(b.created_at ?? 0) - Date.parse(a.created_at ?? 0))
    .slice(0, 5);
  for (const run of runs) {
    const failedJobs = (await client.listRunJobs(run.id)).filter((item) => item.conclusion === 'failure');
    if (failedJobs.length === 0) continue;
    sections.push(`### ${run.display_title} (${run.conclusion}) ${run.html_url ?? ''}`, '');
    for (const job of failedJobs) {
      const logs = await client.getJobLogs(job.id);
      sections.push(`#### Job "${job.name}"`, '', '```text', logs ? logTail(logs).replaceAll('```', "'''") : '(log not available)', '```', '');
    }
  }
  let evidence = sections.join('\n');
  if (Buffer.byteLength(evidence) > MAX_EVIDENCE_BYTES) evidence = `${evidence.slice(0, MAX_EVIDENCE_BYTES)}\n\n_(evidence truncated)_\n`;
  writeFileSync(path.join(outDir, 'evidence.md'), evidence);
  writeFileSync(path.join(outDir, 'context.json'), `${JSON.stringify({
    mode,
    twin,
    pull: pullNumber,
    folder,
    implementationBranch: branch,
    amendmentBranch: amendmentBranch(folder),
    amendmentPull: amendmentNumber,
    round,
    maxCorrectionRounds: MAX_CORRECTION_ROUNDS,
    maxStalledRounds: MAX_STALLED_ROUNDS,
    baseBranch,
    specDir: specDir(folder, { archived }),
    head: pull.head.sha,
    nextTask: next?.id ?? null,
    ...(authorized ? { authorization: authorized } : {}),
    previousDiagnosis: previous ? true : false,
  }, null, 2)}\n`);
  log(`Wrote the evidence for #${twin} to ${outDir}.`);
  return { exitCode: 0 };
}

// Validates and normalizes the agent's report; the agent's text is untrusted, so every field is checked and clipped.
// A rework (`fix`, `revise`) reports a summary and responses to the feedback, but no category.
export function validateReport(raw, { requireCategory = true } = {}) {
  const reasons = [];
  let report = raw;
  if (typeof raw === 'string') {
    try {
      report = JSON.parse(raw);
    } catch (error) {
      return { reasons: [`the report is not valid JSON: ${error.message}`], report: null };
    }
  }
  if (!report || typeof report !== 'object') return { reasons: ['the report is missing'], report: null };
  const text = (value, max) => (typeof value === 'string' ? clip(value.trim(), max) : '');
  const normalized = {
    category: CATEGORIES.includes(report.category) ? report.category : null,
    confidence: CONFIDENCE.includes(report.confidence) ? report.confidence : 'low',
    summary: text(report.summary, 1500),
    cause: text(report.cause, 6000),
    evidence: (Array.isArray(report.evidence) ? report.evidence : []).map((item) => text(item, 1500)).filter(Boolean).slice(0, 12),
    artifacts: (Array.isArray(report.artifacts) ? report.artifacts : [])
      .map((item) => ({ file: text(item?.file, 300), reason: text(item?.reason, 600) }))
      .filter((item) => item.file)
      .slice(0, 20),
    options: (Array.isArray(report.options) ? report.options : [])
      .map((item, index) => normalizeOption(item, index, reasons))
      .filter(Boolean)
      .slice(0, 6),
    questions: (Array.isArray(report.questions) ? report.questions : [])
      .map((item) => ({ question: text(item?.question, 600), choices: (Array.isArray(item?.choices) ? item.choices : []).map((choice) => text(choice, 300)).filter(Boolean).slice(0, 5) }))
      .filter((item) => item.question)
      .slice(0, 3),
    responses: (Array.isArray(report.responses) ? report.responses : [])
      .map((item) => ({ feedback: text(item?.feedback, 400), response: text(item?.response, 1500) }))
      .filter((item) => item.response)
      .slice(0, 20),
  };
  if (requireCategory && !normalized.category) reasons.push(`the category must be one of ${CATEGORIES.join(', ')}`);
  if (!normalized.summary) reasons.push('the summary is missing');
  for (const option of normalized.options) {
    if (option.kind === 'external' && (option.command || (option.copilot && option.target !== 'tooling'))) {
      reasons.push(`option "${option.title}" is an external prerequisite and must not attach an immediately runnable command`);
    }
    if (!option.command) continue;
    const command = removePlaceholderArgument(option.command);
    if (command === null || !COMMAND_PATTERN.test(command) || command.length > MAX_RECOMMENDED_COMMAND || command.includes('```') || parseNextCommand(command)?.unknownFlags.length) {
      reasons.push(`option "${option.title}" has an invalid command`);
      option.command = null;
    } else {
      option.command = command;
      if (parseNextCommand(command)?.verb === 'amend' && !parseNextCommand(command).guidance.trim()) {
        reasons.push(`option "${option.title}" needs concrete amendment guidance`);
        option.command = null;
      }
    }
  }
  for (const option of normalized.options) {
    if (!option.copilot) continue;
    const prompt = option.copilot.replace(/^@copilot\b[\s,:]*/i, '').trim();
    if (option.target === 'tooling' && (normalized.category !== 'outside' || option.kind !== 'external')) {
      reasons.push(`option "${option.title}" needs category outside and kind external for a default-branch tooling prompt`);
      option.copilot = null;
    } else if (option.command) {
      reasons.push(`option "${option.title}" has both a command and a Copilot prompt; the command is kept`);
      option.copilot = null;
    } else if (!prompt || prompt.length > MAX_COPILOT_PROMPT || prompt.includes('```') || prompt.match(PLACEHOLDER_PATTERN)) {
      reasons.push(`option "${option.title}" has an invalid Copilot prompt`);
      option.copilot = null;
    } else if (implementsTasks(prompt)) {
      reasons.push(`option "${option.title}" asks Copilot to do the spec's tasks; a Copilot prompt may only remove what stops the automation, which implements the tasks itself`);
      option.copilot = null;
    } else {
      option.copilot = prompt;
    }
  }
  // Exactly one recommendation: the first one the agent marked.
  let recommended = false;
  for (const option of normalized.options) {
    if (option.recommended && recommended) {
      reasons.push(`option "${option.title}" was marked as recommended too; only the first recommendation counts`);
      option.recommended = false;
    }
    recommended ||= option.recommended;
  }
  normalized.options.sort((a, b) => Number(b.recommended) - Number(a.recommended));
  return { reasons, report: normalized };
}

// Whether a Copilot prompt hands the spec's own work to Copilot: it names a task (`T012`), `tasks.md`, or asks to
// implement tasks. A prompt may only remove what stops the automation (a file outside the spec's reach, a dead
// link); the Spec Kit implementation implements, verifies, and ticks every task itself.
export function implementsTasks(prompt) {
  const text = String(prompt ?? '');
  return /\bT\d{3,}\b/.test(text) || /\btasks\.md\b/i.test(text) || /\bimplement\b[^.\n]{0,80}\btasks?\b/i.test(text);
}

// One option of a report: { title, why, command, copilot, recommended }. An option given as plain text (agents sometimes write
// the option as one sentence) keeps its first sentence as the title, the rest as why, and a `/speckit` command it
// mentions as its command. Options without a usable title are dropped with a reason.
function normalizeOption(item, index, reasons) {
  const text = (value, max) => (typeof value === 'string' ? clip(value.trim(), max) : '');
  if (typeof item === 'string') {
    const value = item.trim().replace(/\s+/g, ' ');
    if (!value) return null;
    const match = value.match(COMMAND_IN_TEXT);
    const [first, ...rest] = value.split(/(?<=[.!?])\s+/);
    reasons.push(`option ${index + 1} was plain text instead of an object; it was read as "${clip(first, 120)}"`);
    return {
      title: clip(first, 400),
      // A sentence that only marks the option as recommended says nothing about why it helps.
      why: clip(rest.filter((sentence) => !/\brecommended\b/i.test(sentence)).join(' '), 400),
      command: match ? (match[1] ?? match[2]).trim() : null,
      copilot: null,
      recommended: /\brecommended\b/i.test(value) && !/\bnot recommended\b/i.test(value),
    };
  }
  const title = text(item?.title, 400);
  if (!title) {
    reasons.push(`option ${index + 1} has no title and was dropped`);
    return null;
  }
  const why = text(item?.why, 400);
  if (!why) reasons.push(`option "${clip(title, 120)}" does not say why it helps`);
  return {
    title,
    why,
    command: typeof item?.command === 'string' && item.command.trim() ? item.command.trim() : null,
    copilot: typeof item?.copilot === 'string' && item.copilot.trim() ? clip(item.copilot.trim(), MAX_COPILOT_PROMPT + 1) : null,
    recommended: item?.recommended === true,
    ...(item?.kind === 'external' ? { kind: 'external' } : {}),
    ...(typeof item?.target === 'string' ? { target: text(item.target, 40) } : {}),
    ...(typeof item?.basis === 'string' ? { basis: text(item.basis, 40) } : {}),
    ...(typeof item?.fixedBy === 'string' ? { fixedBy: text(item.fixedBy, 40) } : {}),
  };
}

// A command whose text after the verb is only a placeholder (`/speckit retry <guidance>`) is reduced to the bare
// command; a placeholder inside other text makes the command unusable (null), because a person could not post it as
// it is.
export function removePlaceholderArgument(command) {
  const parsed = parseNextCommand(command);
  if (!parsed || !parsed.argument) return String(command ?? '').trim();
  const rest = parsed.argument.replace(PLACEHOLDER_PATTERN, '').trim();
  if (rest === parsed.argument) return String(command).trim();
  return /^[\s.,;:-]*$/.test(rest) ? `/speckit ${parsed.verb}` : null;
}

function readAgentItems(env, type) {
  const file = env.GH_AW_AGENT_OUTPUT;
  if (!file || !existsSync(file)) return [];
  try {
    const output = JSON.parse(readFileSync(file, 'utf8'));
    return (output.items ?? []).filter((item) => item.type === type);
  } catch {
    return [];
  }
}

// Diagnose reports its results as `speckit_diagnosis`.
function readAgentReports(env) {
  return readAgentItems(env, 'speckit_diagnosis').map((item) => item.report ?? item);
}

// Whether the diagnose run tracked by a still-open diagnosis check run has ended without reporting (agent failure,
// cancellation, crash): a run for the twin started after the check run exists, and all such runs are completed.
export async function diagnosisRunEnded(client, twin, check) {
  const since = check?.started_at ?? check?.created_at;
  if (!since) return false;
  const runs = (await client.listWorkflowRuns(DIAGNOSE_WORKFLOW_FILE, since, 1)).filter((run) => isTwinRunOf(DIAGNOSE_WORKFLOW_FILE, run.display_title, twin));
  return runs.length > 0 && runs.every((run) => run.status === 'completed');
}

export async function closeUnfinishedDiagnosis(client, check) {
  await client.updateCheckRun(check.id, {
    status: 'completed',
    conclusion: 'failure',
    external_id: CHECK_DIAGNOSED,
    output: { title: 'Diagnosis did not finish', summary: 'The diagnose run ended without a report, or did not report within an hour.' },
  });
}

// The amendment pull request this diagnosis run created (SPECKIT_CREATED_PULL). One that targets another spec, which
// only a misled agent would create, is closed and its branch deleted.
async function createdAmendment(client, env, folder, reasons) {
  const number = Number(env.SPECKIT_CREATED_PULL);
  if (!Number.isInteger(number) || number <= 0) return null;
  const pull = await client.getPullRequest(number);
  if (pull.head?.ref === amendmentBranch(folder) && pull.base?.ref === implementationBranch(folder) && pull.state === 'open') return pull;
  await client.updatePullRequest(number, { state: 'closed' });
  if (String(pull.head?.ref ?? '').startsWith('speckit-amend/')) {
    try {
      await client.deleteBranch(pull.head.ref);
    } catch {
      // Already gone.
    }
  }
  reasons.push(`the created pull request #${number} (\`${pull.head?.ref}\` → \`${pull.base?.ref}\`) does not amend this spec and was closed`);
  return null;
}

const CATEGORY_TEXT = {
  artifacts: 'the spec artifacts need an amendment',
  retry: 'a retry is likely to succeed',
  outside: 'the cause is outside this spec',
  decision: 'a person has to decide how to continue',
  unknown: 'the cause is not clear yet',
};

// The commands that fit each category: a recommendation must use one of them, so the headline and the recommended
// command never disagree (defaultRecommendation chooses the fallback when none fits). Derived from PR_COMMANDS.
export function categoryCommands(commands = PR_COMMANDS) {
  return Object.freeze(Object.fromEntries(CATEGORIES.map((category) => [category, commandsForCategory(category, commands)])));
}
export const CATEGORY_COMMANDS = categoryCommands();

// One line of guidance for the agent of the next attempt, from the diagnosis' findings.
export function guidanceFrom(report, max = 500) {
  const text = [report.summary, report.cause].filter(Boolean).join(' ').replace(/\s+/g, ' ').trim();
  if (text.length <= max) return text;
  const cut = text.slice(0, max);
  const end = Math.max(cut.lastIndexOf('. '), cut.lastIndexOf('; '));
  return end > max / 2 ? cut.slice(0, end + 1) : `${cut.replace(/\s+\S*$/, '')}…`;
}

// The category's own recommendation, used when the report has none that fits.
function defaultRecommendation() {
  return {
    title: 'Review the recovery blocker and provide the missing evidence',
    why: 'No supported recovery was established. Use the reported cause and failed approaches to identify the external fix or missing information; repeating unchanged work is not a remedy.',
    kind: 'external',
  };
}

// The categories whose remedy can be a change a person (or Copilot's cloud agent for them) pushes to the implementation
// branch: a decision a person took, carried out as a commit.
export const COPILOT_CATEGORIES = Object.freeze(['decision']);

// The options of a diagnosis comment with exactly one recommendation that fits the category: the agent's recommended
// option when its command fits, else its first option that fits, else the category's default. An agent recommendation
// that does not fit stays as another option, with a note. With an amendment, the amendment is the recommendation.
export function recommendNextSteps(report, { amendment = null, repeated = null, failed = [], baseFixReady = false } = {}) {
  const notes = [];
  const adjusted = [];
  const options = report.options.map((option) => ({ ...option }));
  if (amendment) {
    for (const option of options) if (option.command) option.recommended = false;
    return { options, notes, adjusted };
  }
  const repeats = (option) => (Boolean(repeated) && !baseFixReady && ['retry', 'sync'].includes(parseNextCommand(option.command)?.verb) &&
      !(option.basis === 'corrective' && option.target === 'implementation' && report.evidence.length > 0 &&
        parseNextCommand(option.command)?.guidance && normalizeRecovery(parseNextCommand(option.command).guidance) !== normalizeRecovery(repeated.guidance)))
    || failed.some((item) => item.route === parseNextCommand(option.command)?.verb);
  const fits = (option) => (option.kind === 'external' && !option.command && !option.copilot && ['outside', 'decision', 'unknown'].includes(report.category))
    || (Boolean(option.command) && CATEGORY_COMMANDS[report.category].includes(parseNextCommand(option.command)?.verb) && !repeats(option) &&
      !(parseNextCommand(option.command)?.verb === 'author' && !['feature', 'environment'].includes(option.target)) &&
      !(parseNextCommand(option.command)?.verb === 'sync' && (option.target !== 'base' || !/^[0-9a-f]{40}$/.test(option.fixedBy ?? ''))) &&
      !(parseNextCommand(option.command)?.verb === 'retry' && report.evidence.length === 0))
    || (Boolean(option.copilot) && (COPILOT_CATEGORIES.includes(report.category) ||
      (report.category === 'outside' && option.kind === 'external' && option.target === 'tooling')));
  const agentPick = options.find((option) => option.recommended) ?? null;
  if (agentPick && fits(agentPick)) return { options, notes, adjusted };
  if (agentPick) {
    agentPick.recommended = false;
    if (repeats(agentPick)) {
      adjusted.push(`the recommended \`/speckit ${parseNextCommand(agentPick.command)?.verb}\` already stopped the same way or was refused on the unchanged branch ("${clip(repeated?.reason ?? failed.map((item) => item.reason).join('; '), 200)}"), so it is listed under the other options`);
    } else {
      notes.push(`the recommended option "${agentPick.title}" does not fit the category \`${report.category}\` or lacks supported target/readiness evidence, so it is listed under the other options`);
    }
  }
  const fitting = options.find(fits);
  if (fitting) {
    fitting.recommended = true;
    return { options: [fitting, ...options.filter((option) => option !== fitting)], notes, adjusted };
  }
  // A command that already failed the same way is replaced by a change only a push can make, when the report has one.
  const pushed = repeated ? options.find((option) => option.copilot) : null;
  if (pushed) {
    pushed.recommended = true;
    return { options: [pushed, ...options.filter((option) => option !== pushed)], notes, adjusted };
  }
  const fallback = defaultRecommendation();
  return { options: [{ ...fallback, recommended: true }, ...options], notes, adjusted };
}

export function renderDiagnosisComment({ report, reasons = [], round, runUrl, amendment = null, behindBase = null, baseBranch = null, defaultBranch = null, implementationPull = null, continuation = null, diagnosisUrl = null, repeated = null, failedSync = null, failed = [], exhausted = false, baseFixReady = false }) {
  const lines = [DIAGNOSIS_COMMENT_MARKER];
  const context = { behindBase, baseBranch, defaultBranch, implementationPull, continuation, diagnosed: true, diagnosisUrl };
  if (failedSync) lines.push('', `**Earlier \`/speckit sync\` failed**: the request ${failedSync.url ? `[at ${neutralizeMarkers(failedSync.at)}](${failedSync.url})` : `at ${neutralizeMarkers(failedSync.at)}`} stopped without moving the implementation branch: ${neutralizeMarkers(failedSync.reason)}`, '',
    baseFixReady ? 'A new relevant fix on the base branch has been verified since that failed synchronization.'
      : 'Another sync uses the same automation and credentials. Without a relevant fix or changed prerequisite, repeating it is not a remedy.', '');
  if (!report) {
    lines.push(
      `**Diagnosis${round > 1 ? ` (round ${round})` : ''} did not produce a usable report**: ${reasons.map(neutralizeMarkers).join('; ') || 'no report'}.`,
      '',
      ...(runUrl ? [`See the [diagnosis run](${runUrl}).`, ''] : []),
      ...renderNextSteps({ ...context, diagnosed: exhausted }),
    );
    return lines.join('\n');
  }
  const safe = (value) => neutralizeMarkers(value);
  const { options, notes, adjusted: repeatNotes } = recommendNextSteps(report, { amendment, repeated, failed, baseFixReady });
  lines.push(
    `**Diagnosis${round > 1 ? ` (round ${round})` : ''}**: ${CATEGORY_TEXT[report.category]} (\`${report.category}\`, confidence ${report.confidence}).`,
    '',
    `_\`${report.category}\` means:_ ${CATEGORY_MEANING[report.category]}`,
    '',
    safe(report.summary),
  );
  if (report.cause) lines.push('', '**Cause**', '', safe(report.cause));
  if (report.evidence.length > 0) {
    lines.push('', '<details><summary>Evidence</summary>', '', ...report.evidence.map((item) => `- ${safe(item)}`), '', '</details>');
  }
  if (amendment) {
    const title = `${safe(amendment.title ?? 'Amendment')} (#${amendment.number})`;
    lines.push(
      '',
      `**Proposed amendment**: ${amendment.html_url ? `[${title}](${amendment.html_url})` : title} (\`${amendment.head.ref}\` → \`${amendment.base.ref}\`). An independent \`/speckit-analyze\` checks it for consistency first, and correction rounds fix what it finds while they make progress; you get a review request there when it is ready.`,
      '',
    );
    for (const item of report.artifacts) lines.push(`- \`${safe(item.file)}\`${item.reason ? `: ${safe(item.reason)}` : ''}`);
  } else if (report.category === 'artifacts') {
    lines.push('', '_No amendment pull request was created._');
  }
  if (report.questions.length > 0) {
    const prefilled = !amendment && report.category === 'decision' && parseNextCommand(options.find((option) => option.recommended)?.command)?.argument;
    lines.push('', prefilled
      ? '**Questions** (the recommended next step answers them with the first, recommended choice of each; change the answers before posting if you decide otherwise)'
      : '**Questions** (the recommended choice is listed first)', '');
    report.questions.forEach((item, index) => {
      lines.push(`${index + 1}. ${safe(item.question)}`);
      for (const choice of item.choices) lines.push(`   - ${safe(choice)}`);
    });
  }
  const pending = amendment ? { number: amendment.number, title: amendment.title ?? null, html_url: amendment.html_url ?? null, status: 'being checked for consistency first' } : null;
  const shown = options.map((option) => ({ ...option, title: safe(option.title), why: safe(option.why ?? ''), command: option.command ? safe(option.command) : null, copilot: option.copilot ? safe(option.copilot) : null }));
  lines.push('', ...renderNextSteps({ ...context, options: shown, amendment: pending }));
  const adjusted = [...reasons, ...notes, ...repeatNotes];
  if (adjusted.length > 0) lines.push('', `_Parts of the report were adjusted: ${adjusted.map(safe).join('; ')}._`);
  if (runUrl) lines.push('', `<sub>[Diagnosis run](${runUrl})</sub>`);
  return lines.join('\n');
}

// The outcome of a rework by the diagnose agent, from its agent output and the safe-output push job (recordRework).
function reworkOutcome(env) {
  const detection = String(env.SPECKIT_DETECTION ?? '').trim();
  const withheld = detection && detection !== 'success';
  const reports = withheld ? [] : readAgentReports(env);
  const { reasons, report } = withheld
    ? { reasons: [`it was withheld because threat detection did not pass (result: ${detection})`], report: null }
    : reports.length > 0 ? validateReport(reports.at(-1), { requireCategory: false }) : { reasons: ['the agent did not report'], report: null };
  return {
    reasons,
    report,
    pushed: String(env.SPECKIT_PUSH_SHA ?? '').trim(),
    pushFailures: Number(env.SPECKIT_PUSH_FAILURES || 0),
    incomplete: readAgentItems(env, 'report_incomplete').map((item) => String(item.reason ?? '').trim()).filter(Boolean),
  };
}

// Custom safe-output job, after the agent and the built-in safe outputs. A diagnosis posts its findings on the
// implementation pull request and marks its check run as waiting for a decision; a created amendment then goes into
// the consistency check. A rework (`fix`, `revise`) reports on the amendment pull request and checks it again.
export async function runRecord({ client, env, log }) {
  const inputs = inputsFrom(env);
  if (inputs.mode === 'amend') return recordAuthorizedAmendment({ client, env, log, inputs });
  if (inputs.mode !== 'diagnose') return recordRework({ client, env, log, inputs });
  const { pull: pullNumber, folder, twin } = inputs;
  const detection = String(env.SPECKIT_DETECTION ?? '').trim();
  const withheld = detection && detection !== 'success';
  const reports = withheld ? [] : readAgentReports(env);
  const { reasons, report } = withheld
    ? { reasons: [`it was withheld because threat detection did not pass (result: ${detection})`], report: null }
    : reports.length > 0 ? validateReport(reports.at(-1)) : { reasons: ['the agent did not report'], report: null };
  const amendment = report?.category && reasons.length === 0 ? await createdAmendment(client, env, folder, reasons) : null;
  const comments = await client.listIssueComments(pullNumber);
  const implementation = await client.getPullRequest(pullNumber);
  const failedRecoveries = failedCopilotRecoveries(comments, implementation.head.sha);
  for (const option of report?.options ?? []) {
    if (option.copilot && failedRecoveries.some((item) => normalizeRecovery(item.request) === normalizeRecovery(`@copilot ${option.copilot}`) ||
      normalizeRecovery(item.request).startsWith(normalizeRecovery(`@copilot ${option.copilot}`)))) {
      reasons.push('The proposed Copilot recovery already failed without a push on this revision; diagnose the refusal and recommend an executable action instead.');
    }
  }
  const repeated = repeatedFailure(comments, implementation.head?.sha, { includeState: true });
  const failed = failedRecoveryRoutes(comments, implementation.head.sha);
  if (report?.category && !amendment && !report.options.some((option) => option.recommended && (option.command || option.copilot || option.kind === 'external'))) {
    reasons.push('the report must recommend one actionable command, a supported Copilot prompt, or a precise external action that fits its category');
  }
  const sync = report?.options.find((option) => option.recommended && parseNextCommand(option.command)?.verb === 'sync');
  let baseFixReady = false;
  if (sync?.fixedBy) {
    if (!/^[0-9a-f]{40}$/.test(sync.fixedBy)) reasons.push('the base fix must name a full commit SHA');
    else {
      try {
        const ahead = await client.aheadBy(baseBranchOf(implementation), sync.fixedBy);
        if (!Number.isInteger(ahead) || ahead < 0) reasons.push('the base fix comparison did not provide valid ancestry evidence');
        else if (ahead > 0) reasons.push('the claimed fix is not on the implementation base branch');
        else if (!repeated) baseFixReady = true;
        else if (repeated.base) {
          const previous = await client.aheadBy(repeated.base, sync.fixedBy);
          if (!Number.isInteger(previous) || previous < 0) reasons.push('the failed synchronization base comparison did not provide valid ancestry evidence');
          else baseFixReady = previous > 0;
        }
      } catch (error) {
        reasons.push(`the base fix could not be verified: ${error.message}`);
      }
    }
  }
  const recommendationProblems = report?.category ? recommendNextSteps(report, { amendment, repeated, failed, baseFixReady }).notes : [];
  reasons.push(...recommendationProblems);
  const usable = report && report.category && reasons.length === 0 ? report : null;
  const round = comments.filter((comment) => comment.user?.login === BOT_LOGIN && String(comment.body ?? '').startsWith(DIAGNOSIS_COMMENT_MARKER)).length + 1;
  const context = await nextStepContext(client, implementation, folder);
  const runUrl = env.GITHUB_RUN_ID ? `${(env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '')}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : null;
  const repeat = !usable || (usable.category === 'unknown' && !usable.options.some((option) => option.kind === 'external' && option.recommended)) || (usable.category === 'artifacts' && !amendment);
  const exhausted = Boolean(recoveryReassessment(comments, implementation.head.sha));
  const comment = await client.createComment(pullNumber, renderDiagnosisComment({
    report: usable,
    reasons,
    round,
    runUrl,
    amendment,
    repeated,
    failedSync: failedSynchronization(comments, implementation.head.sha),
    defaultBranch: defaultBranchOf(env),
    implementationPull: pullNumber,
    failed,
    exhausted,
    baseFixReady,
    diagnosisUrl: diagnosisRunsUrl(env),
    ...context,
  }));
  const checkRun = Number(env.SPECKIT_CHECK_RUN);
  const next = !repeat && !amendment && autoContinueEnabled(env) ? autoContinueCommand(usable, { amendment, repeated, failed, baseFixReady }) : null;
  const continues = next !== null && !(await continuedSincePerson(client, pullNumber, comments));
  if (Number.isInteger(checkRun) && checkRun > 0) {
    await client.updateCheckRun(checkRun, {
      status: 'completed',
      conclusion: usable ? 'neutral' : 'failure',
      external_id: CHECK_DIAGNOSED,
      output: usable
        ? { title: repeat && !exhausted ? `Diagnosis continues: ${usable.category}` : continues ? `Continued automatically: ${usable.category}` : `Waiting for a decision: ${usable.category}`, summary: clip(usable.summary, 2000) }
        : { title: 'Diagnosis without a usable report', summary: clip(reasons.join('; '), 2000) },
    });
  }
  if (repeat) {
    const restarted = await reassessRecovery(client, { pull: implementation, comments,
      guidance: reasons.join('; ') || usable?.cause || 'The report did not establish a supported remedy.',
      rediagnose: ({ guidance, previous }) => startDiagnosis(client, env, {
        twin, pull: pullNumber, folder, head: implementation.head.sha, previous: String(previous || comment?.id || ''),
        guidance, auto: true, preserveAmendment: true,
      }) });
    if (!restarted.started) log(`Warning: could not continue the diagnosis: ${restarted.error}`);
    if (!restarted.started) await setAttention(client, pullNumber, true);
  } else if (amendment) {
    const implementation = await client.getPullRequest(pullNumber);
    await beginAmendment(client, env, { amendment, folder, implementation, twin });
  } else if (continues) {
    await autoContinue(client, env, { twin, pull: pullNumber, folder, command: next });
    log(`Continued #${pullNumber} automatically with /speckit ${next.verb}.`);
  } else {
    await setAttention(client, pullNumber, true);
  }
  log(usable ? `Posted the diagnosis (${usable.category}) on #${pullNumber}.` : `Posted that the diagnosis had no usable report on #${pullNumber}.`);
  return { exitCode: 0, report: usable, reasons, continued: continues ? next.verb : null };
}

// A diagnosis continues by itself (SPECKIT_AUTO_CONTINUE, on unless `false`) when its recommendation is a transient
// one: `/speckit retry` for a `retry` diagnosis of high or medium confidence, or `/speckit sync` for an `outside` cause
// already fixed on the base branch. A repeated failure requires a changed corrective approach or a newly verified base fix.
export function autoContinueEnabled(env) {
  return String(env.SPECKIT_AUTO_CONTINUE ?? '').trim().toLowerCase() !== 'false';
}

// The recommended command a diagnosis continues with by itself ({ verb, guidance, … } of parseNextCommand), or null.
export function autoContinueCommand(report, { amendment = null, repeated = null, failed = [], baseFixReady = false } = {}) {
  if (!report || amendment) return null;
  const option = recommendNextSteps(report, { amendment, repeated, failed, baseFixReady }).options.find((item) => item.recommended && item.command);
  const command = parseNextCommand(option?.command);
  if (!command) return null;
  if (command.verb === 'retry' && report.category === 'retry' && report.evidence.length > 0 && ['transient', 'corrective'].includes(option.basis) && ['high', 'medium'].includes(report.confidence)) return command;
  if (command.verb === 'sync' && report.category === 'outside' && baseFixReady && option.target === 'base' && option.fixedBy) return command;
  return null;
}

// Whether a diagnosis already continued automatically since the last action of a person on the implementation pull
// request (a `/speckit` comment by a person, or a push by a person): it continues once per person's action, so a
// failure that keeps coming back stops for a person.
export async function continuedSincePerson(client, pull, comments) {
  const person = (comment) => comment.user?.login && comment.user.login !== BOT_LOGIN && !String(comment.user.login).endsWith('[bot]') && comment.user.type !== 'Bot';
  const commands = comments.filter((comment) => person(comment) && /^\s*\/speckit\b/.test(String(comment.body ?? ''))).map((comment) => Date.parse(comment.created_at));
  const pushed = Date.parse(latestHumanCommit(await client.listPullRequestCommits(pull)) ?? '') || 0;
  const since = Math.max(0, pushed, ...commands);
  return comments.some((comment) => comment.user?.login === BOT_LOGIN && String(comment.body ?? '').startsWith(AUTO_CONTINUE_MARKER) && Date.parse(comment.created_at) > since);
}

// Continues the implementation like the person's command would (speckit-commands.mjs), with a comment that starts with
// AUTO_CONTINUE_MARKER.
async function autoContinue(client, env, { twin, pull, folder, command }) {
  const implementation = await client.getPullRequest(pull);
  const guidance = neutralizeMarkers(command.guidance).slice(0, MAX_WRITER_GUIDANCE);
  const options = {
    lead: `Continued automatically with \`/speckit ${command.verb}\` as the diagnosis recommends`,
    guidance,
    prefix: `${AUTO_CONTINUE_MARKER}\n`,
    suffix: ' Post another command or push to steer it.',
  };
  if (command.verb === 'sync') {
    await requestSync(client, env, { implementation, twin }, options);
  } else {
    await retryImplementation(client, env, { implementation, twin, folder }, { ...options, effect: RETRY_EFFECT, state: await restartState(client, implementation) });
  }
}

export async function recordAuthorizedAmendment({ client, env, log, inputs }) {
  const { pull: number, folder, twin, authorization } = inputs;
  const implementation = await client.getPullRequest(number);
  const completed = trustedMarker(await client.listIssueComments(number), AMEND_REQUEST_STATE_MARKER)
    .find((item) => item.request === authorization && item.state === 'created');
  if (completed) return { exitCode: 0, outcome: 'already recorded', amendment: completed.amendment };
  const reasons = [];
  if (implementation.head.ref !== implementationBranch(folder)) reasons.push('The authorized amendment folder does not match the implementation branch.');
  let request = null;
  try {
    request = await readAmendmentAuthorization(client, implementation, authorization);
  } catch (error) {
    if (!(error instanceof AmendUsageError)) throw error;
    reasons.push(error.message);
  }
  const detection = String(env.SPECKIT_DETECTION ?? '').trim();
  if (detection && detection !== 'success') reasons.push('The amendment report was withheld by threat detection.');
  const reports = reasons.length ? [] : readAgentReports(env);
  const checked = reports.length ? validateReport(reports.at(-1), { requireCategory: false }) : { reasons: ['No usable amendment report was produced.'], report: null };
  reasons.push(...checked.reasons);
  const amendment = await createdAmendment(client, env, folder, reasons);
  if (!amendment) reasons.push('No amendment pull request was created.');
  if (reasons.length) {
    if (amendment) await client.updatePullRequest(amendment.number, { state: 'closed' });
    await finishAmendmentRequest(client, number, authorization, 'failed',
      `**Amendment preparation failed** for #${number}: ${neutralizeMarkers(reasons.join('; '))}\n\n**Recommended next step**: Address the reported failure and post a new \`/speckit amend\` command with the intended change. Implementation remains stopped.`);
    await setAttention(client, number, true);
  } else {
    await client.createComment(amendment.number, `<!-- ${AMEND_AUTHORIZATION_MARKER} ${JSON.stringify({ request: authorization, implementation: number, head: request.head })} -->\nWriter @${request.actor} authorized this artifact change on #${number}. Completed tasks must remain unchanged in every correction and revision.`);
    await finishAmendmentRequest(client, number, authorization, 'created',
      `**Amendment #${amendment.number} prepared for #${number}**: ${neutralizeMarkers(checked.report.summary)}\n\n**Recommended next step**: Wait for independent analysis of amendment #${amendment.number}; no action is needed while it is checked. Once consistent, review and merge it to resume #${number}. No implementation task has been ticked.`,
      { amendment: amendment.number });
    await beginAmendment(client, env, { amendment, folder, implementation, twin });
  }
  if (Number(env.SPECKIT_CHECK_RUN) > 0) await client.updateCheckRun(Number(env.SPECKIT_CHECK_RUN), {
    status: 'completed', conclusion: reasons.length ? 'failure' : 'neutral', external_id: CHECK_DIAGNOSED,
    output: { title: reasons.length ? 'Amendment preparation failed' : `Amendment #${amendment.number} awaits analysis and review`, summary: reasons.join('; ') || checked.report.summary },
  });
  log(reasons.length ? reasons.join('; ') : `Prepared amendment #${amendment.number}.`);
  return { exitCode: 0, outcome: reasons.length ? 'failed' : 'amendment', amendment: amendment?.number ?? null };
}

// Records a rework of an amendment (`fix` after the consistency check, `revise` after a person's feedback) and starts
// the consistency check of the result. `outcome` is what the rework produced: its validated report (or the reasons
// why there is none), the pushed commit, failed pushes, and why a push was refused. Diagnose reads it from its agent
// output (reworkOutcome); Spec Kit author passes the result of its land job.
export async function recordRework({ client, env, log, inputs, outcome }) {
  const { mode, amendment: number, round, stalls, findings } = inputs;
  const checkRun = Number(env.SPECKIT_CHECK_RUN);
  const complete = async (conclusion, title, summary) => {
    if (Number.isInteger(checkRun) && checkRun > 0) {
      await client.updateCheckRun(checkRun, {
        status: 'completed',
        conclusion,
        external_id: conclusion === 'failure' ? CHECK_AMEND_INCONSISTENT : CHECK_AMEND_CHECKING,
        output: { title, summary: clip(summary || title, 2000) },
      });
    }
  };
  const context = await resolveAmendment(client, env, number);
  if (context.reason || context.amendment.state !== 'open') {
    await complete('neutral', 'The amendment is no longer open', context.reason ?? '');
    return { exitCode: 0, outcome: 'closed' };
  }
  // A failed correction round ends the loop: the requester gets the amendment for review, marked not consistent.
  // (A failed rework answers a person who is already in the conversation.)
  const handBack = async (problem) => {
    if (mode !== 'fix') return;
    const pending = await pendingFeedback(client, context.amendment);
    await presentAmendment(client, env, context, { consistent: false, problem, since: pending.since });
  };
  const { reasons, report, pushed, pushFailures, incomplete } = outcome ?? reworkOutcome(env);
  if (!report || !report.summary) {
    await complete('failure', mode === 'fix' ? `Correction round ${round} did not finish` : 'The rework did not finish', reasons.join('; '));
    const problem = `The ${mode === 'fix' ? `correction round ${round}` : 'rework'} did not produce a result: ${neutralizeMarkers(reasons.join('; ') || 'no report')}.`;
    if (mode === 'fix') await client.createComment(number, problem);
    else await reportSpecPullFailure(client, context, problem);
    await handBack(`correction round ${round} did not finish`);
    await setAttention(client, number, true);
    return { exitCode: 0, outcome: 'failed' };
  }
  // A push the safe-output tool refused never reaches the push job; the agent reports it as incomplete instead.
  if (pushFailures > 0 || (!pushed && incomplete.length > 0)) {
    const what = mode === 'fix' ? `Correction round ${round}` : 'The rework';
    const why = pushFailures > 0 ? `${pushFailures} push(es) to \`${context.amendment.head.ref}\` failed.` : `The push was refused: ${incomplete.join('; ')}`;
    await complete('failure', `${what} could not push its changes`, why);
    const problem = [
      `${what} could not push its changes to this pull request, so nothing changed.`,
      '',
      `> ${neutralizeMarkers(clip(why, 1500)).replace(/\n/g, '\n> ')}`,
    ].join('\n');
    if (mode === 'fix') await client.createComment(number, problem);
    else await reportSpecPullFailure(client, context, problem);
    // A refused push is a correction round without progress; the next round retries the same findings.
    if (mode === 'fix' && stalls + 1 < MAX_STALLED_ROUNDS && round < MAX_CORRECTION_ROUNDS) {
      await startRework(client, env, context, { mode: 'fix', round: round + 1, guidance: env.SPECKIT_GUIDANCE ?? '', stalls: stalls + 1, findings, progress: `Round ${round} could not push its changes.` });
      return { exitCode: 0, outcome: 'push failed' };
    }
    await handBack(`correction round ${round} could not push its changes`);
    await setAttention(client, number, true);
    return { exitCode: 0, outcome: 'push failed' };
  }
  // A correction round may leave findings it judges wrong; then another analysis would only repeat them.
  if (!pushed && mode === 'fix') {
    await complete('neutral', `Correction round ${round} changed nothing`, report.summary);
    await client.createComment(number, `**Correction round ${round} changed nothing**: ${neutralizeMarkers(report.summary)}`);
    await handBack(`correction round ${round} changed nothing`);
    return { exitCode: 0, outcome: 'unchanged' };
  }
  if (pushed && context.amendment.head.sha !== pushed) log(`Amendment #${number}: the head ${context.amendment.head.sha} is not the pushed ${pushed}; a person pushed as well.`);
  const changed = pushed ? 'pushed' : 'finished without changes';
  await complete('neutral', mode === 'fix' ? `Correction round ${round} ${changed}` : `Rework ${changed}`, report.summary);
  if (mode === 'revise') {
    await client.createComment(number, [
      `**${pushed ? 'Reworked' : 'Answered without changes'}**: ${neutralizeMarkers(report.summary)}`,
      ...(report.responses.length > 0 ? ['', ...report.responses.map((item) => `- ${item.feedback ? `_${neutralizeMarkers(item.feedback)}_: ` : ''}${neutralizeMarkers(item.response)}`)] : []),
      '',
      'The consistency check runs again; you get a review request when it is done.',
    ].join('\n'));
  }
  // A rework after a person's feedback starts a fresh correction budget.
  await startAnalysis(client, env, context, mode === 'fix' ? { round, mode: 'loop', stalls, previous: findings } : { round: 0, mode: 'loop' });
  log(`Recorded the ${mode} of amendment #${number}; the consistency check runs.`);
  return { exitCode: 0, outcome: 'checking' };
}

export async function main(argv, { env = process.env, log = console.log, client } = {}) {
  const [command, file] = argv;
  if (command === 'check-report') return checkReportFile(file, log);
  if (command === 'commands') {
    log(renderCommandReference());
    return 0;
  }
  const run = { client: client ?? createGitHubClient(env), env, log };
  if (command === 'evidence') return (await runEvidence(run)).exitCode;
  if (command === 'record') return (await runRecord(run)).exitCode;
  throw new DiagnoseUsageError('Usage: speckit-diagnose.mjs <evidence | record | check-report <report.json> | commands>');
}

// The agent's self-check before it reports: prints what validation changes and the comment a person would see. Exits
// with 1 while anything had to be adjusted, so the agent can fix its report first.
export function checkReportFile(file, log = console.log) {
  if (!file || !existsSync(file)) throw new DiagnoseUsageError('Usage: speckit-diagnose.mjs check-report <report.json>');
  const { reasons, report } = validateReport(readFileSync(file, 'utf8'));
  const notes = report?.category ? recommendNextSteps(report).notes : [];
  if (report?.category && report.category !== 'artifacts' &&
      !report.options.some((option) => option.recommended && (option.command || option.copilot || option.kind === 'external'))) {
    notes.push('the report must recommend one actionable command, a supported Copilot prompt, or a precise external action that fits its category');
  }
  const problems = [...reasons, ...notes];
  log(problems.length > 0 ? `Fix these problems before you report:\n${problems.map((item) => `- ${item}`).join('\n')}` : 'The report is valid.');
  if (report?.category) log(`\nThe comment would read:\n\n${renderDiagnosisComment({ report, reasons, round: 1 })}`);
  return problems.length > 0 || !report?.category ? 1 : 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof DiagnoseUsageError ? 2 : 1;
    },
  );
}
