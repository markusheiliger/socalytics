import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { GitHubClient } from './speckit-prepare-github.mjs';
import { beginAmendment, checkAmendment, decodeTracked, findAmendment, pendingFeedback, presentAmendment, resolveAmendment, startAnalysis, startRework } from './speckit-amend.mjs';
import {
  BOT_LOGIN,
  CHECK_AMEND_CHECKING,
  CHECK_AMEND_INCONSISTENT,
  CHECK_DIAGNOSED,
  CHECK_DIAGNOSING,
  CHECK_RUN_NAME,
  DIAGNOSE_WORKFLOW_FILE,
  DIAGNOSIS_COMMENT_MARKER,
  MAX_CORRECTION_ROUNDS,
  IMPLEMENT_WORKFLOW_FILE,
  MAX_STALLED_ROUNDS,
  amendmentBranch,
  implementationBranch,
  isImplementRunOf,
  neutralizeMarkers,
  nextTaskGroup,
  renderNextSteps,
} from './speckit-implement-core.mjs';

export { checkAmendment, findAmendment };

export const CATEGORIES = ['artifacts', 'retry', 'outside', 'decision', 'unknown'];
export const CONFIDENCE = ['high', 'medium', 'low'];
export const MODES = ['diagnose', 'fix', 'revise'];
const COMMAND_PATTERN = /^\/speckit (diagnose|resume|sync)( [^\n]*)?$/;
const MAX_EVIDENCE_BYTES = 200_000;
const LOG_TAIL_LINES = 150;

const clip = (text, max) => {
  const value = String(text ?? '');
  return value.length > max ? `${value.slice(0, max)}…` : value;
};

export class DiagnoseUsageError extends Error {}

