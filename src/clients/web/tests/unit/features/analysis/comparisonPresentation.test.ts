import { describe, expect, it } from 'vitest';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { AnalysisRunStatus, FactStreamSummary } from '../../../../src/platform/models/analysis';
import type { RecordingSetVersion } from '../../../../src/platform/models/recordings';
import {
  parseComparisonSelection,
  validateComparisonSide,
  validateRunPair,
} from '../../../../src/features/analysis/comparisonPresentation';

const L = '11111111-1111-4111-8111-111111111111';
const R = '22222222-2222-4222-8222-222222222222';
const MATCH = '33333333-3333-4333-8333-333333333333';
const TEAM = '44444444-4444-4444-8444-444444444444';
const SET = '55555555-5555-4555-8555-555555555555';

function run(runId: string, overrides: Partial<AnalysisRunStatus> = {}): AnalysisRunStatus {
  return {
    runId,
    teamId: TEAM,
    matchId: MATCH,
    recordingSetVersionId: SET,
    workflowId: 'wf',
    workflowVersion: '1',
    state: 'Completed',
    createdAt: new Date('2026-01-01T00:00:00Z'),
    isCurrent: false,
    requestedCapabilities: ['cap'],
    nodes: [
      {
        nodeKey: 'n1',
        capabilityId: 'cap',
        capabilityVersion: '1.0.0',
        tier: 'a',
        executionScope: 'match',
        state: 'Completed',
        jobs: { waiting: 0, ready: 0, claimed: 0, completed: 1, failed: 0, blocked: 0 },
      },
    ],
    ...overrides,
  };
}

function stream(overrides: Partial<FactStreamSummary> = {}): FactStreamSummary {
  return {
    nodeKey: 'n1',
    capabilityId: 'cap',
    capabilityVersion: '1.0.0',
    schemaVersion: '1',
    outputType: 'fact',
    executionScope: 'match',
    required: true,
    factCount: 0,
    provenance: {
      workflow: { id: 'wf', version: '1' },
      analystProfile: { id: 'p', digest: 'sha256:aa' },
      imageDigest: 'sha256:bb',
    },
    coverage: { accepted: 0, missing: 0, failed: 0, blocked: 0, skipped: 0 },
    ...overrides,
  };
}

function recordingSet(overrides: Partial<RecordingSetVersion> = {}): RecordingSetVersion {
  return {
    id: SET,
    matchId: MATCH,
    teamId: TEAM,
    finalizedAt: new Date('2026-01-01T00:00:00Z'),
    members: [
      {
        position: 0,
        recordingVersionId: 'rv1',
        recordingContentDigest: 'sha256:cc',
        timelineMappingId: 'tm1',
        timelineMappingDigest: 'sha256:dd',
      },
    ],
    ...overrides,
  };
}

describe('parseComparisonSelection', () => {
  it('allows no selection and a left-only selection', () => {
    expect(parseComparisonSelection('')).toEqual({ kind: 'empty' });
    expect(parseComparisonSelection(`?left=${L}`)).toEqual({ kind: 'left', leftRunId: L });
  });

  it('accepts an ordered pair', () => {
    expect(parseComparisonSelection(`left=${L}&right=${R}`)).toEqual({
      kind: 'pair',
      leftRunId: L,
      rightRunId: R,
    });
  });

  it.each([
    ['left=abc', 'malformedId'],
    [`left=${L}&right=nope`, 'malformedId'],
    [`left=${L}&left=${R}`, 'duplicateParameter'],
    [`left=${L}&right=${R}&right=${R}`, 'duplicateParameter'],
    [`left=${L}&other=1`, 'unsupportedParameter'],
    [`right=${R}`, 'rightWithoutLeft'],
    [`left=${L}&right=${L.toUpperCase()}`, 'identicalIds'],
  ])('rejects %s', (query, reason) => {
    expect(parseComparisonSelection(query)).toEqual({ kind: 'invalid', reason });
  });

  it('never swaps a right-only selection to the left', () => {
    expect(parseComparisonSelection(`right=${R}`).kind).toBe('invalid');
  });
});

