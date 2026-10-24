import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useState } from 'react';
import { PlatformFailure, toPlatformFailure } from '../../platform/failure';

interface PagingState {
  filterKey: string;
  tokens: (string | undefined)[];
  expired: boolean;
}

export interface TokenPage {
  continuationToken?: string;
}

const firstPage = (filterKey: string, expired = false): PagingState => ({ filterKey, tokens: [undefined], expired });

const isExpiredToken = (error: unknown) => {
  if (!error) return false;
  const failure = toPlatformFailure(error);
  return failure instanceof PlatformFailure && failure.kind === 'programmingError' && failure.fieldErrors.some((e) => e.field === 'continuationToken');
};

/**
 * Pages forward and back through a list that the platform pages with opaque continuation tokens. The stack of tokens
 * lives only in component memory per filter key; a filter change, a refresh, or a rejected token returns to the first page.
 * `queryKey` identifies the list and its filters; the token is appended to it.
 */
export function useContinuationPages<P extends TokenPage>(options: {
  queryKey: readonly unknown[];
  fetchPage: (continuationToken: string | undefined) => Promise<P>;
  enabled?: boolean;
}) {
  const { queryKey, fetchPage, enabled = true } = options;
  const queryClient = useQueryClient();
  const filterKey = JSON.stringify(queryKey);
  const [stored, setStored] = useState<PagingState>(firstPage(filterKey));
  const state = stored.filterKey === filterKey ? stored : firstPage(filterKey);
  const token = state.tokens[state.tokens.length - 1];

  const query = useQuery({
    queryKey: [...queryKey, token],
    queryFn: () => fetchPage(token),
    enabled,
    placeholderData: keepPreviousData,
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchOnMount: false,
    refetchInterval: false,
  });

  const rejected = token !== undefined && isExpiredToken(query.error);
  useEffect(() => {
    if (!rejected) return;
    queryClient.removeQueries({ queryKey: [...queryKey, token], exact: true });
    setStored(firstPage(filterKey, true));
  }, [rejected, queryClient, filterKey, token]);

  const page = rejected ? undefined : query.data;
  const nextToken = page?.continuationToken;

  const next = useCallback(() => {
    if (!nextToken) return;
    setStored({ filterKey, tokens: [...state.tokens, nextToken], expired: false });
  }, [nextToken, filterKey, state.tokens]);

  const previous = useCallback(() => {
    if (state.tokens.length < 2) return;
    setStored({ filterKey, tokens: state.tokens.slice(0, -1), expired: false });
  }, [filterKey, state.tokens]);

  const refresh = useCallback(() => {
    queryClient.removeQueries({ queryKey, type: 'inactive' });
    setStored(firstPage(filterKey));
    if (state.tokens.length === 1) void query.refetch();
  }, [queryClient, filterKey, state.tokens.length, query]);

  return {
    page,
    error: rejected ? null : query.error,
    isLoading: query.isPending && enabled,
    isFetching: query.isFetching,
    hasPrevious: state.tokens.length > 1,
    hasNext: Boolean(nextToken),
    pageNumber: state.tokens.length,
    expired: state.expired,
    next,
    previous,
    refresh,
  };
}
