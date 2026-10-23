import { describe, expect, it } from 'vitest';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { AnalysisRunStatus, FactStreamSummary, NodeInputCoverage } from '../../../../src/platform/models/analysis';
import type { RecordingSetVersion } from '../../../../src/platform/models/recordings';
import {
  compareStreams,
  describeStreamAvailability,
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

describe('compareStreams', () => {
  function node(nodeKey: string, capabilityId: string, executionScope: string, inputCoverage?: NodeInputCoverage[]) {
    return {
      ...run(L).nodes[0],
      nodeKey,
      capabilityId,
      executionScope,
      inputCoverage,
    };
  }
  function sideOf(
    runId: string,
    specs: {
      nodeKey: string;
      capabilityId: string;
      executionScope: string;
      coverage?: NodeInputCoverage[];
    }[],
    streamOverrides: Record<string, Partial<FactStreamSummary>> = {},
  ) {
    return {
      run: run(runId, {
        nodes: specs.map((s) => node(s.nodeKey, s.capabilityId, s.executionScope, s.coverage)),
      }),
      recordingSet: recordingSet(),
      streams: specs.map((s) =>
        stream({
          nodeKey: s.nodeKey,
          capabilityId: s.capabilityId,
          executionScope: s.executionScope,
          ...streamOverrides[s.nodeKey],
        }),
      ),
    };
  }
  const dep = (upstreamNodeKey: string, overrides: Partial<NodeInputCoverage> = {}): NodeInputCoverage => ({
    upstreamNodeKey,
    coverageMode: 'all',
    accepted: 1,
    missing: 0,
    failed: 0,
    blocked: 0,
    skipped: 0,
    ...overrides,
  });

  it('aligns exactly one stream per side and lists explicit field differences', () => {
    const left = sideOf(L, [{ nodeKey: 'a', capabilityId: 'cap', executionScope: 'match' }]);
    const right = sideOf(R, [{ nodeKey: 'b', capabilityId: 'cap', executionScope: 'match' }], {
      b: {
        capabilityVersion: '2.0.0',
        schemaVersion: '2',
        outputType: 'other',
        provenance: {
          workflow: { id: 'wf', version: '1' },
          analystProfile: { id: 'p2', digest: 'sha256:a2' },
          imageDigest: 'sha256:b2',
          modelDigest: 'sha256:m2',
        },
      },
    });
    const [entry] = compareStreams(left, right);
    expect(entry.kind).toBe('aligned');
    if (entry.kind !== 'aligned') return;
    expect(entry.differences.map((d) => d.field)).toEqual([
      'nodeKey',
      'capabilityVersion',
      'schemaVersion',
      'outputType',
      'profileId',
      'profileDigest',
      'imageDigest',
      'modelDigest',
    ]);
    expect(entry.differences.at(-1)).toEqual({
      field: 'modelDigest',
      left: undefined,
      right: 'sha256:m2',
    });
  });

  it('reports no differences for identical streams', () => {
    const spec = [{ nodeKey: 'a', capabilityId: 'cap', executionScope: 'match' }];
    const [entry] = compareStreams(sideOf(L, spec), sideOf(R, spec));
    expect(entry.kind === 'aligned' && entry.differences).toEqual([]);
  });

  it('uses collision-safe tuple keys and a deterministic left-first, right-only order', () => {
    const left = sideOf(L, [
      { nodeKey: 'l1', capabilityId: 'a|b', executionScope: 'c' },
      { nodeKey: 'l2', capabilityId: 'a', executionScope: 'b|c' },
      { nodeKey: 'l3', capabilityId: 'only-left', executionScope: 'match' },
    ]);
    const right = sideOf(R, [
      { nodeKey: 'r3', capabilityId: 'only-right', executionScope: 'match' },
      { nodeKey: 'r2', capabilityId: 'a', executionScope: 'b|c' },
      { nodeKey: 'r1', capabilityId: 'a|b', executionScope: 'c' },
    ]);
    const entries = compareStreams(left, right);
    expect(
      entries.map((e) =>
        e.kind === 'aligned' ? `A:${e.left.nodeKey}=${e.right.nodeKey}` : `U:${e.side}:${e.stream.nodeKey}:${e.reason}`,
      ),
    ).toEqual(['A:l1=r1', 'A:l2=r2', 'U:left:l3:noCounterpart', 'U:right:r3:noCounterpart']);
  });

  it('does not align across colliding-looking capability and scope strings', () => {
    const left = sideOf(L, [{ nodeKey: 'l', capabilityId: 'a|b', executionScope: 'c' }]);
    const right = sideOf(R, [{ nodeKey: 'r', capabilityId: 'a', executionScope: 'b|c' }]);
    expect(compareStreams(left, right).map((e) => e.kind)).toEqual(['unmatched', 'unmatched']);
  });

  it('emits every stream of an ambiguous group independently without a Cartesian product', () => {
    const left = sideOf(L, [
      { nodeKey: 'l1', capabilityId: 'cap', executionScope: 'match' },
      { nodeKey: 'l2', capabilityId: 'cap', executionScope: 'match' },
    ]);
    const right = sideOf(R, [
      { nodeKey: 'r1', capabilityId: 'cap', executionScope: 'match' },
      { nodeKey: 'r2', capabilityId: 'cap', executionScope: 'match' },
      { nodeKey: 'r3', capabilityId: 'cap', executionScope: 'match' },
    ]);
    const entries = compareStreams(left, right);
    expect(entries).toHaveLength(5);
    expect(entries.every((e) => e.kind === 'unmatched' && e.reason === 'ambiguous')).toBe(true);
    expect(entries.map((e) => (e.kind === 'unmatched' ? `${e.side}:${e.stream.nodeKey}` : ''))).toEqual([
      'left:l1',
      'left:l2',
      'right:r1',
      'right:r2',
      'right:r3',
    ]);
  });

  it('marks a one-sided ambiguous group as ambiguous for both sides', () => {
    const left = sideOf(L, [{ nodeKey: 'l1', capabilityId: 'cap', executionScope: 'match' }]);
    const right = sideOf(R, [
      { nodeKey: 'r1', capabilityId: 'cap', executionScope: 'match' },
      { nodeKey: 'r2', capabilityId: 'cap', executionScope: 'match' },
    ]);
    const entries = compareStreams(left, right);
    expect(entries.map((e) => e.kind === 'unmatched' && e.reason)).toEqual(['ambiguous', 'ambiguous', 'ambiguous']);
  });

  it('handles empty sides and accounts for each stream once', () => {
    const spec = [{ nodeKey: 'a', capabilityId: 'cap', executionScope: 'match' }];
    expect(compareStreams(sideOf(L, []), sideOf(R, []))).toEqual([]);
    const entries = compareStreams(sideOf(L, []), sideOf(R, spec));
    expect(entries).toHaveLength(1);
    expect(entries[0]).toMatchObject({
      kind: 'unmatched',
      side: 'right',
      reason: 'noCounterpart',
    });
  });

  it('rejects duplicate node keys in a run or its streams', () => {
    const spec = [{ nodeKey: 'a', capabilityId: 'cap', executionScope: 'match' }];
    const good = sideOf(R, spec);
    const dupNodes = sideOf(L, [...spec, ...spec]);
    expect(() => compareStreams(dupNodes, good)).toThrow(PlatformFailure);
    const dupStreams = {
      ...sideOf(L, spec),
      streams: [stream({ nodeKey: 'a' }), stream({ nodeKey: 'a' })],
    };
    expect(() => compareStreams(dupStreams, good)).toThrow(PlatformFailure);
  });

  it('compares dependency coverage only by explicit upstream key, independent of order', () => {
    const base = { nodeKey: 'a', capabilityId: 'cap', executionScope: 'match' };
    const left = sideOf(L, [
      {
        ...base,
        coverage: [dep('u1'), dep('u2', { coverageMode: 'partial', minimumAcceptedFraction: 0.5 }), dep('gone')],
      },
    ]);
    const right = sideOf(R, [
      {
        ...base,
        coverage: [
          dep('u2', {
            coverageMode: 'custom',
            minimumAcceptedFraction: 0.75,
            missing: 2,
          }),
          dep('u1'),
          dep('new'),
        ],
      },
    ]);
    const [entry] = compareStreams(left, right);
    if (entry.kind !== 'aligned') throw new Error('expected aligned');
    expect(entry.inputCoverage.establishesEqualInputs).toBe(false);
    expect(entry.inputCoverage.dependencies.map((d) => `${d.upstreamNodeKey}:${d.kind}`)).toEqual([
      'u1:same',
      'u2:changed',
      'gone:leftOnly',
      'new:rightOnly',
    ]);
    const changed = entry.inputCoverage.dependencies[1];
    expect(changed.kind === 'changed' && changed.changedFields).toEqual([
      'coverageMode',
      'minimumAcceptedFraction',
      'missing',
    ]);
    expect(changed.kind === 'changed' && changed.right.coverageMode).toBe('custom');
  });

  it('keeps not-reported distinct from a reported empty list and unchanged reports do not prove equal inputs', () => {
    const base = { nodeKey: 'a', capabilityId: 'cap', executionScope: 'match' };
    const [notReported] = compareStreams(sideOf(L, [base]), sideOf(R, [{ ...base, coverage: [] }]));
    if (notReported.kind !== 'aligned') throw new Error('expected aligned');
    expect(notReported.inputCoverage.left).toBeUndefined();
    expect(notReported.inputCoverage.right).toEqual([]);
    expect(notReported.inputCoverage.dependencies).toEqual([]);
    const same = [{ ...base, coverage: [dep('u1')] }];
    const [unchanged] = compareStreams(sideOf(L, same), sideOf(R, same));
    if (unchanged.kind !== 'aligned') throw new Error('expected aligned');
    expect(unchanged.inputCoverage.dependencies[0].kind).toBe('same');
    expect(unchanged.inputCoverage.establishesEqualInputs).toBe(false);
  });

  it('rejects duplicate upstream keys instead of merging dependencies', () => {
    const base = { nodeKey: 'a', capabilityId: 'cap', executionScope: 'match' };
    const left = sideOf(L, [{ ...base, coverage: [dep('u1'), dep('u1', { accepted: 5 })] }]);
    expect(() => compareStreams(left, sideOf(R, [base]))).toThrow(PlatformFailure);
  });
});

describe('describeStreamAvailability', () => {
  const counts = (c: Partial<FactStreamSummary['coverage']>) => ({
    accepted: 0,
    missing: 0,
    failed: 0,
    blocked: 0,
    skipped: 0,
    ...c,
  });
  const kind = (factCount: number, c: Partial<FactStreamSummary['coverage']>) =>
    describeStreamAvailability(stream({ factCount, coverage: counts(c) }));

  it('does not treat zero expected units as completeness', () => {
    expect(kind(0, {})).toEqual({
      kind: 'noExpectedUnits',
      zeroFacts: true,
      allSkipped: false,
    });
  });

  it('reports no accepted results, labelling all-skipped work', () => {
    expect(kind(0, { missing: 1, failed: 1 })).toMatchObject({
      kind: 'noAcceptedResults',
      allSkipped: false,
    });
    expect(kind(0, { skipped: 3 })).toEqual({
      kind: 'noAcceptedResults',
      zeroFacts: true,
      allSkipped: true,
    });
    expect(kind(0, { blocked: 1, skipped: 2 }).allSkipped).toBe(false);
  });

  it.each(['missing', 'failed', 'blocked', 'skipped'] as const)(
    'reports incomplete coverage for any %s count',
    (key) => {
      expect(kind(0, { accepted: 2, [key]: 1 })).toMatchObject({
        kind: 'incompleteCoverage',
        zeroFacts: true,
      });
      expect(kind(4, { accepted: 2, [key]: 1 }).kind).toBe('incompleteCoverage');
    },
  );

  it('distinguishes complete zero-fact and complete fact results', () => {
    expect(kind(0, { accepted: 3 }).kind).toBe('completeZeroFacts');
    expect(kind(7, { accepted: 3 }).kind).toBe('completeFacts');
  });

  it('flags facts without accepted units and invalid counts as incompatible', () => {
    expect(kind(2, { missing: 1 }).kind).toBe('incompatible');
    expect(kind(1, {}).kind).toBe('incompatible');
    expect(kind(0, { accepted: -1 }).kind).toBe('incompatible');
    expect(kind(0, { accepted: 1.5 }).kind).toBe('incompatible');
    expect(kind(-1, { accepted: 1 }).kind).toBe('incompatible');
    expect(kind(Number.NaN, { accepted: 1 }).kind).toBe('incompatible');
  });

  it('attaches availability per side to aligned and unmatched entries', () => {
    const mk = (id: string, c: Partial<FactStreamSummary['coverage']>) => ({
      run: run(id),
      recordingSet: recordingSet(),
      streams: [stream({ coverage: counts(c) })],
    });
    const [entry] = compareStreams(mk(L, { accepted: 1 }), mk(R, { skipped: 1 }));
    expect(entry).toMatchObject({
      kind: 'aligned',
      leftAvailability: { kind: 'completeZeroFacts' },
      rightAvailability: { kind: 'noAcceptedResults', allSkipped: true },
    });
  });
});
