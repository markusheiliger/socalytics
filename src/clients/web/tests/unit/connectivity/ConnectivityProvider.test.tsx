import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConnectivityProvider } from '../../../src/connectivity/ConnectivityProvider';
import { useConnectivity } from '../../../src/connectivity/useConnectivity';

function Probe() {
  const { status, reportNetworkFailure } = useConnectivity();
  return (
    <div>
      <span data-testid="status">{status}</span>
      <button onClick={reportNetworkFailure}>fail</button>
    </div>
  );
}

function Data({ fetcher }: { fetcher: () => Promise<string> }) {
  const query = useQuery({ queryKey: ['data'], queryFn: fetcher, staleTime: Infinity });
  return <span data-testid="data">{query.data ?? ''}</span>;
}

function setOnLine(value: boolean) {
  Object.defineProperty(navigator, 'onLine', { configurable: true, value });
}

function renderProvider(children: React.ReactNode) {
  const queryClient = new QueryClient();
  render(
    <QueryClientProvider client={queryClient}>
      <ConnectivityProvider initialDelayMs={100} maxDelayMs={400} probeTimeoutMs={50}>
        {children}
      </ConnectivityProvider>
    </QueryClientProvider>,
  );
}

describe('ConnectivityProvider', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers();
    setOnLine(true);
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    setOnLine(true);
  });

  it('is online by default and does not probe', async () => {
    renderProvider(<Probe />);
    await act(() => vi.advanceTimersByTimeAsync(5000));
    expect(screen.getByTestId('status')).toHaveTextContent('online');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('becomes unreachable on a reported failure and probes with exponential backoff', async () => {
    fetchMock.mockRejectedValue(new TypeError('network'));
    renderProvider(<Probe />);
    act(() => screen.getByText('fail').click());
    expect(screen.getByTestId('status')).toHaveTextContent('unreachable');

    await act(() => vi.advanceTimersByTimeAsync(100));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('/alive');
    await act(() => vi.advanceTimersByTimeAsync(100));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(() => vi.advanceTimersByTimeAsync(100));
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await act(() => vi.advanceTimersByTimeAsync(200));
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await act(() => vi.advanceTimersByTimeAsync(200));
    expect(fetchMock).toHaveBeenCalledTimes(3);
    await act(() => vi.advanceTimersByTimeAsync(400));
    expect(fetchMock).toHaveBeenCalledTimes(4);
    await act(() => vi.advanceTimersByTimeAsync(400));
    expect(fetchMock).toHaveBeenCalledTimes(5);
    expect(screen.getByTestId('status')).toHaveTextContent('unreachable');
  });

  it('treats a non-successful probe response as still unreachable', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 503 }));
    renderProvider(<Probe />);
    act(() => screen.getByText('fail').click());
    await act(() => vi.advanceTimersByTimeAsync(100));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('status')).toHaveTextContent('unreachable');
  });

  it('recovers when the probe succeeds and refetches active queries', async () => {
    fetchMock.mockResolvedValue(new Response('ok', { status: 200 }));
    const fetcher = vi.fn().mockResolvedValueOnce('first').mockResolvedValueOnce('second');
    renderProvider(
      <>
        <Probe />
        <Data fetcher={fetcher} />
      </>,
    );
    await act(() => vi.advanceTimersByTimeAsync(0));
    expect(screen.getByTestId('data')).toHaveTextContent('first');

    act(() => screen.getByText('fail').click());
    await act(() => vi.advanceTimersByTimeAsync(100));
    await act(() => vi.advanceTimersByTimeAsync(0));
    expect(screen.getByTestId('status')).toHaveTextContent('online');
    expect(fetcher).toHaveBeenCalledTimes(2);
    await act(() => vi.advanceTimersByTimeAsync(50));
    expect(screen.getByTestId('data')).toHaveTextContent('second');

    await act(() => vi.advanceTimersByTimeAsync(5000));
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('follows the browser offline and online events', async () => {
    fetchMock.mockResolvedValue(new Response('ok', { status: 200 }));
    renderProvider(<Probe />);
    act(() => {
      setOnLine(false);
      window.dispatchEvent(new Event('offline'));
    });
    expect(screen.getByTestId('status')).toHaveTextContent('unreachable');

    act(() => {
      setOnLine(true);
      window.dispatchEvent(new Event('online'));
    });
    expect(screen.getByTestId('status')).toHaveTextContent('online');
    await act(() => vi.advanceTimersByTimeAsync(5000));
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('starts unreachable when the browser is offline', () => {
    setOnLine(false);
    renderProvider(<Probe />);
    expect(screen.getByTestId('status')).toHaveTextContent('unreachable');
  });

  it('defaults to online outside a provider', () => {
    render(<Probe />);
    expect(screen.getByTestId('status')).toHaveTextContent('online');
  });
});
