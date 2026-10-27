import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { createGitHubClient } from './speckit-script-runtime.mjs';
import { requestSync } from './speckit-commands.mjs';
import { failedShards, HEALER_FORBIDDEN, MAIN_HEALTH_LABEL, MAIN_HEALTH_WORKFLOW_FILE, parseCause } from './speckit-main-health.mjs';
import { ATTENTION_LABEL, BOT_LOGIN, UNBLOCKED_MARKER, currentBlocks, defaultBranchOf, setAttention, setBlocked } from './speckit-implement-core.mjs';

// The healer. When Main health finds the default branch unhealthy (an open issue labelled MAIN_HEALTH_LABEL), the
// workflow Main heal (main-heal.yml) lets a Copilot agent fix it in a read-only job, lands the change as a draft healing
// pull request (branch HEAL_BRANCH_PREFIX), verifies it in full, and asks a person to review and merge it; a failed
// attempt is closed and the next one starts, at most MAX_HEAL_ATTEMPTS per issue. While the issue or a healing pull
// request is open, the Spec Kit scheduler blocks every implementation and starts none; once the default branch is
// healthy again, each blocked implementation merges it (like `/speckit sync`) and continues.

export const HEAL_WORKFLOW_FILE = 'main-heal.yml';
export const HEAL_BRANCH_PREFIX = 'main-heal/';
export const HEAL_LABEL = 'main-heal';
export const MAX_HEAL_ATTEMPTS = 3;
const ATTEMPT_PATTERN = /<!-- speckit:heal-attempt (\{[^\n]*?\}) -->/g;
export const renderHealAttempt = (data) => `<!-- speckit:heal-attempt ${JSON.stringify(data)} -->`;
export const HEAL_STOP_MARKER = '<!-- speckit:heal-stopped -->';
// The check run on the healing pull request's head that shows the healer's verification while it runs.
export const HEAL_CHECK_NAME = 'Main heal verification';
const runUrlOf = (env) => (env.GITHUB_RUN_ID ? `${env.GITHUB_SERVER_URL ?? 'https://github.com'}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}` : '');

