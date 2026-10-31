import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AnalysisTimeline, type TimelineView } from '../../../../../src/features/analysis/timeline/AnalysisTimeline';
import { PlatformFailure } from '../../../../../src/platform/failure';
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
  factCount: 0,
  provenance,
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
  ...over,
});

const accepted = (endMs: number): CoverageWindow[] => [{ startMs: 0, endMs, state: 'accepted' }];

type Behaviour = { starts: number[]; windows: CoverageWindow[] } | { outcome: FactsOutcome } | { throws: unknown } | { rejectsContinuation: true };

function pageFor(request: FactRequest, starts: number[], windows: CoverageWindow[], token?: string): FactsOutcome {
  return {
    kind: 'found',
    page: {
      run: { runId: 'run-1', matchId: 'm', state: 'Completed' },
      stream: { nodeKey: request.nodeKey, capability: request.capabilityId, schemaVersion: '1', outputType: 'events', executionScope: 'Segment', provenance },
      range: {},
      items: starts.map((startMs, ordinal) => ({ acceptedResultId: 'r', ordinal, kind: 'k', startMs, endMs: startMs + 5, confidence: 1, payload: {} })),
      coverage: windows,
      continuationToken: token,
    },
  };
}

function adapterFor(lanes: () => Record<string, Behaviour>, gate?: () => Promise<void> | undefined): AnalysisFactAdapter {
  const listFacts = vi.fn(async (request: FactRequest): Promise<FactsOutcome> => {
    await gate?.();
    const lane = lanes()[request.nodeKey];
    if ('throws' in lane) throw lane.throws;
    if ('outcome' in lane) return lane.outcome;
    if ('rejectsContinuation' in lane) return request.continuationToken ? { kind: 'continuationRejected' } : pageFor(request, [0], accepted(60000), 't1');
    return pageFor(request, lane.starts, lane.windows);
  });
  return { listStreams: vi.fn(), listFacts };
}

interface HarnessProps {
  adapter: AnalysisFactAdapter;
  streams: FactStreamSummary[];
  runState?: string;
  nodeStates?: Record<string, string>;
  onSelectStream?: (nodeKey: string) => void;
}

function Harness({ adapter, streams, runState, nodeStates, onSelectStream }: HarnessProps) {
  const [view, setView] = useState<TimelineView>({});
  return <AnalysisTimeline adapter={adapter} runId="run-1" runState={runState} streams={streams} nodeStates={nodeStates} view={view} onViewChange={setView} onSelectStream={onSelectStream ?? (() => {})} />;
}

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
const lane = (key: string) => screen.getByRole('region', { name: `Timeline of detect 1.2 (${key})` });
const binLabels = (key: string) =>
  within(screen.getByRole('group', { name: `Bins of detect 1.2 (${key})` }))
    .getAllByRole('button')
    .map((bin) => bin.getAttribute('aria-label') ?? '');

const healthy: Behaviour = { starts: [0, 35000], windows: accepted(60000) };

afterEach(() => vi.restoreAllMocks());

