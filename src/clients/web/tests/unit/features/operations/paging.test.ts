import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { useContinuationPages, type TokenPage } from '../../../../src/features/operations/useContinuationPages';
import { PlatformFailure } from '../../../../src/platform/failure';

interface TestPage extends TokenPage {
  index: number;
}

const chain: Record<string, TestPage> = {
  first: { index: 0, continuationToken: 't1' },
  t1: { index: 1, continuationToken: 't2' },
  t2: { index: 2 },
};

function setup(fetchPage?: (token: string | undefined, filter: string) => Promise<TestPage>) {
  const calls: [string, string | undefined][] = [];
  const fetcher =
    fetchPage ??
    (async (token: string | undefined) => {
      return chain[token ?? 'first'];
    });
  const client = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client }, children);
  const hook = renderHook(
    ({ filter }: { filter: string }) =>
      useContinuationPages<TestPage>({
        queryKey: ['operations', 'test', filter],
        fetchPage: (token) => {
          calls.push([filter, token]);
          return fetcher(token, filter);
        },
      }),
    { wrapper, initialProps: { filter: 'all' } },
  );
  return { calls, hook };
}

const index = (r: { current: ReturnType<typeof useContinuationPages<TestPage>> }) => r.current.page?.index;

describe('useContinuationPages', () => {
  it('pages forward and back without skips or repeats', async () => {
    const { calls, hook } = setup();
    await waitFor(() => expect(index(hook.result)).toBe(0));
    expect(hook.result.current.hasPrevious).toBe(false);
    expect(hook.result.current.hasNext).toBe(true);

    act(() => hook.result.current.next());
    await waitFor(() => expect(index(hook.result)).toBe(1));
    act(() => hook.result.current.next());
    await waitFor(() => expect(index(hook.result)).toBe(2));
    expect(hook.result.current.hasNext).toBe(false);
    expect(hook.result.current.pageNumber).toBe(3);

    act(() => hook.result.current.previous());
    await waitFor(() => expect(index(hook.result)).toBe(1));
    act(() => hook.result.current.previous());
    await waitFor(() => expect(index(hook.result)).toBe(0));
    expect(hook.result.current.hasPrevious).toBe(false);
    expect(calls.map(([, t]) => t)).toEqual([undefined, 't1', 't2']);
  });

  it('keeps the previous page while the next one loads', async () => {
    let release: () => void = () => undefined;
    const { hook } = setup(async (token) => {
      if (token === 't1') await new Promise<void>((resolve) => (release = resolve));
      return chain[token ?? 'first'];
    });
    await waitFor(() => expect(index(hook.result)).toBe(0));
    act(() => hook.result.current.next());
    await waitFor(() => expect(hook.result.current.isFetching).toBe(true));
    expect(index(hook.result)).toBe(0);
    act(() => release());
    await waitFor(() => expect(index(hook.result)).toBe(1));
  });

  it('returns to the first page and reloads it on refresh', async () => {
    const { calls, hook } = setup();
    await waitFor(() => expect(index(hook.result)).toBe(0));
    act(() => hook.result.current.next());
    await waitFor(() => expect(index(hook.result)).toBe(1));

    act(() => hook.result.current.refresh());
    await waitFor(() => expect(index(hook.result)).toBe(0));
    expect(hook.result.current.pageNumber).toBe(1);
    expect(calls.map(([, t]) => t)).toEqual([undefined, 't1', undefined]);
  });

  it('refreshes the first page in place', async () => {
    const { calls, hook } = setup();
    await waitFor(() => expect(index(hook.result)).toBe(0));
    act(() => hook.result.current.refresh());
    await waitFor(() => expect(calls).toHaveLength(2));
  });

  it('starts over at the first page when the filter changes', async () => {
    const { calls, hook } = setup();
    await waitFor(() => expect(index(hook.result)).toBe(0));
    act(() => hook.result.current.next());
    await waitFor(() => expect(index(hook.result)).toBe(1));

    hook.rerender({ filter: 'failed' });
    expect(hook.result.current.pageNumber).toBe(1);
    expect(hook.result.current.hasPrevious).toBe(false);
    await waitFor(() => expect(calls[calls.length - 1]).toEqual(['failed', undefined]));
    await waitFor(() => expect(index(hook.result)).toBe(0));
  });

  it('resets to the first page with the expired flag when a token is rejected', async () => {
    let rejectOnce = true;
    const { hook } = setup(async (token) => {
      if (token === 't1' && rejectOnce) {
        rejectOnce = false;
        throw new PlatformFailure('programmingError', { status: 400, fieldErrors: [{ field: 'continuationToken', code: 'invalid' }] });
      }
      return chain[token ?? 'first'];
    });
    await waitFor(() => expect(index(hook.result)).toBe(0));
    act(() => hook.result.current.next());
    await waitFor(() => expect(hook.result.current.expired).toBe(true));
    await waitFor(() => expect(index(hook.result)).toBe(0));
    expect(hook.result.current.pageNumber).toBe(1);
    expect(hook.result.current.error).toBeNull();

    act(() => hook.result.current.next());
    await waitFor(() => expect(index(hook.result)).toBe(1));
    expect(hook.result.current.expired).toBe(false);
  });

  it('reports other failures without resetting', async () => {
    const { hook } = setup(async () => {
      throw new PlatformFailure('unreachable', { status: 503 });
    });
    await waitFor(() => expect(hook.result.current.error).toBeInstanceOf(PlatformFailure));
    expect(hook.result.current.expired).toBe(false);
  });
});
