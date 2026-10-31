import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { AnalysisTimeline, type TimelineView } from '../../../../../src/features/analysis/timeline/AnalysisTimeline';
import type { AnalysisFactAdapter, FactRequest } from '../../../../../src/platform/adapters/analysisFactAdapter';
import type { CoverageWindow, FactStreamSummary, FactsOutcome } from '../../../../../src/platform/models/analysis';
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
  factCount: 4,
  provenance,
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
  ...over,
});

interface LaneData {
  starts: number[];
  windows: CoverageWindow[];
}

const accepted = (endMs: number): CoverageWindow[] => [{ startMs: 0, endMs, state: 'accepted' }];

function adapterFor(data: () => Record<string, LaneData>): AnalysisFactAdapter {
  const listFacts = vi.fn(async (request: FactRequest): Promise<FactsOutcome> => {
    const lane = data()[request.nodeKey];
    return {
      kind: 'found',
      page: {
        run: { runId: 'run-1', matchId: 'm', state: 'Completed' },
        stream: { nodeKey: request.nodeKey, capability: request.capabilityId, schemaVersion: '1', outputType: 'events', executionScope: 'Segment', provenance },
        range: {},
        items: lane.starts.map((startMs, ordinal) => ({ acceptedResultId: 'r', ordinal, kind: 'k', startMs, endMs: startMs + 5, confidence: 1, payload: {} })),
        coverage: lane.windows,
        continuationToken: undefined,
      },
    };
  });
  return { listStreams: vi.fn(), listFacts };
}

interface HarnessProps {
  adapter: AnalysisFactAdapter;
  streams: FactStreamSummary[];
  runState?: string;
  nodeStates?: Record<string, string>;
}

function Harness({ adapter, streams, runState, nodeStates }: HarnessProps) {
  const [view, setView] = useState<TimelineView>({});
  return <AnalysisTimeline adapter={adapter} runId="run-1" runState={runState} streams={streams} nodeStates={nodeStates} view={view} onViewChange={setView} onSelectStream={() => {}} />;
}

const wrap = (props: HarnessProps) => (
  <QueryClientProvider client={new QueryClient()}>
    <Harness {...props} />
  </QueryClientProvider>
);

// Keeps one client across rerenders so that a changed lane key reloads the lane.
function setup(props: HarnessProps) {
  const client = new QueryClient();
  const ui = (next: HarnessProps) => (
    <QueryClientProvider client={client}>
      <Harness {...next} />
    </QueryClientProvider>
  );
  const view = render(ui(props));
  return { ...view, update: (next: HarnessProps) => view.rerender(ui(next)) };
}

const loaded = () => screen.findByRole('group', { name: 'Zoom' });
const binsOf = (name: string) => within(screen.getByRole('group', { name })).getAllByRole('button');

