import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useState } from 'react';
import type { AnalysisFactAdapter } from '../../platform/adapters/analysisFactAdapter';
import type { FactPage, FactSelection } from '../../platform/models/analysis';
import { analysisFactsKey } from './analysisKeys';

interface PagingState {
  selectionKey: string;
  tokens: (string | undefined)[];
  restarted: boolean;
}

const selectionKeyOf = (s: FactSelection) =>
  JSON.stringify([s.runId, s.stream.nodeKey, s.stream.capabilityId, s.stream.capabilityVersion, s.stream.schemaVersion, s.range.fromMs, s.range.toMs]);

/**
 * Pages through the facts of one selection with a continuation-token stack kept in component memory (data-model.md).
 * The stack starts over when the selection changes, on reload, and when the platform rejects a token.
 */
export function useFactPages(adapter: AnalysisFactAdapter, selection: FactSelection, enabled = true) {
  const queryClient = useQueryClient();
  const selectionKey = selectionKeyOf(selection);
  const [stored, setStored] = useState<PagingState>({ selectionKey, tokens: [undefined], restarted: false });
  const state: PagingState = stored.selectionKey === selectionKey ? stored : { selectionKey, tokens: [undefined], restarted: false };
  const token = state.tokens[state.tokens.length - 1];
  const { runId, stream, range } = selection;

  const query = useQuery({
    queryKey: analysisFactsKey(runId, stream.nodeKey, range.fromMs, range.toMs, token),
    queryFn: () =>
      adapter.listFacts({
        runId,
        nodeKey: stream.nodeKey,
        capabilityId: stream.capabilityId,
        capabilityVersion: stream.capabilityVersion,
        schemaVersion: stream.schemaVersion,
        fromMs: range.fromMs,
        toMs: range.toMs,
        continuationToken: token,
      }),
    enabled,
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchOnMount: false,
    refetchInterval: false,
  });

  const outcome = query.data;
  const rejected = outcome?.kind === 'continuationRejected';
  useEffect(() => {
    if (!rejected) return;
    queryClient.removeQueries({ queryKey: analysisFactsKey(runId, stream.nodeKey, range.fromMs, range.toMs, token), exact: true });
    setStored({ selectionKey, tokens: [undefined], restarted: true });
  }, [rejected, queryClient, runId, stream.nodeKey, range.fromMs, range.toMs, token, selectionKey]);

  const page: FactPage | undefined = outcome?.kind === 'found' ? outcome.page : undefined;
  const nextToken = page?.continuationToken;

  const next = useCallback(() => {
    if (!nextToken) return;
    setStored({ selectionKey, tokens: [...state.tokens, nextToken], restarted: false });
  }, [nextToken, selectionKey, state.tokens]);

  const previous = useCallback(() => {
    if (state.tokens.length < 2) return;
    setStored({ selectionKey, tokens: state.tokens.slice(0, -1), restarted: false });
  }, [selectionKey, state.tokens]);

  const reload = useCallback(() => {
    queryClient.removeQueries({ queryKey: ['analysis', 'facts', runId, stream.nodeKey], type: 'inactive' });
    setStored({ selectionKey, tokens: [undefined], restarted: false });
    if (state.tokens.length === 1) void query.refetch();
  }, [queryClient, runId, stream.nodeKey, selectionKey, state.tokens.length, query]);

  return {
    outcome: rejected ? undefined : outcome,
    page,
    error: query.error,
    isLoading: query.isPending && enabled,
    hasPrevious: state.tokens.length > 1,
    hasNext: Boolean(nextToken),
    pageNumber: state.tokens.length,
    restarted: state.restarted,
    next,
    previous,
    reload,
  };
}
