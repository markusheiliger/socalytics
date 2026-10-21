import { appendFileSync, readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { IMPLEMENT_STAGE, IMPLEMENTING_STAGE, TWIN_LABEL, deriveStage, hasLabel, implementBlockers, isImplementRequested, parseTwinFolder, specDir, stageLabel, stageLabelChange } from './speckit-prepare-core.mjs';
import { canWrite, readSpecFolder, renderImplementRequested, updateIssueWithLabels } from './speckit-prepare.mjs';
import { startFixAuthoring } from './speckit-fix.mjs';
import { createGitHubClient } from './speckit-script-runtime.mjs';
import {
  ATTENTION_LABEL,
  BOT_LOGIN,
  COMMAND_SCOPES,
  MAX_UNCHANGED_RESTARTS,
  ORCHESTRATE_WORKFLOW_FILE,
  PR_COMMANDS,
  RESTART_FORCE_FLAG,
  baseBranchOf,
  continuationOf,
  defaultBranchOf,
  describeGuidanceReach,
  dispatchImplementation,
  describeCommandEffect,
  folderOfBranch,
  implementationBranch,
  latestStopReason,
  neutralizeMarkers,
  parseCommandArguments,
  renderRetryComment,
  renderSyncComment,
  setAttention,
  specTitleOfPull,
  unresolvedRepair,
  unchangedRestarts,
} from './speckit-implement-core.mjs';

export class CommandUsageError extends Error {}

const COMMAND_NAMES = PR_COMMANDS.map((command) => command.name);

// Parses a pull request comment in the form `/speckit <command> [flags] [guidance]`, like
// "/speckit retry --force use the existing helper". The command must be the first word; the rest of the comment (first
// line and following lines) is its argument, split into the flags and the guidance (parseCommandArguments).
export function parseCommand(body) {
  const text = String(body ?? '').replace(/^\s+/, '');
  const match = text.match(/^\/speckit(?:[ \t]+([A-Za-z-]+))?(?=\s|$)([\s\S]*)$/);
  if (!match) return null;
  const name = (match[1] ?? 'help').toLowerCase();
  const known = COMMAND_NAMES.includes(name) ? name : 'unknown';
  const argument = match[2].trim();
  return { name: known, given: match[1] ?? '', argument, ...parseCommandArguments(known, argument) };
}

const describeFlags = (command) => {
  const flags = Object.entries(command.flags ?? {});
  return flags.length > 0 ? flags.map(([flag, help]) => `\`${flag}\`: ${help}`).join('; ') : 'none';
};

export function renderCommandTable(commands = PR_COMMANDS) {
  return [
    '| Command | Where | What it does | Flags |',
    '| --- | --- | --- | --- |',
    ...commands.map((command) => `| \`${command.usage}\` | ${COMMAND_SCOPES[command.scope ?? 'pr'] ?? command.scope} | ${command.help} | ${describeFlags(command)} |`),
  ].join('\n');
}

export function renderHelp() {
  return [
    '**Spec Kit commands** — comment one of these on an implementation pull request (`speckit/<folder>`) or, where the table says so, on a spec twin issue. Only people with write access can use them; the command must be the first word of the comment.',
    '',
    'Every command has the form `/speckit <command> [flags] [guidance]`: flags start with `--` and come right after the command (each command supports its own), and everything after them is guidance for the next agent. Put `--` before guidance that itself starts with `--`.',
    '',
    renderCommandTable(),
    '',
    'An amendment pull request (`speckit-amend/<folder>`) or a spec pull request authored by `/speckit author` (`speckit-spec/<folder>`, earlier `speckit-fix/<folder>`) needs no commands: merge it to apply it, comment or submit a review (*Comment* or *Request changes*) to have it reworked, or close it to discard it.',
    '',
    'These are comment commands, not skills. To walk through a stop locally in VS Code, use the skill `/speckit-gha-diagnose`.',
  ].join('\n');
}

const reply = (client, number, lines) => client.createComment(number, (Array.isArray(lines) ? lines : [lines]).join('\n'));

// Resolves the implementation pull request and its twin from the pull request a command was commented on (the
// implementation pull request, or an amendment pull request of it).
async function resolveTarget(client, env, number) {
  const pull = await client.getPullRequest(number);
  const folder = folderOfBranch(pull.head?.ref);
  if (!folder || pull.head?.repo?.full_name !== env.GITHUB_REPOSITORY) return { reason: 'this is not a Spec Kit implementation or amendment pull request' };
  const implementation = pull.head.ref === implementationBranch(folder)
    ? pull
    : (await client.listPullRequestsForHead(implementationBranch(folder))).find((item) => item.state === 'open') ?? null;
  if (!implementation || implementation.state !== 'open') return { reason: `there is no open implementation pull request for \`specs/${folder}\`` };
  const full = implementation === pull ? pull : await client.getPullRequest(implementation.number);
  const twin = Number(String(full.body ?? '').match(/^Closes #(\d+)$/m)?.[1]);
  if (!Number.isInteger(twin)) return { reason: `the implementation pull request #${full.number} does not name its twin` };
  return { folder, implementation: full, twin };
}

// Resolve an amendment comment to its implementation before acquiring the publication lock. Commands on a spec twin
// lock the twin.
export async function runCommandTarget({ client, env }) {
  const number = Number(env.SPECKIT_PULL);
  if (!Number.isInteger(number) || number <= 0) throw new CommandUsageError('Expected SPECKIT_PULL.');
  if (onTwin(env)) return number;
  const command = parseCommand(env.SPECKIT_COMMENT_BODY);
  if (!command || ['help', 'unknown'].includes(command.name)) return number;
  const target = await resolveTarget(client, env, number);
  return target.implementation?.number ?? number;
}

// Whether the comment was posted on an issue (a spec twin) rather than a pull request.
const onTwin = (env) => env.SPECKIT_TARGET === 'twin';

// Starts a new attempt window: the retry comment persists the request (and the guidance), so a replaced
// pending run cannot lose it; starting the implementation chain only makes it happen right away. `lead` says who
// continued it, `effect` what a restart does (replaced by the repair's effect while a repair is pending); `prefix`
// leads the comment (the diagnosis' AUTO_CONTINUE_MARKER) and `suffix` ends its first paragraph.
export async function retryImplementation(client, env, { implementation, twin, folder }, { lead, effect, guidance = '', state = null, prefix = '', suffix = '' }) {
  const repair = unresolvedRepair(await client.listIssueComments(implementation.number));
  let text = `${lead}; ${repair ? describeCommandEffect(`/speckit retry${guidance ? ` ${guidance}` : ''}`, { baseBranch: baseBranchOf(implementation), continuation: { repair } }) : effect}`;
  if (guidance) {
    const continuation = repair ? { repair } : await continuationOf(client, { folder, pull: implementation });
    text = `${text} ${describeGuidanceReach('retry', { continuation })}`;
  }
  await client.createComment(implementation.number, `${prefix}${renderRetryComment(`${text}${suffix}`, guidance, state)}`);
  await setAttention(client, implementation.number, false);
  try {
    await dispatchImplementation(client, env, { twin, pull: implementation.number, title: specTitleOfPull(implementation) });
  } catch {
    // The retry comment persists the request; the hourly scheduler run continues the implementation.
  }
}

// Requests the required synchronization like `/speckit sync`: the sync comment persists the request, which merges the
// pull request base and passes full verification before any work continues. `lead` says who requested it; `prefix`
// and `suffix` as for retryImplementation.
export async function requestSync(client, env, { implementation, twin }, { lead, guidance = '', prefix = '', suffix = '' }) {
  const reach = guidance ? ` ${describeGuidanceReach('sync', { lastStop: latestStopReason(await client.listIssueComments(implementation.number)) })}` : '';
  await client.createComment(implementation.number, `${prefix}${renderSyncComment(
    `${lead}; merge \`${baseBranchOf(implementation)}\` and pass full verification before continuing. Pending repair scope and attempt limits are unchanged.${reach}${suffix}`,
    guidance,
    await restartState(client, implementation),
  )}`);
  await setAttention(client, implementation.number, false);
  try {
    await dispatchImplementation(client, env, { twin, pull: implementation.number, title: specTitleOfPull(implementation) });
  } catch {
    // The durable request survives a failed dispatch or replacement by another pending run.
  }
}

export const RETRY_EFFECT = 'what stopped restarts with a fresh attempt count while preserving the current phase base.';

// Command router (issue_comment): validates the author and the command, reacts, and performs the command. Comments
// by bots never get here; the workflow filters them. SPECKIT_TARGET `twin` marks a comment on a spec twin issue; every
// other comment is on a pull request. A command posted where its scope does not allow it is refused with help.
export async function runCommand({ client, env, log, now = Date.now, rootDir = env.SPECKIT_ROOT_DIR || process.cwd() }) {
  const number = Number(env.SPECKIT_PULL);
  const commentId = Number(env.SPECKIT_COMMENT_ID);
  const actor = String(env.SPECKIT_ACTOR ?? '');
  const command = parseCommand(env.SPECKIT_COMMENT_BODY);
  const twinComment = onTwin(env);
  if (!command || !Number.isInteger(number)) {
    log('Not a Spec Kit command.');
    return { exitCode: 0, handled: false };
  }
  if (actor === BOT_LOGIN || !canWrite(await client.getPermission(actor))) {
    log(`@${actor} has no write access; ignored.`);
    return { exitCode: 0, handled: false };
  }
  if (Number.isInteger(commentId) && commentId > 0) {
    try {
      await client.addCommentReaction(commentId, 'eyes');
    } catch {
      // The reaction is only a receipt.
    }
  }
  if (command.name === 'help' || command.name === 'unknown') {
    await reply(client, number, [
      ...(command.name === 'unknown' ? [`Unknown command \`/speckit ${neutralizeMarkers(command.given)}\`.`, ''] : []),
      renderHelp(),
    ]);
    return { exitCode: 0, handled: true, command: command.name };
  }
  const definition = PR_COMMANDS.find((item) => item.name === command.name);
  if (command.unknownFlags.length > 0) {
    const supported = Object.keys(definition.flags ?? {});
    await reply(client, number, [
      `**\`/speckit ${command.name}\` was not run**: it does not support ${command.unknownFlags.map((flag) => `\`${neutralizeMarkers(flag)}\``).join(', ')}. ${supported.length > 0 ? `Its flags: ${describeFlags(definition)}.` : 'It supports no flags.'}`,
      '',
      `Use \`${definition.usage}\`; put \`--\` before guidance that itself starts with \`--\`.`,
    ]);
    return { exitCode: 0, handled: true, command: command.name, rejected: 'unsupported flag' };
  }
  const scope = definition.scope ?? 'pr';
  if (scope !== 'any' && (scope === 'twin') !== twinComment) {
    await reply(client, number, [
      `**\`/speckit ${command.name}\` was not run**: it works on ${scope === 'twin' ? 'a spec twin issue (label `speckit:spec`), not on a pull request' : 'an implementation pull request (`speckit/<folder>`), not on a spec twin issue'}.`,
      '',
      renderHelp(),
    ]);
    return { exitCode: 0, handled: true, command: command.name, rejected: 'wrong scope' };
  }
  if (twinComment) {
    const result = await TWIN_HANDLERS[command.name]({ client, env, actor, number, commentId, rootDir });
    log(`/speckit ${command.name} by @${actor} on #${number}: ${result.outcome}`);
    return { exitCode: 0, handled: true, command: command.name, ...result };
  }
  const target = await resolveTarget(client, env, number);
  if (target.reason) {
    await reply(client, number, `\`/speckit ${command.name}\` cannot run here: ${target.reason}.`);
    return { exitCode: 0, handled: true, command: command.name, rejected: target.reason };
  }
  if (env.SPECKIT_PUBLICATION_PULL && Number(env.SPECKIT_PUBLICATION_PULL) !== target.implementation.number) {
    await reply(client, number, 'The implementation pull request changed while the command waited for publication. Post the command again on the current implementation pull request.');
    return { exitCode: 0, handled: true, command: command.name, rejected: 'publication target changed' };
  }
  const handler = HANDLERS[command.name];
  const result = await handler({ client, env, actor, number, flags: command.flags, guidance: command.guidance.slice(0, 4000), now, ...target });
  log(`/speckit ${command.name} by @${actor}: ${result.outcome}`);
  return { exitCode: 0, handled: true, command: command.name, ...result };
}

