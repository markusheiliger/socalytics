import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createAnalysisFactAdapter } from '../../platform/adapters/analysisFactAdapter';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import { createMatchAdapter } from '../../platform/adapters/matchAdapter';
import { createRecordingLineageAdapter } from '../../platform/adapters/recordingLineageAdapter';
import { createTeamAdapter } from '../../platform/adapters/teamAdapter';
import { PlatformFailure, toPlatformFailure } from '../../platform/failure';
import { canViewTeam } from '../../session/roles';
import { useSession } from '../../session/sessionContext';
import {
  validateComparisonSide,
  validateRunPair,
  type ComparisonSelection,
  type ComparisonSide,
  type InvalidSelectionReason,
} from './comparisonPresentation';

export const COMPARISON_KEY_ROOT = 'analysis-comparison';

export type ComparisonState =
  | 'selecting'
  | 'invalidSelection'
  | 'loading'
  | 'ready'
  | 'notFound'
  | 'notPermitted'
  | 'offline'
  | 'incompatible'
  | 'error';

export interface ComparisonModel {
  matchId: string;
  teamId: string;
  left: ComparisonSide;
  right: ComparisonSide;
}

export type InvalidSelectionDetail =
  | { reason: InvalidSelectionReason }
  | { reason: 'notTerminal' | 'unknownState'; side: 'left' | 'right' };

export interface AnalysisComparison {
  state: ComparisonState;
  model?: ComparisonModel;
  invalid?: InvalidSelectionDetail;
  failure?: PlatformFailure;
  /** Starts a new generation for the same selection; ready content is dropped before any read begins. */
  refresh: () => void;
}

type LoadOutcome =
  | { kind: 'ready'; model: ComparisonModel }
  | { kind: 'ineligible'; reason: 'notTerminal' | 'unknownState'; side: 'left' | 'right' }
  | { kind: 'notFound' }
  | { kind: 'notPermitted' };

let lastGeneration = 0;
const newGeneration = () => ++lastGeneration;

const isNotFoundFailure = (error: unknown) => {
  const failure = toPlatformFailure(error);
  return failure.status === 404;
};

/**
 * Loads one validated two-run comparison. All reads belong to a dedicated, generation-scoped query identity that is
 * never retained after the hook is released and never writes to the shared run, stream, or lineage caches (FR-015).
 */