describe('timeline lane states', () => {
  it.each([
    ['capabilityNotInRun', { outcome: { kind: 'capabilityNotInRun' } } as Behaviour, "The chosen capability is not part of this run. Choose one of the run's fact streams."],
    ['unavailable', { rejectsContinuation: true } as Behaviour, 'The timeline of this stream is unavailable. The platform did not accept the position in the list twice.'],
    ['error', { throws: new Error('boom') } as Behaviour, 'The timeline of this stream could not be loaded.'],
    ['connectionRequired', { throws: new PlatformFailure('unreachable') } as Behaviour, 'A connection is required to show the timeline.'],
    ['noCoverage', { starts: [], windows: [] } as Behaviour, 'The platform reports no coverage windows for this selection.'],
    ['completeWithoutFacts', { starts: [], windows: accepted(60000) } as Behaviour, 'The analysis is complete and produced no facts.'],
  ])('shows the %s state with its own label and no bins', async (_name, behaviour, label) => {
    const adapter = adapterFor(() => ({ 'ok-1': healthy, 'odd-1': behaviour }));
    render(
      <QueryClientProvider client={new QueryClient()}>
        <Harness adapter={adapter} streams={[stream({ nodeKey: 'ok-1', factCount: 2 }), stream({ nodeKey: 'odd-1' })]} />
      </QueryClientProvider>,
    );
    await loaded();
    await waitFor(() => expect(within(lane('odd-1')).getByText(label)).toBeInTheDocument());

    expect(within(lane('odd-1')).queryByRole('group', { name: /^Bins of / })).not.toBeInTheDocument();
    expect(within(lane('odd-1')).queryByText(/Facts present|, 0/)).not.toBeInTheDocument();
    expect(within(lane('ok-1')).getByRole('group', { name: /^Bins of / })).toBeInTheDocument();
  });

  it('offers a reload for an unavailable lane and an error lane', async () => {
    const user = userEvent.setup();
    let fail = true;
    const adapter = adapterFor(() => ({ 'ok-1': healthy, 'odd-1': fail ? { throws: new Error('boom') } : healthy }));
    render(
      <QueryClientProvider client={new QueryClient()}>
        <Harness adapter={adapter} streams={[stream({ nodeKey: 'ok-1', factCount: 2 }), stream({ nodeKey: 'odd-1', factCount: 2 })]} />
      </QueryClientProvider>,
    );
    await loaded();
    await within(lane('odd-1')).findByText('The timeline of this stream could not be loaded.');

    fail = false;
    await user.click(within(lane('odd-1')).getByRole('button', { name: 'Reload the timeline' }));
    await within(lane('odd-1')).findByRole('group', { name: /^Bins of / });
  });

  it('shows an in-progress lane without density and never a zero', async () => {
    const adapter = adapterFor(() => ({ 'detect-1': { starts: [], windows: accepted(60000) } }));
    render(
      <QueryClientProvider client={new QueryClient()}>
        <Harness adapter={adapter} streams={[stream()]} runState="Running" />
      </QueryClientProvider>,
    );
    await loaded();

    expect(within(lane('detect-1')).getByText('The analysis is still in progress; no facts yet.')).toBeInTheDocument();
    expect(within(lane('detect-1')).queryByRole('group', { name: /^Bins of / })).not.toBeInTheDocument();
    expect(within(lane('detect-1')).queryByText(/analysis is complete/)).not.toBeInTheDocument();
  });

  it('shows no zero count outside accepted coverage and the notLoaded label in a partial lane', async () => {
    const windows: CoverageWindow[] = [
      { startMs: 0, endMs: 60000, state: 'accepted' },
      { startMs: 60000, endMs: 120000, state: 'failed' },
      { startMs: 120000, endMs: 180000, state: 'skipped' },
    ];
    const adapter = adapterFor(() => ({ 'detect-1': { starts: [10000], windows } }));
    render(
      <QueryClientProvider client={new QueryClient()}>
        <Harness adapter={adapter} streams={[stream({ factCount: 10 })]} runState="Completed" />
      </QueryClientProvider>,
    );
    await loaded();

    const bins = within(screen.getByRole('group', { name: 'Bins of detect 1.2 (detect-1)' })).getAllByRole('button');
    const names = bins.map((bin) => bin.getAttribute('aria-label') ?? '');
    expect(names.some((name) => name.endsWith('Facts beyond the loading bound not shown'))).toBe(true);
    expect(names.some((name) => name.endsWith('Facts present, 1'))).toBe(true);
    const uncovered = bins.filter((_, index) => !/Facts present|Analysed, no facts|loading bound/.test(names[index]));
    expect(uncovered.length).toBeGreaterThan(0);
    for (const bin of uncovered) expect(bin.textContent).not.toMatch(/\d/);
    for (const name of names) expect(name).not.toMatch(/, 0$/);
    expect(within(lane('detect-1')).getByText('Partial')).toBeInTheDocument();
  });

  it('keeps the previous data marked as updating until the new pages complete', async () => {
    let release: (() => void) | undefined;
    let hold = false;
    let data: Record<string, Behaviour> = { 'detect-1': { starts: [0, 35000], windows: accepted(60000) } };
    const adapter = adapterFor(
      () => data,
      () => (hold ? new Promise<void>((resolve) => (release = resolve)) : undefined),
    );
    const { update } = setup({ adapter, streams: [stream({ factCount: 2 })] });
    await loaded();
    const before = binLabels('detect-1');

    hold = true;
    data = { 'detect-1': { starts: [0, 35000, 40000], windows: accepted(60000) } };
    update({ adapter, streams: [stream({ factCount: 2, coverage: { accepted: 2, missing: 0, failed: 0, blocked: 0, skipped: 0 } })] });
    await screen.findByText('Updating…');

    expect(binLabels('detect-1')).toEqual(before);
    expect(screen.getByRole('status')).toHaveTextContent('The timeline of detect is updating.');

    hold = false;
    release?.();
    await waitFor(() => expect(screen.queryByText('Updating…')).not.toBeInTheDocument());
    expect(binLabels('detect-1')).not.toEqual(before);
    expect(screen.getByRole('status')).toHaveTextContent('The timeline data changed.');
  });

  it('keeps the selection, zoom, and extent and the focus where it was when a refresh changes the data', async () => {
    const user = userEvent.setup();
    const onSelectStream = vi.fn();
    let data: Record<string, Behaviour> = { 'detect-1': { starts: [0, 6_000_000], windows: accepted(6_010_000) } };
    const adapter = adapterFor(() => data);
    const { update } = setup({ adapter, streams: [stream({ factCount: 2 })], onSelectStream });
    await loaded();
    await user.click(screen.getByRole('button', { name: '1 minute' }));
    await user.click(screen.getByRole('button', { name: 'Later' }));
    await user.click(screen.getByRole('button', { name: 'Show details of detect-1' }));
    const bin = binLabels('detect-1').length;
    const focused = within(screen.getByRole('group', { name: 'Bins of detect 1.2 (detect-1)' })).getAllByRole('button')[2];
    focused.focus();

    data = { 'detect-1': { starts: [0, 6_000_000, 6_000_500], windows: accepted(6_010_000) } };
    update({ adapter, streams: [stream({ factCount: 3 })] });
    await waitFor(() => expect(adapter.listFacts).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByText('Updating…')).not.toBeInTheDocument());

    expect(screen.getByText('Showing 15:00–45:00')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '1 minute' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Hide details of detect-1' })).toHaveAttribute('aria-expanded', 'true');
    expect(binLabels('detect-1')).toHaveLength(bin);
    expect(focused).toHaveFocus();
    expect(onSelectStream).not.toHaveBeenCalled();
  });

  it('does not reload a lane on its own while the page is hidden', async () => {
    const adapter = adapterFor(() => ({ 'detect-1': healthy }));
    const { update } = setup({ adapter, streams: [stream({ factCount: 2 })] });
    await loaded();
    expect(adapter.listFacts).toHaveBeenCalledTimes(1);

    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
    document.dispatchEvent(new Event('visibilitychange'));
    update({ adapter, streams: [stream({ factCount: 2 })] });
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(adapter.listFacts).toHaveBeenCalledTimes(1);
  });

  it.each(['Failed', 'Cancelled', 'Paused'])('never shows the %s run as in progress and shows unknown raw values', async (runState) => {
    const user = userEvent.setup();
    const adapter = adapterFor(() => ({ 'detect-1': { starts: [], windows: accepted(60000) } }));
    render(
      <QueryClientProvider client={new QueryClient()}>
        <Harness adapter={adapter} streams={[stream()]} runState={runState} nodeStates={{ 'detect-1': 'Odd' }} />
      </QueryClientProvider>,
    );
    await loaded();

    expect(within(lane('detect-1')).queryByText(/still in progress/)).not.toBeInTheDocument();
    expect(within(lane('detect-1')).getByText('The analysis is complete and produced no facts.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show details of detect-1' }));
    expect(screen.getByText('Node state: Odd')).toBeInTheDocument();
    if (runState === 'Paused') expect(screen.getByText('Run state: Paused')).toBeInTheDocument();
    else expect(screen.queryByText(/^Run state:/)).not.toBeInTheDocument();
  });
});
