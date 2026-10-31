import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import type { AnalysisFactAdapter } from '../../../platform/adapters/analysisFactAdapter';
import { PlatformFailure } from '../../../platform/failure';
import type { CoverageWindow, FactStreamSummary } from '../../../platform/models/analysis';
import { analysisTimelineLaneKey } from '../analysisKeys';
import { isLanePartial, type LaneLoadStatus } from './timelineLane';

/** The only parts of a fact the timeline keeps; the payload is dropped as soon as a page arrives. */
export interface LaneFact {
  startMs: number;
  endMs: number;
}

export type LaneLoadResult =
  | { status: 'loaded'; facts: LaneFact[]; windows: CoverageWindow[]; runState: string }
  | { status: 'capabilityNotInRun' | 'unavailable' };

/** Loads every page up to the allowance; one rejected continuation restarts the load once, a second makes the lane unavailable. */
export async function loadLaneFacts(
  adapter: AnalysisFactAdapter,
  runId: string,
  stream: FactStreamSummary,
  allowance: number,
  signal?: AbortSignal,
): Promise<LaneLoadResult> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const facts: LaneFact[] = [];
    let windows: CoverageWindow[] = [];
    let runState = '';
    let token: string | undefined;
    let first = true;
    let rejected = false;

    do {
      signal?.throwIfAborted();
      const outcome = await adapter.listFacts({
        runId,
        nodeKey: stream.nodeKey,
        capabilityId: stream.capabilityId,
        capabilityVersion: stream.capabilityVersion,
        schemaVersion: stream.schemaVersion,
        continuationToken: token,
      });
      if (outcome.kind === 'capabilityNotInRun') return { status: 'capabilityNotInRun' };
      if (outcome.kind === 'runNotFound') throw new PlatformFailure('unexpected', { status: 404 });
      if (outcome.kind === 'continuationRejected') {
        rejected = true;
        break;
      }
      const { page } = outcome;
      if (first) {
        windows = page.coverage;
        runState = page.run.state;
        first = false;
      }
      for (const item of page.items) {
        if (facts.length >= allowance) break;
        facts.push({ startMs: item.startMs, endMs: item.endMs });
      }
      token = facts.length >= allowance ? undefined : page.continuationToken;
    } while (token);

    if (!rejected) return { status: 'loaded', facts, windows, runState };
  }
  return { status: 'unavailable' };
}

export interface LaneFactsView {
  status: LaneLoadStatus;
  facts: readonly LaneFact[];
  windows: readonly CoverageWindow[];
  /** The run state of the first page, undefined until loaded. */
  runState: string | undefined;
  loadedFactCount: number;
  partial: boolean;
  /** True while a reload runs and the previous data is still shown. */
  isUpdating: boolean;
  error: unknown;
  /** Reloads the lane's pages. */
  reload: () => void;
}

const NO_FACTS: readonly LaneFact[] = [];
const NO_WINDOWS: readonly CoverageWindow[] = [];

/** One query per lane: no garbage-collection time, never stale, nothing persisted, reloaded only when the lane key changes. */
export function useLaneFacts(adapter: AnalysisFactAdapter, runId: string, stream: FactStreamSummary, allowance: number, enabled = true): LaneFactsView {
  const query = useQuery({
    queryKey: [...analysisTimelineLaneKey(runId, stream.nodeKey, stream.factCount, stream.coverage), allowance] as const,
    queryFn: ({ signal }) => loadLaneFacts(adapter, runId, stream, allowance, signal),
    enabled,
    gcTime: 0,
    staleTime: Infinity,
    retry: false,
    placeholderData: keepPreviousData,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchOnMount: false,
    refetchInterval: false,
  });

  const data = query.data;
  const error = query.error;
  const refetch = query.refetch;
  return useMemo<LaneFactsView>(() => {
    const isUpdating = query.isPlaceholderData || (query.isFetching && data !== undefined);
    if (data?.status === 'loaded') {
      return {
        status: 'loaded',
        facts: data.facts,
        windows: data.windows,
        runState: data.runState,
        loadedFactCount: data.facts.length,
        partial: isLanePartial(data.facts.length, stream.factCount),
        isUpdating,
        error: null,
        reload: () => void refetch(),
      };
    }
    let status: LaneLoadStatus = 'loading';
    if (data) status = data.status;
    else if (error) status = error instanceof PlatformFailure && error.kind === 'unreachable' ? 'connectionRequired' : 'error';
    return { status, facts: NO_FACTS, windows: NO_WINDOWS, runState: undefined, loadedFactCount: 0, partial: false, isUpdating, error, reload: () => void refetch() };
  }, [data, error, refetch, query.isPlaceholderData, query.isFetching, stream.factCount]);
}
