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
  if (!match) return { error: 'Unrecognized command. Use `/openspec approve`, `retry`, `answer <text>`, or `abort`.' };
  const name = match[1]?.toLowerCase();
  const rest = match[2];
  if (!name || !COMMANDS.has(name)) {
    return { error: `Unknown command \`/openspec ${name ?? ''}\`. Use \`/openspec approve\`, \`retry\`, \`answer <text>\`, or \`abort\`.` };
  }
  const argument = rest.trim();
  if (name === 'answer') {
    if (argument === '') return { error: '`/openspec answer` needs your answer after the command.' };
    if (argument.length > MAX_ANSWER_LENGTH) {
      return { error: `Answers must be at most ${MAX_ANSWER_LENGTH} characters.` };
    }
    return { name, text: argument };
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
