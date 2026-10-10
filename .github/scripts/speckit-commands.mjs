import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { GitHubClient } from './speckit-prepare-github.mjs';
import { canWrite } from './speckit-prepare.mjs';
import { closeUnfinishedDiagnosis, diagnosisRunEnded, startDiagnosis } from './speckit-diagnose.mjs';
import {
  BOT_LOGIN,
  CHECK_DIAGNOSING,
  CHECK_RUN_NAME,
  DIAGNOSIS_COMMENT_MARKER,
  DIAGNOSIS_STALE_MS,
  PR_COMMANDS,
  dispatchImplementation,
  folderOfBranch,
  implementationBranch,
  latestCheckRun,
  neutralizeMarkers,
  renderResumeComment,
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
    'An amendment pull request (`speckit-amend/<folder>`) needs no commands: merge it to apply the amendment, comment or submit a review (*Comment* or *Request changes*) to have it reworked, or close it to discard it.',
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

// Starts a new attempt window: the resume comment persists the request (and the guidance), so a replaced pending
// run cannot lose it; starting the implementation chain only makes it happen right away.
async function resumeImplementation(client, env, { implementation, twin }, lead, guidance = '') {
  await client.createComment(implementation.number, renderResumeComment(lead, guidance));
  try {
    await dispatchImplementation(client, env, { twin, pull: implementation.number });
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
    notes: argument,
    // The previous diagnosis is context, for example when the notes answer its questions.
    previous: previous ? previous.id : '',
    actor,
  });
  if (!started.started) {
    await reply(client, number, `The diagnosis could not start: ${neutralizeMarkers(started.error)}`);
    return { outcome: 'not started' };
  }
  await reply(client, number, [
    `${previous ? 'A new diagnosis' : 'A diagnosis'} was started by @${actor}${argument ? ' with your notes' : ''}. The implementation waits; the findings will be posted on #${implementation.number}.`,
  ]);
  return { outcome: 'started' };
}

async function handleResume({ client, env, actor, number, argument, implementation, twin }) {
  const guidance = argument.slice(0, 4000);
  await resumeImplementation(client, env, { implementation, twin }, `Implementation resumed by @${actor}; the stopped step restarts with a fresh attempt count.`, guidance);
  if (number !== implementation.number) await reply(client, number, `Resumed; the implementation continues on #${implementation.number}.`);
  return { outcome: 'resumed' };
}

async function handleSync({ client, env, actor, number, folder, implementation, twin }) {
  const defaultBranch = env.SPECKIT_BRANCH || 'main';
  const branch = implementationBranch(folder);
  let result;
  try {
    result = await client.mergeBranch(branch, defaultBranch, `Merge ${defaultBranch} into ${branch} (/speckit sync by @${actor})`);
  } catch (error) {
    await reply(client, number, [
      `\`${defaultBranch}\` could not be merged into \`${branch}\`: ${neutralizeMarkers(error.message.slice(0, 500))}`,
      '',
      'Merge it locally and push (the implementation continues automatically). GitHub refuses merges that bring in workflow changes for the workflow token.',
    ]);
    return { outcome: 'failed' };
  }
  if (result === 'conflict') {
    await reply(client, number, [
      `\`${defaultBranch}\` conflicts with \`${branch}\`, so it was not merged.`,
      '',
      'Merge it locally, resolve the conflicts, and push (the implementation continues automatically), or walk through it with the skill `/speckit-gha-diagnose`.',
    ]);
    return { outcome: 'conflict' };
  }
  await resumeImplementation(client, env, { implementation, twin }, result === 'up-to-date'
    ? `\`${branch}\` already contains \`${defaultBranch}\`; resumed by @${actor} with a fresh attempt count.`
    : `@${actor} merged \`${defaultBranch}\` into \`${branch}\`; the stopped step restarts with a fresh attempt count.`);
  if (number !== implementation.number) await reply(client, number, `Synced; the implementation continues on #${implementation.number}.`);
  return { outcome: result };
}

const HANDLERS = {
  diagnose: handleDiagnose,
  resume: handleResume,
  sync: handleSync,
};

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
  if (command !== 'command') throw new CommandUsageError('Usage: speckit-commands.mjs command (inputs come from SPECKIT_* environment variables)');
  const result = await runCommand({ client: client ?? githubClient(env), env, log });
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
