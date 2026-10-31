import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { AnalysisTimeline, type TimelineView } from '../../../../../src/features/analysis/timeline/AnalysisTimeline';
import type { AnalysisFactAdapter, FactRequest } from '../../../../../src/platform/adapters/analysisFactAdapter';
import type { CoverageWindow, FactStreamSummary, FactsOutcome } from '../../../../../src/platform/models/analysis';
import '../../../../../src/i18n';

const provenance = { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' };

const stream: FactStreamSummary = {
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
};

const windows: CoverageWindow[] = [{ startMs: 0, endMs: 60000, state: 'accepted' }];

function adapter(): AnalysisFactAdapter {
  const listFacts = vi.fn(
    async (request: FactRequest): Promise<FactsOutcome> => ({
      kind: 'found',
      page: {
        run: { runId: 'run-1', matchId: 'm', state: 'Completed' },
        stream: { nodeKey: request.nodeKey, capability: request.capabilityId, schemaVersion: '1', outputType: 'events', executionScope: 'Segment', provenance },
        range: {},
        items: [0, 35000].map((startMs, ordinal) => ({ acceptedResultId: 'r', ordinal, kind: 'k', startMs, endMs: startMs + 5, confidence: 1, payload: {} })),
        coverage: windows,
        continuationToken: undefined,
      },
    }),
  );
  return { listStreams: vi.fn(), listFacts };
}

function Harness() {
  const [view, setView] = useState<TimelineView>({});
  return (
    <>
      <button type="button">Before</button>
      <AnalysisTimeline adapter={adapter()} runId="run-1" runState="Completed" streams={[stream]} view={view} onViewChange={setView} onSelectStream={() => {}} />
    </>
  );
}

async function setup() {
  const user = userEvent.setup();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <Harness />
    </QueryClientProvider>,
  );
  await screen.findByRole('group', { name: 'Zoom' });
  const bins = () => within(screen.getByRole('group', { name: 'Bins of detect 1.2 (detect-1)' })).getAllByRole('button');
  return { user, bins };
}

describe('timeline keyboard operation', () => {
  it('keeps one bin in the tab order and moves focus with the arrow keys, Home, and End', async () => {
    const { user, bins } = await setup();
    expect(bins().filter((bin) => bin.tabIndex === 0)).toHaveLength(1);
    expect(bins()[0]).toHaveAttribute('tabindex', '0');

    bins()[0].focus();
    await user.keyboard('{ArrowRight}');
    expect(bins()[1]).toHaveFocus();
    expect(bins()[1]).toHaveAttribute('tabindex', '0');
    expect(bins()[0]).toHaveAttribute('tabindex', '-1');

    await user.keyboard('{ArrowLeft}{ArrowLeft}');
    expect(bins()[0]).toHaveFocus();

    await user.keyboard('{End}');
    expect(bins().at(-1)).toHaveFocus();
    await user.keyboard('{ArrowRight}');
    expect(bins().at(-1)).toHaveFocus();
    await user.keyboard('{Home}');
    expect(bins()[0]).toHaveFocus();
  });

  it('selects the focused bin with Enter or Space and clears the selection with Escape', async () => {
    const { user, bins } = await setup();
    bins()[3].focus();

    await user.keyboard('{Enter}');
    expect(bins()[3]).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('group', { name: 'Selection' })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('group', { name: 'Selection' })).not.toBeInTheDocument();
    expect(bins()[3]).toHaveAttribute('aria-pressed', 'false');

    await user.keyboard(' ');
    expect(bins()[3]).toHaveAttribute('aria-pressed', 'true');
  });

  it('extends a span with Shift and an arrow key', async () => {
    const { user, bins } = await setup();
    bins()[0].focus();

    await user.keyboard('{Shift>}{ArrowRight}{ArrowRight}{/Shift}');

    expect(within(screen.getByRole('group', { name: 'Selection' })).getByText('0:00–0:30')).toBeInTheDocument();
    expect(bins().slice(0, 5).map((bin) => bin.getAttribute('aria-pressed'))).toEqual(['true', 'true', 'true', 'false', 'false']);
    expect(bins()[2]).toHaveFocus();
  });

  it('gives every bin an accessible name with its range and count or coverage state', async () => {
    const { bins } = await setup();
    const names = bins().map((bin) => bin.getAttribute('aria-label'));

    expect(names[0]).toBe('from 0:00 to 0:10 (end excluded): Facts present, 1');
    expect(names[1]).toBe('from 0:10 to 0:20 (end excluded): Analysed, no facts');
  });

  it('announces a selection in the live region without moving focus', async () => {
    const { user, bins } = await setup();
    const status = screen.getByRole('status');
    bins()[0].focus();

    await user.keyboard('{Enter}');

    expect(status).toHaveTextContent('Selected 0:00–0:10 on detect 1.2: 1 facts.');
    expect(bins()[0]).toHaveFocus();

    await user.keyboard('{Escape}');
    expect(bins()[0]).toHaveFocus();
  });

  it('announces a zoom change without moving focus', async () => {
    const { user, bins } = await setup();
    bins()[2].focus();

    await user.click(screen.getByRole('button', { name: '1 minute' }));

    expect(screen.getByRole('status')).toHaveTextContent(/^Zoom changed to /);
    expect(screen.getByRole('button', { name: '1 minute' })).toHaveFocus();
  });
});
