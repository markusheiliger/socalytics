import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AnalysisTimeline, type TimelineView } from '../../../../../src/features/analysis/timeline/AnalysisTimeline';
import type { AnalysisFactAdapter, FactRequest } from '../../../../../src/platform/adapters/analysisFactAdapter';
import type { CoverageWindow, FactStreamSummary, FactsOutcome } from '../../../../../src/platform/models/analysis';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';
import '../../../../../src/i18n';

const provenance = { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' };

const stream = (over: Partial<FactStreamSummary> = {}): FactStreamSummary => ({
  nodeKey: 'detect-1',
  capabilityId: 'detect',
  capabilityVersion: '1.2',
  schemaVersion: '1',
  outputType: 'events',
  executionScope: 'Segment',
  required: true,
  factCount: 2,
  provenance,
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
  ...over,
});

const accepted = (endMs: number): CoverageWindow[] => [{ startMs: 0, endMs, state: 'accepted' }];

interface LaneData {
  starts: number[];
  windows: CoverageWindow[];
}

function adapterFor(data: LaneData): AnalysisFactAdapter {
  const listFacts = vi.fn(
    async (request: FactRequest): Promise<FactsOutcome> => ({
      kind: 'found',
      page: {
        run: { runId: 'run-1', matchId: 'm', state: 'Completed' },
        stream: { nodeKey: request.nodeKey, capability: request.capabilityId, schemaVersion: '1', outputType: 'events', executionScope: 'Segment', provenance },
        range: {},
        items: data.starts.map((startMs, ordinal) => ({ acceptedResultId: 'r', ordinal, kind: 'k', startMs, endMs: startMs + 5, confidence: 1, payload: {} })),
        coverage: data.windows,
        continuationToken: undefined,
      },
    }),
  );
  return { listStreams: vi.fn(), listFacts };
}

function Harness({ adapter, streams, onShowFacts }: { adapter: AnalysisFactAdapter; streams: FactStreamSummary[]; onShowFacts: (nodeKey: string, fromMs: number, toMs: number) => void }) {
  const [view, setView] = useState<TimelineView>({});
  return <AnalysisTimeline adapter={adapter} runId="run-1" runState="Completed" streams={streams} view={view} onViewChange={setView} onSelectStream={() => {}} onShowFacts={onShowFacts} />;
}

function renderTimeline(data: LaneData, streamOver: Partial<FactStreamSummary> = {}) {
  const onShowFacts = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <Harness adapter={adapterFor(data)} streams={[stream(streamOver)]} onShowFacts={onShowFacts} />
    </QueryClientProvider>,
  );
  return onShowFacts;
}

const loaded = () => screen.findByRole('group', { name: 'Zoom' });
const bins = () => within(screen.getByRole('group', { name: 'Bins of detect 1.2 (detect-1)' })).getAllByRole('button');
const bin = (range: string) => bins().find((button) => button.getAttribute('aria-label')?.startsWith(`from ${range} (end excluded)`))!;
const selection = () => screen.getByRole('group', { name: 'Selection' });

afterEach(() => vi.unstubAllGlobals());