describe('validateRunPair', () => {
  const base = { matchId: MATCH, teamId: TEAM, leftRunId: L, rightRunId: R };

  it('is eligible for two known terminal runs regardless of current status', () => {
    expect(
      validateRunPair({
        ...base,
        left: run(L, { isCurrent: true }),
        right: run(R, { state: 'Failed', supersededAt: new Date(), isCurrent: false }),
      }),
    ).toEqual({ kind: 'eligible' });
    expect(
      validateRunPair({ ...base, left: run(L, { state: 'Cancelled' }), right: run(R, { state: 'PartiallyCompleted' }) }),
    ).toEqual({ kind: 'eligible' });
  });

  it('reports nonterminal and unknown states by side', () => {
    expect(validateRunPair({ ...base, left: run(L), right: run(R, { state: 'Running' }) })).toEqual({
      kind: 'ineligible',
      side: 'right',
      reason: 'notTerminal',
    });
    expect(validateRunPair({ ...base, left: run(L, { state: 'Weird' }), right: run(R) })).toEqual({
      kind: 'ineligible',
      side: 'left',
      reason: 'unknownState',
    });
  });

  it.each([
    ['mismatched run ID', { left: run(R), right: run(R) }],
    ['cross-match run', { left: run(L), right: run(R, { matchId: R }) }],
    ['cross-team run', { left: run(L, { teamId: R }), right: run(R) }],
  ])('treats a %s as incompatible', (_name, runs) => {
    expect(() => validateRunPair({ ...base, ...runs })).toThrow(PlatformFailure);
  });

  it('rejects identical requested IDs', () => {
    expect(() => validateRunPair({ ...base, rightRunId: L, left: run(L), right: run(L) })).toThrow(PlatformFailure);
  });
});

describe('validateComparisonSide', () => {
  const side = { matchId: MATCH, teamId: TEAM };

  it('returns a consistent side unchanged', () => {
    const r = run(L);
    const streams = [stream()];
    const set = recordingSet();
    const validated = validateComparisonSide({ ...side, run: r, streams, recordingSet: set });
    expect(validated).toEqual({ run: r, streams, recordingSet: set });
  });

  it('accepts an explicitly empty stream collection', () => {
    expect(() => validateComparisonSide({ ...side, run: run(L), streams: [], recordingSet: recordingSet() })).not.toThrow();
  });

  it.each([
    ['set ID', { id: R }],
    ['set match', { matchId: R }],
    ['set team', { teamId: R }],
    ['finalization', { finalizedAt: new Date('invalid') }],
    ['member digest', { members: [{ ...recordingSet().members[0], recordingContentDigest: '' }] }],
    ['member mapping ID', { members: [{ ...recordingSet().members[0], timelineMappingId: '' }] }],
    ['member order', { members: [recordingSet().members[0], recordingSet().members[0]] }],
  ])('rejects a recording set with a bad %s', (_name, overrides) => {
    expect(() =>
      validateComparisonSide({ ...side, run: run(L), streams: [], recordingSet: recordingSet(overrides) }),
    ).toThrow(PlatformFailure);
  });

  it.each([
    ['duplicate nodeKey', [stream(), stream()]],
    ['unknown node', [stream({ nodeKey: 'other' })]],
    ['capability mismatch', [stream({ capabilityId: 'x' })]],
    ['version mismatch', [stream({ capabilityVersion: '2.0.0' })]],
    ['scope mismatch', [stream({ executionScope: 'segment' })]],
    [
      'workflow mismatch',
      [stream({ provenance: { ...stream().provenance, workflow: { id: 'other', version: '1' } } })],
    ],
  ])('rejects streams with %s', (_name, streams) => {
    expect(() =>
      validateComparisonSide({ ...side, run: run(L), streams, recordingSet: recordingSet() }),
    ).toThrow(PlatformFailure);
  });
});
