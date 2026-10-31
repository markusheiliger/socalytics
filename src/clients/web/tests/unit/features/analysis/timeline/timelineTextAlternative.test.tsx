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
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 1 },
};

const windows: CoverageWindow[] = [
  { startMs: 0, endMs: 30000, state: 'accepted' },
  { startMs: 30000, endMs: 60000, state: 'skipped' },
];

function adapter(): AnalysisFactAdapter {
  const listFacts = vi.fn(
    async (request: FactRequest): Promise<FactsOutcome> => ({
      kind: 'found',
      page: {
        run: { runId: 'run-1', matchId: 'm', state: 'Completed' },
        stream: { nodeKey: request.nodeKey, capability: request.capabilityId, schemaVersion: '1', outputType: 'events', executionScope: 'Segment', provenance },
        range: {},
        items: [0, 5000, 12000].map((startMs, ordinal) => ({ acceptedResultId: 'r', ordinal, kind: 'k', startMs, endMs: startMs + 5, confidence: 1, payload: {} })),
        coverage: windows,
        continuationToken: undefined,
      },
    }),
  );
  return { listStreams: vi.fn(), listFacts };
}

function Harness() {
  const [view, setView] = useState<TimelineView>({});
  return <AnalysisTimeline adapter={adapter()} runId="run-1" runState="Completed" streams={[stream]} view={view} onViewChange={setView} onSelectStream={() => {}} />;
}

const binGroup = () => within(screen.getByRole('group', { name: 'Bins of detect 1.2 (detect-1)' }));
const table = () => screen.getByRole('table', { name: 'Bins of detect 1.2 (detect-1)' });

async function setup() {
  const user = userEvent.setup();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <Harness />
    </QueryClientProvider>,
  );
  await screen.findByRole('group', { name: 'Zoom' });
  return user;
}

// The graphic's bins and the table rows each reduce to the bin's range and its state text.
const graphicNames = () => binGroup().getAllByRole('button').map((bin) => bin.getAttribute('aria-label')!);
const tableRows = () =>
  within(table())
    .getAllByRole('row')
    .slice(1)
    .map((row) => within(row).getAllByRole('cell').concat(within(row).getAllByRole('rowheader')).map((cell) => cell.textContent));

describe('timeline text alternative', () => {
  it('is hidden until the toggle is pressed and can be hidden again', async () => {
    const user = await setup();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();

    const toggle = screen.getByRole('button', { name: 'Show text alternative' });
    expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await user.click(toggle);
    expect(table()).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Hide text alternative' })).toHaveAttribute('aria-pressed', 'true');

    await user.click(screen.getByRole('button', { name: 'Hide text alternative' }));
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('has one row per bin of the graphic with its range and count or coverage state', async () => {
    const user = await setup();
    await user.click(screen.getByRole('button', { name: 'Show text alternative' }));

    const names = graphicNames();
    const rows = tableRows();
    expect(rows).toHaveLength(names.length);
    rows.forEach(([state, range], index) => {
      const compact = names[index].replace(/^from (\d+:\d\d) to (\d+:\d\d) \(end excluded\): /, '$1–$2|');
      expect(`${range}|${state}`).toBe(compact);
    });
    expect(rows[0]).toEqual(['Facts present, 2', '0:00–0:10']);
    expect(rows[3]).toEqual(['Not analysed', '0:30–0:40']);
  });

  it('keeps the rows equal to the bins after the zoom changes', async () => {
    const user = await setup();
    await user.click(screen.getByRole('button', { name: 'Show text alternative' }));
    expect(tableRows()[0][1]).toBe('0:00–0:10');

    await user.click(screen.getByRole('button', { name: '1 minute' }));

    const names = graphicNames();
    const rows = tableRows();
    expect(rows.length).toBe(names.length);
    expect(rows[0][1]).toBe('0:00–1:00');
    rows.forEach(([state, range], index) => {
      expect(names[index]).toContain(state);
      expect(names[index]).toContain(range.replace('–', ' to '));
    });
  });
});