async function handleRetry({ client, env, actor, number, flags, guidance, folder, implementation, twin }) {
  const force = flags.includes(RESTART_FORCE_FLAG);
  const state = await restartState(client, implementation);
  if (!force && (await refuseUnchangedRestart(client, { name: 'retry', number, implementation, folder, state }))) return { outcome: 'refused' };
  await retryImplementation(client, env, { implementation, twin, folder }, { lead: `Implementation retried by @${actor}`, effect: RETRY_EFFECT, guidance, state });
  if (number !== implementation.number) await reply(client, number, `Retried; the implementation continues on #${implementation.number}.`);
  return { outcome: 'retried' };
}

async function handleSync({ client, env, actor, number, guidance, implementation, twin }) {
  await requestSync(client, env, { implementation, twin }, { lead: `Required synchronization requested by @${actor}`, guidance });
  if (number !== implementation.number) await reply(client, number, `Synchronization requested; progress is reported on #${implementation.number}.`);
  return { outcome: 'sync requested' };
}

export async function restartState(client, implementation) {
  let base = null;
  try {
    base = await client.getBranchSha(baseBranchOf(implementation));
  } catch {}
  return { head: implementation.head.sha, base };
}

// Refuses a restart while the implementation is stopped and the last MAX_UNCHANGED_RESTARTS restarts left its branch
// where it is: another one would most likely stop the same way.
async function refuseUnchangedRestart(client, { name, number, implementation, folder, state }) {
  const stopped = (implementation.labels ?? []).some((label) => (label?.name ?? label) === ATTENTION_LABEL);
  if (!stopped) return false;
  const restarts = unchangedRestarts(await client.listIssueComments(implementation.number), state.head);
  if (restarts.length < MAX_UNCHANGED_RESTARTS) return false;
  const base = baseBranchOf(implementation);
  await reply(client, number, [
    `**\`/speckit ${name}\` was not run**: the implementation stopped again after each of the last ${restarts.length} restarts, and \`${implementationBranch(folder)}\` is still at \`${state.head.slice(0, 7)}\`. Restarting it unchanged would most likely stop the same way.`,
    '',
    `**Recommended next step**: Review the latest diagnosis on #${implementation.number}, apply its requested correction, and push it to \`${implementationBranch(folder)}\`.`,
    '',
    '- **Why**: Two restarts left the branch unchanged; another blind retry is unlikely to fix the cause.',
    '- **What it does**: A push continues the implementation automatically with a fresh attempt count and the corrected branch.',
    '',
    '<details><summary>Other options</summary>',
    '',
    `- If the diagnosis locates the cause on \`${base}\`, post \`/speckit author\` with its concrete findings to author a new spec for it.`,
    `- Only if the cause has already cleared (for example an outage has ended), insist with \`/speckit ${name} ${RESTART_FORCE_FLAG}\`; add the evidence after the command as guidance.`,
    '',
    '</details>',
  ]);
  return true;
}

