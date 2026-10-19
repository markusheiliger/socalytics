import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { FactBrowser } from '../../../../src/features/analysis/FactBrowser';
import type { AnalysisFactAdapter, FactRequest } from '../../../../src/platform/adapters/analysisFactAdapter';
import type { FactPage, FactSelection, FactsOutcome } from '../../../../src/platform/models/analysis';
import '../../../../src/i18n';

const provenance = { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' };
const stream = {
  nodeKey: 'detect-1',
  capabilityId: 'detect',
  capabilityVersion: '1.2',
  schemaVersion: '1',
  outputType: 'foundational-facts',
  executionScope: 'Segment',
  required: true,
  factCount: 3,
  provenance,
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
} as FactSelection['stream'];

const pageOf = (index: number, token?: string, kind = `kind-${index}`, payload: FactPage['items'][number]['payload'] = { a: index }): FactPage => ({
  run: { runId: 'run-1', matchId: 'm', state: 'Completed' },
  stream: { nodeKey: 'detect-1', capability: 'detect', schemaVersion: '1', outputType: 'foundational-facts', executionScope: 'Segment', provenance },
  range: { fromMs: 0, toMs: 100000 },
  items: [{ acceptedResultId: `r${index}`, ordinal: index, kind, startMs: 62500, endMs: 65000, confidence: 0.876, payload }],
  coverage: [{ startMs: 0, endMs: 60000, state: 'accepted' }],
  continuationToken: token,
});

const chain: Record<string, FactPage> = { first: pageOf(0, 't1'), t1: pageOf(1, 't2'), t2: pageOf(2) };

function renderBrowser(listFacts?: (request: FactRequest) => Promise<FactsOutcome>, onRangeChange = vi.fn()) {
  const adapter: AnalysisFactAdapter = {
    listStreams: vi.fn(),
    listFacts: vi.fn(listFacts ?? (async (request): Promise<FactsOutcome> => ({ kind: 'found', page: chain[request.continuationToken ?? 'first'] }))),
  };
  const selection: FactSelection = { runId: 'run-1', stream, range: {} };
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FactBrowser adapter={adapter} selection={selection} onRangeChange={onRangeChange} />
    </QueryClientProvider>,
  );
  return { adapter, onRangeChange };
}

describe('FactBrowser', () => {
  it('shows range, kind, confidence as a whole percentage, summary, provenance, and coverage', async () => {
    renderBrowser();
    expect(await screen.findByText('kind-0')).toBeInTheDocument();
    expect(screen.getByText('1:02.500–1:05')).toBeInTheDocument();
    expect(screen.getByText('88%')).toBeInTheDocument();
    expect(screen.getByText('a: 0')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Provenance' })).toBeInTheDocument();
    expect(screen.getByText('0:00–1:00: Accepted')).toBeInTheDocument();
  });

  it('pages forward and back, showing every fact once with first- and last-page indication', async () => {
    renderBrowser();
    await screen.findByText('kind-0');
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled();
    expect(screen.getByText(/This is the first page\./)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('kind-1')).toBeInTheDocument();
    expect(screen.queryByText('kind-0')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('kind-2')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled();
    expect(screen.getByText(/This is the last page\./)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Previous page' }));
    expect(await screen.findByText('kind-1')).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'Facts of the selected stream' })).getAllByRole('listitem')).toHaveLength(1);
  });

  it('shows the full payload as text, never as markup', async () => {
    const hostile = '<img src=x onerror=alert(1)>';
    const outcome: FactsOutcome = { kind: 'found', page: pageOf(0, undefined, '<b>bold</b>', { note: hostile }) };
    renderBrowser(async () => outcome);
    expect(await screen.findByText('<b>bold</b>')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Show full payload' }));
    const pre = screen.getByLabelText('Full payload of the fact');
    expect(pre.tagName).toBe('PRE');
    expect(pre.textContent).toBe(JSON.stringify({ note: hostile }, null, 2));
    expect(pre.querySelector('img')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Hide full payload' }));
    expect(screen.queryByLabelText('Full payload of the fact')).not.toBeInTheDocument();
  });

  it('reports a valid range to the caller and nothing for an invalid one', async () => {
    const { adapter, onRangeChange } = renderBrowser();
    await screen.findByText('kind-0');
    await userEvent.type(screen.getByLabelText('From'), '5:00');
    await userEvent.type(screen.getByLabelText('To'), '1:00');
    await userEvent.click(screen.getByRole('button', { name: 'Apply range' }));
    expect(onRangeChange).not.toHaveBeenCalled();
    expect(screen.getByText('The end must be after the start.')).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText('To'));
    await userEvent.type(screen.getByLabelText('To'), '6:00');
    await userEvent.click(screen.getByRole('button', { name: 'Apply range' }));
    expect(onRangeChange).toHaveBeenCalledWith({ fromMs: 300000, toMs: 360000 });
    expect(adapter.listFacts).toHaveBeenCalledTimes(1);
  });

  it('shows the empty message and the failure message', async () => {
    const empty = { ...pageOf(0), items: [] };
    renderBrowser(async () => ({ kind: 'found', page: empty }));
    expect(await screen.findByText('No facts to show.')).toBeInTheDocument();
  });

  it('shows a failure without facts', async () => {
    renderBrowser(async () => {
      throw new Error('boom');
    });
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('The analysis could not be loaded.'));
  });
});
