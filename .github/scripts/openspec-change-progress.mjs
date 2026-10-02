import {
  describeActivity,
  decideNext,
  operationLabel,
  shortTaskTitle,
} from './openspec-change-state.mjs';

const STAGES = Object.freeze(['apply', 'verify', 'sync', 'archive', 'merge']);
const STAGE_NAMES = Object.freeze({
  apply: 'Apply',
  verify: 'Verify',
  sync: 'Sync specs',
  archive: 'Archive',
  merge: 'Merge',
});
const LOG_MARKER = /<!-- openspec:log change=([a-z0-9-]+) n=(\d+) event=([^\s]+) -->/;
const OVERVIEW_MARKER = (change) => `<!-- openspec:overview change=${change} -->`;

export function repositoryUrl({ serverUrl = 'https://github.com', owner, repo }) {
  return `${serverUrl}/${owner}/${repo}`;
}

export function commentUrl(context, pr, commentId) {
  return `${repositoryUrl(context)}/pull/${pr}#issuecomment-${commentId}`;
}

export function shortSha(sha) {
  return sha.slice(0, 7);
}

function commitLink(context, sha) {
  return `[\`${shortSha(sha)}\`](${repositoryUrl(context)}/commit/${sha})`;
}

function compareLink(context, before, after) {
  if (before === after) return commitLink(context, after);
  return `[\`${shortSha(before)}…${shortSha(after)}\`](${repositoryUrl(context)}/compare/${before}...${after})`;
}

export function sanitizeAgentText(text, context, maxLength = 500) {
  const repoPrefix = repositoryUrl(context);
  let value = String(text ?? '').replace(/\r\n/g, '\n').trim();
  value = value
    .replace(/<!--|-->/g, '')
    .replace(/```/g, "'''")
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/^(\s*)#{1,6}\s*/gm, '$1')
    .replace(/@(?=[A-Za-z0-9])/g, '@\u200b')
    .replace(/!?\[([^\]]*)\]\(([^)\s]+)[^)]*\)/g, (match, label, url) => (
      url.startsWith(repoPrefix) ? match.replace(/^!/, '') : label
    ))
    .replace(/(?<!\]\()https?:\/\/[^\s)]+/g, (url) => (url.startsWith(repoPrefix) ? url : `\`${url}\``));
  if (value.length > maxLength) value = `${value.slice(0, maxLength - 1).trimEnd()}…`;
  return value;
}

function quote(text) {
  return text.split('\n').map((line) => `> ${line}`).join('\n');
}

export function renderLogMarker({ change, n, event }) {
  return `<!-- openspec:log change=${change} n=${n} event=${event} -->`;
}

export function parseLogMarker(body) {
  const match = String(body ?? '').match(LOG_MARKER);
  return match ? { change: match[1], n: Number(match[2]), event: match[3] } : null;
}

export function nextLogNumber(comments, change) {
  return comments
    .map((comment) => parseLogMarker(comment.body))
    .filter((marker) => marker?.change === change)
    .reduce((max, marker) => Math.max(max, marker.n), 0) + 1;
}

export function findLogComment(comments, change, event) {
  return comments.find((comment) => {
    const marker = parseLogMarker(comment.body);
    return marker?.change === change && marker.event === event;
  }) ?? null;
}

export function isOverviewComment(body, change) {
  return String(body ?? '').includes(OVERVIEW_MARKER(change));
}

export function renderLogEntry({
  change,
  n,
  event,
  emoji,
  title,
  text = [],
  agent = null,
  evidence = [],
  next = null,
  details = null,
}) {
  const parts = [`### ${emoji} #${n} · ${title}`];
  const paragraphs = Array.isArray(text) ? text : [text];
  if (paragraphs.length > 0) parts.push(paragraphs.join('\n\n'));
  if (agent) parts.push(quote(`**Agent:** ${agent}`));
  if (evidence.length > 0) parts.push(evidence.join(' · '));
  if (next) parts.push(`**Next:** ${next}`);
  if (details && details.lines.length > 0) {
    parts.push(`<details><summary>${details.summary}</summary>\n\n${details.lines.join('\n')}\n\n</details>`);
  }
  parts.push(renderLogMarker({ change, n, event }));
  return `${parts.join('\n\n')}\n`;
}