async function handleAuthor({ client, env, actor, number, guidance, now, implementation, twin }) {
  const started = await startFixAuthoring(client, env, { twin, implementation, guidance, actor, now: now() });
  if (!started.started) {
    await reply(client, number, `\`/speckit author\` cannot start: ${neutralizeMarkers(started.reason)}.`);
    return { outcome: 'refused' };
  }
  if (number !== implementation.number) await reply(client, number, `A new spec is being authored; its progress is reported on #${implementation.number}.`);
  return { outcome: 'started' };
}

const HANDLERS = {
  retry: handleRetry,
  sync: handleSync,
  author: handleAuthor,
};

// The one next step for a twin whose implementation cannot be requested yet.
function implementNextStep(folder, computedStage, openChecklistItems) {
  if (['specified', 'planned'].includes(computedStage)) {
    return [
      `**Recommended next step**: Complete the spec with \`/speckit-author ${folder}\` in Copilot Chat (plan, tasks, analysis, and the publish checks; it pushes the spec at the end), then comment \`/speckit implement\` here again.`,
      '',
      '- **Why**: Implementation can only be requested for a spec with its task list (`tasked`).',
    ];
  }
  if (computedStage === 'tasked' && openChecklistItems > 0) {
    return [
      `**Recommended next step**: Resolve the open items in \`specs/${folder}/checklists/\`, merge the checked checklists to the default branch, and comment \`/speckit implement\` here again.`,
      '',
      '- **Why**: An open checklist item means the spec is not signed off yet.',
    ];
  }
  return [
    `**Recommended next step**: Continue the implementation that already started: tasks of \`specs/${folder}/tasks.md\` are checked on the default branch, so it is implemented locally (\`/speckit-implement ${folder}\`).`,
    '',
    '- **Why**: GitHub automation only starts specs whose tasks are all unchecked on the default branch.',
  ];
}

