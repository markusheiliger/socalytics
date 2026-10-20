import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { ConnectivityContext } from '../../../../src/connectivity/connectivityContext';
import { TriageFailure, type AgentConversationAdapter } from '../../../../src/platform/adapters/agentConversationAdapter';
import { PlatformFailure } from '../../../../src/platform/failure';
import { toTurnState, type Conditional, type InvocationStatus } from '../../../../src/platform/models/agents';
import { useInvocationPolling } from '../../../../src/features/triage/useInvocationPolling';

const status = (state: string, etag: string, retryAfterSeconds?: number): Conditional<InvocationStatus> => ({
  kind: 'changed',
  etag,
  retryAfterSeconds,
  value: {
    invocationId: 'inv-1',
    conversationId: 'conv-1',
    agentId: 'agent-a',
    route: 'r',
    state: toTurnState(state),
    attemptCount: 1,
    etag,
  },
});

const unchanged: Conditional<InvocationStatus> = { kind: 'unchanged' };

function setVisibility(value: 'visible' | 'hidden') {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => value });
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('useInvocationPolling', () => {
  let getInvocationStatus: ReturnType<typeof vi.fn>;
  let queryClient: QueryClient;
  let unreachable: boolean;

  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(
      QueryClientProvider,
      { client: queryClient },
      createElement(
        ConnectivityContext.Provider,
        {
          value: {
            status: unreachable ? 'unreachable' : 'online',
            isUnreachable: unreachable,
            reportNetworkFailure: () => {},
          },
        },
        children,
      ),
    );

  const render = () => {
    const adapter = { getInvocationStatus } as unknown as AgentConversationAdapter;
    const targets = [{ invocationId: 'inv-1', retryAfterSeconds: 2 }];
    return renderHook(() => useInvocationPolling(adapter, 'conv-1', targets), { wrapper });
  };

  const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms); });

  beforeEach(() => {
    vi.useFakeTimers();
    getInvocationStatus = vi.fn();
    queryClient = new QueryClient();
    unreachable = false;
    setVisibility('visible');
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('backs off by 1.5 while unchanged and sends the last ETag', async () => {
    getInvocationStatus.mockResolvedValueOnce(status('running', 'e1')).mockResolvedValue(unchanged);
    render();
    await advance(1999);
    expect(getInvocationStatus).not.toHaveBeenCalled();
    await advance(1);
    expect(getInvocationStatus).toHaveBeenCalledTimes(1);
    expect(getInvocationStatus).toHaveBeenLastCalledWith('inv-1', undefined);
    await advance(2000);
    expect(getInvocationStatus).toHaveBeenCalledTimes(2);
    expect(getInvocationStatus).toHaveBeenLastCalledWith('inv-1', 'e1');
    await advance(2999);
    expect(getInvocationStatus).toHaveBeenCalledTimes(2);
    await advance(1);
    expect(getInvocationStatus).toHaveBeenCalledTimes(3);
    await advance(4499);
    expect(getInvocationStatus).toHaveBeenCalledTimes(3);
    await advance(1);
    expect(getInvocationStatus).toHaveBeenCalledTimes(4);
  });

  it('resets the delay on a change and counts it', async () => {
    getInvocationStatus
      .mockResolvedValueOnce(unchanged)
      .mockResolvedValueOnce(unchanged)
      .mockResolvedValueOnce(status('running', 'e2'))
      .mockResolvedValue(unchanged);
    const { result } = render();
    await advance(2000);
    await advance(3000);
    await advance(4500);
    expect(getInvocationStatus).toHaveBeenCalledTimes(3);
    expect(result.current.changes['inv-1']).toBe(1);
    await advance(1999);
    expect(getInvocationStatus).toHaveBeenCalledTimes(3);
    await advance(1);
    expect(getInvocationStatus).toHaveBeenCalledTimes(4);
  });

  it('pauses while hidden and checks at once on return', async () => {
    getInvocationStatus.mockResolvedValue(unchanged);
    render();
    act(() => setVisibility('hidden'));
    await advance(60000);
    expect(getInvocationStatus).not.toHaveBeenCalled();
    act(() => setVisibility('visible'));
    await advance(0);
    expect(getInvocationStatus).toHaveBeenCalledTimes(1);
  });

  it('pauses while offline and checks at once when back online', async () => {
    getInvocationStatus.mockResolvedValue(unchanged);
    const view = render();
    unreachable = true;
    view.rerender();
    await advance(60000);
    expect(getInvocationStatus).not.toHaveBeenCalled();
    unreachable = false;
    view.rerender();
    await advance(0);
    expect(getInvocationStatus).toHaveBeenCalledTimes(1);
  });

  it.each(['succeeded', 'failed'])('stops on %s, invalidates the turns, and sends no further request', async (state) => {
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    getInvocationStatus.mockResolvedValueOnce(status(state, 'e9'));
    render();
    await advance(2000);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['triage', 'turns', 'conv-1'] });
    await advance(120000);
    expect(getInvocationStatus).toHaveBeenCalledTimes(1);
  });

  it('stops on notFound', async () => {
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    getInvocationStatus.mockRejectedValueOnce(new TriageFailure({ kind: 'notFound' }, new PlatformFailure('incompatible', { status: 404 })));
    render();
    await advance(2000);
    await advance(120000);
    expect(getInvocationStatus).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenCalledTimes(1);
  });

  it('keeps polling after a transient failure and sends no request after unmount', async () => {
    getInvocationStatus.mockRejectedValueOnce(new Error('network')).mockResolvedValue(unchanged);
    const view = render();
    await advance(2000);
    await advance(3000);
    expect(getInvocationStatus).toHaveBeenCalledTimes(2);
    view.unmount();
    await advance(120000);
    expect(getInvocationStatus).toHaveBeenCalledTimes(2);
  });
});
