import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { AnalysisFactAdapter, FactRequest } from '../../../../../src/platform/adapters/analysisFactAdapter';
import type { FactPage, FactStreamSummary, FactsOutcome } from '../../../../../src/platform/models/analysis';
import { useLaneFacts } from '../../../../../src/features/analysis/timeline/useLaneFacts';

const provenance = { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' };

const makeStream = (factCount: number): FactStreamSummary => ({
  nodeKey: 'detect-1',
  capabilityId: 'detect',
  capabilityVersion: '1.2',
  schemaVersion: '1',
  outputType: 'events',
  executionScope: 'Segment',
  required: true,
  factCount,
  provenance,
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
});

const FIRST_COVERAGE = [{ startMs: 0, endMs: 1000, state: 'accepted' }];

// Page n holds two facts (2n and 2n + 1); the token of page n is `t<n + 1>`, and the last page has none.
function pageOf(index: number, lastIndex: number): FactPage {
  return {
    run: { runId: 'run-1', matchId: 'm', state: index === 0 ? 'Running' : 'Completed' },
    stream: { nodeKey: 'detect-1', capability: 'detect', schemaVersion: '1', outputType: 'events', executionScope: 'Segment', provenance },
    range: {},
    items: [0, 1].map((offset) => ({
      acceptedResultId: `r${index}`,
      ordinal: index * 2 + offset,
      kind: 'k',
      startMs: (index * 2 + offset) * 10,
      endMs: (index * 2 + offset) * 10 + 5,
      confidence: 1,
      payload: { secret: 'payload' },
    })),
    coverage: index === 0 ? FIRST_COVERAGE : [{ startMs: 0, endMs: 5, state: 'failed' }],
    continuationToken: index < lastIndex ? `t${index + 1}` : undefined,
  };
}

const pagesFrom = (lastIndex: number) => async (request: FactRequest): Promise<FactsOutcome> => {
  const index = request.continuationToken ? Number(request.continuationToken.slice(1)) : 0;
  return { kind: 'found', page: pageOf(index, lastIndex) };
};

function setup(listFacts: (request: FactRequest) => Promise<FactsOutcome>, initial: { stream: FactStreamSummary; allowance: number }) {
  const spy = vi.fn(listFacts);
  const adapter: AnalysisFactAdapter = { listStreams: vi.fn(), listFacts: spy };
  const client = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  const hook = renderHook(({ stream, allowance }: { stream: FactStreamSummary; allowance: number }) => useLaneFacts(adapter, 'run-1', stream, allowance), {
    wrapper,
    initialProps: initial,
  });
  return { hook, listFacts: spy };
}

const tokens = (listFacts: ReturnType<typeof vi.fn>) => listFacts.mock.calls.map(([request]) => (request as FactRequest).continuationToken);

describe('useLaneFacts', () => {
  it('pages over at least three pages and keeps only the fact bounds', async () => {
    const { hook, listFacts } = setup(pagesFrom(3), { stream: makeStream(8), allowance: 5000 });
    await waitFor(() => expect(hook.result.current.status).toBe('loaded'));

    expect(tokens(listFacts)).toEqual([undefined, 't1', 't2', 't3']);
    expect(hook.result.current.loadedFactCount).toBe(8);
    expect(hook.result.current.partial).toBe(false);
    expect(hook.result.current.facts[7]).toEqual({ startMs: 70, endMs: 75 });
  });

  it('does not retain the payload or any other fact field', async () => {
    const { hook } = setup(pagesFrom(2), { stream: makeStream(6), allowance: 5000 });
    await waitFor(() => expect(hook.result.current.status).toBe('loaded'));

    for (const fact of hook.result.current.facts) expect(Object.keys(fact).sort()).toEqual(['endMs', 'startMs']);
    expect(JSON.stringify(hook.result.current.facts)).not.toContain('payload');
  });

  it('stops at the allowance and marks the lane partial', async () => {
    const { hook, listFacts } = setup(pagesFrom(50), { stream: makeStream(102), allowance: 5 });
    await waitFor(() => expect(hook.result.current.status).toBe('loaded'));

    expect(tokens(listFacts)).toEqual([undefined, 't1', 't2']);
    expect(hook.result.current.loadedFactCount).toBe(5);
    expect(hook.result.current.partial).toBe(true);
  });

  it('takes the coverage and run state from the first page', async () => {
    const { hook } = setup(pagesFrom(2), { stream: makeStream(6), allowance: 5000 });
    await waitFor(() => expect(hook.result.current.status).toBe('loaded'));

    expect(hook.result.current.windows).toEqual(FIRST_COVERAGE);
    expect(hook.result.current.runState).toBe('Running');
  });

  it('restarts once after a rejected continuation', async () => {
    let rejections = 0;
    const { hook, listFacts } = setup(
      async (request) => {
        if (request.continuationToken === 't1' && rejections++ === 0) return { kind: 'continuationRejected' };
        return pagesFrom(2)(request);
      },
      { stream: makeStream(6), allowance: 5000 },
    );
    await waitFor(() => expect(hook.result.current.status).toBe('loaded'));

    expect(tokens(listFacts)).toEqual([undefined, 't1', undefined, 't1', 't2']);
    expect(hook.result.current.loadedFactCount).toBe(6);
  });

  it('becomes unavailable after the second rejected continuation', async () => {
    const { hook, listFacts } = setup(
      async (request) => (request.continuationToken === 't1' ? { kind: 'continuationRejected' } : pagesFrom(2)(request)),
      { stream: makeStream(6), allowance: 5000 },
    );
    await waitFor(() => expect(hook.result.current.status).toBe('unavailable'));

    expect(tokens(listFacts)).toEqual([undefined, 't1', undefined, 't1']);
    expect(hook.result.current.facts).toEqual([]);
  });

  it('reloads when the lane key changes and not on an equal rerender', async () => {
    const { hook, listFacts } = setup(pagesFrom(1), { stream: makeStream(4), allowance: 5000 });
    await waitFor(() => expect(hook.result.current.status).toBe('loaded'));
    expect(tokens(listFacts)).toEqual([undefined, 't1']);

    hook.rerender({ stream: makeStream(4), allowance: 5000 });
    expect(listFacts).toHaveBeenCalledTimes(2);

    hook.rerender({ stream: makeStream(6), allowance: 5000 });
    await waitFor(() => expect(listFacts).toHaveBeenCalledTimes(4));
    await waitFor(() => expect(hook.result.current.isUpdating).toBe(false));
    expect(hook.result.current.status).toBe('loaded');
  });
});
