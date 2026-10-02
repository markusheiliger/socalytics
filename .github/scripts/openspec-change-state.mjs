import { JSON_CONTRACTS, MAX_FEEDBACK, validateRunState } from './openspec-change-core.mjs';

export const MAX_ATTEMPTS = 2;
export const DISPATCH_GRACE_MS = 10 * 60 * 1000;
export const LIFECYCLE_CHECK_NAME = 'OpenSpec lifecycle';
export const MANAGED_ISSUE_LABELS = Object.freeze([
  'openspec:processing',
  'openspec:needs-attention',
  'openspec:awaiting-review',
]);
export const STAGE_LABELS = Object.freeze([
  'openspec:stage:apply',
  'openspec:stage:verify',
  'openspec:stage:sync',
  'openspec:stage:archive',
]);
export const GATE_COMMANDS = Object.freeze({
  decision: Object.freeze(['answer', 'approve', 'abort']),
  review: Object.freeze(['approve', 'retry', 'abort']),
  failure: Object.freeze(['retry', 'abort']),
  merge: Object.freeze(['abort']),
});
export const MAX_ANSWERS = 5;
export const APPROVE_DECISION_TEXT = 'Approved: proceed with the option you recommended and record the decision in the change artifacts.';

const ACTIVE_AGENT_STATES = new Set(['queued', 'in_progress']);

function timestamp(now) {
  return now.toISOString();
}

function transition(state, patch, now) {
  return validateRunState({
    ...state,
    ...patch,
    revision: state.revision + 1,
    updatedAt: timestamp(now),
  });
}

function requireStatus(state, statuses, action) {
  if (!statuses.includes(state.status)) {
    throw new Error(`Cannot ${action} while the run is ${state.status}`);
  }
}

