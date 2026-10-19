import { describe, expect, it } from 'vitest';
import { agentName, triageMessage, turnPresentation } from '../../../../src/features/triage/turnPresentation';
import {
  toFailureReason,
  toTurnState,
  type Turn,
  type TriageOutcome,
} from '../../../../src/platform/models/agents';
import triage from '../../../../src/i18n/en/triage.json';

function turn(state: string, failureReason?: string): Turn {
  return {
    invocationId: 'i1',
    agentId: 'specialist-tactics',
    question: 'q',
    state: toTurnState(state),
    failureReason: failureReason === undefined ? undefined : toFailureReason(failureReason),
    submittedAt: '2026-10-19T10:00:00Z',
  };
}

function lookup(key: string): unknown {
  return key.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], triage);
}

describe('turnPresentation', () => {
  it.each(['accepted', 'queued'])('presents %s as waiting and polls', (state) => {
    const presentation = turnPresentation(turn(state));
    expect(presentation).toMatchObject({ kind: 'pending', message: { key: 'turn.queued' }, polling: true });
  });

  it.each(['claimed', 'running'])('presents %s as working and polls', (state) => {
    const presentation = turnPresentation(turn(state));
    expect(presentation).toMatchObject({ kind: 'pending', message: { key: 'turn.running' }, polling: true });
    expect(presentation.attempt).toBeUndefined();
  });

  it('shows the attempt number only above 1', () => {
    expect(turnPresentation(turn('running'), 1).attempt).toBeUndefined();
    expect(turnPresentation(turn('running'), 2).attempt).toEqual({ key: 'turn.attempt', params: { count: 2 } });
  });

  it('presents a succeeded turn as advice and stops polling', () => {
    expect(turnPresentation(turn('succeeded'))).toMatchObject({ kind: 'succeeded', polling: false });
  });

  it.each(['attempts-exhausted', 'execution-failed', 'scope-lost'])('words the failure reason %s', (reason) => {
    expect(turnPresentation(turn('failed', reason))).toEqual({
      kind: 'failed',
      message: { key: `turn.failed.${reason}` },
      polling: false,
    });
  });

  it('shows the raw reason for another failure reason', () => {
    expect(turnPresentation(turn('failed', 'quota'))).toMatchObject({
      kind: 'failed',
      message: { key: 'turn.failed.other', params: { reason: 'quota' } },
      polling: false,
    });
  });

  it('presents a failure without a reason', () => {
    expect(turnPresentation(turn('failed'))).toMatchObject({ message: { key: 'turn.failed.none' }, polling: false });
  });

  it('shows an unknown state raw and keeps polling', () => {
    expect(turnPresentation(turn('paused'))).toEqual({
      kind: 'unknown',
      message: { key: 'turn.unknownState', params: { state: 'paused' } },
      polling: true,
    });
  });

  it('only returns keys that exist in the triage namespace', () => {
    const turns = [
      turn('queued'),
      turn('running'),
      turn('succeeded'),
      turn('failed', 'attempts-exhausted'),
      turn('failed', 'execution-failed'),
      turn('failed', 'scope-lost'),
      turn('failed', 'x'),
      turn('failed'),
      turn('weird'),
    ];
    for (const item of turns) {
      expect(lookup(turnPresentation(item).message.key)).toBeTypeOf('string');
    }
    expect(lookup(turnPresentation(turn('running'), 3).attempt!.key)).toBeTypeOf('string');
  });
});

describe('triageMessage', () => {
  const simple: TriageOutcome['kind'][] = [
    'tooManyUnfinished',
    'agentNotAvailable',
    'agentNotParticipant',
    'participantExists',
    'staleVersion',
    'runNotEligible',
    'notPermitted',
    'notFound',
    'unknownOutcome',
  ];

  it.each(simple)('words %s', (kind) => {
    const message = triageMessage({ kind } as TriageOutcome);
    expect(message).toEqual({ key: `refusal.${kind}` });
    expect(lookup(message.key)).toBeTypeOf('string');
  });

  it('words a required question field error', () => {
    expect(triageMessage({ kind: 'invalidQuestion', fieldErrors: [{ field: 'question', code: 'Required' }] })).toEqual({
      key: 'composer.questionRequired',
    });
  });

  it('words a too long question with the maximum', () => {
    expect(
      triageMessage(
        { kind: 'invalidQuestion', fieldErrors: [{ field: 'question', code: 'too-long' }] },
        { maxQuestionCharacters: 4000 },
      ),
    ).toEqual({ key: 'composer.questionTooLong', params: { max: 4000 } });
  });

  it('words any other field error generically', () => {
    expect(triageMessage({ kind: 'invalidQuestion', fieldErrors: [{ field: 'question', code: 'odd' }] })).toEqual({
      key: 'composer.questionInvalid',
    });
    expect(triageMessage({ kind: 'invalidQuestion', fieldErrors: [] })).toEqual({ key: 'composer.questionInvalid' });
  });

  it('words offline and other shared failures', () => {
    expect(triageMessage({ kind: 'platform', failureKind: 'unreachable' })).toEqual({ key: 'refusal.offline' });
    expect(triageMessage({ kind: 'platform', failureKind: 'conflict' })).toEqual({ key: 'refusal.failed' });
  });
});

describe('agentName', () => {
  const t = (key: string, options?: Record<string, unknown>) =>
    key === 'agents.specialist-tactics' ? 'Tactics specialist' : String(options?.defaultValue);

  it('resolves a known agent name', () => {
    expect(agentName('specialist-tactics', t)).toBe('Tactics specialist');
  });

  it('falls back to the identifier', () => {
    expect(agentName('specialist-new', t)).toBe('specialist-new');
  });
});