describe('AnalysisTimeline', () => {
  it('shows one lane per stream, also for two nodes of the same capability', async () => {
    const data = { 'detect-1': { starts: [0, 35000], windows: accepted(60000) }, 'detect-2': { starts: [10000], windows: accepted(60000) }, 'track-1': { starts: [], windows: accepted(60000) } };
    const adapter = adapterFor(() => data);
    render(wrap({ adapter, streams: [stream({ nodeKey: 'detect-1', factCount: 2 }), stream({ nodeKey: 'detect-2', factCount: 1 }), stream({ nodeKey: 'track-1', capabilityId: 'track', factCount: 0 })] }));
    await loaded();

    expect(screen.getByRole('region', { name: 'Timeline of detect 1.2 (detect-1)' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Timeline of detect 1.2 (detect-2)' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Timeline of track 1.2 (track-1)' })).toBeInTheDocument();
    expect(screen.getAllByRole('group', { name: /^Bins of / })).toHaveLength(2);
    expect(binsOf('Bins of detect 1.2 (detect-1)')).toHaveLength(30);
  });

  it('labels the shared axis and the bins beyond 90 minutes', async () => {
    const adapter = adapterFor(() => ({ 'detect-1': { starts: [0, 6_000_000], windows: accepted(6_010_000) } }));
    render(wrap({ adapter, streams: [stream({ factCount: 2 })] }));
    await loaded();

    expect(screen.getByText('Showing 0:00–150:00')).toBeInTheDocument();
    for (const tick of ['0:00', '25:00', '75:00', '100:00', '125:00', '150:00']) expect(screen.getByText(tick)).toBeInTheDocument();
    const names = binsOf('Bins of detect 1.2 (detect-1)').map((bin) => bin.getAttribute('aria-label'));
    expect(names).toContain('from 100:00 to 105:00 (end excluded): Facts present, 1');
  });

  it('changes the zoom and the extent', async () => {
    const user = userEvent.setup();
    const adapter = adapterFor(() => ({ 'detect-1': { starts: [0, 6_000_000], windows: accepted(6_010_000) } }));
    render(wrap({ adapter, streams: [stream({ factCount: 2 })] }));
    await loaded();
    expect(screen.getByRole('button', { name: '5 minutes' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Earlier' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: '1 minute' }));
    expect(screen.getByRole('button', { name: '1 minute' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByText('Current zoom: 1 minute per bin')).toBeInTheDocument();
    expect(screen.getByText('Showing 0:00–30:00')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Zoom changed to 1 minute per bin.');

    await user.click(screen.getByRole('button', { name: 'Later' }));
    expect(screen.getByText('Showing 15:00–45:00')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Showing 15:00–45:00.');
    expect(screen.getByRole('button', { name: 'Earlier' })).toBeEnabled();

    await user.click(screen.getByRole('button', { name: 'Earlier' }));
    expect(screen.getByText('Showing 0:00–30:00')).toBeInTheDocument();
  });

  it('chooses the default zoom once and keeps zoom and extent when the lanes reload', async () => {
    const user = userEvent.setup();
    let data: Record<string, LaneData> = { 'detect-1': { starts: [0, 6_000_000], windows: accepted(6_010_000) } };
    const adapter = adapterFor(() => data);
    const { update } = setup({ adapter, streams: [stream({ factCount: 2 })] });
    await loaded();
    await user.click(screen.getByRole('button', { name: '1 minute' }));
    await user.click(screen.getByRole('button', { name: 'Later' }));
    expect(screen.getByText('Showing 15:00–45:00')).toBeInTheDocument();

    // The new extent would make 10 seconds the default zoom.
    data = { 'detect-1': { starts: [0, 35000], windows: accepted(60000) } };
    update({ adapter, streams: [stream({ factCount: 3 })] });
    await waitFor(() => expect(adapter.listFacts).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByText('Updating…')).not.toBeInTheDocument());

    expect(screen.getByRole('button', { name: '1 minute' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: '10 seconds' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByText('Current zoom: 1 minute per bin')).toBeInTheDocument();
  });

  it('agrees with the reported count when every fact is loaded', async () => {
    const adapter = adapterFor(() => ({ 'detect-1': { starts: [0, 10000, 35000, 100000], windows: accepted(110000) } }));
    render(wrap({ adapter, streams: [stream({ factCount: 4 })] }));
    await loaded();

    expect(screen.getByText('4 facts reported')).toBeInTheDocument();
    expect(screen.queryByText('Partial')).not.toBeInTheDocument();
    const total = binsOf('Bins of detect 1.2 (detect-1)').reduce((sum, bin) => sum + Number(/: Facts present, (\d+)$/.exec(bin.getAttribute('aria-label') ?? '')?.[1] ?? 0), 0);
    expect(total).toBe(4);
  });

  it('states partial density and labels the bins beyond the loaded facts', async () => {
    const adapter = adapterFor(() => ({ 'detect-1': { starts: [0, 35000, 100000], windows: accepted(300000) } }));
    render(wrap({ adapter, streams: [stream({ factCount: 10 })] }));
    await loaded();

    expect(screen.getByText('10 facts reported')).toBeInTheDocument();
    expect(screen.getByText('Partial')).toBeInTheDocument();
    expect(screen.getByText(/Based on 3 of 10 facts/)).toBeInTheDocument();
    const names = binsOf('Bins of detect 1.2 (detect-1)').map((bin) => bin.getAttribute('aria-label'));
    expect(names).toContain('from 0:40 to 0:50 (end excluded): Analysed, no facts');
    expect(names).toContain('from 1:40 to 1:50 (end excluded): Facts present, 1');
    expect(names).toContain('from 1:50 to 2:00 (end excluded): Facts beyond the loading bound not shown');
    expect(names).toContain('from 4:50 to 5:00 (end excluded): Facts beyond the loading bound not shown');
  });

  it('renders markup-like keys, capability ids, and states as literal text', async () => {
    const key = '<img src=x onerror=alert(1)>';
    const capability = '<b>cap</b>';
    const adapter = adapterFor(() => ({ [key]: { starts: [0], windows: [{ startMs: 0, endMs: 5000, state: '<s>bad</s>' }, { startMs: 5000, endMs: 10000, state: 'accepted' }] } }));
    const user = userEvent.setup();
    const { container } = render(wrap({ adapter, streams: [stream({ nodeKey: key, capabilityId: capability, factCount: 1 })], runState: '<script>x</script>', nodeStates: { [key]: '<u>odd</u>' } }));
    await loaded();

    expect(screen.getByRole('region', { name: `Timeline of ${capability} 1.2 (${key})` })).toBeInTheDocument();
    expect(screen.getByText(`${capability} 1.2`)).toBeInTheDocument();
    expect(screen.getByText(key)).toBeInTheDocument();
    expect(binsOf(`Bins of ${capability} 1.2 (${key})`).some((bin) => bin.getAttribute('aria-label')?.includes('Not analysed (unknown state: <s>bad</s>)'))).toBe(true);

    await user.click(screen.getByRole('button', { name: `Show details of ${key}` }));
    expect(screen.getByText('Run state: <script>x</script>')).toBeInTheDocument();
    expect(screen.getByText('Node state: <u>odd</u>')).toBeInTheDocument();
    expect(container.querySelector('img, b, s, u, script')).toBeNull();
  });
});
