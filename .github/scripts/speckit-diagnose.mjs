import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { GitHubClient } from './speckit-prepare-github.mjs';
import {
  BOT_LOGIN,
  CHECK_DIAGNOSED,
  CHECK_DIAGNOSING,
  CHECK_RUN_NAME,
  DIAGNOSE_WORKFLOW_FILE,
  DIAGNOSIS_COMMENT_MARKER,
  STEPS,
  amendmentBranch,
  implementationBranch,
  neutralizeMarkers,
  nextTaskGroup,
  parseStepRunName,
  renderNextSteps,
  validateAmendment,
} from './speckit-implement-core.mjs';

export const CATEGORIES = ['artifacts', 'retry', 'outside', 'decision', 'unknown'];
export const CONFIDENCE = ['high', 'medium', 'low'];
const COMMAND_PATTERN = /^\/speckit (diagnose|revise|apply|discard|resume|sync)( [^\n]*)?$/;
const MAX_EVIDENCE_BYTES = 200_000;
const LOG_TAIL_LINES = 150;

const clip = (text, max) => {
  const value = String(text ?? '');
  return value.length > max ? `${value.slice(0, max)}…` : value;
};

export class DiagnoseUsageError extends Error {}

function setOutput(env, name, value) {
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `${name}=${value}\n`);
}

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
  return { twin, pull, folder };
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
  const { twin, pull: pullNumber, folder } = inputsFrom(env);
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
    `- Spec folder: \`specs/${folder}\``,
    `- Implementation branch: \`${branch}\` at ${pull.head.sha}`,
    `- Default branch \`${defaultBranch}\`: ${behind ?? '?'} commit(s) the implementation branch lacks; the branch is ${ahead ?? '?'} commit(s) ahead`,
    `- Next unchecked task: ${next ? `${next.id} ${next.text}` : 'none (all tasks are checked)'}`,
    '',
    '## Notes from the person who started this diagnosis',
    '',
    env.SPECKIT_NOTES?.trim() ? env.SPECKIT_NOTES.trim() : '_None._',
    '',
    '## Check runs on the head (oldest first)',
    '',
    ...checks.map((check) => `- ${check.external_id ?? '-'} ${check.status}/${check.conclusion ?? '-'}: ${check.output?.title ?? ''}${check.output?.summary ? ` — ${clip(check.output.summary, 600).replace(/\s+/g, ' ')}` : ''}`),
    '',
  ];
  if (previous) sections.push('## Previous diagnosis', '', clip(previous.body, 20_000), '');
  sections.push('## Recent pull request comments (oldest first)', '');
  for (const comment of comments.slice(-15)) {
    sections.push(`### ${comment.user?.login ?? 'unknown'} at ${comment.created_at}`, '', clip(comment.body, 8000), '');
  }
  sections.push('## Failed worker runs of this implementation', '');
  for (const [step, { file }] of Object.entries(STEPS)) {
    const runs = (await client.listWorkflowRuns(file, pull.created_at, 2))
      .filter((run) => parseStepRunName(step, run.display_title)?.twin === twin && run.status === 'completed' && run.conclusion !== 'success')
      .slice(0, 3);
    for (const run of runs) {
      sections.push(`### ${run.display_title} (${run.conclusion}) ${run.html_url ?? ''}`, '');
      for (const job of (await client.listRunJobs(run.id)).filter((item) => item.conclusion === 'failure')) {
        const logs = await client.getJobLogs(job.id);
        sections.push(`#### Job "${job.name}"`, '', '```text', logs ? logTail(logs).replaceAll('```', "'''") : '(log not available)', '```', '');
      }
    }
  }
  let evidence = sections.join('\n');
  if (Buffer.byteLength(evidence) > MAX_EVIDENCE_BYTES) evidence = `${evidence.slice(0, MAX_EVIDENCE_BYTES)}\n\n_(evidence truncated)_\n`;
  writeFileSync(path.join(outDir, 'evidence.md'), evidence);
  writeFileSync(path.join(outDir, 'context.json'), `${JSON.stringify({
    twin,
    pull: pullNumber,
    folder,
    implementationBranch: branch,
    amendmentBranch: amendmentBranch(folder),
    defaultBranch,
    head: pull.head.sha,
    nextTask: next?.id ?? null,
    previousDiagnosis: previous ? true : false,
  }, null, 2)}\n`);
  log(`Wrote the evidence for #${twin} to ${outDir}.`);
  return { exitCode: 0 };
}

// Validates and normalizes the agent's report; the agent's text is untrusted, so every field is checked and clipped.
export function validateReport(raw) {
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
  };
  if (!normalized.category) reasons.push(`the category must be one of ${CATEGORIES.join(', ')}`);
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

function readAgentReports(env) {
  const file = env.GH_AW_AGENT_OUTPUT;
  if (!file || !existsSync(file)) return [];
  try {
    const output = JSON.parse(readFileSync(file, 'utf8'));
    return (output.items ?? []).filter((item) => item.type === 'speckit_diagnosis').map((item) => item.report ?? item);
  } catch {
    return [];
  }
}

