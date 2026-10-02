export const COMMAND_PREFIX = '/openspec';
export const WRITE_PERMISSIONS = Object.freeze(['admin', 'maintain', 'write']);
export const MAX_ANSWER_LENGTH = 2000;

const COMMANDS = new Set(['approve', 'retry', 'answer', 'abort']);

export function isCommandComment(body) {
  return typeof body === 'string' && body.trimStart().startsWith(COMMAND_PREFIX);
}

export function parseCommand(body) {
  if (!isCommandComment(body)) return null;
  const text = body.replace(/\r\n/g, '\n').trim();
  const match = text.match(/^\/openspec(?:[ \t]+([A-Za-z-]+))?(?:[ \t]+|\n|$)([\s\S]*)$/);
  if (!match) return { error: 'Unrecognized command. Use `/openspec approve`, `retry [guidance]`, `answer <text>`, or `abort`.' };
  const name = match[1]?.toLowerCase();
  const rest = match[2];
  if (!name || !COMMANDS.has(name)) {
    return { error: `Unknown command \`/openspec ${name ?? ''}\`. Use \`/openspec approve\`, \`retry [guidance]\`, \`answer <text>\`, or \`abort\`.` };
  }
  const argument = rest.trim();
  if (name === 'answer' || name === 'retry') {
    if (name === 'answer' && argument === '') return { error: '`/openspec answer` needs your answer after the command.' };
    if (argument.length > MAX_ANSWER_LENGTH) {
      return { error: `${name === 'answer' ? 'Answers' : 'Retry guidance'} must be at most ${MAX_ANSWER_LENGTH} characters.` };
    }
    return { name, text: argument === '' ? null : argument };
  }
  return { name, text: null };
}

export function isBotUser(user) {
  return user?.type === 'Bot' || /\[bot\]$/.test(user?.login ?? '');
}

export function pendingCommandComments(comments, cursor) {
  return comments
    .filter((comment) => comment.id > cursor)
    .filter((comment) => isCommandComment(comment.body))
    .filter((comment) => !isBotUser(comment.user))
    .sort((left, right) => left.id - right.id);
}

export function authorizeCommand(comment, permission) {
  if (!WRITE_PERMISSIONS.includes(permission)) {
    return { ok: false, reason: `@${comment.user.login} needs write access to the repository to run OpenSpec commands.` };
  }
  if (comment.updated_at && comment.created_at && comment.updated_at !== comment.created_at) {
    return { ok: false, reason: 'This command was edited before it was processed. Post the command again as a new comment.' };
  }
  return { ok: true };
}

export const BRANCH_PREFIX = 'openspec/';

export function isRunBranch(ref) {
  return typeof ref === 'string' && ref.startsWith(BRANCH_PREFIX);
}

export function classifyEvent(eventName, payload = {}) {
  switch (eventName) {
    case 'issues': {
      const labels = (payload.issue?.labels ?? []).map((label) => label.name ?? label);
      if (!labels.includes('openspec:change')) return { relevant: false, reason: 'not an OpenSpec issue twin' };
      return { relevant: true, reason: `issue #${payload.issue.number} ${payload.action}` };
    }
    case 'pull_request_target': {
      if (!isRunBranch(payload.pull_request?.head?.ref)) {
        return { relevant: false, reason: 'not an OpenSpec pull request' };
      }
      return { relevant: true, reason: `pull request #${payload.pull_request.number} ${payload.action}` };
    }
    case 'issue_comment': {
      if (!payload.issue?.pull_request) return { relevant: false, reason: 'comment is not on a pull request' };
      if (!isCommandComment(payload.comment?.body)) return { relevant: false, reason: 'comment is not an /openspec command' };
      return { relevant: true, reason: `command on #${payload.issue.number}` };
    }
    case 'push':
      return { relevant: true, reason: 'OpenSpec changes updated on the default branch' };
    case 'schedule':
      return { relevant: true, reason: 'watchdog' };
    case 'workflow_dispatch':
      return { relevant: true, reason: 'manual run' };
    default:
      return { relevant: false, reason: `unsupported event ${eventName}` };
  }
}
