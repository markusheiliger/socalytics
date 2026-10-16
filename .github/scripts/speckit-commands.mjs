import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { canWrite } from './speckit-prepare.mjs';
import { closeUnfinishedDiagnosis, diagnosisRunEnded, startDiagnosis } from './speckit-diagnose.mjs';
import { startFixAuthoring } from './speckit-fix.mjs';
import { createGitHubClient } from './speckit-script-runtime.mjs';
import {
  ATTENTION_LABEL,
  BOT_LOGIN,
  CHECK_DIAGNOSING,
  CHECK_RUN_NAME,
  DIAGNOSIS_COMMENT_MARKER,
  DIAGNOSIS_STALE_MS,
  MAX_UNCHANGED_RESTARTS,
  PR_COMMANDS,
  RESTART_FORCE_FLAG,
  baseBranchOf,
  dispatchImplementation,
  describeCommandEffect,
  folderOfBranch,
  implementationBranch,
  latestCheckRun,
  neutralizeMarkers,
  renderResumeComment,
  renderRetryComment,
  renderSyncComment,
  setAttention,
  specTitleOfPull,
  unresolvedRepair,
  unchangedRestarts,
} from './speckit-implement-core.mjs';

export class CommandUsageError extends Error {}

const COMMAND_NAMES = PR_COMMANDS.map((command) => command.name);

// Parses a pull request comment like "/speckit resume use the existing helper". The command must be the first word;
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