describe('timeline selection and jump', () => {
  it('selects a span of bins and passes its range to the jump', async () => {
    const user = userEvent.setup();
    const onShowFacts = renderTimeline({ starts: [0, 35000], windows: accepted(60000) });
    await loaded();

    await user.click(bin('0:00 to 0:10'));
    await user.keyboard('{Shift>}');
    await user.click(bin('0:20 to 0:30'));
    await user.keyboard('{/Shift}');

    expect(within(selection()).getByText('0:00–0:30')).toBeInTheDocument();
    expect(within(selection()).getByText('1 facts')).toBeInTheDocument();
    await user.click(within(selection()).getByRole('button', { name: 'Show facts of detect-1 for from 0:00 to 0:30 (end excluded)' }));
    expect(onShowFacts).toHaveBeenCalledWith('detect-1', 0, 30000);
  });

  it('clears the selection with an announcement when the extent moves it out of view', async () => {
    const user = userEvent.setup();
    renderTimeline({ starts: [0, 6_000_000], windows: accepted(6_010_000) });
    await loaded();
    await user.click(screen.getByRole('button', { name: '1 minute' }));
    await user.click(bin('0:00 to 1:00'));
    expect(screen.getByRole('group', { name: 'Selection' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Later' }));

    await waitFor(() => expect(screen.queryByRole('group', { name: 'Selection' })).not.toBeInTheDocument());
    expect(screen.getByRole('status')).toHaveTextContent('Selection cleared.');
  });

  it('describes a not-analysed range and still jumps to it', async () => {
    const user = userEvent.setup();
    const onShowFacts = renderTimeline({
      starts: [0],
      windows: [
        { startMs: 0, endMs: 30000, state: 'accepted' },
        { startMs: 30000, endMs: 60000, state: 'skipped' },
      ],
    });
    await loaded();

    await user.click(bin('0:40 to 0:50'));

    expect(within(selection()).getByText('Not analysed')).toBeInTheDocument();
    expect(within(selection()).queryByText(/\d+ facts|At least/)).not.toBeInTheDocument();
    await user.click(within(selection()).getByRole('button', { name: /^Show facts of detect-1/ }));
    expect(onShowFacts).toHaveBeenCalledWith('detect-1', 40000, 50000);
  });

  it('marks the count of a partial lane as at least N, based on loaded facts', async () => {
    const user = userEvent.setup();
    renderTimeline({ starts: [0, 35000], windows: accepted(60000) }, { factCount: 10 });
    await loaded();

    await user.click(bin('0:30 to 0:40'));

    expect(within(selection()).getByText('At least 1 facts, based on loaded facts')).toBeInTheDocument();
  });

  it('shows the not-loaded state label for bins after the loaded horizon of a partial lane', async () => {
    const user = userEvent.setup();
    renderTimeline({ starts: [0, 35000], windows: accepted(60000) }, { factCount: 10 });
    await loaded();

    await user.click(bin('0:50 to 1:00'));

    expect(within(selection()).getByText('Facts beyond the loading bound not shown')).toBeInTheDocument();
    expect(within(selection()).queryByText(/At least/)).not.toBeInTheDocument();
  });
});

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const MATCH = '00000000-0000-4000-8000-000000000021';
const RUN = '00000000-0000-4000-8000-0000000000d1';

const runDto = {
  runId: RUN,
  teamId: TEAM,
  matchId: MATCH,
  recordingSetVersionId: '00000000-0000-4000-8000-0000000000c1',
  workflowId: 'default',
  workflowVersion: '3',
  state: 'Completed',
  createdAt: '2026-10-12T12:00:00Z',
  requestedCapabilities: ['detect'],
  nodes: [
    {
      nodeKey: 'detect-1',
      capabilityId: 'detect',
      capabilityVersion: '1.2',
      tier: 'LowLevel',
      executionScope: 'Segment',
      state: 'Completed',
      jobs: { waiting: 0, ready: 0, claimed: 0, completed: 4, failed: 0, blocked: 0 },
    },
  ],
};
const matchDto = { id: MATCH, teamId: TEAM, opponent: { name: 'Rivals FC' }, kickoffAt: '2026-11-01T15:00:00Z', homeAway: 'Home', competition: 'League', createdAt: '2026-10-12T12:00:00Z', version: 1 };
const teamDto = { id: TEAM, seasonId: SEASON, seasonState: 'Active', name: 'First Team', createdAt: '2026-10-12T12:00:00Z', version: 1 };
const streamsDto = {
  items: [
    {
      nodeKey: 'detect-1',
      capability: { id: 'detect', version: '1.2' },
      schemaVersion: '1',
      outputType: 'foundational-facts',
      executionScope: 'Segment',
      required: true,
      factCount: 2,
      provenance,
      coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 1 },
    },
  ],
};
const factsDto = (items: number[], coverage: unknown[]) => ({
  run: { runId: RUN, matchId: MATCH, state: 'Completed' },
  stream: { nodeKey: 'detect-1', capability: { id: 'detect', version: '1.2' }, schemaVersion: '1', outputType: 'foundational-facts', executionScope: 'Segment', provenance },
  range: {},
  items: items.map((startMs, ordinal) => ({ acceptedResultId: '00000000-0000-4000-8000-0000000000e1', ordinal, kind: 'pass', startMs, endMs: startMs + 1000, confidence: 0.5, payload: { a: 1 } })),
  coverage,
});

// The whole-stream reads of the lanes carry no range; the range read of the fact list does.
function stubPlatform(windows: unknown[], starts = [1000, 6_000_000]) {
  const factUrls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      if (url.includes('/fact-streams')) return json(200, streamsDto);
      if (url.includes('/facts')) {
        factUrls.push(url);
        const ranged = url.includes('fromMs=');
        return json(200, factsDto(ranged ? [] : starts, windows));
      }
      if (url.includes('/analysis/runs/')) return json(200, runDto);
      if (url.includes('/matches/')) return json(200, matchDto);
      return json(200, teamDto);
    }),
  );
  return factUrls;
}

const coach = () =>
  fakeAdapter({
    restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role: 'Coach' }] })),
  });
const path = `/matches/${MATCH}/analysis-runs/${RUN}`;

describe('timeline jump on the run page', () => {
  it('passes the selected range to the fact list, closes the timeline, and keeps zoom and extent on return', async () => {
    const user = userEvent.setup();
    const factUrls = stubPlatform([{ startMs: 0, endMs: 6_010_000, state: 'accepted' }]);
    const { router } = renderApp(coach(), path);
    await user.click(await screen.findByRole('button', { name: '1 minute' }));
    await user.click(screen.getByRole('button', { name: 'Later' }));
    expect(screen.getByText('Showing 15:00–45:00')).toBeInTheDocument();
    await user.click(bin('15:00 to 16:00'));

    await user.click(screen.getByRole('button', { name: /^Show facts of detect-1 for from 15:00 to 16:00/ }));

    await waitFor(() => expect(router.state.location.search).toBe('?stream=detect-1&fromMs=900000&toMs=960000'));
    await waitFor(() => expect(factUrls.some((url) => url.includes('fromMs=900000') && url.includes('toMs=960000'))).toBe(true));
    expect(screen.queryByRole('region', { name: 'Timeline' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('From')).toHaveValue('15:00');

    await user.click(screen.getByRole('button', { name: 'Back to timeline' }));

    expect(await screen.findByText('Showing 15:00–45:00')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '1 minute' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.queryByRole('group', { name: 'Selection' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Back to timeline' })).not.toBeInTheDocument();
  });

  it('lands a not-analysed range on the fact list with its coverage', async () => {
    const user = userEvent.setup();
    stubPlatform([
      { startMs: 0, endMs: 30000, state: 'accepted' },
      { startMs: 30000, endMs: 60000, state: 'skipped' },
    ], [1000, 5000]);
    const { router } = renderApp(coach(), path);
    await loaded();

    await user.click(bin('0:40 to 0:50'));
    await user.click(within(selection()).getByRole('button', { name: /^Show facts of detect-1 for / }));

    await waitFor(() => expect(router.state.location.search).toBe('?stream=detect-1&fromMs=40000&toMs=50000'));
    expect((await screen.findAllByText(/skipped/i)).length).toBeGreaterThan(0);
  });
});