export function sessionLabel(session) {
  return session.runtime === 'actions' ? 'Agent run' : 'Agent session';
}

function sessionEvidence(context, current, afterSha = null) {
  const evidence = [];
  if (current.session?.url) evidence.push(`[${sessionLabel(current.session)}](${current.session.url})`);
  evidence.push(afterSha ? compareLink(context, current.startSha, afterSha) : `from ${commitLink(context, current.startSha)}`);
  return evidence;
}

function taskPhrase(current) {
  return current.task
    ? `${operationLabel(current.operation, current.task)} · ${current.task.title}`
    : operationLabel(current.operation);
}

function findingCountText(findings) {
  const parts = [];
  if (findings.critical) parts.push(`${findings.critical} critical`);
  if (findings.warning) parts.push(`${findings.warning} warning${findings.warning === 1 ? '' : 's'}`);
  if (findings.suggestion) parts.push(`${findings.suggestion} suggestion${findings.suggestion === 1 ? '' : 's'}`);
  return parts.length > 0 ? parts.join(', ') : 'no findings';
}

function findingLines(findings, context) {
  const lines = [];
  for (const severity of ['critical', 'warning', 'suggestion']) {
    const items = findings.items.filter((item) => item.severity === severity);
    if (items.length === 0) continue;
    lines.push(`**${severity[0].toUpperCase()}${severity.slice(1)}**`, '');
    for (const item of items) lines.push(`- ${sanitizeAgentText(item.text, context, 300)}`);
    lines.push('');
  }
  return lines;
}

export function admittedEntry({ state, context, blockers = [] }) {
  const blockerText = blockers.length > 0
    ? `All blockers are archived on \`${state.base.ref}\` (${blockers.map((number) => `#${number}`).join(', ')}).`
    : 'No blockers.';
  return {
    event: 'admit',
    emoji: '📥',
    title: 'Started',
    text: [
      `Requested by \`${state.requestedBy}\` on #${state.issue}. ${blockerText}`,
      `Created branch \`${state.branch}\` from ${commitLink(context, state.base.sha)} on \`${state.base.ref}\`.`,
    ],
    next: 'Start the first task.',
  };
}

export function sessionStartedEntry({ state, context }) {
  const { current } = state;
  const attempt = current.attempt > 1 ? ` (attempt ${current.attempt})` : '';
  return {
    event: `session:${current.session.id}`,
    emoji: '▶️',
    title: `${taskPhrase(current)}: running${attempt}`,
    text: `${current.session.runtime === 'actions' ? 'A new agentic workflow run' : 'A new Copilot agent session'} is working on ${current.task ? `task ${current.task.id}` : `the ${current.operation} step`}.`,
    evidence: sessionEvidence(context, current),
    next: 'Wait for the session to push its checkpoint.',
  };
}

export function sessionFinishedEntry({ before, after, result, checkpoint = null, checkpointSha = null, context }) {
  const current = before.current;
  const base = {
    event: `session:${current.session.id}`,
    evidence: sessionEvidence(context, current, checkpointSha ?? after.headSha),
    agent: checkpoint ? sanitizeAgentText(checkpoint.summary, context) : null,
  };
  const validation = checkpoint ? [`Validation: ${sanitizeAgentText(checkpoint.validation, context, 300)}`] : [];
  const attemptNote = current.attempt > 1 ? ` (attempt ${current.attempt})` : '';
  switch (result.kind) {
    case 'credited': {
      if (current.operation === 'verify') {
        return {
          ...base,
          emoji: '✅',
          title: `${taskPhrase(current)}: no findings`,
          text: ['Verification passed with no findings, so specs are synchronized next.', ...validation],
          next: describeNext(after),
        };
      }
      return {
        ...base,
        emoji: '✅',
        title: `${taskPhrase(current)}: done${attemptNote}`,
        text: [
          current.task
            ? `Task ${current.task.id} is complete and was checked off by the agent.`
            : `The ${current.operation} step is complete.`,
          ...validation,
        ],
        next: describeNext(after),
      };
    }
    case 'retry': {
      const { feedback } = result;
      return {
        ...base,
        emoji: '⚠️',
        title: `${taskPhrase(current)}: retrying`,
        text: [`${sanitizeAgentText(result.reason, context, 500)} A fresh session will continue from the latest commit.`, ...validation],
        next: `Retry (attempt ${result.nextAttempt}).`,
        details: failureDetails(feedback, result.reason, 'Details passed to the next attempt'),
      };
    }
    case 'gate': {
      if (result.gate === 'decision') {
        return {
          ...base,
          emoji: '❓',
          title: `${taskPhrase(current)}: decision needed`,
          text: ['The agent stopped because it needs a decision. See the question below.'],
          next: 'Answer the question.',
        };
      }
      if (result.gate === 'review') {
        return {
          ...base,
          emoji: '🔎',
          title: `${taskPhrase(current)}: ${findingCountText(result.findings)}`,
          text: ['Verification finished with findings that need a human review.', ...validation],
          next: 'Review the findings.',
        };
      }
      if (result.gate === 'merge') {
        return {
          ...base,
          emoji: '✅',
          title: `${taskPhrase(current)}: done`,
          text: ['The change was moved to the archive on this branch.', ...validation],
          next: describeNext(after),
        };
      }
      return {
        ...base,
        emoji: '⛔',
        title: `${taskPhrase(current)}: stopped`,
        text: [sanitizeAgentText(result.reason, context, 500), ...validation],
        next: 'A human needs to decide how to continue.',
        details: failureDetails(result.feedback, result.reason, 'Failure details'),
      };
    }
    default:
      throw new Error(`Cannot render session result ${result.kind}`);
  }
}