// `/speckit implement` on a spec twin: the only way to request an implementation. Checks the spec on the default
// branch (this job's checkout) like `speckit-orchestrate.mjs request`, records who requested it in a workflow comment
// (renderImplementRequested, read by resolveImplementRequester), sets the implement stage, and starts the scheduler.
async function handleImplement({ client, env, actor, number, commentId, rootDir }) {
  const issue = await client.getIssue(number);
  const folder = hasLabel(issue, TWIN_LABEL) ? parseTwinFolder(issue.body) : null;
  const refuse = async (reasons, next = []) => {
    await reply(client, number, [
      `**\`/speckit implement\` was not accepted**, @${actor}:`,
      '',
      ...reasons.map((reason) => `- ${reason}`),
      ...(next.length > 0 ? ['', ...next] : []),
    ]);
    return { outcome: 'refused', reasons };
  };
  if (!folder) return refuse(['this issue is not a spec twin']);
  if (issue.state !== 'open') return refuse(['the spec twin is closed']);
  const entry = readSpecFolder(rootDir, folder);
  if (!entry) {
    if (readSpecFolder(rootDir, folder, { archived: true })) return refuse([`the spec is already implemented and archived in \`${specDir(folder, { archived: true })}\``]);
    return refuse([`\`specs/${folder}\` does not exist on the default branch`], [`**Recommended next step**: Merge the spec folder to the default branch, wait for this twin to be updated, and comment \`/speckit implement\` here again.`]);
  }
  const computedStage = deriveStage(entry.artifacts, entry.tasks);
  const openBlockers = (await client.listBlockedBy(number)).filter((blocker) => blocker.state === 'open');
  const waits = openBlockers.length > 0
    ? `It waits for its open prerequisites ${openBlockers.map((blocker) => `#${blocker.number}`).join(', ')} and starts once they are implemented.`
    : 'It has no open prerequisites and starts as soon as an implementation slot is free.';
  if (isImplementRequested(issue, computedStage)) {
    const stage = hasLabel(issue, stageLabel(IMPLEMENT_STAGE)) ? IMPLEMENT_STAGE : IMPLEMENTING_STAGE;
    await reply(client, number, `**Already requested**: this spec is at \`${stageLabel(stage)}\`, so nothing changed. ${stage === IMPLEMENT_STAGE ? waits : 'Its implementation pull request is open; follow it there.'}`);
    return { outcome: 'already requested' };
  }
  const reasons = implementBlockers({ folder, computedStage, openChecklistItems: entry.openChecklistItems });
  if (reasons.length > 0) return refuse(reasons, implementNextStep(folder, computedStage, entry.openChecklistItems));
  await client.createComment(number, [
    renderImplementRequested({ by: actor, comment: Number.isInteger(commentId) && commentId > 0 ? commentId : null }),
    `**Implementation requested** by @${actor}: \`specs/${folder}\` is \`tasked\` and its checklists are checked on the default branch. ${waits} Its implementation pull request is opened by the scheduler and assigned to @${actor}.`,
  ].join('\n'));
  await updateIssueWithLabels(client, issue, stageLabelChange(issue, IMPLEMENT_STAGE));
  try {
    await client.dispatchWorkflow(ORCHESTRATE_WORKFLOW_FILE, defaultBranchOf(env), {});
  } catch {
    // The hourly scheduler run starts it.
  }
  return { outcome: 'requested', blockers: openBlockers.map((blocker) => blocker.number) };
}