export function shortTaskTitle(title, maxLength = 72) {
  const plain = String(title)
    .replace(/\*{0,2}Capabilities:\s*[a-z][a-z0-9]*(?:\s*,\s*[a-z][a-z0-9]*)*\s*\.?\*{0,2}/gi, ' ')
    .replace(/[*_`]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
  const sentence = plain.match(/^(.+?[.;:])(?:\s|$)/)?.[1] ?? plain;
  const clean = sentence.replace(/[.;:]$/, '').trim() || 'Untitled task';
  return clean.length > maxLength ? `${clean.slice(0, maxLength - 1).trimEnd()}…` : clean;
}

export function toRunTask(task) {
  return {
    id: task.id,
    title: shortTaskTitle(task.title),
    capabilities: [...task.capabilities],
  };
}

export function operationLabel(operation, task = null) {
  const names = { apply: 'Apply', verify: 'Verify', sync: 'Sync', archive: 'Archive' };
  return task ? `${names[operation]} ${task.id}` : names[operation];
}

export function createRunState({
  change,
  issue,
  pr,
  branch,
  baseRef = 'main',
  baseSha,
  headSha,
  requestedBy,
  now,
}) {
  return validateRunState({
    $schema: JSON_CONTRACTS.changeRunState,
    change,
    issue,
    pr,
    branch,
    base: { ref: baseRef, sha: baseSha },
    requestedBy,
    revision: 1,
    headSha,
    phase: 'apply',
    status: 'ready',
    outcome: null,
    current: null,
    credited: [],
    gate: null,
    answers: [],
    commandCursor: 0,
    updatedAt: timestamp(now),
  });
}

export function decideNext(state, { tasks = null } = {}) {
  if (state.status === 'closed') return { action: 'none', reason: 'closed' };
  if (state.status === 'gated') {
    return state.gate.notified
      ? { action: 'none', reason: 'waiting-for-human' }
      : { action: 'notify-gate' };
  }
  if (state.status !== 'ready') return { action: 'none', reason: 'session-in-flight' };
  if (state.current) {
    return {
      action: 'dispatch',
      operation: state.current.operation,
      task: state.current.task,
      attempt: state.current.attempt,
    };
  }
  if (state.phase === 'apply') {
    if (!Array.isArray(tasks)) throw new Error('Parsed tasks are required to plan apply');
    const nextTask = tasks.find((task) => !task.completed);
    if (nextTask) {
      return { action: 'dispatch', operation: 'apply', task: toRunTask(nextTask), attempt: 1 };
    }
    return { action: 'dispatch', operation: 'verify', task: null, attempt: 1 };
  }
  if (['verify', 'sync', 'archive'].includes(state.phase)) {
    return { action: 'dispatch', operation: state.phase, task: null, attempt: 1 };
  }
  return { action: 'none', reason: `phase-${state.phase}` };
}

export function markDispatching(state, { operation, task = null, attempt, startSha, dispatchId = null, now }) {
  requireStatus(state, ['ready'], 'dispatch');
  return transition(state, {
    phase: operation,
    status: 'dispatching',
    headSha: startSha,
    current: {
      operation,
      task: operation === 'apply' ? task : null,
      attempt,
      startSha,
      // Evidence is judged against the head before the first attempt, so earlier failed attempts cannot hide changes.
      baselineSha: state.current?.baselineSha ?? startSha,
      dispatchId,
      session: null,
      dispatchedAt: timestamp(now),
      feedback: state.current?.feedback ?? null,
    },
  }, now);
}

export function markRunning(state, { session, now }) {
  requireStatus(state, ['dispatching'], 'record a started session');
  const recorded = { runtime: session.runtime, id: String(session.id), state: session.state ?? 'queued' };
  if (session.url) recorded.url = session.url;
  return transition(state, {
    status: 'running',
    current: { ...state.current, session: recorded },
  }, now);
}

export function openGate(state, {
  kind,
  operation = state.current?.operation ?? null,
  task = state.current?.task ?? null,
  question,
  reason,
  findings,
  now,
}) {
  const gate = {
    kind,
    operation,
    commands: [...GATE_COMMANDS[kind]],
    openedAt: timestamp(now),
    notified: false,
    log: null,
  };
  if (task) gate.task = task;
  if (question) gate.question = question;
  if (reason) gate.reason = reason;
  if (findings) gate.findings = findings;
  return transition(state, { status: 'gated', gate }, now);
}

export function markGateNotified(state, { log, now }) {
  requireStatus(state, ['gated'], 'record a gate notification');
  return transition(state, { gate: { ...state.gate, notified: true, log } }, now);
}

export function recoverDispatch(state, { matches, now, graceMs = DISPATCH_GRACE_MS }) {
  requireStatus(state, ['dispatching'], 'recover a dispatch');
  if (matches.length === 1) {
    return { action: 'adopted', state: markRunning(state, { session: matches[0], now }) };
  }
  if (matches.length > 1) {
    return {
      action: 'gate',
      state: openGate(state, {
        kind: 'failure',
        reason: `Found ${matches.length} agent sessions for one dispatch. Check them, stop extra sessions, then retry.`,
        now,
      }),
    };
  }
  const elapsed = now.getTime() - Date.parse(state.current.dispatchedAt);
  if (elapsed < graceMs) return { action: 'wait', state };
  return {
    action: 'redispatch',
    state: transition(state, {
      status: 'ready',
      current: { ...state.current, session: null, dispatchedAt: null },
    }, now),
  };
}

function withFinalAgentState(current, agentState) {
  if (!current.session || !agentState) return current;
  return { ...current, session: { ...current.session, state: agentState } };
}

function boundedFeedback(text) {
  const value = String(text ?? '').trim();
  if (!value) return null;
  return value.length > MAX_FEEDBACK ? `…${value.slice(-(MAX_FEEDBACK - 1))}` : value;
}

// Human guidance leads the feedback; the earlier failure details are shortened to fit behind it.
function feedbackWithGuidance(previous, guidance, by) {
  const lead = `Guidance from @${by} for this retry: ${guidance}`;
  if (!previous) return boundedFeedback(lead);
  const room = MAX_FEEDBACK - lead.length - 2;
  if (room < 20) return boundedFeedback(lead);
  const earlier = previous.length > room ? `…${previous.slice(-(room - 1))}` : previous;
  return `${lead}\n\n${earlier}`;
}

function retryOrFail(state, reason, now, feedback = null) {
  const current = { ...state.current, feedback: boundedFeedback(feedback ?? reason) };
  state = { ...state, current };
  if (current.attempt < MAX_ATTEMPTS) {
    return {
      state: transition(state, {
        status: 'ready',
        current: {
          ...current,
          attempt: current.attempt + 1,
          startSha: state.headSha,
          session: null,
          dispatchedAt: null,
        },
      }, now),
      result: { kind: 'retry', reason, nextAttempt: current.attempt + 1, feedback: current.feedback },
    };
  }
  return {
    state: openGate(state, { kind: 'failure', reason, now }),
    result: { kind: 'gate', gate: 'failure', reason, feedback: current.feedback },
  };
}

function findingsTotal(findings) {
  return findings.critical + findings.warning + findings.suggestion;
}

function creditComplete(state, { checkpoint, checkpointSha, sessionLog }, now) {
  const { current } = state;
  const credit = { operation: current.operation, sha: checkpointSha, attempt: current.attempt, log: sessionLog ?? null };
  if (current.task) credit.task = current.task.id;
  const credited = [...state.credited, credit];
  const done = { credited, current: null, headSha: checkpointSha };
  switch (current.operation) {
    case 'apply':
      return {
        state: transition(state, { ...done, status: 'ready', phase: 'apply' }, now),
        result: { kind: 'credited' },
      };
    case 'verify': {
      const { findings } = checkpoint;
      if (findingsTotal(findings) === 0) {
        return {
          state: transition(state, { ...done, status: 'ready', phase: 'sync' }, now),
          result: { kind: 'credited', findings },
        };
      }
      const verified = transition(state, { ...done, status: 'ready', phase: 'verify' }, now);
      if (findings.critical > 0) {
        const reason = `Verification found ${findings.critical} critical issue(s). Fix them on the branch, then retry verification.`;
        return {
          state: openGate(verified, { kind: 'failure', operation: 'verify', task: null, reason, findings, now }),
          result: { kind: 'gate', gate: 'failure', reason, findings },
        };
      }
      return {
        state: openGate(verified, { kind: 'review', operation: 'verify', task: null, findings, now }),
        result: { kind: 'gate', gate: 'review', findings },
      };
    }
    case 'sync':
      return {
        state: transition(state, { ...done, status: 'ready', phase: 'archive' }, now),
        result: { kind: 'credited' },
      };
    case 'archive': {
      const archived = transition(state, { ...done, status: 'ready', phase: 'merge' }, now);
      return {
        state: openGate(archived, { kind: 'merge', operation: null, task: null, now }),
        result: { kind: 'gate', gate: 'merge' },
      };
    }
    default:
      throw new Error(`Unknown operation: ${current.operation}`);
  }
}

export function creditSession(state, observation, now) {
  requireStatus(state, ['running'], 'credit a session');
  const {
    agentState = null,
    headSha = state.headSha,
    checkpoint = null,
    checkpointSha = null,
    checkpointError = null,
    evidence = null,
    sessionLog = null,
  } = observation;
  const observed = { ...state, headSha, current: withFinalAgentState(state.current, agentState) };

  if (checkpoint) {
    switch (checkpoint.verdict) {
      case 'complete':
        if (!evidence?.ok) {
          return retryOrFail(observed, `The checkpoint failed controller validation: ${evidence?.reason ?? 'no evidence'}`, now, evidence?.feedback ?? null);
        }
        return creditComplete(observed, { checkpoint, checkpointSha, sessionLog }, now);
      case 'partial':
        return retryOrFail(observed, 'The session pushed partial progress and stopped before finishing the task.', now);
      case 'needs_decision':
        return {
          state: openGate(observed, { kind: 'decision', question: checkpoint.question, now }),
          result: { kind: 'gate', gate: 'decision', question: checkpoint.question },
        };
      case 'failed':
        return retryOrFail(observed, checkpoint.summary, now);
      default:
        throw new Error(`Unknown checkpoint verdict: ${checkpoint.verdict}`);
    }
  }
  if (checkpointError) {
    return retryOrFail(observed, `The checkpoint trailer is invalid: ${checkpointError}`, now);
  }
  if (agentState === null || ACTIVE_AGENT_STATES.has(agentState)) {
    return { state, result: { kind: 'wait' } };
  }
  if (agentState === 'waiting_for_user') {
    const question = 'The agent session is waiting for input. Open the session to read its question, then answer here.';
    return {
      state: openGate(observed, { kind: 'decision', question, now }),
      result: { kind: 'gate', gate: 'decision', question },
    };
  }
  if (agentState === 'cancelled') {
    const reason = 'The agent session was cancelled.';
    return {
      state: openGate(observed, { kind: 'failure', reason, now }),
      result: { kind: 'gate', gate: 'failure', reason },
    };
  }
  return retryOrFail(observed, `The agent session ended (${agentState}) without a checkpoint commit.`, now);
}

function rejected(state, commentId, message, now) {
  return {
    accepted: false,
    message,
    state: transition(state, { commandCursor: Math.max(state.commandCursor, commentId) }, now),
  };
}

function restartCurrent(current, headSha) {
  return { ...current, attempt: 1, startSha: headSha, session: null, dispatchedAt: null };
}

export function applyCommand(state, { name, text = null, by, commentId, now }) {
  if (state.status === 'closed') {
    return rejected(state, commentId, 'This change is no longer being processed.', now);
  }
  const cursor = Math.max(state.commandCursor, commentId);
  if (name === 'abort') {
    return {
      accepted: true,
      message: `@${by} stopped processing.`,
      state: transition(state, {
        phase: 'aborted',
        status: 'closed',
        outcome: 'aborted',
        gate: null,
        current: null,
        commandCursor: cursor,
      }, now),
    };
  }
  if (state.status !== 'gated') {
    return rejected(state, commentId, `\`/openspec ${name}\` is only available while the workflow is waiting for input. Only \`/openspec abort\` works right now.`, now);
  }
  const { gate } = state;
  if (!gate.commands.includes(name)) {
    const available = gate.commands.map((command) => `\`/openspec ${command}\``).join(', ');
    return rejected(state, commentId, `\`/openspec ${name}\` is not available for this ${gate.kind} gate. Available: ${available}.`, now);
  }

  if (gate.kind === 'decision') {
    const answerText = name === 'answer' ? text : APPROVE_DECISION_TEXT;
    return {
      accepted: true,
      message: name === 'answer'
        ? `@${by} answered: ${answerText}`
        : `@${by} approved the agent's recommended option.`,
      state: transition(state, {
        status: 'ready',
        gate: null,
        current: restartCurrent(state.current, state.headSha),
        answers: [...state.answers, { question: gate.question, text: answerText, by, comment: commentId }].slice(-MAX_ANSWERS),
        commandCursor: cursor,
      }, now),
    };
  }
  if (gate.kind === 'review') {
    return {
      accepted: true,
      message: name === 'approve'
        ? `@${by} accepted the verification findings.`
        : `@${by} asked to verify again.`,
      state: transition(state, {
        status: 'ready',
        phase: name === 'approve' ? 'sync' : 'verify',
        gate: null,
        current: null,
        commandCursor: cursor,
      }, now),
    };
  }
  // failure gate: retry restarts the stopped operation from the current head.
  const restarted = state.current ? restartCurrent(state.current, state.headSha) : null;
  if (restarted && text) restarted.feedback = feedbackWithGuidance(restarted.feedback, text, by);
  return {
    accepted: true,
    message: text ? `@${by} asked to retry with guidance: ${text}` : `@${by} asked to retry.`,
    state: transition(state, {
      status: 'ready',
      phase: gate.operation ?? state.phase,
      gate: null,
      current: restarted,
      commandCursor: cursor,
    }, now),
  };
}

