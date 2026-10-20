import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ConnectivityContext } from '../../../../src/connectivity/connectivityContext';
import { FactBrowser } from '../../../../src/features/analysis/FactBrowser';
import type { AnalysisFactAdapter, FactRequest } from '../../../../src/platform/adapters/analysisFactAdapter';
import type { CoverageWindow, FactPage, FactSelection, FactsOutcome } from '../../../../src/platform/models/analysis';
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
  factCount: 0,
  provenance,
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
} as FactSelection['stream'];

const accepted: CoverageWindow = { startMs: 0, endMs: 60000, state: 'accepted' };

function page(options: { runState?: string; withFact?: boolean; coverage?: CoverageWindow[]; token?: string } = {}): FactPage {
  return {
    run: { runId: 'run-1', matchId: 'm', state: options.runState ?? 'Completed' },
    stream: { nodeKey: 'detect-1', capability: 'detect', schemaVersion: '1', outputType: 'foundational-facts', executionScope: 'Segment', provenance },
    range: { fromMs: 0, toMs: 100000 },
    items: options.withFact ? [{ acceptedResultId: 'r0', ordinal: 0, kind: 'kind-0', startMs: 1000, endMs: 2000, confidence: 0.5, payload: { a: 1 } }] : [],
    coverage: options.coverage ?? [accepted],
    continuationToken: options.token,
  };
}

function renderBrowser(listFacts: (request: FactRequest) => Promise<FactsOutcome>, range: FactSelection['range'] = {}, unreachable = false) {
  const adapter: AnalysisFactAdapter = { listStreams: vi.fn(), listFacts: vi.fn(listFacts) };
  render(
    <ConnectivityContext.Provider value={{ status: unreachable ? 'unreachable' : 'online', isUnreachable: unreachable, reportNetworkFailure: () => {} }}>
      <QueryClientProvider client={new QueryClient()}>
        <FactBrowser adapter={adapter} selection={{ runId: 'run-1', stream, range }} onRangeChange={vi.fn()} />
      </QueryClientProvider>
    </ConnectivityContext.Provider>,
  );
  return adapter;
}

const found = (p: FactPage) => async (): Promise<FactsOutcome> => ({ kind: 'found', page: p });

describe('FactBrowser states', () => {
  it('connectionRequired: shows the connection message and no data', async () => {
    renderBrowser(found(page({ withFact: true })), {}, true);
    expect(await screen.findByText(/A connection is required to review analysis results/)).toBeInTheDocument();
    expect(screen.queryByText('kind-0')).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Coverage' })).not.toBeInTheDocument();
  });

  it('capabilityNotInRun: shows its own message', async () => {
    renderBrowser(async () => ({ kind: 'capabilityNotInRun' }));
    expect(await screen.findByText(/The chosen capability is not part of this run/)).toBeInTheDocument();
  });

  it('notFound: shows its own message', async () => {
    renderBrowser(async () => ({ kind: 'runNotFound' }));
    expect(await screen.findByText('This analysis run was not found for this match.')).toBeInTheDocument();
  });

  it('notPermitted: shows its own message for a refused request', async () => {
    renderBrowser(async () => {
      throw Object.assign(new Error('forbidden'), { status: 403 });
    });
    expect(await screen.findByText('You are not permitted to review the analysis of this match.')).toBeInTheDocument();
  });

  it('error: shows the failure with a reload action', async () => {
    renderBrowser(async () => {
      throw new Error('boom');
    });
    expect(await screen.findByText('The analysis could not be loaded.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
  });

  it('loading: shows an announced progress message', () => {
    renderBrowser(() => new Promise<FactsOutcome>(() => {}));
    expect(screen.getByRole('status')).toHaveTextContent('Loading facts…');
  });

  it('facts: shows facts and coverage without the incomplete note for a complete run', async () => {
    renderBrowser(found(page({ withFact: true })));
    expect(await screen.findByText('kind-0')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Coverage' })).toBeInTheDocument();
    expect(screen.queryByText(/These results are incomplete/)).not.toBeInTheDocument();
  });

  it('facts: marks facts of a running run as incomplete', async () => {
    renderBrowser(found(page({ withFact: true, runState: 'Running' })));
    expect(await screen.findByText('kind-0')).toBeInTheDocument();
    expect(screen.getByText(/These results are incomplete/)).toBeInTheDocument();
  });

  it('facts: marks facts as incomplete when a coverage window is not accepted', async () => {
    renderBrowser(found(page({ withFact: true, coverage: [accepted, { startMs: 60000, endMs: 120000, state: 'failed' }] })));
    expect(await screen.findByText('kind-0')).toBeInTheDocument();
    expect(screen.getByText(/These results are incomplete/)).toBeInTheDocument();
  });

  it('noFactsInRange: shows its message with coverage', async () => {
    renderBrowser(found(page()), { fromMs: 0, toMs: 1000 });
    expect(await screen.findByText('No facts overlap this range.')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Coverage' })).toBeInTheDocument();
  });

  it('inProgress: shows its message with coverage', async () => {
    renderBrowser(found(page({ runState: 'Running' })));
    expect(await screen.findByText('The analysis is still in progress; no facts yet.')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Coverage' })).toBeInTheDocument();
  });

  it('incomplete: shows its message with the non-accepted windows only', async () => {
    renderBrowser(found(page({ coverage: [accepted, { startMs: 60000, endMs: 120000, state: 'failed' }, { startMs: 120000, endMs: 180000, state: 'blocked' }] })));
    expect(await screen.findByText('The analysis is incomplete. Some windows are not accepted.')).toBeInTheDocument();
    const windows = screen.getByRole('region', { name: 'Windows that are not accepted' });
    expect(within(windows).getAllByRole('listitem')).toHaveLength(2);
    expect(within(windows).getByText('1:00–2:00: Failed')).toBeInTheDocument();
    expect(within(windows).getByText('2:00–3:00: Blocked')).toBeInTheDocument();
    expect(within(windows).queryByText('0:00–1:00: Accepted')).not.toBeInTheDocument();
  });

  it('completeWithoutFacts: shows its message with coverage', async () => {
    renderBrowser(found(page()));
    expect(await screen.findByText('The analysis is complete and produced no facts.')).toBeInTheDocument();
    expect(screen.getByText('0:00–1:00: Accepted')).toBeInTheDocument();
  });

  it('restart note: explains a rejected continuation and shows the first page again', async () => {
    const listFacts = vi.fn(async (request: FactRequest): Promise<FactsOutcome> => {
      if (request.continuationToken === 't1') return { kind: 'continuationRejected' };
      return { kind: 'found', page: page({ withFact: true, token: 't1' }) };
    });
    renderBrowser(listFacts);
    expect(await screen.findByText('kind-0')).toBeInTheDocument();
    const { default: userEvent } = await import('@testing-library/user-event');
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await waitFor(() => expect(screen.getByText(/so the list restarted at the first page/)).toBeInTheDocument());
  });
});