const TWIN_HANDLERS = {
  implement: handleImplement,
};

export const COMMAND_DOCS_FILE = '.github/workflows/README.md';
export const COMMAND_DOCS_START = '<!-- speckit-commands:start -->';
export const COMMAND_DOCS_END = '<!-- speckit-commands:end -->';
const COMMAND_DOCS_REFRESH = 'node .github/scripts/speckit-commands.mjs docs --write';

export function renderCommandDocs(commands = PR_COMMANDS) {
  return [
    COMMAND_DOCS_START,
    `<!-- Generated from PR_COMMANDS in .github/scripts/speckit-implement-core.mjs; refresh with \`${COMMAND_DOCS_REFRESH}\`. -->`,
    '',
    renderCommandTable(commands),
    '',
    COMMAND_DOCS_END,
  ].join('\n');
}

// Replaces the generated block between the markers; the rest of the document stays as it is.
export function syncCommandDocs(text, commands = PR_COMMANDS) {
  const start = text.indexOf(COMMAND_DOCS_START);
  const end = text.indexOf(COMMAND_DOCS_END);
  if (start < 0 || end < start) throw new CommandUsageError(`${COMMAND_DOCS_FILE} has no ${COMMAND_DOCS_START} … ${COMMAND_DOCS_END} block.`);
  return text.slice(0, start) + renderCommandDocs(commands) + text.slice(end + COMMAND_DOCS_END.length);
}