// Starts a diagnosis of the implementation pull request `pull`: a check run on the head marks the implementation as
// waiting for the diagnosis, and the agentic diagnose workflow is dispatched. A dispatch failure leaves the check run
// as a reported diagnosis, so the implementation waits for a person instead of for a diagnosis that never runs.
export async function startDiagnosis(client, env, { twin, pull, folder, head, notes = '', previous = '', actor = null, auto = false }) {
  // A new diagnosis supersedes an earlier amendment; its branch must not exist when the new run proposes one.
  const superseded = await findAmendment(client, folder);
  if (superseded) {
    await client.updatePullRequest(superseded.number, { state: 'closed' });
    await client.createComment(superseded.number, 'Superseded by a new diagnosis; a new amendment, if any, follows in its own pull request.');
  }
  try {
    await client.deleteBranch(amendmentBranch(folder));
  } catch {
    // The branch usually does not exist.
  }
  const summary = [
    auto ? 'Started automatically because the implementation stopped.' : `Started by @${actor}.`,
    ...(notes ? ['', `Notes: ${clip(notes, 2000)}`] : []),
  ].join('\n');
  const check = await client.createCheckRun({
    name: CHECK_RUN_NAME,
    head_sha: head,
    status: 'in_progress',
    external_id: CHECK_DIAGNOSING,
    output: { title: 'Diagnosis in progress', summary },
  });
  try {
    await client.dispatchWorkflow(DIAGNOSE_WORKFLOW_FILE, env.SPECKIT_BRANCH || 'main', {
      twin: String(twin),
      pull: String(pull),
      folder,
      notes: clip(notes, 4000),
      previous: String(previous ?? ''),
      check_run: String(check.id),
      mode: 'diagnose',
      amendment: '',
      round: '0',
    });
    return { started: true, check };
  } catch (error) {
    await client.updateCheckRun(check.id, {
      status: 'completed',
      conclusion: 'failure',
      external_id: CHECK_DIAGNOSED,
      output: { title: 'Diagnosis could not start', summary: clip(error.message, 2000) },
    });
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
  if (mode !== 'diagnose' && (!Number.isInteger(amendment) || amendment <= 0)) throw new DiagnoseUsageError(`Mode ${mode} needs SPECKIT_AMENDMENT.`);
  return {
    twin,
    pull,
    folder,
    mode,
    amendment: mode === 'diagnose' ? null : amendment,
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
  const { twin, pull: pullNumber, folder, mode, amendment: amendmentNumber, round, stalls } = inputsFrom(env);
  const outDir = path.resolve(env.SPECKIT_EVIDENCE_DIR || 'evidence');
  mkdirSync(outDir, { recursive: true });
  const defaultBranch = env.SPECKIT_BRANCH || 'main';
  const branch = implementationBranch(folder);
  const pull = await client.getPullRequest(pullNumber);
  const tasksMarkdown = await client.getFileContent(`specs/${folder}/tasks.md`, pull.head.sha);
  const next = nextTaskGroup(tasksMarkdown, 1)[0] ?? null;
  const checks = [...await client.listCheckRuns(pull.head.sha, CHECK_RUN_NAME)].sort((a, b) => a.id - b.id);
  const comments = await client.listIssueComments(pullNumber);
  const previousId = Number(env.SPECKIT_PREVIOUS);
  const previous = Number.isInteger(previousId) && previousId > 0 ? comments.find((comment) => comment.id === previousId) : null;
  let behind = null;
  let ahead = null;
  try {
    behind = await client.aheadBy(branch, defaultBranch);
    ahead = await client.aheadBy(defaultBranch, branch);
  } catch {
    // The comparison is only context.
  }

  const sections = [
    `# Evidence for the diagnosis of #${twin} (pull request #${pullNumber})`,
    '',
    `- Mode: \`${mode}\`${mode === 'fix' ? ` (correction round ${round}; rounds without progress so far: ${stalls} of ${MAX_STALLED_ROUNDS})` : ''}${amendmentNumber ? `; amendment pull request #${amendmentNumber} on \`${amendmentBranch(folder)}\`` : ''}`,
    `- Spec folder: \`specs/${folder}\``,
    `- Implementation branch: \`${branch}\` at ${pull.head.sha}`,
    `- Default branch \`${defaultBranch}\`: ${behind ?? '?'} commit(s) the implementation branch lacks; the branch is ${ahead ?? '?'} commit(s) ahead`,
    `- Next unchecked task: ${next ? `${next.id} ${next.text}` : 'none (all tasks are checked)'}`,
    '',
  ];
  if (mode === 'fix') {
    sections.push('## Findings of the consistency check to fix', '', env.SPECKIT_NOTES?.trim() || '_None._', '');
  } else if (mode === 'revise') {
    // The feedback was collected when the rework started (the notes); its marker already moved the feedback window.
    sections.push('## Feedback on the amendment to address', '', env.SPECKIT_NOTES?.trim() || '_None._', '');
    const comments = await client.listIssueComments(amendmentNumber);
    sections.push('## Earlier comments on the amendment (oldest first)', '');
    for (const comment of comments.slice(-10)) sections.push(`### ${comment.user?.login ?? 'unknown'} at ${comment.created_at}`, '', clip(comment.body, 4000), '');
  } else {
    sections.push('## Notes from the person who started this diagnosis', '', env.SPECKIT_NOTES?.trim() ? env.SPECKIT_NOTES.trim() : '_None._', '');
  }
  sections.push(
    '## Check runs on the head (oldest first)',
    '',
    ...checks.map((check) => `- ${check.external_id ?? '-'} ${check.status}/${check.conclusion ?? '-'}: ${check.output?.title ?? ''}${check.output?.summary ? ` — ${clip(check.output.summary, 600).replace(/\s+/g, ' ')}` : ''}`),
    '',
  );
  if (previous) sections.push('## Previous diagnosis', '', clip(previous.body, 20_000), '');
  sections.push('## Recent pull request comments (oldest first)', '');
  for (const comment of comments.slice(-15)) {
    sections.push(`### ${comment.user?.login ?? 'unknown'} at ${comment.created_at}`, '', clip(comment.body, 8000), '');
  }
  // A task that fails its verification is reported in the pull request comments above; failed jobs are crashes,
  // timeouts, and infrastructure problems.
  sections.push('## Failed jobs of recent implementation runs', '');
  const runs = (await client.listWorkflowRuns(IMPLEMENT_WORKFLOW_FILE, pull.created_at, 2))
    .filter((run) => isImplementRunOf(run.display_title, twin) && run.status === 'completed')
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
    defaultBranch,
    head: pull.head.sha,
    nextTask: next?.id ?? null,
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
      .map((item) => ({ title: text(item?.title, 400), command: typeof item?.command === 'string' ? item.command.trim() : null, recommended: item?.recommended === true }))
      .filter((item) => item.title)
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
    if (option.command && (!COMMAND_PATTERN.test(option.command) || option.command.length > 600)) {
      reasons.push(`option "${option.title}" has an invalid command`);
      option.command = null;
    }
  }
  normalized.options.sort((a, b) => Number(b.recommended) - Number(a.recommended));
  return { reasons, report: normalized };
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

function readAgentReports(env) {
  return readAgentItems(env, 'speckit_diagnosis').map((item) => item.report ?? item);
}

// Whether the diagnose run tracked by a still-open diagnosis check run has ended without reporting (agent failure,
// cancellation, crash): a run for the twin started after the check run exists, and all such runs are completed.
export async function diagnosisRunEnded(client, twin, check) {
  const since = check?.started_at ?? check?.created_at;
  if (!since) return false;
  const runs = (await client.listWorkflowRuns(DIAGNOSE_WORKFLOW_FILE, since, 1)).filter((run) => run.display_title === `Spec Kit diagnose #${twin}`);
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

export function renderDiagnosisComment({ report, reasons = [], round, runUrl, amendment = null, behindMain = false }) {
  const lines = [DIAGNOSIS_COMMENT_MARKER];
  if (!report) {
    lines.push(
      `**Diagnosis${round > 1 ? ` (round ${round})` : ''} did not produce a usable report**: ${reasons.map(neutralizeMarkers).join('; ') || 'no report'}.`,
      '',
      ...(runUrl ? [`See the [diagnosis run](${runUrl}).`, ''] : []),
      ...renderNextSteps({ behindMain, diagnosed: true }),
    );
    return lines.join('\n');
  }
  const safe = (value) => neutralizeMarkers(value);
  lines.push(
    `**Diagnosis${round > 1 ? ` (round ${round})` : ''}**: ${CATEGORY_TEXT[report.category]} (\`${report.category}\`, confidence ${report.confidence}).`,
    '',
    safe(report.summary),
  );
  if (report.cause) lines.push('', '**Cause**', '', safe(report.cause));
  if (report.evidence.length > 0) {
    lines.push('', '<details><summary>Evidence</summary>', '', ...report.evidence.map((item) => `- ${safe(item)}`), '', '</details>');
  }
  if (amendment) {
    lines.push(
      '',
      `**Proposed amendment**: #${amendment.number} (\`${amendment.head.ref}\` → \`${amendment.base.ref}\`). An independent \`/speckit-analyze\` checks it for consistency first, and correction rounds fix what it finds while they make progress; you get a review request there when it is ready.`,
      '',
    );
    for (const item of report.artifacts) lines.push(`- \`${safe(item.file)}\`${item.reason ? `: ${safe(item.reason)}` : ''}`);
  } else if (report.category === 'artifacts') {
    lines.push('', '_No amendment pull request was created._');
  }
  if (report.questions.length > 0) {
    lines.push('', '**Questions** (answer with `/speckit diagnose <answers>`)', '');
    report.questions.forEach((item, index) => {
      lines.push(`${index + 1}. ${safe(item.question)}`);
      for (const choice of item.choices) lines.push(`   - ${safe(choice)}`);
    });
  }
  const manual = report.options.filter((option) => !option.command);
  if (manual.length > 0) {
    lines.push('', '**Actions outside these commands**', '', ...manual.map((option) => `- ${option.recommended ? '**Recommended:** ' : ''}${safe(option.title)}`));
  }
  const commandOptions = report.options
    .filter((option) => option.command)
    .map((option) => ({ title: `${option.recommended ? '**Recommended:** ' : ''}${safe(option.title)}`, command: option.command }));
  lines.push('', ...renderNextSteps({ options: commandOptions, amendmentPull: amendment?.number ?? null, behindMain, diagnosed: true }));
  if (reasons.length > 0) lines.push('', `_Parts of the report were dropped: ${reasons.map(safe).join('; ')}._`);
  if (runUrl) lines.push('', `<sub>[Diagnosis run](${runUrl})</sub>`);
  return lines.join('\n');
}

// Custom safe-output job, after the agent and the built-in safe outputs. A diagnosis posts its findings on the
// implementation pull request and marks its check run as waiting for a decision; a created amendment then goes into
// the consistency check. A rework (`fix`, `revise`) reports on the amendment pull request and checks it again.
export async function runRecord({ client, env, log }) {
  const inputs = inputsFrom(env);
  if (inputs.mode !== 'diagnose') return recordRework({ client, env, log, inputs });
  const { pull: pullNumber, folder, twin } = inputs;
  const detection = String(env.SPECKIT_DETECTION ?? '').trim();
  const withheld = detection && detection !== 'success';
  const reports = withheld ? [] : readAgentReports(env);
  const { reasons, report } = withheld
    ? { reasons: [`it was withheld because threat detection did not pass (result: ${detection})`], report: null }
    : reports.length > 0 ? validateReport(reports.at(-1)) : { reasons: ['the agent did not report'], report: null };
  const usable = report && report.category ? report : null;
  const comments = await client.listIssueComments(pullNumber);
  const round = comments.filter((comment) => comment.user?.login === BOT_LOGIN && String(comment.body ?? '').startsWith(DIAGNOSIS_COMMENT_MARKER)).length + 1;
  const amendment = usable ? await createdAmendment(client, env, folder, reasons) : null;
  let behindMain = false;
  try {
    behindMain = (await client.aheadBy(implementationBranch(folder), env.SPECKIT_BRANCH || 'main')) > 0;
  } catch {
    // Only used to suggest /speckit sync.
  }
  const runUrl = env.GITHUB_RUN_ID ? `${(env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '')}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : null;
  await client.createComment(pullNumber, renderDiagnosisComment({ report: usable, reasons, round, runUrl, amendment, behindMain }));
  const checkRun = Number(env.SPECKIT_CHECK_RUN);
  if (Number.isInteger(checkRun) && checkRun > 0) {
    await client.updateCheckRun(checkRun, {
      status: 'completed',
      conclusion: usable ? 'neutral' : 'failure',
      external_id: CHECK_DIAGNOSED,
      output: usable
        ? { title: `Waiting for a decision: ${usable.category}`, summary: clip(usable.summary, 2000) }
        : { title: 'Diagnosis without a usable report', summary: clip(reasons.join('; '), 2000) },
    });
  }
  if (amendment) {
    const implementation = await client.getPullRequest(pullNumber);
    await beginAmendment(client, env, { amendment, folder, implementation, twin });
  }
  log(usable ? `Posted the diagnosis (${usable.category}) on #${pullNumber}.` : `Posted that the diagnosis had no usable report on #${pullNumber}.`);
  return { exitCode: 0, report: usable, reasons };
}

// Records a rework of an amendment (`fix` after the consistency check, `revise` after a person's feedback) and starts
// the consistency check of the result.
async function recordRework({ client, env, log, inputs }) {
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
  const detection = String(env.SPECKIT_DETECTION ?? '').trim();
  const withheld = detection && detection !== 'success';
  const reports = withheld ? [] : readAgentReports(env);
  const { reasons, report } = withheld
    ? { reasons: [`it was withheld because threat detection did not pass (result: ${detection})`], report: null }
    : reports.length > 0 ? validateReport(reports.at(-1), { requireCategory: false }) : { reasons: ['the agent did not report'], report: null };
  if (!report || !report.summary) {
    await complete('failure', mode === 'fix' ? `Correction round ${round} did not finish` : 'The rework did not finish', reasons.join('; '));
    await client.createComment(number, `The ${mode === 'fix' ? `correction round ${round}` : 'rework'} did not produce a result: ${neutralizeMarkers(reasons.join('; ') || 'no report')}. Comment on this pull request to try again.`);
    await handBack(`correction round ${round} did not finish`);
    return { exitCode: 0, outcome: 'failed' };
  }
  const pushFailures = Number(env.SPECKIT_PUSH_FAILURES || 0);
  const pushed = String(env.SPECKIT_PUSH_SHA ?? '').trim();
  // A push the safe-output tool refused never reaches the push job; the agent reports it as incomplete instead.
  const incomplete = readAgentItems(env, 'report_incomplete').map((item) => String(item.reason ?? '').trim()).filter(Boolean);
  if (pushFailures > 0 || (!pushed && incomplete.length > 0)) {
    const what = mode === 'fix' ? `Correction round ${round}` : 'The rework';
    const why = pushFailures > 0 ? `${pushFailures} push(es) to \`${context.amendment.head.ref}\` failed.` : `The push was refused: ${incomplete.join('; ')}`;
    await complete('failure', `${what} could not push its changes`, why);
    await client.createComment(number, [
      `${what} could not push its changes to this pull request, so nothing changed. Comment on this pull request to try again.`,
      '',
      `> ${neutralizeMarkers(clip(why, 1500)).replace(/\n/g, '\n> ')}`,
    ].join('\n'));
    // A refused push is a correction round without progress; the next round retries the same findings.
    if (mode === 'fix' && stalls + 1 < MAX_STALLED_ROUNDS && round < MAX_CORRECTION_ROUNDS) {
      await startRework(client, env, context, { mode: 'fix', round: round + 1, notes: env.SPECKIT_NOTES ?? '', stalls: stalls + 1, findings, progress: `Round ${round} could not push its changes.` });
      return { exitCode: 0, outcome: 'push failed' };
    }
    await handBack(`correction round ${round} could not push its changes`);
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

function githubClient(env) {
  return new GitHubClient({
    token: env.GITHUB_TOKEN || env.GH_TOKEN,
    repository: env.GITHUB_REPOSITORY,
    apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
    graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
  });
}

export async function main(argv, { env = process.env, log = console.log, client } = {}) {
  const [command] = argv;
  const run = { client: client ?? githubClient(env), env, log };
  if (command === 'evidence') return (await runEvidence(run)).exitCode;
  if (command === 'record') return (await runRecord(run)).exitCode;
  throw new DiagnoseUsageError('Usage: speckit-diagnose.mjs <evidence | record>');
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
