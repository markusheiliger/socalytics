import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { canWrite } from './speckit-prepare.mjs';
import { startFixAuthoring } from './speckit-fix.mjs';
import { createGitHubClient } from './speckit-script-runtime.mjs';
import {
  ATTENTION_LABEL,
  BOT_LOGIN,
  MAX_UNCHANGED_RESTARTS,
  PR_COMMANDS,
  RESTART_FORCE_FLAG,
  baseBranchOf,
  dispatchImplementation,
  describeCommandEffect,
  folderOfBranch,
  implementationBranch,
  neutralizeMarkers,
  renderRetryComment,
  renderSyncComment,
  setAttention,
  specTitleOfPull,
  unresolvedRepair,
  unchangedRestarts,
} from './speckit-implement-core.mjs';

export class CommandUsageError extends Error {}

const COMMAND_NAMES = PR_COMMANDS.map((command) => command.name);

// Parses a pull request comment like "/speckit retry use the existing helper". The command must be the first word;
// the rest of the comment (first line and following lines) is its argument.
export function parseCommand(body) {
  const text = String(body ?? '').replace(/^\s+/, '');
  const match = text.match(/^\/speckit(?:[ \t]+([A-Za-z-]+))?(?=\s|$)([\s\S]*)$/);
  if (!match) return null;
  const name = (match[1] ?? 'help').toLowerCase();
  return { name: COMMAND_NAMES.includes(name) ? name : 'unknown', given: match[1] ?? '', argument: match[2].trim() };
}

export function renderHelp() {
  return [
    '**Spec Kit commands** — comment one of these on an implementation pull request (`speckit/<folder>`). Only people with write access can use them; the command must be the first word of the comment.',
    '',
    '| Command | What it does |',
    '| --- | --- |',
    ...PR_COMMANDS.map((command) => `| \`${command.usage}\` | ${command.help} |`),
    '',
    'An amendment pull request (`speckit-amend/<folder>`) or fix spec pull request (`speckit-fix/<folder>`) needs no commands: merge it to apply it, comment or submit a review (*Comment* or *Request changes*) to have it reworked, or close it to discard it.',
    '',
    'These are pull request comment commands, not skills. To walk through a stop locally in VS Code, use the skill `/speckit-gha-diagnose`.',
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

// Resolve an amendment comment to its implementation before acquiring the publication lock.
export async function runCommandTarget({ client, env }) {
  const number = Number(env.SPECKIT_PULL);
  if (!Number.isInteger(number) || number <= 0) throw new CommandUsageError('Expected SPECKIT_PULL.');
  const command = parseCommand(env.SPECKIT_COMMENT_BODY);
  if (!command || ['help', 'unknown'].includes(command.name)) return number;
  const target = await resolveTarget(client, env, number);
  return target.implementation?.number ?? number;
}

// Starts a new attempt window: the retry comment persists the request (and the guidance), so a replaced
// pending run cannot lose it; starting the implementation chain only makes it happen right away.
async function retryImplementation(client, env, { implementation, twin, folder }, lead, guidance = '', state = null) {
  const repair = unresolvedRepair(await client.listIssueComments(implementation.number));
  if (repair) {
    lead = `${lead.split(';')[0]}; ${describeCommandEffect(`/speckit retry${guidance ? ` ${guidance}` : ''}`, { baseBranch: baseBranchOf(implementation), continuation: { repair } })}`;
  }
  await client.createComment(implementation.number, renderRetryComment(lead, guidance, state));
  await setAttention(client, implementation.number, false);
  try {
    await dispatchImplementation(client, env, { twin, pull: implementation.number, title: specTitleOfPull(implementation) });
  } catch {
    // The retry comment persists the request; the hourly scheduler run continues the implementation.
  }
}

// Command router (issue_comment): validates the author and the command, reacts, and performs the command. Comments
// by bots never get here; the workflow filters them.
export async function runCommand({ client, env, log, now = Date.now }) {
  const number = Number(env.SPECKIT_PULL);
  const commentId = Number(env.SPECKIT_COMMENT_ID);
  const actor = String(env.SPECKIT_ACTOR ?? '');
  const command = parseCommand(env.SPECKIT_COMMENT_BODY);
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
  const result = await handler({ client, env, actor, number, argument: command.argument, now, ...target });
  log(`/speckit ${command.name} by @${actor}: ${result.outcome}`);
  return { exitCode: 0, handled: true, command: command.name, ...result };
}

async function handleRetry({ client, env, actor, number, argument, folder, implementation, twin }) {
  const { force, guidance } = restartArgument(argument);
  const state = await restartState(client, implementation);
  if (!force && (await refuseUnchangedRestart(client, { name: 'retry', number, implementation, folder, state }))) return { outcome: 'refused' };
  await retryImplementation(client, env, { implementation, twin, folder }, `Implementation retried by @${actor}; what stopped restarts with a fresh attempt count while preserving the current phase base.`, guidance, state);
  if (number !== implementation.number) await reply(client, number, `Retried; the implementation continues on #${implementation.number}.`);
  return { outcome: 'retried' };
}

async function handleSync({ client, env, actor, number, argument, implementation, twin }) {
  await client.createComment(implementation.number, renderSyncComment(
    `Required synchronization requested by @${actor}; merge \`${baseBranchOf(implementation)}\` and pass full verification before continuing. Pending repair scope and attempt limits are unchanged.`,
    argument,
    await restartState(client, implementation),
  ));
  await setAttention(client, implementation.number, false);
  try {
    await dispatchImplementation(client, env, { twin, pull: implementation.number, title: specTitleOfPull(implementation) });
  } catch {
    // The durable request survives a failed dispatch or replacement by another pending run.
  }
  if (number !== implementation.number) await reply(client, number, `Synchronization requested; progress is reported on #${implementation.number}.`);
  return { outcome: 'sync requested' };
}

// `--force` as the first word of a retry skips the unchanged-restart guard; the rest is the guidance.
function restartArgument(argument) {
  const match = argument.match(/^--force(?:\s+|$)([\s\S]*)$/i);
  return { force: Boolean(match), guidance: (match ? match[1] : argument).trim().slice(0, 4000) };
}

async function restartState(client, implementation) {
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
    `- If the diagnosis locates the cause on \`${base}\`, post \`/speckit fix\` with its concrete findings to author a prerequisite fix spec.`,
    `- Only if the cause has already cleared (for example an outage has ended), insist with \`/speckit ${name} ${RESTART_FORCE_FLAG}\`; add the evidence after the command as guidance.`,
    '',
    '</details>',
  ]);
  return true;
}

async function handleFix({ client, env, actor, number, argument, now, implementation, twin }) {
  const started = await startFixAuthoring(client, env, { twin, implementation, guidance: argument.slice(0, 4000), actor, now: now() });
  if (!started.started) {
    await reply(client, number, `\`/speckit fix\` cannot start: ${neutralizeMarkers(started.reason)}.`);
    return { outcome: 'refused' };
  }
  if (number !== implementation.number) await reply(client, number, `A fix spec is being authored; its progress is reported on #${implementation.number}.`);
  return { outcome: 'started' };
}

const HANDLERS = {
  retry: handleRetry,
  sync: handleSync,
  fix: handleFix,
};

export async function main(argv, { env = process.env, log = console.log, client } = {}) {
  const [command] = argv;
  if (!['command', 'target'].includes(command)) throw new CommandUsageError('Usage: speckit-commands.mjs <command | target> (inputs come from SPECKIT_* environment variables)');
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
