import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConnectivityContext } from '../../../../../src/connectivity/connectivityContext';
import { analysisTimelineKey } from '../../../../../src/features/analysis/analysisKeys';
import { AnalysisTimeline, type TimelineView } from '../../../../../src/features/analysis/timeline/AnalysisTimeline';
import type { AnalysisFactAdapter } from '../../../../../src/platform/adapters/analysisFactAdapter';
import type { FactStreamSummary } from '../../../../../src/platform/models/analysis';
import type { TeamRoleName } from '../../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';
import '../../../../../src/i18n';

vi.mock('../../../../../src/features/lineage/AnalysisRunStates', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../../../src/features/lineage/AnalysisRunStates')>()),
  RUN_REFRESH_MS: 40,
}));

const provenance = { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' };

const stream: FactStreamSummary = {
  nodeKey: 'detect-1',
  capabilityId: 'detect',
  capabilityVersion: '1.2',
  schemaVersion: '1',
  outputType: 'events',
  executionScope: 'Segment',
  required: true,
  factCount: 1,
  provenance,
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
};

const adapter: AnalysisFactAdapter = {
  listStreams: vi.fn(),
  listFacts: vi.fn(async (request) => ({
    kind: 'found' as const,
    page: {
      run: { runId: 'run-1', matchId: 'm', state: 'Completed' },
      stream: { nodeKey: request.nodeKey, capability: request.capabilityId, schemaVersion: '1', outputType: 'events', executionScope: 'Segment', provenance },
      range: {},
      items: [{ acceptedResultId: 'r', ordinal: 0, kind: 'k', startMs: 1000, endMs: 2000, confidence: 1, payload: {} }],
      coverage: [{ startMs: 0, endMs: 60000, state: 'accepted' }],
      continuationToken: undefined,
    },
  })),
};

const timelineQueries = (client: QueryClient) => client.getQueryCache().findAll({ queryKey: analysisTimelineKey });

function Harness({ unreachable }: { unreachable: boolean }) {
  const [view, setView] = useState<TimelineView>({});
  return (
    <ConnectivityContext.Provider value={{ status: unreachable ? 'unreachable' : 'online', isUnreachable: unreachable, reportNetworkFailure: () => {} }}>
      <AnalysisTimeline adapter={adapter} runId="run-1" streams={[stream]} view={view} onViewChange={setView} onSelectStream={() => {}} />
    </ConnectivityContext.Provider>
  );
}

const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const MATCH = '00000000-0000-4000-8000-000000000021';
const RUN = '00000000-0000-4000-8000-0000000000d1';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });

const runDto = {
  runId: RUN,
  teamId: TEAM,
  matchId: MATCH,
  recordingSetVersionId: '00000000-0000-4000-8000-0000000000c1',
  workflowId: 'default',
  workflowVersion: '3',
  state: 'Running',
  createdAt: '2026-10-12T12:00:00Z',
  requestedCapabilities: ['detect'],
  nodes: [
    {
      nodeKey: 'detect-1',
      capabilityId: 'detect',
      capabilityVersion: '1.2',
      tier: 'LowLevel',
      executionScope: 'Segment',
      state: 'Running',
      jobs: { waiting: 0, ready: 0, claimed: 1, completed: 0, failed: 0, blocked: 0 },
    },
  ],
};
const streamsDto = {
  items: [
    {
      nodeKey: 'detect-1',
      capability: { id: 'detect', version: '1.2' },
      schemaVersion: '1',
      outputType: 'foundational-facts',
      executionScope: 'Segment',
      required: true,
      factCount: 1,
      provenance,
      coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
    },
  ],
};
const factsDto = {
  run: { runId: RUN, matchId: MATCH, state: 'Running' },
  stream: { nodeKey: 'detect-1', capability: { id: 'detect', version: '1.2' }, schemaVersion: '1', outputType: 'foundational-facts', executionScope: 'Segment', provenance },
  range: {},
  items: [{ acceptedResultId: '00000000-0000-4000-8000-0000000000e1', ordinal: 0, kind: 'pass', startMs: 1000, endMs: 2000, confidence: 0.5, payload: {} }],
  coverage: [{ startMs: 0, endMs: 60000, state: 'accepted' }],
};

