import { appendFileSync, existsSync, readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { GitHubClient } from './speckit-prepare-github.mjs';
import { canWrite, resolveImplementRequester } from './speckit-prepare.mjs';
import {
  AMEND_CHECK_NAME,
  AMEND_PRESENTED_MARKER,
  AMEND_REWORK_MARKER,
  ANALYZE_WORKFLOW_FILE,
  CHECK_AMEND_CHECKING,
  CHECK_AMEND_CONSISTENT,
  CHECK_AMEND_INCONSISTENT,
  DIAGNOSE_WORKFLOW_FILE,
  DIAGNOSIS_STALE_MS,
  MAX_CORRECTION_ROUNDS,
  MAX_STALLED_ROUNDS,
  amendmentBranch,
  dispatchImplementation,
  folderOfBranch,
  implementationBranch,
  latestCheckRun,
  neutralizeMarkers,
  renderAmendmentHowTo,
  renderNextSteps,
  renderResumeComment,
  setAmendmentStatus,
  validateAmendment,
} from './speckit-implement-core.mjs';

export const SEVERITIES = ['CRITICAL', 'HIGH', 'MEDIUM', 'LOW'];
// Findings of these severities make an amendment inconsistent; LOW findings are shown but do not block.
const BLOCKING = new Set(['CRITICAL', 'HIGH', 'MEDIUM']);

const clip = (text, max) => {
  const value = String(text ?? '');
  return value.length > max ? `${value.slice(0, max)}…` : value;
};
const isBot = (user) => user?.type === 'Bot' || String(user?.login ?? '').endsWith('[bot]');
// Marks the end of the feedback a rework or presentation handled (a GitHub timestamp), so feedback that arrives while
// the marker is posted stays pending.
const CUTOFF_PATTERN = /<!-- speckit-amend:cutoff (\S+) -->/;
export const renderCutoff = (iso) => `<!-- speckit-amend:cutoff ${iso} -->`;
// Marks, on the implementation pull request, that a person's merge or close of an amendment was handled.
export const renderClosedMarker = (number) => `<!-- speckit-amend:closed ${number} -->`;
const AMEND_RUN_WORKFLOWS = [ANALYZE_WORKFLOW_FILE, DIAGNOSE_WORKFLOW_FILE];
const defaultSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export class AmendUsageError extends Error {}

export async function findAmendment(client, folder) {
  const pulls = await client.listPullRequestsForHead(amendmentBranch(folder));
  return pulls.find((pull) => pull.state === 'open') ?? null;
}

// Keeps Spec Kit pull requests out of pull request stacks, and amendments on their implementation branch. Merging a
// stacked pull request also merges every pull request below it, so a stacked amendment would carry the unfinished
// implementation into the default branch; an amendment with another base would land there instead of on the
// implementation branch.
export async function guardPullRequest(client, env, pull, { report = () => {} } = {}) {
  const head = String(pull.head?.ref ?? '');
  const folder = folderOfBranch(head);
  if (!folder || pull.state !== 'open' || (env.GITHUB_REPOSITORY && pull.head?.repo?.full_name !== env.GITHUB_REPOSITORY)) return { actions: [] };
  const isAmendment = head === amendmentBranch(folder);
  const notes = [];
  const actions = [];
  if (pull.stack?.number) {
    await client.unstackPullRequests(pull.stack.number);
    actions.push('unstacked');
    notes.push(isAmendment
      ? 'This pull request was removed from its pull request stack. Merging a stacked pull request also merges the ones below it, so the unfinished implementation would reach the default branch with it.'
      : 'This pull request was removed from its pull request stack. Spec Kit pull requests are not stacked: merging a stacked amendment would also merge this unfinished implementation into the default branch.');
  }
  const base = implementationBranch(folder);
  if (isAmendment && pull.base?.ref !== base) {
    await client.updatePullRequest(pull.number, { base });
    actions.push(`retargeted from ${pull.base?.ref}`);
    notes.push(`The base branch was set back from \`${pull.base?.ref}\` to \`${base}\`: an amendment only merges into its implementation branch. The implementation reaches the default branch through its own pull request once it is complete.`);
  }
  if (actions.length > 0) {
    await client.createComment(pull.number, notes.join('\n\n'));
    report(`- Guarded #${pull.number}: ${actions.join(', ')}.`);
  }
  return { actions };
}

// Checks an amendment pull request against the hard rules for amendments (paths and task checks).
export async function checkAmendment(client, { folder, amendment }) {
  const files = await client.listPullRequestFiles(amendment.number);
  const changedPaths = [...new Set(files.flatMap((file) => [file.filename, file.previous_filename]).filter(Boolean))];
  const tasksPath = `specs/${folder}/tasks.md`;
  const changesTasks = changedPaths.includes(tasksPath);
  const beforeTasks = changesTasks ? await client.getFileContent(tasksPath, amendment.base.sha) : null;
  const afterTasks = changesTasks ? await client.getFileContent(tasksPath, amendment.head.sha) : null;
  return { changedPaths, ...validateAmendment({ folder, changedPaths, beforeTasks, afterTasks }) };
}

// Resolves an amendment pull request (head speckit-amend/<folder>) to its open implementation pull request and twin.
export async function resolveAmendment(client, env, number) {
  const amendment = await client.getPullRequest(number);
  const head = String(amendment.head?.ref ?? '');
  const folder = head.startsWith('speckit-amend/') ? folderOfBranch(head) : null;
  if (!folder || amendment.head?.repo?.full_name !== env.GITHUB_REPOSITORY) return { reason: 'this is not a Spec Kit amendment pull request' };
  const listed = (await client.listPullRequestsForHead(implementationBranch(folder))).find((pull) => pull.state === 'open');
  if (!listed) return { reason: `there is no open implementation pull request for \`specs/${folder}\``, amendment, folder };
  const implementation = await client.getPullRequest(listed.number);
  const twin = Number(String(implementation.body ?? '').match(/^Closes #(\d+)$/m)?.[1]);
  if (!Number.isInteger(twin)) return { reason: `the implementation pull request #${implementation.number} does not name its twin`, amendment, folder };
  return { amendment, folder, implementation, twin };
}

async function requesterOf(client, twin) {
  try {
    return (await resolveImplementRequester(client, twin))?.login ?? null;
  } catch {
    return null;
  }
}

function runLink(env) {
  return env.GITHUB_RUN_ID ? `${(env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, '')}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : null;
}

// Starts the independent consistency analysis of the amendment's current head. `mode` is `loop` (findings lead to
// correction rounds) or `check` (after a person's push: report only). After a correction round, `previous` holds the
// round's tracked findings and `stalls` the rounds without progress so far.
export async function startAnalysis(client, env, { amendment, folder, implementation, twin }, { round, mode = 'loop', stalls = 0, previous = [] }) {
  const current = await client.getPullRequest(amendment.number);
  const check = await client.createCheckRun({
    name: AMEND_CHECK_NAME,
    head_sha: current.head.sha,
    status: 'in_progress',
    external_id: CHECK_AMEND_CHECKING,
    output: {
      title: round === 0 || mode === 'check' ? 'Checking consistency' : `Checking consistency after correction round ${round}`,
      summary: 'An independent /speckit-analyze checks every artifact of the spec folder against the others and the constitution.',
    },
  });
  try {
    await client.dispatchWorkflow(ANALYZE_WORKFLOW_FILE, env.SPECKIT_BRANCH || 'main', {
      twin: String(twin),
      pull: String(implementation.number),
      folder,
      amendment: String(amendment.number),
      round: String(round),
      stalls: String(stalls),
      previous: previous.length > 0 ? encodeTracked(previous) : '',
      mode,
      check_run: String(check.id),
    });
    return { started: true, check };
  } catch (error) {
    await client.updateCheckRun(check.id, {
      status: 'completed',
      conclusion: 'failure',
      external_id: CHECK_AMEND_INCONSISTENT,
      output: { title: 'The consistency check could not start', summary: clip(error.message, 2000) },
    });
    await client.createComment(amendment.number, `The consistency check could not start: ${neutralizeMarkers(clip(error.message, 500))}. Comment on this pull request to try again.`);
    return { started: false, check, error: error.message };
  }
}

// Starts a rework of the amendment: `revise` with a person's feedback (`feedback`, from pendingFeedback, handed to the
// rework as notes), or `fix` with the findings of the consistency check. The pull request goes back to draft until it
// is presented again.
export async function startRework(client, env, context, { mode, round = 0, notes = '', feedback = null, stalls = 0, findings = [], progress = null }) {
  const { amendment, folder, implementation, twin } = context;
  const current = await client.getPullRequest(amendment.number);
  if (!current.draft) await client.convertToDraft(current.node_id);
  if (mode === 'revise') {
    const items = feedback?.items ?? [];
    const authors = [...new Set(items.map((item) => item.author))];
    const cutoff = items.length > 0 ? items.map((item) => item.at).sort((a, b) => Date.parse(b) - Date.parse(a))[0] : feedback?.since;
    notes = clip(renderFeedback(items), 20_000);
    await client.createComment(amendment.number, [
      AMEND_REWORK_MARKER,
      ...(cutoff ? [renderCutoff(cutoff)] : []),
      `**Reworking this amendment** with the feedback${authors.length > 0 ? ` from ${authors.map((login) => `@${login}`).join(', ')}` : ''}. It returns here for your review once it is consistent again.`,
    ].join('\n'));
  } else {
    await client.createComment(amendment.number, [
      `**Correction round ${round}**: the consistency check found inconsistencies; they are being fixed.${progress ? ` ${progress}` : ''} Rounds without progress so far: ${stalls} of ${MAX_STALLED_ROUNDS}.`,
      '',
      '<details><summary>Findings</summary>',
      '',
      neutralizeMarkers(clip(notes, 6000)),
      '',
      '</details>',
    ].join('\n'));
  }
  const check = await client.createCheckRun({
    name: AMEND_CHECK_NAME,
    head_sha: current.head.sha,
    status: 'in_progress',
    external_id: CHECK_AMEND_CHECKING,
    output: { title: mode === 'revise' ? 'Rework in progress' : `Correction round ${round} in progress`, summary: clip(notes, 2000) || 'Reworking the amendment.' },
  });
  try {
    await client.dispatchWorkflow(DIAGNOSE_WORKFLOW_FILE, env.SPECKIT_BRANCH || 'main', {
      twin: String(twin),
      pull: String(implementation.number),
      folder,
      notes: clip(notes, 20_000),
      previous: '',
      check_run: String(check.id),
      mode,
      amendment: String(amendment.number),
      round: String(round),
      stalls: String(stalls),
      findings: findings.length > 0 ? encodeTracked(findings) : '',
    });
    return { started: true, check };
  } catch (error) {
    await client.updateCheckRun(check.id, {
      status: 'completed',
      conclusion: 'failure',
      external_id: CHECK_AMEND_INCONSISTENT,
      output: { title: 'The rework could not start', summary: clip(error.message, 2000) },
    });
    await client.createComment(amendment.number, `The rework could not start: ${neutralizeMarkers(clip(error.message, 500))}. Comment on this pull request to try again.`);
    return { started: false, check, error: error.message };
  }
}

// The first steps for an amendment a diagnosis just created: mark the start of the feedback window, describe the
// state, and start the consistency check.
export async function beginAmendment(client, env, context) {
  const { amendment, implementation } = context;
  const requester = await requesterOf(client, context.twin);
  await client.createComment(amendment.number, [
    AMEND_REWORK_MARKER,
    renderCutoff(amendment.created_at ?? new Date(0).toISOString()),
    `This amendment comes from the diagnosis on #${implementation.number}. An independent \`/speckit-analyze\` now checks it for consistency, and correction rounds fix what it finds while they make progress; then it is handed to ${requester ? `@${requester}` : 'you'} for review.`,
  ].join('\n'));
  const current = await client.getPullRequest(amendment.number);
  await client.updatePullRequest(amendment.number, {
    body: setAmendmentStatus(current.body, ['⏳ **Being checked for consistency.** You are asked to review it when the check is done.', '', ...renderAmendmentHowTo(implementation.number)]),
  });
  return startAnalysis(client, env, context, { round: 0, mode: 'loop' });
}

// Feedback from people with write access since the last rework or presentation (their cutoff time): conversation
// comments (not commands), reviews with Comment or Request changes, and their line comments.
export async function pendingFeedback(client, amendment) {
  const comments = await client.listIssueComments(amendment.number);
  const marks = comments
    .filter((comment) => isBot(comment.user) && [AMEND_REWORK_MARKER, AMEND_PRESENTED_MARKER].some((marker) => String(comment.body ?? '').startsWith(marker)))
    .map((comment) => Date.parse(String(comment.body).match(CUTOFF_PATTERN)?.[1] ?? comment.created_at) || 0);
  const since = Math.max(Date.parse(amendment.created_at ?? 0) || 0, ...marks);
  const after = (time) => Date.parse(time ?? 0) > since;
  const permissions = new Map();
  const writer = async (login) => {
    if (!permissions.has(login)) permissions.set(login, canWrite(await client.getPermission(login)));
    return permissions.get(login);
  };
  const items = [];
  for (const comment of comments) {
    if (isBot(comment.user) || !after(comment.created_at) || /^\s*\/speckit\b/.test(String(comment.body ?? ''))) continue;
    if (await writer(comment.user.login)) items.push({ kind: 'comment', author: comment.user.login, body: comment.body, at: comment.created_at });
  }
  const reviews = await client.listPullRequestReviews(amendment.number);
  const reviewComments = await client.listPullRequestReviewComments(amendment.number);
  for (const review of reviews) {
    if (isBot(review.user) || !after(review.submitted_at) || !['COMMENTED', 'CHANGES_REQUESTED'].includes(review.state)) continue;
    if (!(await writer(review.user.login))) continue;
    const lines = reviewComments.filter((comment) => comment.pull_request_review_id === review.id);
    items.push({
      kind: review.state === 'CHANGES_REQUESTED' ? 'review (changes requested)' : 'review',
      author: review.user.login,
      body: review.body,
      at: review.submitted_at,
      lines: lines.map((comment) => ({ path: comment.path, line: comment.line ?? comment.original_line, body: comment.body })),
    });
  }
  const covered = new Set(reviews.map((review) => review.id));
  for (const comment of reviewComments) {
    if (covered.has(comment.pull_request_review_id) || isBot(comment.user) || !after(comment.created_at)) continue;
    if (await writer(comment.user.login)) items.push({ kind: 'line comment', author: comment.user.login, body: '', at: comment.created_at, lines: [{ path: comment.path, line: comment.line ?? comment.original_line, body: comment.body }] });
  }
  return { since: new Date(since).toISOString(), items: items.sort((a, b) => Date.parse(a.at) - Date.parse(b.at)) };
}

export function renderFeedback(items) {
  const lines = [];
  for (const item of items) {
    lines.push(`### ${item.kind} by @${item.author} at ${item.at}`, '');
    if (String(item.body ?? '').trim()) lines.push(clip(item.body, 6000), '');
    for (const line of item.lines ?? []) lines.push(`- \`${line.path}\`${line.line ? ` line ${line.line}` : ''}: ${clip(line.body, 3000)}`);
    if ((item.lines ?? []).length > 0) lines.push('');
  }
  return lines.join('\n');
}

// Validates the consistency analysis the agent reported; its text is untrusted.
export function validateAnalysis(raw) {
  let report = raw;
  if (typeof raw === 'string') {
    try {
      report = JSON.parse(raw);
    } catch (error) {
      return { reasons: [`the analysis is not valid JSON: ${error.message}`], analysis: null };
    }
  }
  if (!report || typeof report !== 'object' || !Array.isArray(report.findings)) return { reasons: ['the analysis has no findings list'], analysis: null };
  const text = (value, max) => (typeof value === 'string' ? clip(value.trim(), max) : '');
  const findings = report.findings
    .map((item) => ({
      severity: SEVERITIES.includes(String(item?.severity ?? '').toUpperCase()) ? String(item.severity).toUpperCase() : 'HIGH',
      category: text(item?.category, 80),
      location: text(item?.location, 300),
      summary: text(item?.summary, 1200),
      recommendation: text(item?.recommendation, 1200),
    }))
    .filter((item) => item.summary)
    .slice(0, 40);
  const resolved = Array.isArray(report.resolved) ? [...new Set(report.resolved.map((id) => String(id).trim().toUpperCase()).filter((id) => /^F\d{1,3}$/.test(id)))] : [];
  return { reasons: [], analysis: { summary: text(report.summary, 1500), findings, resolved } };
}

// The blocking findings and broken amendment rules of a round, with ids (F1…, R1…) so the next analysis can say which
// of them the correction resolved. Carried between the workflows as compact JSON.
export function trackFindings(blocking, rules = []) {
  return [
    ...rules.map((reason, index) => ({ id: `R${index + 1}`, severity: 'RULE', summary: clip(reason, 400) })),
    ...blocking.map((item, index) => ({ id: `F${index + 1}`, severity: item.severity, location: clip(item.location ?? '', 200), summary: clip(item.summary, 600) })),
  ];
}

export function encodeTracked(tracked) {
  const items = [...tracked];
  let json = JSON.stringify(items);
  while (json.length > 12_000 && items.length > 1) {
    items.pop();
    json = JSON.stringify(items);
  }
  return json;
}

export function decodeTracked(raw) {
  try {
    const items = JSON.parse(String(raw ?? '').trim() || '[]');
    return Array.isArray(items) ? items.filter((item) => item && typeof item.id === 'string' && typeof item.summary === 'string') : [];
  } catch {
    return [];
  }
}

// Progress of a correction round: the analysis resolved one of the previous round's blocking findings, or a broken
// amendment rule of the previous round holds again.
export function correctionProgress(previous, analysis, rules) {
  const ids = new Set(previous.filter((item) => item.id.startsWith('F')).map((item) => item.id));
  const resolved = (analysis?.resolved ?? []).filter((id) => ids.has(id));
  const fixedRules = previous.filter((item) => item.id.startsWith('R') && !rules.some((reason) => clip(reason, 400) === item.summary));
  return { progress: resolved.length + fixedRules.length > 0, resolved: [...resolved, ...fixedRules.map((item) => item.id)] };
}

function renderFindings(findings, rules = [], { ids = false } = {}) {
  return [
    ...rules.map((reason, index) => `- ${ids ? `[R${index + 1}] ` : ''}**RULE**: ${reason}`),
    ...findings.map((item) => `- ${item.id ? `[${item.id}] ` : ''}**${item.severity}**${item.category ? ` (${neutralizeMarkers(item.category)})` : ''}${item.location ? ` \`${neutralizeMarkers(item.location)}\`` : ''}: ${neutralizeMarkers(item.summary)}${item.recommendation ? ` — ${neutralizeMarkers(item.recommendation)}` : ''}`),
  ].join('\n');
}

// Hands the amendment to the person who requested the implementation: ready for review when it is consistent (a
// draft otherwise), assigned, review requested, status in the description, and a note on the implementation.
export async function presentAmendment(client, env, context, { consistent, analysis = null, rules = [], problem = null, since = null }) {
  const { amendment, folder, implementation, twin } = context;
  const current = await client.getPullRequest(amendment.number);
  const requester = await requesterOf(client, twin);
  if (consistent && current.draft) await client.markReadyForReview(current.node_id);
  if (requester) {
    try {
      await client.addAssignees(amendment.number, [requester]);
    } catch {
      // Assignment is a convenience.
    }
    try {
      await client.requestReviewers(amendment.number, [requester]);
    } catch {
      // A review request can fail, for example for the author; the mention still notifies.
    }
  }
  const findings = analysis?.findings ?? [];
  const blocking = findings.filter((item) => BLOCKING.has(item.severity));
  const status = consistent
    ? `✅ **Consistent**: an independent \`/speckit-analyze\` of \`specs/${folder}/\` found no CRITICAL, HIGH, or MEDIUM inconsistencies${findings.length > 0 ? ` (${findings.length} LOW finding(s), listed in the comments)` : ''}.`
    : `⚠️ **Not consistent**: ${problem ?? `${blocking.length + rules.length} inconsistency(ies) remain`}. Review the findings in the comments; merging is possible but not recommended.`;
  await client.updatePullRequest(amendment.number, { body: setAmendmentStatus(current.body, [status, '', ...renderAmendmentHowTo(implementation.number)]) });
  await client.createComment(amendment.number, [
    AMEND_PRESENTED_MARKER,
    ...(since ? [renderCutoff(since)] : []),
    `${requester ? `@${requester} ` : ''}${status}`,
    ...(analysis?.summary ? ['', neutralizeMarkers(analysis.summary)] : []),
    ...(findings.length > 0 || rules.length > 0 ? ['', '<details><summary>Findings</summary>', '', renderFindings(findings, rules), '', '</details>'] : []),
    '',
    ...renderAmendmentHowTo(implementation.number),
  ].join('\n'));
  await client.createComment(implementation.number, consistent
    ? `**Amendment #${amendment.number} is ready for your review**: it is consistent. Merge it to apply it (the implementation then continues), comment or request changes there to have it reworked, or close it to discard it.`
    : `**Amendment #${amendment.number} is not consistent** (${problem ?? `${blocking.length + rules.length} inconsistency(ies) remain`}). Review its findings there: comment or request changes to have it reworked, or close it.`);
  return { consistent, requester };
}

function readAnalysisReports(env) {
  const file = env.GH_AW_AGENT_OUTPUT;
  if (!file || !existsSync(file)) return [];
  try {
    const output = JSON.parse(readFileSync(file, 'utf8'));
    return (output.items ?? []).filter((item) => item.type === 'speckit_analysis').map((item) => item.report ?? item);
  } catch {
    return [];
  }
}

function amendmentInputs(env) {
  const number = Number(env.SPECKIT_AMENDMENT);
  if (!Number.isInteger(number) || number <= 0) throw new AmendUsageError('Expected SPECKIT_AMENDMENT as the amendment pull request number.');
  return number;
}

// Custom safe-output job of Spec Kit analyze: decides between a correction round, a rework with pending feedback,
// and presenting the amendment.
export async function runRecordAnalysis({ client, env, log }) {
  const number = amendmentInputs(env);
  const round = Math.max(0, Number(env.SPECKIT_ROUND) || 0);
  const mode = env.SPECKIT_MODE === 'check' ? 'check' : 'loop';
  const checkRun = Number(env.SPECKIT_CHECK_RUN);
  const complete = async (conclusion, title, summary) => {
    if (Number.isInteger(checkRun) && checkRun > 0) {
      await client.updateCheckRun(checkRun, {
        status: 'completed',
        conclusion,
        external_id: conclusion === 'success' ? CHECK_AMEND_CONSISTENT : CHECK_AMEND_INCONSISTENT,
        output: { title, summary: clip(summary || title, 60_000) },
      });
    }
  };
  const context = await resolveAmendment(client, env, number);
  if (context.reason || context.amendment.state !== 'open') {
    await complete('neutral', 'The amendment is no longer open', context.reason ?? 'Nothing to check.');
    log(`Amendment #${number} is no longer open.`);
    return { exitCode: 0, outcome: 'closed' };
  }
  const detection = String(env.SPECKIT_DETECTION ?? '').trim();
  const withheld = detection && detection !== 'success';
  const reports = withheld ? [] : readAnalysisReports(env);
  const { reasons, analysis } = withheld
    ? { reasons: [`the analysis was withheld because threat detection did not pass (result: ${detection})`], analysis: null }
    : reports.length > 0 ? validateAnalysis(reports.at(-1)) : { reasons: ['the analysis did not report'], analysis: null };
  const rules = (await checkAmendment(client, { folder: context.folder, amendment: context.amendment })).reasons;
  const blocking = (analysis?.findings ?? []).filter((item) => BLOCKING.has(item.severity));
  const consistent = Boolean(analysis) && rules.length === 0 && blocking.length === 0;
  const findingsText = renderFindings(analysis?.findings ?? [], rules);
  if (!analysis) {
    await complete('failure', 'The consistency check did not report', reasons.join('; '));
    const pending = await pendingFeedback(client, context.amendment);
    await presentAmendment(client, env, context, { consistent: false, rules, problem: reasons.join('; '), since: pending.since });
    return { exitCode: 0, outcome: 'no analysis' };
  }
  // After a correction round, only a round without progress counts against the budget.
  const previous = decodeTracked(env.SPECKIT_PREVIOUS);
  let stalls = Math.max(0, Number(env.SPECKIT_STALLS) || 0);
  let progressNote = null;
  if (mode === 'loop' && round > 0) {
    const { progress, resolved } = correctionProgress(previous, analysis, rules);
    if (!progress) stalls += 1;
    progressNote = progress ? `Round ${round} resolved ${resolved.join(', ')}.` : `Round ${round} resolved none of the previous findings.`;
  }
  const remaining = blocking.length + rules.length;
  const tracked = trackFindings(blocking, rules);
  const trackedBlocking = blocking.map((item, index) => ({ ...item, id: `F${index + 1}` }));
  if (!consistent && mode === 'loop' && stalls < MAX_STALLED_ROUNDS && round < MAX_CORRECTION_ROUNDS) {
    await complete('neutral', `${remaining} inconsistency(ies) found; correction round ${round + 1} follows`, [progressNote, renderFindings(trackedBlocking.concat(analysis.findings.filter((item) => !BLOCKING.has(item.severity))), rules, { ids: true })].filter(Boolean).join('\n\n'));
    await startRework(client, env, context, { mode: 'fix', round: round + 1, notes: renderFindings(trackedBlocking, rules, { ids: true }), stalls, findings: tracked, progress: progressNote });
    log(`Amendment #${number}: ${remaining} inconsistency(ies); started correction round ${round + 1} (${stalls} without progress).`);
    return { exitCode: 0, outcome: 'correcting', stalls };
  }
  await complete(consistent ? 'success' : 'failure', consistent ? 'Consistent' : `${remaining} inconsistency(ies) remain`, [progressNote, findingsText || analysis.summary || 'No findings.'].filter(Boolean).join('\n\n'));
  const pending = await pendingFeedback(client, context.amendment);
  if (pending.items.length > 0) {
    await startRework(client, env, context, { mode: 'revise', feedback: pending });
    log(`Amendment #${number}: feedback arrived during the check; started a rework.`);
    return { exitCode: 0, outcome: 'rework' };
  }
  const problem = consistent || mode !== 'loop'
    ? null
    : stalls >= MAX_STALLED_ROUNDS
      ? `${remaining} inconsistency(ies) remain; ${stalls} of ${round} correction round(s) made no progress`
      : `${remaining} inconsistency(ies) remain after the limit of ${MAX_CORRECTION_ROUNDS} correction rounds`;
  await presentAmendment(client, env, context, { consistent, analysis, rules, problem, since: pending.since });
  log(`Amendment #${number}: presented (${consistent ? 'consistent' : 'not consistent'}).`);
  return { exitCode: 0, outcome: consistent ? 'consistent' : 'inconsistent', stalls };
}

// Whether a consistency check or rework of the amendment is running: a diagnose or analyze run of the twin is active
// (the push of a rework moves the head before its record job starts the next check), or the head's check run was
// started moments ago and its run is not visible yet. A check run in progress without a run is stale.
async function reworkRunning(client, context, now) {
  const since = new Date(now - 2 * DIAGNOSIS_STALE_MS).toISOString();
  for (const file of AMEND_RUN_WORKFLOWS) {
    const runs = (await client.listWorkflowRuns(file, since, 1)).filter((run) => String(run.display_title ?? '').endsWith(`#${context.twin}`));
    if (runs.some((run) => run.status !== 'completed')) return { running: true };
  }
  const current = await client.getPullRequest(context.amendment.number);
  const latest = latestCheckRun(await client.listCheckRuns(current.head.sha, AMEND_CHECK_NAME));
  if (!latest || latest.status === 'completed' || latest.external_id !== CHECK_AMEND_CHECKING) return { running: false, latest };
  const started = Date.parse(latest.started_at ?? latest.created_at ?? 0);
  // A dispatched run takes a few seconds to become visible.
  if (now - started < 5 * 60 * 1000) return { running: true };
  return { running: false, stale: latest };
}

async function closeStaleCheck(client, check) {
  await client.updateCheckRun(check.id, {
    status: 'completed',
    conclusion: 'failure',
    external_id: CHECK_AMEND_INCONSISTENT,
    output: { title: 'The check or rework did not finish', summary: 'The run ended without a result.' },
  });
}

// A person commented on, or reviewed, an amendment pull request: start a rework. While a check or rework runs, wait
// for it (up to `maxWaitMs`) and decide again, so feedback is never left behind by the end of a running round.
export async function runFeedback({ client, env, log, now = Date.now, sleep = defaultSleep, maxWaitMs = 20 * 60 * 1000 }) {
  const number = Number(env.SPECKIT_PULL);
  const context = await resolveAmendment(client, env, number);
  if (context.reason || context.amendment.state !== 'open') {
    log(`#${number}: ${context.reason ?? 'the amendment is not open'}.`);
    return { exitCode: 0, outcome: 'ignored' };
  }
  const commentId = Number(env.SPECKIT_COMMENT_ID);
  if (Number.isInteger(commentId) && commentId > 0 && env.SPECKIT_ACTOR && canWrite(await client.getPermission(env.SPECKIT_ACTOR))) {
    try {
      await client.addCommentReaction(commentId, 'eyes');
    } catch {
      // The reaction is only a receipt.
    }
  }
  const started = now();
  for (;;) {
    const amendment = await client.getPullRequest(number);
    if (amendment.state !== 'open') return { exitCode: 0, outcome: 'ignored' };
    const pending = await pendingFeedback(client, amendment);
    if (pending.items.length === 0) {
      log(`Amendment #${number}: no feedback to handle.`);
      return { exitCode: 0, outcome: 'none' };
    }
    const state = await reworkRunning(client, { ...context, amendment }, now());
    if (!state.running) {
      if (state.stale) await closeStaleCheck(client, state.stale);
      await startRework(client, env, { ...context, amendment }, { mode: 'revise', feedback: pending });
      log(`Amendment #${number}: started a rework with ${pending.items.length} feedback item(s).`);
      return { exitCode: 0, outcome: 'rework' };
    }
    if (now() - started >= maxWaitMs) {
      log(`Amendment #${number}: a check or rework is still running; the hourly scheduler run (Spec Kit orchestrate) picks up the feedback.`);
      return { exitCode: 0, outcome: 'queued' };
    }
    await sleep(30_000);
  }
}

// Handles a person's merge or close of an amendment pull request once (a marker on the implementation pull request
// records it). A merge resumes the implementation with a fresh attempt count: the amendment's commits may be the bot's
// (rebase merges keep their author), so the merge itself does not start a new attempt window.
export async function settleClosedAmendment(client, env, { amendment, folder, implementation, actor = null, dispatch = true }) {
  if (amendment.state === 'open') return { outcome: 'open' };
  const marker = renderClosedMarker(amendment.number);
  const comments = await client.listIssueComments(implementation.number);
  if (comments.some((comment) => isBot(comment.user) && String(comment.body ?? '').includes(marker))) return { outcome: 'handled' };
  const amendmentComments = await client.listIssueComments(amendment.number);
  if (!amendment.merged_at && amendmentComments.some((comment) => isBot(comment.user) && String(comment.body ?? '').startsWith('Superseded by a new diagnosis'))) return { outcome: 'superseded' };
  const by = actor ? `@${actor}` : 'a person';
  const base = implementationBranch(folder);
  // Merged into another branch (its base was changed, or it was merged as part of a stack): the implementation did not
  // get it.
  if (amendment.merged_at && amendment.base?.ref !== base) {
    await client.createComment(implementation.number, [
      `${marker}\n**Amendment #${amendment.number} was merged into \`${amendment.base?.ref}\`** by ${by}, not into \`${base}\`. The implementation did not get it and stays stopped. Revert that merge on \`${amendment.base?.ref}\`.`,
      '',
      ...renderNextSteps({ diagnosed: true }),
    ].join('\n'));
    return { outcome: 'misdirected' };
  }
  try {
    await client.deleteBranch(amendmentBranch(folder));
  } catch {
    // Already gone, or GitHub deleted it.
  }
  if (amendment.merged_at) {
    await client.createComment(implementation.number, renderResumeComment(
      `${marker}\n**Amendment #${amendment.number} was merged** by ${by}. The implementation continues with a fresh attempt count, starting with the next unchecked task.`,
    ));
    const twin = Number(String(implementation.body ?? '').match(/^Closes #(\d+)$/m)?.[1]);
    if (dispatch && Number.isInteger(twin)) {
      try {
        await dispatchImplementation(client, env, { twin, pull: implementation.number });
      } catch {
        // The resume comment persists the request; the next scheduler run continues the implementation.
      }
    }
    return { outcome: 'merged' };
  }
  let behindMain = false;
  try {
    behindMain = (await client.aheadBy(implementationBranch(folder), env.SPECKIT_BRANCH || 'main')) > 0;
  } catch {
    // Only used to suggest /speckit sync.
  }
  await client.createComment(implementation.number, [`${marker}\nAmendment #${amendment.number} was closed without merging by ${by}.`, '', ...renderNextSteps({ diagnosed: true, behindMain })].join('\n'));
  return { outcome: 'discarded' };
}

// An amendment pull request was closed by a person: merged (the implementation continues on the implementation
// pull request) or discarded.
export async function runClosed({ client, env, log }) {
  const number = Number(env.SPECKIT_PULL);
  const amendment = await client.getPullRequest(number);
  const head = String(amendment.head?.ref ?? '');
  const folder = head.startsWith('speckit-amend/') ? folderOfBranch(head) : null;
  if (!folder) return { exitCode: 0, outcome: 'ignored' };
  const implementation = (await client.listPullRequestsForHead(implementationBranch(folder))).find((pull) => pull.state === 'open');
  if (!implementation) return { exitCode: 0, outcome: 'no implementation' };
  const result = await settleClosedAmendment(client, env, { amendment, folder, implementation, actor: env.SPECKIT_ACTOR || null });
  log(`Amendment #${number}: ${result.outcome}.`);
  return { exitCode: 0, outcome: result.outcome };
}

// A person pushed to an amendment branch: check the result again, without correction rounds.
export async function runPushed({ client, env, log }) {
  const context = await resolveAmendment(client, env, Number(env.SPECKIT_PULL));
  if (context.reason || context.amendment.state !== 'open') return { exitCode: 0, outcome: 'ignored' };
  await startAnalysis(client, env, context, { round: 0, mode: 'check' });
  log(`Amendment #${context.amendment.number}: checking the pushed changes.`);
  return { exitCode: 0, outcome: 'checking' };
}

// Scheduler side (Spec Kit orchestrate), from the default branch for every implementation in progress, because the event jobs of Spec Kit
// commands run from the pull request's branches, which may not carry them: settles a merged or closed amendment, and
// for an open one starts a rework for feedback no event delivered, checks a person's push no event reported, and
// closes checks that never finished.
export async function maintainAmendment(client, env, { folder, implementation, twin }, { now = Date.now(), report = () => {} } = {}) {
  await guardPullRequest(client, env, await client.getPullRequest(implementation.number), { report });
  const pulls = await client.listPullRequestsForHead(amendmentBranch(folder));
  const latest = pulls.find((pull) => pull.state === 'open') ?? pulls[0];
  if (!latest) return { outcome: 'none' };
  if (latest.state !== 'open') {
    const amendment = await client.getPullRequest(latest.number);
    const settled = await settleClosedAmendment(client, env, { amendment, folder, implementation, actor: amendment.merged_by?.login ?? null, dispatch: false });
    if (['merged', 'discarded', 'misdirected'].includes(settled.outcome)) report(`- Amendment #${amendment.number} was ${settled.outcome}.`);
    return { outcome: settled.outcome };
  }
  await guardPullRequest(client, env, await client.getPullRequest(latest.number), { report });
  const context = { amendment: await client.getPullRequest(latest.number), folder, implementation, twin };
  const state = await reworkRunning(client, context, now);
  if (state.running) return { outcome: 'running' };
  if (state.stale) {
    await closeStaleCheck(client, state.stale);
    await client.createComment(latest.number, 'The consistency check or rework did not finish. Comment on this pull request to try again.');
    report(`- Closed the unfinished check of amendment #${latest.number}.`);
  }
  const pending = await pendingFeedback(client, context.amendment);
  if (pending.items.length > 0) {
    await startRework(client, env, context, { mode: 'revise', feedback: pending });
    report(`- Started a rework of amendment #${latest.number} with ${pending.items.length} feedback item(s).`);
    return { outcome: 'rework' };
  }
  if (!state.stale && !state.latest) {
    await startAnalysis(client, env, context, { round: 0, mode: 'check' });
    report(`- Checking the changes pushed to amendment #${latest.number}.`);
    return { outcome: 'checking' };
  }
  return { outcome: state.stale ? 'stale' : 'idle' };
}

// A Spec Kit pull request was opened, edited (for example its base branch), reopened, pushed, or marked ready.
export async function runGuard({ client, env, log }) {
  const pull = await client.getPullRequest(Number(env.SPECKIT_PULL));
  const { actions } = await guardPullRequest(client, env, pull, { report: log });
  return { exitCode: 0, outcome: actions.length > 0 ? 'guarded' : 'ok' };
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
  const handlers = { 'record-analysis': runRecordAnalysis, feedback: runFeedback, closed: runClosed, pushed: runPushed, guard: runGuard };
  if (!handlers[command]) throw new AmendUsageError(`Usage: speckit-amend.mjs <${Object.keys(handlers).join(' | ')}>`);
  const result = await handlers[command](run);
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `outcome=${result.outcome ?? ''}\n`);
  return result.exitCode;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof AmendUsageError ? 2 : 1;
    },
  );
}
