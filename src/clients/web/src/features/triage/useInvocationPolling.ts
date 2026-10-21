import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { TriageFailure, type AgentConversationAdapter } from '../../platform/adapters/agentConversationAdapter';
import { isTurnPending } from '../../platform/models/agents';
import { initialPollDelay, nextPollDelay } from './pollDelay';
import { triageKeys } from './triageKeys';

export interface PollTarget {
  invocationId: string;
  /** The platform's suggested interval from the submission or an earlier read. */
  retryAfterSeconds?: number;
}

export interface InvocationPolling {
  /** Per invocation, how often a read returned a changed status; open lineage panels refresh when it grows. */
  changes: Readonly<Record<string, number>>;
}

interface PollState {
  etag?: string;
  base: number;
  delay: number;
}

function isHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden';
}

/**
 * Conditionally polls the status of every unfinished invocation of a conversation, one timer each, and stops an
 * invocation on `succeeded`, `failed`, or `notFound`. It pauses while the page is hidden or the platform is unreachable
 * and checks at once on return; a terminal state invalidates the conversation's turns.
 */
export function useInvocationPolling(
  adapter: AgentConversationAdapter,
  conversationId: string,
  targets: readonly PollTarget[],
  onNotFound?: () => void,
): InvocationPolling {
  const queryClient = useQueryClient();
  const { isUnreachable } = useConnectivity();
  const [hidden, setHidden] = useState(isHidden);
  const [changes, setChanges] = useState<Record<string, number>>({});
  const states = useRef(new Map<string, PollState>());
  const finished = useRef(new Set<string>());
  const resumed = useRef(false);
  const targetsRef = useRef(targets);
  targetsRef.current = targets;
  const onNotFoundRef = useRef(onNotFound);
  onNotFoundRef.current = onNotFound;
  const paused = hidden || isUnreachable;
  const targetsKey = useMemo(() => targets.map((target) => target.invocationId).join('|'), [targets]);

  useEffect(() => {
    const onVisibility = () => setHidden(isHidden());
    document.addEventListener('visibilitychange', onVisibility);
    return () => document.removeEventListener('visibilitychange', onVisibility);
  }, []);

  useEffect(() => {
    states.current.clear();
    finished.current.clear();
    setChanges({});
  }, [conversationId]);

  useEffect(() => {
    if (paused) {
      resumed.current = true;
      return undefined;
    }
    const checkAtOnce = resumed.current;
    resumed.current = false;
    let cancelled = false;
    const timers = new Set<ReturnType<typeof setTimeout>>();

    const stop = (invocationId: string, invalidate: boolean) => {
      finished.current.add(invocationId);
      states.current.delete(invocationId);
      if (invalidate) void queryClient.invalidateQueries({ queryKey: triageKeys.turns(conversationId) });
    };

    const schedule = (invocationId: string, delay: number) => {
      const timer = setTimeout(() => {
        timers.delete(timer);
        void poll(invocationId);
      }, delay);
      timers.add(timer);
    };

    const poll = async (invocationId: string) => {
      const state = states.current.get(invocationId);
      if (!state || cancelled) return;
      let changed = false;
      try {
        const answer = await adapter.getInvocationStatus(invocationId, state.etag);
        if (cancelled) return;
        if (answer.kind === 'changed') {
          changed = true;
          state.etag = answer.etag;
          state.base = initialPollDelay(answer.retryAfterSeconds ?? answer.value.retryAfterSeconds);
          setChanges((current) => ({ ...current, [invocationId]: (current[invocationId] ?? 0) + 1 }));
          if (!isTurnPending(answer.value.state)) {
            stop(invocationId, true);
            return;
          }
        }
      } catch (error) {
        if (cancelled) return;
        if (error instanceof TriageFailure && error.outcome.kind === 'notFound') {
          stop(invocationId, true);
          onNotFoundRef.current?.();
          return;
        }
      }
      state.delay = nextPollDelay(state.delay, changed, state.base);
      schedule(invocationId, state.delay);
    };

    for (const target of targetsRef.current) {
      const { invocationId } = target;
      if (finished.current.has(invocationId)) continue;
      let state = states.current.get(invocationId);
      if (!state) {
        const base = initialPollDelay(target.retryAfterSeconds);
        state = { base, delay: base };
        states.current.set(invocationId, state);
        schedule(invocationId, base);
      } else {
        schedule(invocationId, checkAtOnce ? 0 : state.delay);
      }
    }

    return () => {
      cancelled = true;
      timers.forEach(clearTimeout);
      timers.clear();
    };
  }, [adapter, conversationId, paused, queryClient, targetsKey]);

  return { changes };
}