export function advanceCommandCursor(state, { commentId, now }) {
  if (commentId <= state.commandCursor) return state;
  return transition(state, { commandCursor: commentId }, now);
}

export function noteHeadMoved(state, { headSha, now }) {
  if (headSha === state.headSha || ['dispatching', 'running', 'closed'].includes(state.status)) {
    return { moved: false, state };
  }
  const current = state.current ? { ...state.current, baselineSha: headSha } : null;
  return { moved: true, state: transition(state, { headSha, current }, now) };
}

export function finalizeMerged(state, { now }) {
  if (state.outcome === 'merged') return state;
  return transition(state, {
    phase: 'done',
    status: 'closed',
    outcome: 'merged',
    gate: null,
    current: null,
  }, now);
}

export function finalizeClosedUnmerged(state, { now }) {
  if (state.status === 'closed') return state;
  return transition(state, {
    phase: 'aborted',
    status: 'closed',
    outcome: 'closed-unmerged',
    gate: null,
    current: null,
  }, now);
}

function shortReason(reason, maxLength = 70) {
  return reason.length > maxLength ? `${reason.slice(0, maxLength - 1).trimEnd()}…` : reason;
}

export function describeActivity(state) {
  if (state.status === 'closed') {
    return {
      merged: 'Merged',
      aborted: 'Aborted',
      'closed-unmerged': 'Closed without merging',
    }[state.outcome];
  }
  if (state.status === 'gated') {
    const { gate } = state;
    const where = gate.operation ? ` · ${operationLabel(gate.operation, gate.task)}` : '';
    if (gate.kind === 'merge') return 'Archived · ready for human merge';
    if (gate.kind === 'decision') return `Decision needed${where}`;
    if (gate.kind === 'review') {
      const total = findingsTotal(gate.findings);
      return `Review needed · verification found ${total} finding(s)`;
    }
    return `Stopped${where} · ${shortReason(gate.reason ?? 'needs attention')}`;
  }
  const current = state.current;
  if (current && state.status !== 'ready') {
    const attempt = current.attempt > 1 ? ` (attempt ${current.attempt})` : '';
    const title = current.task ? ` · ${current.task.title}` : '';
    return `${operationLabel(current.operation, current.task)} running${attempt}${title}`;
  }
  if (current) return `${operationLabel(current.operation, current.task)} queued for retry`;
  return `${operationLabel(state.phase === 'merge' ? 'archive' : state.phase)} next`;
}

export function lifecycleCheck(state) {
  const title = describeActivity(state);
  if (state.status === 'closed') {
    return { status: 'completed', conclusion: state.outcome === 'merged' ? 'success' : 'cancelled', title };
  }
  if (state.status === 'gated') {
    return {
      status: 'completed',
      conclusion: state.gate.kind === 'merge' ? 'success' : 'action_required',
      title,
    };
  }
  return { status: 'in_progress', title };
}

export function issueLabels(state) {
  if (state.status === 'closed') return [];
  if (state.status === 'gated') {
    return state.gate.kind === 'merge'
      ? ['openspec:awaiting-review']
      : ['openspec:processing', 'openspec:needs-attention'];
  }
  return ['openspec:processing'];
}

export function stageLabel(state) {
  return ['apply', 'verify', 'sync', 'archive'].includes(state.phase)
    ? `openspec:stage:${state.phase}`
    : null;
}