// Serves the page until the run read starts to answer with `refusal`.
function stubPlatform(refusal: () => Response | undefined) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      if (url.includes('/fact-streams')) return json(200, streamsDto);
      if (url.includes('/facts')) return json(200, factsDto);
      if (url.includes('/analysis/runs/')) return refusal() ?? json(200, runDto);
      if (url.includes('/matches/')) {
        return json(200, { id: MATCH, teamId: TEAM, opponent: { name: 'Rivals FC' }, kickoffAt: '2026-11-01T15:00:00Z', homeAway: 'Home', competition: 'League', createdAt: '2026-10-12T12:00:00Z', version: 1 });
      }
      return json(200, { id: TEAM, seasonId: SEASON, seasonState: 'Active', name: 'First Team', createdAt: '2026-10-12T12:00:00Z', version: 1 });
    }),
  );
}

const asCoach = (role: TeamRoleName = 'Coach') =>
  fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role }] })) });

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('timeline discard', () => {
  it('removes the timeline queries on unmount and writes nothing to browser storage', async () => {
    const writes = vi.spyOn(Storage.prototype, 'setItem');
    const client = new QueryClient();
    const view = render(
      <QueryClientProvider client={client}>
        <Harness unreachable={false} />
      </QueryClientProvider>,
    );
    await screen.findByRole('group', { name: 'Zoom' });
    expect(timelineQueries(client).length).toBeGreaterThan(0);

    view.unmount();
    await waitFor(() => expect(timelineQueries(client)).toHaveLength(0));
    expect(writes).not.toHaveBeenCalled();
    expect(localStorage).toHaveLength(0);
    expect(sessionStorage).toHaveLength(0);
  });

  it('removes the timeline queries and shows the connection-required state offline', async () => {
    const client = new QueryClient();
    const ui = (unreachable: boolean) => (
      <QueryClientProvider client={client}>
        <Harness unreachable={unreachable} />
      </QueryClientProvider>
    );
    const view = render(ui(false));
    await screen.findByRole('group', { name: 'Zoom' });
    expect(timelineQueries(client).length).toBeGreaterThan(0);

    view.rerender(ui(true));
    expect(await screen.findByRole('alert')).toHaveTextContent('connection');
    await waitFor(() => expect(timelineQueries(client)).toHaveLength(0));
    expect(screen.queryByRole('group', { name: 'Zoom' })).toBeNull();
    expect(localStorage).toHaveLength(0);
    expect(sessionStorage).toHaveLength(0);
  });

  it('removes the timeline queries when a read is refused as forbidden', async () => {
    let refuse = false;
    stubPlatform(() => (refuse ? json(403, { code: 'forbidden' }) : undefined));
    const { queryClient } = renderApp(asCoach(), `/matches/${MATCH}/analysis-runs/${RUN}`);
    await screen.findByRole('group', { name: 'Zoom' });
    expect(timelineQueries(queryClient).length).toBeGreaterThan(0);

    refuse = true;
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
    await waitFor(() => expect(timelineQueries(queryClient)).toHaveLength(0));
    expect(screen.queryByRole('group', { name: 'Zoom' })).toBeNull();
  });

  it('removes the timeline queries when a read is refused as not found', async () => {
    let refuse = false;
    stubPlatform(() => (refuse ? json(404, { code: 'analysis-run-not-found' }) : undefined));
    const { queryClient } = renderApp(asCoach('Viewer'), `/matches/${MATCH}/analysis-runs/${RUN}`);
    await screen.findByRole('group', { name: 'Zoom' });
    expect(timelineQueries(queryClient).length).toBeGreaterThan(0);

    refuse = true;
    expect(await screen.findByText('This page does not exist.')).toBeInTheDocument();
    await waitFor(() => expect(timelineQueries(queryClient)).toHaveLength(0));
    expect(screen.queryByRole('group', { name: 'Zoom' })).toBeNull();
  });
});