export function useAnalysisComparison(matchId: string, selection: ComparisonSelection): AnalysisComparison {
  const { platform, session } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const [generation, setGeneration] = useState(newGeneration);
  const member = session?.member;
  const memberId = member?.id;
  const pair = selection.kind === 'pair' ? selection : undefined;

  const adapters = useMemo(
    () => ({
      runs: createAnalysisRunAdapter(platform),
      facts: createAnalysisFactAdapter(platform),
      matches: createMatchAdapter(platform),
      teams: createTeamAdapter(platform),
      lineage: createRecordingLineageAdapter(platform),
    }),
    [platform],
  );

  const leftRunId = pair?.leftRunId ?? '';
  const rightRunId = pair?.rightRunId ?? '';
  const queryKey = useMemo(
    () => [COMPARISON_KEY_ROOT, memberId, matchId, [leftRunId, rightRunId], generation] as const,
    [memberId, matchId, leftRunId, rightRunId, generation],
  );
  const activeKey = useRef<string>('');
  activeKey.current = JSON.stringify(queryKey);

  const drop = useCallback(
    (key: readonly unknown[]) => {
      void queryClient.cancelQueries({ queryKey: key, exact: true });
      queryClient.removeQueries({ queryKey: key, exact: true });
    },
    [queryClient],
  );

  // A pair, member, session, generation, or mount change retires the previous identity and discards its data.
  useEffect(() => () => drop(queryKey), [drop, queryKey]);
  useEffect(() => {
    if (isUnreachable) setGeneration(newGeneration());
  }, [isUnreachable]);

  const enabled = Boolean(pair && memberId && !isUnreachable);
  const query = useQuery({
    queryKey,
    enabled,
    retry: false,
    gcTime: 0,
    staleTime: Infinity,
    networkMode: 'always',
    refetchOnMount: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false,
    queryFn: async (): Promise<LoadOutcome> => {
      const identity = JSON.stringify(queryKey);
      // Late completions of a retired identity are discarded even when the transport cannot be aborted.
      const guard = <T,>(value: T): T => {
        if (activeKey.current !== identity) throw new PlatformFailure('unreachable');
        return value;
      };
      let match;
      let team;
      try {
        match = guard(await adapters.matches.get(matchId));
        team = guard(await adapters.teams.get(match.teamId));
      } catch (error) {
        if (isNotFoundFailure(error)) return { kind: 'notFound' };
        throw error;
      }
      if (!member || !canViewTeam(member, match.teamId)) return { kind: 'notPermitted' };
      if (team.id.toLowerCase() !== match.teamId.toLowerCase()) throw new PlatformFailure('incompatible');

      const [left, right] = guard(await Promise.all([adapters.runs.getRun(leftRunId), adapters.runs.getRun(rightRunId)]));
      if (left.kind !== 'found' || right.kind !== 'found') return { kind: 'notFound' };
      const eligibility = validateRunPair({
        matchId,
        teamId: match.teamId,
        leftRunId,
        rightRunId,
        left: left.run,
        right: right.run,
      });
      if (eligibility.kind === 'ineligible') return { kind: 'ineligible', side: eligibility.side, reason: eligibility.reason };

      const leftSetId = left.run.recordingSetVersionId;
      const rightSetId = right.run.recordingSetVersionId;
      const shared = leftSetId.toLowerCase() === rightSetId.toLowerCase();
      const leftSet = adapters.lineage.getSet(matchId, leftSetId);
      const rightSet = shared ? leftSet : adapters.lineage.getSet(matchId, rightSetId);
      const [leftStreams, rightStreams, leftRecordingSet, rightRecordingSet] = guard(await Promise.all([
        adapters.facts.listStreams(leftRunId),
        adapters.facts.listStreams(rightRunId),
        leftSet,
        rightSet,
      ]));
      if (leftStreams.kind !== 'found' || rightStreams.kind !== 'found') return { kind: 'notFound' };

      const base = { matchId, teamId: match.teamId };
      return {
        kind: 'ready',
        model: {
          ...base,
          left: validateComparisonSide({ ...base, run: left.run, streams: leftStreams.streams, recordingSet: leftRecordingSet }),
          right: validateComparisonSide({ ...base, run: right.run, streams: rightStreams.streams, recordingSet: rightRecordingSet }),
        },
      };
    },
  });

  const refresh = useCallback(() => {
    drop(queryKey);
    setGeneration(newGeneration());
  }, [drop, queryKey]);

  if (selection.kind === 'empty' || selection.kind === 'left') return { state: 'selecting', refresh };
  if (selection.kind === 'invalid') return { state: 'invalidSelection', invalid: { reason: selection.reason }, refresh };
  if (isUnreachable) return { state: 'offline', refresh };
  if (!memberId) return { state: 'error', failure: new PlatformFailure('sessionEnded'), refresh };

  if (query.error) {
    const failure = toPlatformFailure(query.error);
    if (failure.kind === 'notPermitted') return { state: 'notPermitted', failure, refresh };
    if (failure.kind === 'unreachable') return { state: 'offline', failure, refresh };
    if (failure.kind === 'incompatible') return { state: 'incompatible', failure, refresh };
    return { state: 'error', failure, refresh };
  }
  const outcome = query.data;
  if (!outcome) return { state: 'loading', refresh };
  if (outcome.kind === 'ready') return { state: 'ready', model: outcome.model, refresh };
  if (outcome.kind === 'ineligible') return { state: 'invalidSelection', invalid: { reason: outcome.reason, side: outcome.side }, refresh };
  return { state: outcome.kind, refresh };
}