function failureDetails(feedback, reason, summary) {
  if (!feedback || feedback === reason) return null;
  return { summary, lines: ['```text', feedback.replace(/```/g, "'''").slice(-3000), '```'] };
}

export function gateEntry({ state, context }) {
  const { gate } = state;
  const mention = `@${state.requestedBy}`;
  const where = gate.operation ? `${operationLabel(gate.operation, gate.task ?? null)}${gate.task ? ` · ${gate.task.title}` : ''}` : 'Merge';
  const commands = gate.commands.map((command) => {
    if (command === 'answer') return '`/openspec answer <text>`';
    if (command === 'retry' && gate.kind === 'failure') return '`/openspec retry [guidance for the agent]`';
    return `\`/openspec ${command}\``;
  }).join(' · ');
  const event = `gate:${gate.openedAt}`;
  if (gate.kind === 'decision') {
    return {
      event,
      emoji: '❓',
      title: `${where}: decision needed · ${mention}`,
      text: [quote(`**Agent asks:** ${sanitizeAgentText(gate.question, context, 1000)}`), `Reply with ${commands}.`],
    };
  }
  if (gate.kind === 'review') {
    return {
      event,
      emoji: '🔎',
      title: `Review needed · ${mention}`,
      text: [
        `Verification found ${findingCountText(gate.findings)} and no critical issues. Review them before specs are synchronized.`,
        'Reply `/openspec approve` to accept them and continue, push fixes and reply `/openspec retry` to verify again, or reply `/openspec abort` to stop.',
      ],
      details: { summary: 'Findings', lines: findingLines(gate.findings, context) },
    };
  }
  if (gate.kind === 'merge') {
    return {
      event,
      emoji: '🏁',
      title: `Archived: ready for human merge · ${mention}`,
      text: [
        'All tasks are done, verification passed, specs are synchronized, and the change is archived on this branch.',
        'Mark this pull request ready for review, review it, and merge it. The archive reaches the default branch with the merge.',
      ],
    };
  }
  return {
    event,
    emoji: '⛔',
    title: `${where}: stopped · ${mention}`,
    text: [sanitizeAgentText(gate.reason, context, 1000), `Reply with ${commands}.`],
    details: gate.findings ? { summary: 'Findings', lines: findingLines(gate.findings, context) } : null,
  };
}

export function commandEntry({ comment, accepted, message, after, context }) {
  return {
    event: `command:${comment.id}`,
    emoji: accepted ? '💬' : '🚫',
    title: accepted ? 'Decision recorded' : 'Command not accepted',
    text: sanitizeAgentText(message, context, 2500).replace(/^@\u200b/, '@'),
    evidence: [`[Comment](${commentUrl(context, after.pr, comment.id)})`],
    next: accepted ? describeNext(after) : null,
  };
}

export function humanPushEntry({ before, after, authors = [], context }) {
  const by = authors.length > 0 ? ` by ${authors.map((login) => `\`${login}\``).join(', ')}` : '';
  return {
    event: `push:${after.headSha}`,
    emoji: '🧑‍💻',
    title: 'New commits on the branch',
    text: `The branch moved${by} while no agent session was running. The next step starts from the new commit.`,
    evidence: [compareLink(context, before.headSha, after.headSha)],
  };
}

export function closedEntry({ state, context, archivePath = null }) {
  if (state.outcome === 'merged') {
    return {
      event: 'end:merged',
      emoji: '🎉',
      title: 'Merged',
      text: [
        `Merged into \`${state.base.ref}\`.${archivePath ? ` The change is archived at \`${archivePath}\`.` : ''} #${state.issue} is closed as completed.`,
      ],
    };
  }
  if (state.outcome === 'aborted') {
    return {
      event: 'end:aborted',
      emoji: '🛑',
      title: 'Aborted',
      text: `Processing stopped. To start again, add the \`openspec:enqueued\` label to #${state.issue}.`,
    };
  }
  return {
    event: 'end:closed',
    emoji: '🛑',
    title: 'Closed without merging',
    text: `The pull request was closed without merging, so processing stopped. To start again, add the \`openspec:enqueued\` label to #${state.issue}.`,
    evidence: [commitLink(context, state.headSha)],
  };
}