const TEST_FILE = /(^|\/)(tests?|Tests?)\/|(^|\/)[^/]*Tests?\/|\.(test|spec)\.[cm]?[jt]sx?$|Tests?\.cs$|(^|\/)test_[^/]*\.py$|_test\.py$/;
const DISABLED_TEST = /\bSkip\s*=|\[(Fact|Theory)\s*\(\s*Skip|\b(it|test|describe)\.(skip|only|fixme)\s*\(|\bx(it|describe|test)\s*\(|pytest\.mark\.(skip|xfail)|@unittest\.skip|Assert\.Skip\(|\[Ignore/;

// The open health issue and healing pull request: { unhealthy, issue, heal, gate }. SPECKIT_HEALTH_GATE=false turns
// the gate off.
export async function healthState(client, env) {
  if (String(env.SPECKIT_HEALTH_GATE ?? '').trim().toLowerCase() === 'false') return { unhealthy: false, issue: null, heal: null, gate: 'off' };
  const issues = (await client.listTwinIssues(MAIN_HEALTH_LABEL)).filter((issue) => issue.state === 'open').sort((a, b) => a.number - b.number);
  const heals = (await client.listOpenPullRequests()).filter((pull) => String(pull.head?.ref ?? '').startsWith(HEAL_BRANCH_PREFIX));
  return { unhealthy: issues.length > 0 || heals.length > 0, issue: issues[0]?.number ?? null, heal: heals[0]?.number ?? null, gate: 'on' };
}

// The explanation of a health block (setBlocked). The key names only the issue, so the block is explained once per
// unhealthy period, not again for every healing attempt.
export function healthBlock(health) {
  const where = health.issue ? `#${health.issue}` : `healing pull request #${health.heal}`;
  return {
    key: { health: health.issue ?? health.heal },
    lines: [
      `**Blocked** while the default branch is unhealthy: its full verification fails (${where}). Every implementation waits, so none builds on a broken default branch. Nothing is needed on this pull request.`,
      '',
      `- **Healing**: the healer fixes it in a healing pull request linked from ${where}; a person reviews and merges it.`,
      '- **Unblocks by itself** once the default branch is healthy again: this implementation then merges the healed default branch (like `/speckit sync`) and continues.',
    ],
  };
}

// Lifts the health block of an implementation once the default branch is healthy again: the lift comment ends every
// block explained so far (a dependency block is explained again by the scheduler right after), and the required
// synchronization merges the healed default branch before any work continues. Returns whether it lifted a block.
export async function liftHealthBlock(client, env, { implementation, twin, waiting = false }) {
  const comments = await client.listIssueComments(implementation.number);
  if (!currentBlocks(comments).some((key) => key && Object.hasOwn(key, 'health'))) return false;
  await client.createComment(implementation.number, `${UNBLOCKED_MARKER}\n**The default branch is healthy again**: ${waiting
    ? 'this implementation still waits for its prerequisites; once they are implemented, it merges the healed default branch first, then continues.'
    : 'the block is lifted. This implementation merges the healed default branch first, then continues.'}`);
  await setBlocked(client, implementation.number, false);
  await requestSync(client, env, { implementation, twin }, { lead: 'Required synchronization requested after the default branch was healed' });
  return true;
}

// The attempts recorded on the health issue, by attempt number: { attempt, outcome, pull, reasons }.
export function healAttempts(comments) {
  const attempts = new Map();
  for (const comment of comments ?? []) {
    if (comment.user?.login !== BOT_LOGIN) continue;
    for (const match of String(comment.body ?? '').matchAll(ATTEMPT_PATTERN)) {
      try {
        const data = JSON.parse(match[1]);
        if (Number.isInteger(data.attempt)) attempts.set(data.attempt, { ...attempts.get(data.attempt), ...data });
      } catch {
        // A damaged marker records nothing.
      }
    }
  }
  return [...attempts.values()].sort((a, b) => a.attempt - b.attempt);
}

// Decides what a Main heal run does: heal (the next attempt), none (healthy, or a healing pull request is open), or
// stop (the attempts are used up; asks a person once).
export async function decideHeal(client, env) {
  const health = await healthState(client, { ...env, SPECKIT_HEALTH_GATE: 'true' });
  if (!health.issue) return { action: 'none', reason: 'the default branch has no open health issue' };
  if (health.heal) return { action: 'none', reason: `healing pull request #${health.heal} is open`, issue: health.issue };
  // The issue names what made the default branch red; a cause only in paths the healer may not change needs a person.
  const cause = parseCause((await client.getIssue(health.issue)).body);
  if (cause && cause.healable === false) return { action: 'none', reason: 'the cause is in paths the healer may not change; the issue asks a person', issue: health.issue };
  const comments = await client.listIssueComments(health.issue);
  const attempts = healAttempts(comments);
  if (attempts.length >= MAX_HEAL_ATTEMPTS) {
    if (!comments.some((comment) => comment.user?.login === BOT_LOGIN && String(comment.body ?? '').includes(HEAL_STOP_MARKER))) {
      await client.createComment(health.issue, [
        HEAL_STOP_MARKER,
        `**The healer could not fix it in ${MAX_HEAL_ATTEMPTS} attempts**: ${attempts.map((item) => `attempt ${item.attempt} ${item.pull ? `(#${item.pull}) ` : ''}${item.outcome}`).join('; ')}. Every implementation stays blocked until the default branch is healthy again.`,
        '',
        '**Recommended next step**: Fix it directly in a pull request against the default branch, starting from the failed attempts and their verification logs linked above; the next green Main health run lifts the blocks.',
        '',
        '- **Why**: Every Spec Kit implementation verifies against the default branch, so none can continue before it is healthy.',
        '- **What it does**: Once merged, Main health runs again; a green run closes this issue, and every blocked implementation merges the healed default branch and continues.',
      ].join('\n'));
      try {
        await client.addLabels(health.issue, [ATTENTION_LABEL]);
      } catch {
        // The comment asks for the person either way.
      }
    }
    return { action: 'stop', issue: health.issue, attempt: attempts.length };
  }
  return { action: 'heal', issue: health.issue, attempt: attempts.length + 1, branch: `${HEAL_BRANCH_PREFIX}${health.issue}-${attempts.length + 1}`, previous: attempts, cause };
}

// What the agent is told about the cause (speckit-main-health.mjs attributeCause).
export function renderCauseGuidance(cause, evidence) {
  if (!cause || cause.kind === 'unknown') return ['Cause: unknown (no green verification to compare with); search the default branch.'];
  if (cause.kind === 'drift') return [`Cause: nothing changed in the repository since the last green verification (${cause.green}). The cause is outside it (an SDK, runner image, download, or service) or a flaky test: pin, harden, or make it deterministic; do not look for a culprit commit.`];
  const diff = `${evidence}/cause.diff holds the diff from the last green verification (${cause.green}) to the failing commit (${cause.red}), and ${evidence}/cause.log its commits; start there.`;
  const describe = (commit) => `${commit.sha.slice(0, 12)}${commit.pull ? ` (pull request #${commit.pull})` : ' (direct push)'} "${commit.subject}"`;
  if (cause.kind === 'spec') return [`Cause: the merge of the Spec Kit implementation ${describe(cause.commits[0])}. ${diff}`, 'Fix it forward. Never revert the implementation: its spec is archived and its twin closed as implemented.'];
  if (cause.kind === 'pull' || cause.kind === 'direct') return [`Cause: ${describe(cause.commits[0])}. ${diff}`, 'Reverting it (only within the paths you may change) is acceptable when that is the smallest correct fix; otherwise fix it forward.'];
  return [
    `Cause: ${cause.commits.length + (cause.more ?? 0)} changes since the last green verification: ${cause.commits.map(describe).join('; ')}. ${diff}`,
    'Each may be fine on its own; check how they interact. Never revert a Spec Kit implementation; fix it forward.',
  ];
}

export function renderHealPrompt({ issue, attempt, evidence, failed, previous = [], cause = null }) {
  return [
    `You are the healer of this repository. Its default branch fails its full verification (health issue #${issue}); every`,
    'implementation is blocked until it is healthy again. Make it healthy with the smallest correct change.',
    '',
    `The evidence is in ${evidence}/: verification.log holds the combined verification log of the failing Main health run, and`,
    'shards/<n>/ the log and plan of each shard.',
    '',
    ...renderCauseGuidance(cause, evidence),
    '',
    `Failing parts: ${failed.length > 0 ? failed.map((item) => `shard ${item.shard} (${item.outcome}): ${item.checks}`).join('; ') : 'see the log'}.`,
    ...(previous.length > 0 ? ['', `Earlier attempts did not fix it: ${previous.map((item) => `attempt ${item.attempt} ${item.outcome}${item.reasons ? ` (${item.reasons})` : ''}`).join('; ')}. Do not repeat them.`] : []),
    '',
    'Rules:',
    '- Find the root cause in the logs and the code first, then fix it. Reproduce the failing tests locally where the tools allow (dotnet, npm, uv, docker are set up).',
    '- You may change product code and tests, and the folders .github/actions/environment-setup and .github/actions/environment-verify for environment problems. Never change other files under .github/, .specify/, or specs/.',
    '- Never weaken a test: do not delete, skip, disable, or loosen tests or their assertions, and do not mark them flaky. A flaky test is fixed by making it deterministic.',
    '- Do not commit, push, or open pull requests; leave the change in the working tree.',
    `- As soon as you have a fix, write ${evidence}/summary.md (the root cause and the fix, in a few sentences) before you run long checks, then add how you checked it. The run can end while a long check still runs, so never leave the summary for last.`,
    '',
    `This is attempt ${attempt} of ${MAX_HEAL_ATTEMPTS}.`,
  ].join('\n');
}

// The reasons a healing change cannot land: forbidden paths, deleted test files, disabled tests, or no change.
export function validateHealChange({ changes, patch }) {
  const reasons = [];
  if (changes.length === 0) reasons.push('the agent made no change');
  for (const { status, path: file } of changes) {
    if (HEALER_FORBIDDEN.some((pattern) => pattern.test(file))) reasons.push(`${file} may not be changed by the healer`);
    if (status.startsWith('D') && TEST_FILE.test(file)) reasons.push(`${file} is a test file and may not be deleted`);
  }
  const added = String(patch ?? '').split('\n').filter((line) => line.startsWith('+') && !line.startsWith('+++'));
  const disabled = added.filter((line) => DISABLED_TEST.test(line));
  if (disabled.length > 0) reasons.push(`the change disables tests: ${disabled.slice(0, 3).map((line) => `\`${line.slice(1).trim().slice(0, 120)}\``).join(', ')}`);
  return [...new Set(reasons)];
}

export function parseNameStatus(text) {
  return String(text ?? '').split('\n').filter(Boolean).map((line) => {
    const [status, ...rest] = line.split('\t');
    return { status, path: rest.at(-1) };
  });
}

const read = (file) => (existsSync(file) ? readFileSync(file, 'utf8') : '');

async function nextAttemptOrStop(client, env, issue) {
  try {
    await client.dispatchWorkflow(HEAL_WORKFLOW_FILE, defaultBranchOf(env), { issue: String(issue) });
  } catch {
    // The next red Main health run starts the healer again.
  }
}

// Main heal, job "Open the healing pull request" (write token, never runs agent-written code): validates the agent's
// change, then commits it on a new healing branch from the base it was made on and opens the draft healing pull
// request. A change that cannot land is recorded as a failed attempt, and the next attempt starts.
export async function runLand({ client, env, git, resultDir, log = console.log }) {
  const issue = Number(env.SPECKIT_HEAL_ISSUE);
  const attempt = Number(env.SPECKIT_HEAL_ATTEMPT);
  const branch = String(env.SPECKIT_HEAL_BRANCH);
  const base = read(path.join(resultDir, 'base.txt')).trim();
  const patch = read(path.join(resultDir, 'patch.diff'));
  const changes = parseNameStatus(read(path.join(resultDir, 'changes.txt')));
  const summary = read(path.join(resultDir, 'summary.md')).trim();
  const agentExit = read(path.join(resultDir, 'agent-exit.txt')).trim();
  const reasons = [
    ...(agentExit && agentExit !== '0' ? [`the agent ended with exit code ${agentExit}`] : []),
    ...(/^[0-9a-f]{40}$/.test(base) ? [] : ['the base commit is missing']),
    ...validateHealChange({ changes, patch }),
  ];
  const outputs = (fields) => { if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, Object.entries(fields).map(([key, value]) => `${key}=${value}\n`).join('')); };
  if (reasons.length === 0) {
    const run = (args, input) => {
      const result = git(args, input);
      if (result.status !== 0) throw new Error(`git ${args.join(' ')} failed: ${result.stderr || result.stdout}`);
      return result.stdout;
    };
    try {
      run(['checkout', '-B', branch, base]);
      writeFileSync(path.join(resultDir, 'apply.diff'), patch);
      run(['apply', '--index', '--whitespace=nowarn', path.join(resultDir, 'apply.diff')]);
      run(['-c', 'user.name=github-actions[bot]', '-c', 'user.email=41898282+github-actions[bot]@users.noreply.github.com', 'commit', '-m', `Heal the default branch (#${issue}, attempt ${attempt})`, '-m', summary || 'See the healing pull request.']);
      run(['push', '--force', 'origin', `HEAD:refs/heads/${branch}`]);
    } catch (error) {
      reasons.push(error.message.slice(0, 500));
    }
  }
  if (reasons.length > 0) {
    await client.createComment(issue, [
      renderHealAttempt({ attempt, outcome: 'failed to land', reasons: reasons.join('; ').slice(0, 300) }),
      `**Healing attempt ${attempt} did not produce a change that can land**:`,
      '',
      ...reasons.map((reason) => `- ${reason}`),
      ...(summary ? ['', '<details><summary>Agent summary</summary>', '', summary, '', '</details>'] : []),
      '',
      attempt < MAX_HEAL_ATTEMPTS ? 'The next attempt starts now. Nothing is needed yet.' : 'That was the last attempt.',
    ].join('\n'));
    await nextAttemptOrStop(client, env, issue);
    outputs({ landed: 'false' });
    log(`Attempt ${attempt} did not land: ${reasons.join('; ')}`);
    return { landed: false, reasons };
  }
  const head = git(['rev-parse', 'HEAD']).stdout.trim();
  const run = env.SPECKIT_HEAL_EVIDENCE_URL ?? '';
  const healRun = runUrlOf(env);
  const pull = await client.createPullRequest({
    title: `Heal the default branch (#${issue})`,
    head: branch,
    base: defaultBranchOf(env),
    draft: true,
    body: [
      `Heals the default branch, whose full verification fails (#${issue}); attempt ${attempt} of ${MAX_HEAL_ATTEMPTS}${run ? `, from [this failing run](${run})` : ''}.`,
      '',
      `The change was written by a Copilot agent in ${healRun ? `[this Main heal run](${healRun})` : 'the Main heal workflow'}, which also runs its full verification now (check **${HEAL_CHECK_NAME}**). No action is needed until it passes.`,
      '',
      summary || `The agent wrote no summary; its log is in the \`main-heal-result\` artifact of ${healRun ? `[the run](${healRun})` : 'the run'}.`,
      '',
      'It is never merged automatically: once it passed, a person reviews and merges it. While it is open, every Spec Kit implementation waits.',
    ].join('\n'),
  });
  try {
    await client.createCheckRun({
      name: HEAL_CHECK_NAME,
      head_sha: head,
      status: 'in_progress',
      ...(healRun ? { details_url: healRun } : {}),
      output: { title: 'Verifying the healing change in full', summary: `The full verification of attempt ${attempt} runs in ${healRun || 'the Main heal run'}.` },
    });
  } catch {
    // The pull request body links the run as well.
  }
  try {
    await client.ensureLabel({ name: HEAL_LABEL, color: '0e8a16', description: 'Heals the default branch' });
    await client.addLabels(pull.number, [HEAL_LABEL]);
  } catch {
    // The branch name identifies a healing pull request.
  }
  await client.createComment(issue, `${renderHealAttempt({ attempt, outcome: 'verifying', pull: pull.number })}\n**Healing attempt ${attempt}** opened #${pull.number}; its full verification runs now. Nothing is needed yet.`);
  outputs({ landed: 'true', pull: pull.number, head });
  log(`Opened healing pull request #${pull.number} (${head}).`);
  return { landed: true, pull: pull.number, head };
}

async function completeHealCheck(client, sha, result, runUrl) {
  if (!sha) return;
  const passed = result === 'success';
  const fields = {
    status: 'completed',
    conclusion: passed ? 'success' : 'failure',
    ...(runUrl ? { details_url: runUrl } : {}),
    output: { title: passed ? 'The full verification passed' : 'The full verification failed', summary: passed ? 'Ready for a person to review and merge.' : `The verification ended with ${result || 'no result'}; this attempt is closed.` },
  };
  try {
    const existing = (await client.listCheckRuns(sha, HEAL_CHECK_NAME)).at(-1);
    if (existing) await client.updateCheckRun(existing.id, fields);
    else await client.createCheckRun({ name: HEAL_CHECK_NAME, head_sha: sha, ...fields });
  } catch {
    // The comments report the outcome as well.
  }
}

// Main heal, job "Record the healing verification": a passed healing pull request is marked ready and handed to a
// person for review and merge; a failed one is closed, and the next attempt starts.
export async function runReport({ client, env, log = console.log }) {
  const issue = Number(env.SPECKIT_HEAL_ISSUE);
  const attempt = Number(env.SPECKIT_HEAL_ATTEMPT);
  const pullNumber = Number(env.SPECKIT_HEAL_PULL);
  const result = String(env.SPECKIT_HEAL_RESULT ?? '');
  const runUrl = runUrlOf(env);
  const pull = await client.getPullRequest(pullNumber);
  await completeHealCheck(client, pull.head?.sha, result, runUrl);
  if (result === 'success') {
    try {
      if (pull.draft && pull.node_id) await client.markReadyForReview(pull.node_id);
    } catch {
      // A draft can still be reviewed.
    }
    const reviewer = env.SPECKIT_HEAL_REVIEWER || '';
    if (reviewer) {
      try {
        await client.requestReviewers(pullNumber, [reviewer]);
      } catch {
        // The comment and the label still ask for the review.
      }
    }
    await client.createComment(pullNumber, [
      `**This healing pull request passed the full verification** ([run](${runUrl})).${reviewer ? ` @${reviewer}` : ''}`,
      '',
      '**Recommended next step**: Review and merge this pull request.',
      '',
      `- **Why**: The default branch is unhealthy (#${issue}), and every Spec Kit implementation waits for it.`,
      '- **What it does**: The merge starts Main health; a green run closes the health issue, and every blocked implementation merges the healed default branch and continues.',
      '',
      '<details><summary>Other options</summary>',
      '',
      '- Request changes or close it: closing it lets the next red Main health run start another attempt.',
      '',
      '</details>',
    ].join('\n'));
    await setAttention(client, pullNumber, true);
    await client.createComment(issue, `${renderHealAttempt({ attempt, outcome: 'ready for review', pull: pullNumber })}\n**Healing pull request #${pullNumber} passed the full verification** and waits for a person's review and merge.`);
    log(`#${pullNumber} passed; waiting for review.`);
    return { outcome: 'ready' };
  }
  const failed = failedShards(read(env.SPECKIT_HEAL_LOG ?? ''));
  const parts = failed.length > 0 ? failed.map((item) => `shard ${item.shard} (${item.outcome}): ${item.checks}`).join('; ') : `the verification did not finish (${result})`;
  await client.createComment(pullNumber, `**The full verification failed** ([run](${runUrl})): ${parts}. This attempt is closed; ${attempt < MAX_HEAL_ATTEMPTS ? 'the next one starts now' : 'it was the last one'}.`);
  await client.updatePullRequest(pullNumber, { state: 'closed' });
  try {
    await client.deleteBranch(pull.head.ref);
  } catch {
    // A leftover healing branch does not hold anything.
  }
  await client.createComment(issue, `${renderHealAttempt({ attempt, outcome: 'failed verification', pull: pullNumber, reasons: parts.slice(0, 300) })}\n**Healing attempt ${attempt}** (#${pullNumber}) failed its full verification: ${parts}.`);
  await nextAttemptOrStop(client, env, issue);
  log(`#${pullNumber} failed: ${parts}.`);
  return { outcome: 'failed' };
}

export async function runDecide({ client, env, log = console.log }) {
  const decision = await decideHeal(client, env);
  // The evidence: the latest failed Main health run on the default branch (or the run named by the dispatch).
  let evidenceRun = String(env.SPECKIT_HEAL_RUN ?? '').trim();
  if (decision.action === 'heal' && !evidenceRun) {
    try {
      const runs = await client.listWorkflowRuns(MAIN_HEALTH_WORKFLOW_FILE, new Date(Date.now() - 14 * 24 * 60 * 60 * 1000).toISOString(), 2);
      const branch = defaultBranchOf(env);
      const failed = runs
        .filter((run) => run.status === 'completed' && ['failure', 'timed_out'].includes(run.conclusion) && (!run.head_branch || run.head_branch === branch))
        .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at) || b.id - a.id)[0];
      evidenceRun = failed ? String(failed.id) : '';
    } catch {
      evidenceRun = '';
    }
  }
  if (env.GITHUB_OUTPUT) {
    appendFileSync(env.GITHUB_OUTPUT, [
      `action=${decision.action}`, `issue=${decision.issue ?? ''}`, `attempt=${decision.attempt ?? ''}`, `branch=${decision.branch ?? ''}`,
      `previous=${JSON.stringify(decision.previous ?? [])}`, `run=${evidenceRun}`, `cause=${JSON.stringify(decision.cause ?? null)}`,
    ].map((line) => `${line}\n`).join(''));
  }
  log(`Decision: ${decision.action}${decision.reason ? ` (${decision.reason})` : ''}${decision.attempt ? `, attempt ${decision.attempt}` : ''}.`);
  return decision;
}

export function runPrompt({ env, log = console.log }) {
  const evidence = env.SPECKIT_HEAL_EVIDENCE ?? '.speckit-heal-evidence';
  const failed = failedShards(read(path.join(env.SPECKIT_WORKSPACE ?? '.', evidence, 'verification.log')));
  let previous = [];
  try {
    previous = JSON.parse(env.SPECKIT_HEAL_PREVIOUS || '[]');
  } catch {
    previous = [];
  }
  let cause = null;
  try {
    cause = JSON.parse(env.SPECKIT_HEAL_CAUSE || 'null');
  } catch {
    cause = null;
  }
  writeFileSync(env.SPECKIT_HEAL_PROMPT_FILE, renderHealPrompt({ issue: env.SPECKIT_HEAL_ISSUE, attempt: Number(env.SPECKIT_HEAL_ATTEMPT), evidence, failed, previous, cause }));
  log(`Wrote the healing prompt (${failed.length} failing part(s)).`);
}

export async function main(argv, { env = process.env, log = console.log, client } = {}) {
  const [command, resultDir] = argv;
  const github = () => client ?? createGitHubClient(env);
  if (command === 'decide') return (await runDecide({ client: github(), env, log }), 0);
  if (command === 'prompt') return (runPrompt({ env, log }), 0);
  if (command === 'land') {
    const git = (args) => {
      const result = spawnSync('git', args, { encoding: 'utf8', maxBuffer: 50 * 1024 * 1024 });
      return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
    };
    await runLand({ client: github(), env, git, resultDir, log });
    return 0;
  }
  if (command === 'report') return (await runReport({ client: github(), env, log }), 0);
  log('Usage: speckit-heal.mjs <decide | prompt | land <result dir> | report>');
  return 2;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (error) => { console.error(error.message); process.exitCode = 1; });
}
