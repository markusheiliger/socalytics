import { PlatformFailure } from '../../../../src/platform/failure';
import type { AnalysisRunStatus, FactPage, FactStreamSummary } from '../../../../src/platform/models/analysis';
import { deriveFactViewState, type FactViewInput } from '../../../../src/features/analysis/reviewState';

const provenance = { workflow: { id: 'w', version: '1' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' };

const stream: FactStreamSummary = {
  nodeKey: 'n1',
  capabilityId: 'c',
  capabilityVersion: '1',
  schemaVersion: '1',
  outputType: 'events',
  executionScope: 'Match',
  required: true,
  factCount: 1,
  provenance,
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
};

function run(state: string, matchId = 'm1'): AnalysisRunStatus {
  return {
    runId: 'r1', teamId: 't1', matchId, recordingSetVersionId: 'v1', workflowId: 'w', workflowVersion: '1', state,
    createdAt: new Date(0), requestedCapabilities: ['c'], nodes: [],
  };
}

function page(items: number, states: string[] = ['accepted'], runState = 'Completed'): FactPage {
  return {
    run: { runId: 'r1', matchId: 'm1', state: runState },
    stream: { nodeKey: 'n1', capability: 'c', schemaVersion: '1', outputType: 'events', executionScope: 'Match', provenance },
    range: { fromMs: 0, toMs: 1000 },
    items: Array.from({ length: items }, (_, i) => ({
      acceptedResultId: 'a', ordinal: i, kind: 'k', startMs: 0, endMs: 1, confidence: 1, payload: {},
    })),
    coverage: states.map((state, i) => ({ startMs: i * 10, endMs: i * 10 + 10, state })),
  };
}

function input(overrides: Partial<FactViewInput> = {}): FactViewInput {
  return {
    online: true,
    routeMatchId: 'm1',
    hasTeamRole: true,
    runOutcome: { kind: 'found', run: run('Completed') },
    streamsOutcome: { kind: 'found', streams: [stream] },
    nodeKey: 'n1',
    rangeValid: true,
    rangeSet: false,
    pending: false,
    page: page(0),
    stream,
    ...overrides,
  };
}

describe('deriveFactViewState rows', () => {
  it('connectionRequired when offline', () => {
    expect(deriveFactViewState(input({ online: false })).state).toBe('connectionRequired');
  });

  it('notFound for a missing run, streams, facts, or a different match', () => {
    expect(deriveFactViewState(input({ runOutcome: { kind: 'runNotFound' } })).state).toBe('notFound');
    expect(deriveFactViewState(input({ streamsOutcome: { kind: 'runNotFound' } })).state).toBe('notFound');
    expect(deriveFactViewState(input({ factsOutcome: { kind: 'runNotFound' } })).state).toBe('notFound');
    expect(deriveFactViewState(input({ runOutcome: { kind: 'found', run: run('Completed', 'other') } })).state).toBe('notFound');
  });

  it('notPermitted without a role or on a refusal', () => {
    expect(deriveFactViewState(input({ hasTeamRole: false })).state).toBe('notPermitted');
    expect(deriveFactViewState(input({ failure: new PlatformFailure('notPermitted', { status: 403 }) })).state).toBe('notPermitted');
    expect(deriveFactViewState(input({ failure: new PlatformFailure('unexpected', { status: 403 }) })).state).toBe('notPermitted');
  });

  it('capabilityNotInRun for the outcome or an unknown node key', () => {
    expect(deriveFactViewState(input({ factsOutcome: { kind: 'capabilityNotInRun' } })).state).toBe('capabilityNotInRun');
    expect(deriveFactViewState(input({ nodeKey: 'zzz' })).state).toBe('capabilityNotInRun');
  });

  it('invalidRange when the range is not valid', () => {
    expect(deriveFactViewState(input({ rangeValid: false })).state).toBe('invalidRange');
  });

  it('error for any other failure', () => {
    expect(deriveFactViewState(input({ failure: new PlatformFailure('unreachable') })).state).toBe('error');
  });

  it('loading while pending without data', () => {
    expect(deriveFactViewState(input({ pending: true, page: undefined })).state).toBe('loading');
  });

  it('facts, complete or incomplete', () => {
    expect(deriveFactViewState(input({ page: page(2) }))).toEqual({ state: 'facts', incomplete: false });
    expect(deriveFactViewState(input({ page: page(2), pending: true }))).toEqual({ state: 'facts', incomplete: false });
    expect(deriveFactViewState(input({ page: page(2, ['accepted', 'missing']) }))).toEqual({ state: 'facts', incomplete: true });
    expect(deriveFactViewState(input({ page: page(2), runOutcome: { kind: 'found', run: run('Running') } }))).toEqual({
      state: 'facts',
      incomplete: true,
    });
    expect(deriveFactViewState(input({ page: page(2), runOutcome: { kind: 'found', run: run('requested') } }))).toEqual({
      state: 'facts',
      incomplete: true,
    });
  });

  it('noFactsInRange when a range is set', () => {
    expect(deriveFactViewState(input({ rangeSet: true })).state).toBe('noFactsInRange');
  });

  it('inProgress while the run is requested or running', () => {
    expect(deriveFactViewState(input({ runOutcome: { kind: 'found', run: run('Requested') } })).state).toBe('inProgress');
    expect(deriveFactViewState(input({ runOutcome: { kind: 'found', run: run('Running') } })).state).toBe('inProgress');
  });

  it('incomplete when a window is not accepted', () => {
    expect(deriveFactViewState(input({ page: page(0, ['accepted', 'failed']) })).state).toBe('incomplete');
    expect(deriveFactViewState(input({ page: page(0, ['unknown-state']) })).state).toBe('incomplete');
  });

  it('completeWithoutFacts when every window is accepted', () => {
    expect(deriveFactViewState(input())).toEqual({ state: 'completeWithoutFacts' });
    expect(deriveFactViewState(input({ page: page(0, []) })).state).toBe('completeWithoutFacts');
  });

  it('uses stream coverage counts while no page is loaded', () => {
    const partial = { ...stream, coverage: { ...stream.coverage, blocked: 1 } };
    expect(deriveFactViewState(input({ page: undefined, stream: partial })).state).toBe('incomplete');
    expect(deriveFactViewState(input({ page: undefined })).state).toBe('completeWithoutFacts');
  });

  it('falls back to the page run state when the run is not loaded', () => {
    expect(deriveFactViewState(input({ runOutcome: undefined, page: page(0, ['accepted'], 'Running') })).state).toBe('inProgress');
  });
});

describe('deriveFactViewState precedence', () => {
  it('connectionRequired beats everything', () => {
    expect(deriveFactViewState(input({ online: false, runOutcome: { kind: 'runNotFound' }, hasTeamRole: false, rangeValid: false })).state).toBe(
      'connectionRequired',
    );
  });

  it('notFound beats notPermitted', () => {
    expect(deriveFactViewState(input({ runOutcome: { kind: 'runNotFound' }, hasTeamRole: false })).state).toBe('notFound');
  });

  it('notPermitted beats capabilityNotInRun', () => {
    expect(deriveFactViewState(input({ hasTeamRole: false, factsOutcome: { kind: 'capabilityNotInRun' } })).state).toBe('notPermitted');
  });

  it('capabilityNotInRun beats invalidRange', () => {
    expect(deriveFactViewState(input({ nodeKey: 'zzz', rangeValid: false })).state).toBe('capabilityNotInRun');
  });

  it('invalidRange beats error and loading', () => {
    expect(deriveFactViewState(input({ rangeValid: false, failure: new PlatformFailure('unreachable') })).state).toBe('invalidRange');
    expect(deriveFactViewState(input({ rangeValid: false, pending: true, page: undefined })).state).toBe('invalidRange');
  });

  it('error beats loading and facts', () => {
    expect(deriveFactViewState(input({ failure: new PlatformFailure('unreachable'), pending: true, page: undefined })).state).toBe('error');
    expect(deriveFactViewState(input({ failure: new PlatformFailure('unreachable'), page: page(1) })).state).toBe('error');
  });

  it('loading beats the empty states', () => {
    expect(deriveFactViewState(input({ pending: true, page: undefined, rangeSet: true })).state).toBe('loading');
  });

  it('facts beat noFactsInRange', () => {
    expect(deriveFactViewState(input({ page: page(1), rangeSet: true })).state).toBe('facts');
  });

  it('noFactsInRange beats inProgress and incomplete', () => {
    expect(
      deriveFactViewState(input({ rangeSet: true, page: page(0, ['failed']), runOutcome: { kind: 'found', run: run('Running') } })).state,
    ).toBe('noFactsInRange');
  });

  it('inProgress beats incomplete', () => {
    expect(
      deriveFactViewState(input({ page: page(0, ['missing']), runOutcome: { kind: 'found', run: run('Running') } })).state,
    ).toBe('inProgress');
  });
});