// Starts a new attempt window: the resume or retry comment persists the request (and the guidance), so a replaced
// pending run cannot lose it; starting the implementation chain only makes it happen right away.
async function resumeImplementation(client, env, { implementation, twin, folder }, lead, guidance = '', render = renderResumeComment, state = null) {
  const repair = unresolvedRepair(await client.listIssueComments(implementation.number));
  if (repair) {
    const command = render === renderResumeComment ? 'resume' : 'retry';
    lead = `${lead.split(';')[0]}; ${describeCommandEffect(`/speckit ${command}${guidance ? ` ${guidance}` : ''}`, { baseBranch: baseBranchOf(implementation), continuation: { repair } })}`;
  }
  await client.createComment(implementation.number, render(lead, guidance, state));
  await setAttention(client, implementation.number, false);
  try {
    await dispatchImplementation(client, env, { twin, pull: implementation.number, title: specTitleOfPull(implementation) });
  } catch {
    // The resume comment persists the request; the hourly scheduler run continues the implementation.
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

async function handleDiagnose({ client, env, actor, number, argument, now, folder, implementation, twin }) {
  const checks = await client.listCheckRuns(implementation.head.sha, CHECK_RUN_NAME);
  const latest = latestCheckRun(checks);
  if (latest?.external_id === CHECK_DIAGNOSING && latest.status !== 'completed') {
    const fresh = now() - Date.parse(latest.started_at ?? latest.created_at ?? 0) < DIAGNOSIS_STALE_MS;
    if (fresh && !(await diagnosisRunEnded(client, twin, latest))) {
      await reply(client, number, 'A diagnosis is already running; its findings will be posted on the implementation pull request.');
      return { outcome: 'already running' };
    }
    await closeUnfinishedDiagnosis(client, latest);
  }
  const comments = await client.listIssueComments(implementation.number);
  const previous = comments.filter((comment) => comment.user?.login === BOT_LOGIN && String(comment.body ?? '').startsWith(DIAGNOSIS_COMMENT_MARKER)).at(-1);
  const started = await startDiagnosis(client, env, {
    twin,
    pull: implementation.number,
    folder,
    head: implementation.head.sha,
    guidance: argument,
    // The previous diagnosis is context, for example when the guidance answers its questions.
    previous: previous ? previous.id : '',
    actor,
  });
  if (!started.started) {
    await reply(client, number, `The diagnosis could not start: ${neutralizeMarkers(started.error)}`);
    return { outcome: 'not started' };
  }
  await reply(client, number, [
    `${previous ? 'A new diagnosis' : 'A diagnosis'} was started by @${actor}${argument ? ' with your guidance' : ''}. The implementation waits; the findings will be posted on #${implementation.number}.`,
  ]);
  return { outcome: 'started' };
}

async function handleResume({ client, env, actor, number, argument, folder, implementation, twin }) {
  const { force, guidance } = restartArgument(argument);
  const state = await restartState(client, implementation);
  if (!force && (await refuseUnchangedRestart(client, { name: 'resume', number, implementation, folder, state }))) return { outcome: 'refused' };
  await resumeImplementation(client, env, { implementation, twin, folder }, `Implementation resumed by @${actor}; the base branch is merged first, then what stopped restarts with a fresh attempt count.`, guidance, renderResumeComment, state);
  if (number !== implementation.number) await reply(client, number, `Resumed; the implementation continues on #${implementation.number}.`);
  return { outcome: 'resumed' };
}

async function handleRetry({ client, env, actor, number, argument, folder, implementation, twin }) {
  const { force, guidance } = restartArgument(argument);
  const state = await restartState(client, implementation);
  if (!force && (await refuseUnchangedRestart(client, { name: 'retry', number, implementation, folder, state }))) return { outcome: 'refused' };
  await resumeImplementation(client, env, { implementation, twin, folder }, `Implementation retried by @${actor}; what stopped restarts with a fresh attempt count, without merging the base branch for it.`, guidance, renderRetryComment, state);
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

// `--force` as the first word of a retry or resume skips the unchanged-restart guard; the rest is the guidance.
function restartArgument(argument) {
  const match = argument.match(/^--force(?:\s+|$)([\s\S]*)$/i);
  return { force: Boolean(match), guidance: (match ? match[1] : argument).trim().slice(0, 4000) };
}

async function restartState(client, implementation) {
  let base = null;
  try {
    base = await client.getBranchSha(baseBranchOf(implementation));
  } catch {
    // Without the base branch head, a resume is not refused for an unchanged base branch.
  }
  return { head: implementation.head.sha, base };
}

// Refuses a restart while the implementation is stopped and the last MAX_UNCHANGED_RESTARTS restarts left its branch
// where it is: another one would most likely stop the same way. A resume is still accepted when the base branch moved
// since the last restart, because it merges those commits first.
async function refuseUnchangedRestart(client, { name, number, implementation, folder, state }) {
  const stopped = (implementation.labels ?? []).some((label) => (label?.name ?? label) === ATTENTION_LABEL);
  if (!stopped) return false;
  const restarts = unchangedRestarts(await client.listIssueComments(implementation.number), state.head);
  if (restarts.length < MAX_UNCHANGED_RESTARTS) return false;
  if (name === 'resume' && state.base && restarts[0].base && state.base !== restarts[0].base) return false;
  const base = baseBranchOf(implementation);
  await reply(client, number, [
    `**\`/speckit ${name}\` was not run**: the implementation stopped again after each of the last ${restarts.length} restarts, and \`${implementationBranch(folder)}\` is still at \`${state.head.slice(0, 7)}\`${name === 'resume' ? ` while \`${base}\` has no new commits` : ''}. Restarting it unchanged would most likely stop the same way. Change something first:`,
    '',
    `- **Push** the change the diagnosis asks for to \`${implementationBranch(folder)}\`; a push continues the implementation by itself.`,
    '- **Diagnose again** with what you know, to find out why the previous restarts did not help: `/speckit diagnose <what you know>`.',
    `- **Fix the cause on \`${base}\`** with \`/speckit fix <what to fix>\`, or post \`/speckit resume\` once the fix has landed there.`,
    '',
    `If you are sure a restart helps now (for example an outage has ended), insist with \`/speckit ${name} ${RESTART_FORCE_FLAG} <guidance>\`.`,
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
  diagnose: handleDiagnose,
  retry: handleRetry,
  resume: handleResume,
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
