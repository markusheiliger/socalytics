import type { Turn, TriageOutcome } from '../../platform/models/agents';

/** A translation key of the `triage` namespace with its parameters; components only render it. */
export interface Message {
  key: string;
  params?: Record<string, string | number>;
}

export type TurnPresentationKind = 'pending' | 'succeeded' | 'failed' | 'unknown';

export interface TurnPresentation {
  kind: TurnPresentationKind;
  message: Message;
  /** The attempt line, present only above the first attempt. */
  attempt?: Message;
  /** True while the Web UI keeps polling the invocation. */
  polling: boolean;
}

export type AgentNameTranslator = (key: string, options?: Record<string, unknown>) => string;

export function turnPresentation(turn: Turn, attemptCount?: number): TurnPresentation {
  const state = turn.state;

  if (state.recognized === 'succeeded') {
    return { kind: 'succeeded', message: { key: 'advice.title' }, polling: false };
  }

  if (state.recognized === 'failed') {
    const reason = turn.failureReason;
    let message: Message;
    if (reason === undefined) {
      message = { key: 'turn.failed.none' };
    } else if (reason.recognized !== undefined) {
      message = { key: `turn.failed.${reason.recognized}` };
    } else {
      message = { key: 'turn.failed.other', params: { reason: reason.value } };
    }
    return { kind: 'failed', message, polling: false };
  }

  if (state.recognized === 'pending') {
    if (state.value === 'claimed' || state.value === 'running') {
      const attempt =
        attemptCount !== undefined && attemptCount > 1
          ? { key: 'turn.attempt', params: { count: attemptCount } }
          : undefined;
      return { kind: 'pending', message: { key: 'turn.running' }, attempt, polling: true };
    }
    return { kind: 'pending', message: { key: 'turn.queued' }, polling: true };
  }

  return {
    kind: 'unknown',
    message: { key: 'turn.unknownState', params: { state: state.value } },
    polling: true,
  };
}

export interface TriageMessageOptions {
  maxQuestionCharacters?: number;
}

export function triageMessage(outcome: TriageOutcome, options: TriageMessageOptions = {}): Message {
  switch (outcome.kind) {
    case 'invalidQuestion': {
      const codes = outcome.fieldErrors.map((error) => error.code.toLowerCase());
      if (codes.some((code) => /required|empty|blank/.test(code))) {
        return { key: 'composer.questionRequired' };
      }
      if (codes.some((code) => /long|max|length/.test(code))) {
        return { key: 'composer.questionTooLong', params: { max: options.maxQuestionCharacters ?? '' } };
      }
      return { key: 'composer.questionInvalid' };
    }
    case 'platform':
      return { key: outcome.failureKind === 'unreachable' ? 'refusal.offline' : 'refusal.failed' };
    default:
      return { key: `refusal.${outcome.kind}` };
  }
}

export function agentName(agentId: string, t: AgentNameTranslator): string {
  return t(`agents.${agentId}`, { defaultValue: agentId });
}
