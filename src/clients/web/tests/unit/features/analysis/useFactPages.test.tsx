import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { useFactPages } from '../../../../src/features/analysis/useFactPages';
import type { AnalysisFactAdapter, FactRequest } from '../../../../src/platform/adapters/analysisFactAdapter';
import type { FactPage, FactSelection, FactsOutcome } from '../../../../src/platform/models/analysis';

const stream = {
  nodeKey: 'detect-1',
  capabilityId: 'detect',
  capabilityVersion: '1.2',
  schemaVersion: '1',
  outputType: 'events',
  executionScope: 'Segment',
  required: true,
  factCount: 3,
  provenance: { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' },
  coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
} as FactSelection['stream'];

const selection = (range: FactSelection['range'] = {}): FactSelection => ({ runId: 'run-1', stream, range });

const pageOf = (index: number, token?: string): FactPage => ({
  run: { runId: 'run-1', matchId: 'm', state: 'Completed' },
  stream: { nodeKey: 'detect-1', capability: 'detect', schemaVersion: '1', outputType: 'events', executionScope: 'Segment', provenance: stream.provenance },
  range: { fromMs: 0, toMs: 1000 },
  items: [{ acceptedResultId: `r${index}`, ordinal: index, kind: 'k', startMs: index, endMs: index + 1, confidence: 1, payload: null }],
  coverage: [],
  continuationToken: token,
});

// Pages are chained through tokens: first -> t1 -> t2 -> last.
const chain: Record<string, FactPage> = { first: pageOf(0, 't1'), t1: pageOf(1, 't2'), t2: pageOf(2) };

function setup(listFacts?: (request: FactRequest) => Promise<FactsOutcome>) {
  const calls: (string | undefined)[] = [];
  const adapter: AnalysisFactAdapter = {
    listStreams: vi.fn(),
    listFacts:
      listFacts ??
      (async (request) => {
        calls.push(request.continuationToken);
        return { kind: 'found', page: chain[request.continuationToken ?? 'first'] };
      }),
  };
  const client = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  const hook = renderHook(({ sel }: { sel: FactSelection }) => useFactPages(adapter, sel), { wrapper, initialProps: { sel: selection() } });
  return { adapter, calls, hook };
}

const ordinal = (result: { current: ReturnType<typeof useFactPages> }) => result.current.page?.items[0].ordinal;

describe('useFactPages', () => {
  it('pages three pages forward and back without repeats or refetches', async () => {
    const { calls, hook } = setup();
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
    expect(hook.result.current.hasPrevious).toBe(false);

    act(() => hook.result.current.next());
    await waitFor(() => expect(ordinal(hook.result)).toBe(1));
    act(() => hook.result.current.next());
    await waitFor(() => expect(ordinal(hook.result)).toBe(2));
    expect(hook.result.current.hasNext).toBe(false);
    expect(hook.result.current.pageNumber).toBe(3);

    act(() => hook.result.current.previous());
    await waitFor(() => expect(ordinal(hook.result)).toBe(1));
    act(() => hook.result.current.previous());
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
    expect(hook.result.current.hasPrevious).toBe(false);
    expect(calls).toEqual([undefined, 't1', 't2']);
  });

  it('starts over when the selection changes', async () => {
    const { hook } = setup();
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
    act(() => hook.result.current.next());
    await waitFor(() => expect(ordinal(hook.result)).toBe(1));

    hook.rerender({ sel: selection({ fromMs: 10 }) });
    expect(hook.result.current.pageNumber).toBe(1);
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
  });

  it('starts over on reload and loads the first page again', async () => {
    const { calls, hook } = setup();
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
    act(() => hook.result.current.next());
    await waitFor(() => expect(ordinal(hook.result)).toBe(1));

    act(() => hook.result.current.reload());
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
    expect(hook.result.current.pageNumber).toBe(1);
    expect(calls).toEqual([undefined, 't1', undefined]);
  });

  it('reloads the first page in place', async () => {
    const { calls, hook } = setup();
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
    act(() => hook.result.current.reload());
    await waitFor(() => expect(calls).toEqual([undefined, undefined]));
  });

  it('restarts from the first page and reports it when a token is rejected', async () => {
    let rejectOnce = true;
    const { hook } = setup(async (request) => {
      if (request.continuationToken === 't1' && rejectOnce) {
        rejectOnce = false;
        return { kind: 'continuationRejected' };
      }
      return { kind: 'found', page: chain[request.continuationToken ?? 'first'] };
    });
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
    act(() => hook.result.current.next());
    await waitFor(() => expect(hook.result.current.restarted).toBe(true));
    await waitFor(() => expect(ordinal(hook.result)).toBe(0));
    expect(hook.result.current.pageNumber).toBe(1);

    act(() => hook.result.current.next());
    await waitFor(() => expect(ordinal(hook.result)).toBe(1));
    expect(hook.result.current.restarted).toBe(false);
  });
});
