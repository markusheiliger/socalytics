import { isCommandComment } from './openspec-change-commands.mjs';

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