export function describeNext(state, tasks = null) {
  if (state.status === 'closed') return 'Nothing. Processing has ended.';
  if (state.status === 'gated') {
    return {
      decision: 'Answer with `/openspec answer <text>`, or `/openspec approve` to accept the agent\'s recommendation.',
      review: 'Review the findings, then `/openspec approve` to continue, or push fixes and `/openspec retry`.',
      failure: 'Fix the cause if needed, then `/openspec retry`, or `/openspec abort`.',
      merge: 'Mark this pull request ready for review, review it, and merge it.',
    }[state.gate.kind];
  }
  if (state.status !== 'ready') return 'Wait for the agent session to push its checkpoint.';
  if (state.phase === 'apply' && !Array.isArray(tasks) && !state.current) return 'Start the next task.';
  const decision = decideNext(state, { tasks });
  if (decision.action !== 'dispatch') return 'Nothing to do right now.';
  const attempt = decision.attempt > 1 ? ` (attempt ${decision.attempt})` : '';
  return `Start ${operationLabel(decision.operation, decision.task)}${decision.task ? ` · ${decision.task.title}` : ''}${attempt}.`;
}

function stageRows(state, tasks) {
  const doneTasks = tasks ? tasks.filter((task) => task.completed).length : null;
  const credited = new Set(state.credited.map((entry) => entry.operation));
  const index = state.phase === 'done' ? STAGES.length : STAGES.indexOf(state.phase);
  return STAGES.map((stage, stageIndex) => {
    let icon;
    if (state.phase === 'aborted') {
      icon = credited.has(stage) && stage !== 'apply' ? '✅' : '⬜';
    } else if (stageIndex < index || (stage === 'merge' && state.outcome === 'merged')) {
      icon = '✅';
    } else if (stageIndex === index) {
      icon = state.status === 'gated' && state.gate.kind !== 'merge' ? '⏸️' : '🟡';
      if (stage === 'merge') icon = '👀';
    } else {
      icon = '⬜';
    }
    let status = '';
    if (stage === 'apply' && tasks) status = `${doneTasks} / ${tasks.length} tasks`;
    if (stageIndex === index && state.status !== 'closed') status = [status, describeActivity(state)].filter(Boolean).join(' · ');
    return `| ${icon} | ${STAGE_NAMES[stage]} | ${status} |`;
  });
}

function taskLines(state, tasks, context) {
  const logByTask = new Map(state.credited
    .filter((entry) => entry.task && entry.log)
    .map((entry) => [entry.task, entry.log]));
  return tasks.map((task) => {
    const parts = [`- [${task.completed ? 'x' : ' '}] ${task.id} ${shortTaskTitle(task.title)}`, `\`${task.capabilities.join(', ')}\``];
    if (logByTask.has(task.id)) parts.push(`[log](${commentUrl(context, state.pr, logByTask.get(task.id))})`);
    if (state.current?.task?.id === task.id && state.status !== 'closed') {
      if (state.status === 'gated') parts.push(state.gate.kind === 'decision' ? '❓ decision needed' : '⛔ stopped');
      else if (state.status === 'ready') parts.push(`🔁 retry ${state.current.attempt}`);
      else parts.push('▶️ running');
    }
    return parts.join(' · ');
  });
}