function runDocs(args, { file = new URL('../workflows/README.md', import.meta.url), log }) {
  const unknown = args.filter((arg) => arg !== '--write');
  if (unknown.length > 0) throw new CommandUsageError(`Unknown docs argument: ${unknown.join(' ')}`);
  const label = typeof file === 'string' ? file : COMMAND_DOCS_FILE;
  const current = readFileSync(file, 'utf8');
  const eol = current.includes('\r\n') ? '\r\n' : '\n';
  const synced = syncCommandDocs(current.replace(/\r\n/g, '\n')).replace(/\n/g, eol);
  if (synced === current) {
    log(`${label}: the command table is current.`);
    return 0;
  }
  if (args.includes('--write')) {
    writeFileSync(file, synced);
    log(`${label}: the command table was refreshed.`);
    return 0;
  }
  log(`${label}: the command table is stale; run \`${COMMAND_DOCS_REFRESH}\`.`);
  return 1;
}

export async function main(argv, { env = process.env, log = console.log, client, docsFile } = {}) {
  const [command, ...args] = argv;
  if (command === 'docs') return runDocs(args, { file: docsFile, log });
  if (!['command', 'target'].includes(command)) throw new CommandUsageError('Usage: speckit-commands.mjs <command | target | docs [--write]> (command and target take their inputs from SPECKIT_* environment variables)');
  const github = client ?? createGitHubClient(env);
  if (command === 'target') {
    const pull = await runCommandTarget({ client: github, env });
    if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `pull=${pull}\n`);
    return 0;
  }
  const result = await runCommand({ client: github, env, log });
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `handled=${result.handled}\n`);
  return result.exitCode;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof CommandUsageError ? 2 : 1;
    },
  );
}
