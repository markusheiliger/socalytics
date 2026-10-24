import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ConnectivityContext } from '../../../../src/connectivity/connectivityContext';
import { parseComparisonSelection } from '../../../../src/features/analysis/comparisonPresentation';
import { useAnalysisComparison } from '../../../../src/features/analysis/useAnalysisComparison';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { AnalysisRunStatus, FactStreamSummary } from '../../../../src/platform/models/analysis';
import type { RecordingSetVersion } from '../../../../src/platform/models/recordings';
import { SessionContext, type SessionApi } from '../../../../src/session/sessionContext';
import { makeSession } from '../../session/helpers';

const mocks = vi.hoisted(() => ({
  calls: [] as string[],
  getMatch: vi.fn(),
  getTeam: vi.fn(),
  getRun: vi.fn(),
  listStreams: vi.fn(),
  getSet: vi.fn(),
}));

vi.mock('../../../../src/platform/adapters/matchAdapter', () => ({ createMatchAdapter: () => ({ get: mocks.getMatch }) }));
vi.mock('../../../../src/platform/adapters/teamAdapter', () => ({ createTeamAdapter: () => ({ get: mocks.getTeam }) }));
vi.mock('../../../../src/platform/adapters/analysisRunAdapter', () => ({ createAnalysisRunAdapter: () => ({ getRun: mocks.getRun }) }));
vi.mock('../../../../src/platform/adapters/analysisFactAdapter', () => ({ createAnalysisFactAdapter: () => ({ listStreams: mocks.listStreams }) }));
vi.mock('../../../../src/platform/adapters/recordingLineageAdapter', () => ({ createRecordingLineageAdapter: () => ({ getSet: mocks.getSet }) }));

const L = '11111111-1111-4111-8111-111111111111';
const R = '22222222-2222-4222-8222-222222222222';
const MATCH = '33333333-3333-4333-8333-333333333333';
const TEAM = '44444444-4444-4444-8444-444444444444';
const SET_L = '55555555-5555-4555-8555-555555555555';
const SET_R = '66666666-6666-4666-8666-666666666666';

const run = (runId: string, recordingSetVersionId = SET_L, over: Partial<AnalysisRunStatus> = {}): AnalysisRunStatus => ({
  runId,
  teamId: TEAM,
  matchId: MATCH,
  recordingSetVersionId,
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
  ...over,
});

const stream = (): FactStreamSummary => ({
  nodeKey: 'n1',
  capabilityId: 'cap',
  capabilityVersion: '1.0.0',
  schemaVersion: '1',
  outputType: 'fact',
  executionScope: 'match',
  required: true,
  factCount: 1,
  provenance: { workflow: { id: 'wf', version: '1' }, analystProfile: { id: 'p', digest: 'sha256:aa' }, imageDigest: 'sha256:bb' },
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
});

const recordingSet = (id: string): RecordingSetVersion => ({
  id,
  matchId: MATCH,
  teamId: TEAM,
  finalizedAt: new Date('2026-01-01T00:00:00Z'),
  members: [{ position: 0, recordingVersionId: 'rv1', recordingContentDigest: 'sha256:cc', timelineMappingId: 'tm1', timelineMappingDigest: 'sha256:dd' }],
});

const runs: Record<string, AnalysisRunStatus> = {};

beforeEach(() => {
  vi.resetAllMocks();
  mocks.calls.length = 0;
  runs[L] = run(L, SET_L);
  runs[R] = run(R, SET_L);
  mocks.getMatch.mockImplementation(async () => {
    mocks.calls.push('match');
    return { id: MATCH, teamId: TEAM };
  });
  mocks.getTeam.mockImplementation(async () => {
    mocks.calls.push('team');
    return { id: TEAM };
  });
  mocks.getRun.mockImplementation(async (id: string) => {
    mocks.calls.push(`run:${id}`);
    return { kind: 'found', run: runs[id] };
  });
  mocks.listStreams.mockImplementation(async (id: string) => {
    mocks.calls.push(`streams:${id}`);
    return { kind: 'found', streams: [stream()] };
  });
  mocks.getSet.mockImplementation(async (_match: string, id: string) => {
    mocks.calls.push(`set:${id}`);
    return recordingSet(id);
  });
});