function callout(state, context) {
  if (state.status !== 'gated' || state.gate.kind === 'merge') return null;
  const link = state.gate.log ? ` [See details](${commentUrl(context, state.pr, state.gate.log)}).` : '';
  const heading = { decision: 'Decision needed', review: 'Review needed', failure: 'Stopped' }[state.gate.kind];
  return [`> [!IMPORTANT]`, `> **${heading}.**${link}`, `> ${describeNext(state)}`].join('\n');
}

export function renderOverview({ state, tasks = null, context, includeMarker = true }) {
  const parts = [
    `## 🧭 OpenSpec · \`${state.change}\``,
    `Issue #${state.issue} · branch \`${state.branch}\` · base ${commitLink(context, state.base.sha)} on \`${state.base.ref}\` · requested by \`${state.requestedBy}\``,
  ];
  const alert = callout(state, context);
  if (alert) {
    parts.push(alert, `**Now:** ${describeActivity(state)}`);
  } else {
    parts.push(`**Now:** ${describeActivity(state)}  \n**Next:** ${describeNext(state, tasks)}`);
  }
  parts.push(['| | Stage | Status |', '|---|---|---|', ...stageRows(state, tasks)].join('\n'));
  if (tasks && tasks.length > 0) {
    const done = tasks.filter((task) => task.completed).length;
    parts.push(`<details${state.phase === 'apply' ? ' open' : ''}><summary>Tasks (${done}/${tasks.length})</summary>\n\n${taskLines(state, tasks, context).join('\n')}\n\n</details>`);
  }
  parts.push(`<sub>Maintained by the OpenSpec orchestrator · state revision ${state.revision} · ${state.updatedAt}. Commands: \`/openspec approve\` · \`retry [guidance]\` · \`answer <text>\` · \`abort\`.</sub>`);
  if (includeMarker) parts.push(OVERVIEW_MARKER(state.change));
  return `${parts.join('\n\n')}\n`;
}

export function operationCheckRun({ before, result, checkpoint = null, context }) {
  const current = before.current;
  const name = `OpenSpec ${operationLabel(current.operation, current.task).toLowerCase()}`;
  const lines = [];
  if (current.task) lines.push(`**Task ${current.task.id}:** ${current.task.title}`, '');
  if (checkpoint) {
    lines.push(`**Agent summary:** ${sanitizeAgentText(checkpoint.summary, context)}`, '');
    lines.push(`**Validation:** ${sanitizeAgentText(checkpoint.validation, context, 300)}`, '');
  }
  if (result.findings) lines.push(...findingLines(result.findings, context));
  if (result.reason) lines.push(`**Outcome:** ${sanitizeAgentText(result.reason, context, 1000)}`, '');
  if (result.feedback && result.feedback !== result.reason) {
    lines.push('**Details:**', '', '```text', result.feedback.replace(/```/g, "'''"), '```', '');
  }
  if (current.session?.url) lines.push(`[${sessionLabel(current.session)}](${current.session.url}) · attempt ${current.attempt}`);
  let conclusion;
  let title;
  if (result.kind === 'credited' || (result.kind === 'gate' && result.gate === 'merge')) {
    conclusion = 'success';
    title = current.operation === 'verify' ? 'Verified · no findings' : `Credited · attempt ${current.attempt}`;
  } else if (result.kind === 'gate' && result.gate === 'review') {
    conclusion = 'success';
    title = `Verified · ${findingCountText(result.findings)} · review needed`;
  } else if (result.kind === 'gate' && result.gate === 'decision') {
    conclusion = 'neutral';
    title = 'Agent requested a decision';
  } else if (result.kind === 'retry') {
    conclusion = 'failure';
    title = `Attempt ${current.attempt} did not finish · retrying`;
  } else {
    conclusion = 'failure';
    title = result.findings ? `Verification found ${findingCountText(result.findings)}` : 'Stopped · needs attention';
  }
  return { name, conclusion, title, summary: lines.join('\n').trim() || title };
}