// Checks the open amendment pull request of the folder against the rules for amendments.
export async function checkAmendment(client, { folder, amendment }) {
  const files = await client.listPullRequestFiles(amendment.number);
  const changedPaths = [...new Set(files.flatMap((file) => [file.filename, file.previous_filename]).filter(Boolean))];
  const tasksPath = `specs/${folder}/tasks.md`;
  const changesTasks = changedPaths.includes(tasksPath);
  const beforeTasks = changesTasks ? await client.getFileContent(tasksPath, amendment.base.sha) : null;
  const afterTasks = changesTasks ? await client.getFileContent(tasksPath, amendment.head.sha) : null;
  return { changedPaths, ...validateAmendment({ folder, changedPaths, beforeTasks, afterTasks }) };
}

export async function findAmendment(client, folder) {
  const pulls = await client.listPullRequestsForHead(amendmentBranch(folder));
  return pulls.find((pull) => pull.state === 'open' && pull.base?.ref === implementationBranch(folder)) ?? null;
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

export function renderDiagnosisComment({ report, reasons = [], round, runUrl, amendment = null, amendmentCheck = null, behindMain = false }) {
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
  const amendmentValid = amendment && amendmentCheck && amendmentCheck.reasons.length === 0;
  if (amendment) {
    lines.push('', `**Proposed amendment**: #${amendment.number} (\`${amendment.head.ref}\` → \`${amendment.base.ref}\`). Review and edit it there before applying.`, '');
    for (const item of report.artifacts) lines.push(`- \`${safe(item.file)}\`${item.reason ? `: ${safe(item.reason)}` : ''}`);
    for (const note of amendmentCheck?.notes ?? []) lines.push(`- Note: ${note}.`);
    if (amendmentCheck?.reasons.length) lines.push('', `It cannot be applied: ${amendmentCheck.reasons.join('; ')}.`);
  } else if (report.category === 'artifacts') {
    lines.push('', '_No amendment pull request was created._');
  }
  if (report.questions.length > 0) {
    lines.push('', '**Questions** (answer with `/speckit revise <answers>`)', '');
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
    .filter((option) => option.command && (!/^\/speckit (apply|discard)\b/.test(option.command) || amendmentValid))
    .map((option) => ({ title: `${option.recommended ? '**Recommended:** ' : ''}${safe(option.title)}`, command: option.command }));
  lines.push('', ...renderNextSteps({ options: commandOptions, amendmentPull: amendmentValid ? amendment.number : null, behindMain, diagnosed: true }));
  if (reasons.length > 0) lines.push('', `_Parts of the report were dropped: ${reasons.map(safe).join('; ')}._`);
  if (runUrl) lines.push('', `<sub>[Diagnosis run](${runUrl})</sub>`);
  return lines.join('\n');
}

// Custom safe-output job, after the agent and the built-in safe outputs: validates the agent's report and the
// amendment pull request, posts the diagnosis on the implementation pull request, and marks the check run as
// waiting for a decision.
export async function runRecord({ client, env, log }) {
  const { pull: pullNumber, folder } = inputsFrom(env);
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
  const amendmentCheck = amendment ? await checkAmendment(client, { folder, amendment }) : null;
  let behindMain = false;
  try {
    behindMain = (await client.aheadBy(implementationBranch(folder), env.SPECKIT_BRANCH || 'main')) > 0;
  } catch {
    // Only used to suggest /speckit sync.
  }
  const runUrl = env.GITHUB_RUN_ID ? `${(env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '')}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : null;
  await client.createComment(pullNumber, renderDiagnosisComment({ report: usable, reasons, round, runUrl, amendment, amendmentCheck, behindMain }));
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
  log(usable ? `Posted the diagnosis (${usable.category}) on #${pullNumber}.` : `Posted that the diagnosis had no usable report on #${pullNumber}.`);
  return { exitCode: 0, report: usable, reasons };
}

// Pull request check of an amendment that a person may have edited: are the changes still applicable?
export async function runCheckAmendment({ client, env, log }) {
  const pullNumber = Number(env.SPECKIT_PULL);
  const amendment = await client.getPullRequest(pullNumber);
  const folder = String(amendment.head.ref).slice('speckit-amend/'.length);
  const result = await checkAmendment(client, { folder, amendment });
  await client.createCheckRun({
    name: 'Spec Kit amendment',
    head_sha: amendment.head.sha,
    status: 'completed',
    conclusion: result.reasons.length === 0 ? 'success' : 'failure',
    output: {
      title: result.reasons.length === 0 ? 'The amendment can be applied' : 'The amendment cannot be applied',
      summary: [...result.reasons.map((reason) => `- ${reason}`), ...result.notes.map((note) => `- Note: ${note}`)].join('\n') || 'Only spec artifacts change.',
    },
  });
  setOutput(env, 'valid', String(result.reasons.length === 0));
  log(result.reasons.length === 0 ? 'The amendment can be applied.' : `The amendment cannot be applied: ${result.reasons.join('; ')}`);
  return { exitCode: 0, ...result };
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
  if (command === 'check-amendment') return (await runCheckAmendment(run)).exitCode;
  throw new DiagnoseUsageError('Usage: speckit-diagnose.mjs <evidence | record | check-amendment>');
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