interface Options {
  member?: ReturnType<typeof makeSession>;
  unreachable?: boolean;
}

function setup(search: string, options: Options = {}) {
  const client = new QueryClient();
  const session =
    options.member ?? makeSession({ teamRoles: [{ teamId: TEAM, teamName: 'First', seasonId: 's', role: 'Coach' }] });
  const api = { platform: {}, session, status: 'signedIn' } as unknown as SessionApi;
  const connectivity = (unreachable: boolean) => ({ status: unreachable ? ('unreachable' as const) : ('online' as const), isUnreachable: unreachable, reportNetworkFailure: () => {} });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>
      <ConnectivityContext.Provider value={connectivity(Boolean(options.unreachable))}>
        <SessionContext.Provider value={api}>{children}</SessionContext.Provider>
      </ConnectivityContext.Provider>
    </QueryClientProvider>
  );
  const hook = renderHook(({ query }: { query: string }) => useAnalysisComparison(MATCH, parseComparisonSelection(query)), {
    wrapper,
    initialProps: { query: search },
  });
  return { client, hook };
}

const pairSearch = `left=${L}&right=${R}`;

describe('useAnalysisComparison', () => {
  it('stays selecting or invalid without reads', () => {
    expect(setup('').hook.result.current.state).toBe('selecting');
    expect(setup(`left=${L}`).hook.result.current.state).toBe('selecting');
    const invalid = setup(`left=${L}&right=${L}`).hook.result.current;
    expect(invalid.state).toBe('invalidSelection');
    expect(invalid.invalid).toEqual({ reason: 'identicalIds' });
    expect(mocks.calls).toEqual([]);
  });

  it('reads match, team, both runs, then streams and one shared recording set, and publishes ready', async () => {
    const { hook } = setup(pairSearch);
    expect(hook.result.current.state).toBe('loading');
    await waitFor(() => expect(hook.result.current.state).toBe('ready'));
    expect(mocks.calls.slice(0, 2)).toEqual(['match', 'team']);
    expect(mocks.calls.slice(2, 4).sort()).toEqual([`run:${L}`, `run:${R}`].sort());
    expect(mocks.calls.slice(4).sort()).toEqual([`set:${SET_L}`, `streams:${L}`, `streams:${R}`].sort());
    expect(mocks.calls).toHaveLength(7);
    expect(hook.result.current.model?.left.run.runId).toBe(L);
    expect(hook.result.current.model?.right.run.runId).toBe(R);
  });

  it('reads two distinct recording sets when the runs pin different ones', async () => {
    runs[R] = run(R, SET_R);
    const { hook } = setup(pairSearch);
    await waitFor(() => expect(hook.result.current.state).toBe('ready'));
    expect(mocks.getSet).toHaveBeenCalledTimes(2);
    expect(mocks.listStreams).toHaveBeenCalledTimes(2);
  });

  it('stays loading until every read succeeds and publishes nothing when one side fails', async () => {
    let release: (value: unknown) => void = () => {};
    mocks.listStreams.mockImplementation(async (id: string) => {
      if (id === R) await new Promise((resolve) => (release = resolve));
      return { kind: 'found', streams: [stream()] };
    });
    const { hook } = setup(pairSearch);
    await waitFor(() => expect(mocks.listStreams).toHaveBeenCalledTimes(2));
    await act(async () => {
      await Promise.resolve();
    });
    expect(hook.result.current.state).toBe('loading');
    expect(hook.result.current.model).toBeUndefined();
    await act(async () => release(undefined));
    await waitFor(() => expect(hook.result.current.state).toBe('ready'));

    mocks.getSet.mockRejectedValue(new PlatformFailure('unexpected', { status: 500 }));
    const failed = setup(pairSearch);
    await waitFor(() => expect(failed.hook.result.current.state).toBe('error'));
    expect(failed.hook.result.current.model).toBeUndefined();
  });

  it('maps concealed 404s, refusals, ineligible runs, and incompatible data to their states', async () => {
    mocks.getRun.mockResolvedValue({ kind: 'runNotFound' });
    const notFound = setup(pairSearch);
    await waitFor(() => expect(notFound.hook.result.current.state).toBe('notFound'));

    mocks.getRun.mockImplementation(async (id: string) => ({ kind: 'found', run: runs[id] }));
    mocks.getMatch.mockRejectedValue(new PlatformFailure('notPermitted', { status: 403 }));
    const refused = setup(pairSearch);
    await waitFor(() => expect(refused.hook.result.current.state).toBe('notPermitted'));

    mocks.getMatch.mockResolvedValue({ id: MATCH, teamId: TEAM });
    mocks.getRun.mockClear();
    const noRole = setup(pairSearch, { member: makeSession() });
    await waitFor(() => expect(noRole.hook.result.current.state).toBe('notPermitted'));
    expect(mocks.getRun).not.toHaveBeenCalled();

    runs[R] = run(R, SET_L, { state: 'Running' });
    const running = setup(pairSearch);
    await waitFor(() => expect(running.hook.result.current.state).toBe('invalidSelection'));
    expect(running.hook.result.current.invalid).toEqual({ reason: 'notTerminal', side: 'right' });
    expect(mocks.listStreams).not.toHaveBeenCalled();

    runs[R] = run(R, SET_L, { matchId: '99999999-9999-4999-8999-999999999999' });
    const mismatch = setup(pairSearch);
    await waitFor(() => expect(mismatch.hook.result.current.state).toBe('incompatible'));
  });

  it('shows offline without reads', () => {
    const { hook } = setup(pairSearch, { unreachable: true });
    expect(hook.result.current.state).toBe('offline');
    expect(mocks.calls).toEqual([]);
  });

  it('invalidates ready content on refresh and reads again with the same identities', async () => {
    const { hook } = setup(pairSearch);
    await waitFor(() => expect(hook.result.current.state).toBe('ready'));
    act(() => hook.result.current.refresh());
    expect(hook.result.current.state).toBe('loading');
    expect(hook.result.current.model).toBeUndefined();
    await waitFor(() => expect(hook.result.current.state).toBe('ready'));
    expect(mocks.getMatch).toHaveBeenCalledTimes(2);
    expect(mocks.getRun).toHaveBeenCalledTimes(4);
  });

  it('drops ready content when the pair changes and ignores a late completion of the retired pair', async () => {
    let release: (value: unknown) => void = () => {};
    let waiting = false;
    mocks.getRun.mockImplementation(async (id: string) => {
      if (id === R && !waiting) {
        waiting = true;
        await new Promise((resolve) => (release = resolve));
      }
      return { kind: 'found', run: runs[id] };
    });
    const { hook } = setup(pairSearch);
    await waitFor(() => expect(mocks.getRun).toHaveBeenCalledTimes(2));
    hook.rerender({ query: `left=${L}` });
    expect(hook.result.current.state).toBe('selecting');
    await act(async () => release(undefined));
    expect(hook.result.current.state).toBe('selecting');
    expect(hook.result.current.model).toBeUndefined();
    expect(mocks.listStreams).not.toHaveBeenCalled();
  });

  it('leaves no comparison or shared cache entry after unmount and never reads payloads or grants', async () => {
    const { client, hook } = setup(pairSearch);
    await waitFor(() => expect(hook.result.current.state).toBe('ready'));
    expect(client.getQueryCache().getAll().every((query) => query.queryKey[0] === 'analysis-comparison')).toBe(true);
    hook.unmount();
    expect(client.getQueryCache().getAll()).toHaveLength(0);
    expect(mocks.calls.some((call) => /fact|grant|object|payload/i.test(call))).toBe(false);
  });
});
