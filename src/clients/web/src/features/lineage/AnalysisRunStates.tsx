import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import { createRecordingLineageAdapter } from '../../platform/adapters/recordingLineageAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import type { AnalysisRunSummary } from '../../platform/models/recordings';
import { canEditTeamMatches } from '../../session/roles';
import { useSession } from '../../session/sessionContext';
import { FailureAlert, useFieldError } from '../shared/FailureAlert';
import { CancelRunDialog } from './CancelRunDialog';
import { formatKickoff } from '../matches/MatchForm';

export const LINEAGE_KEY = (matchId: string) => ['matches', 'lineage', matchId];
export const RUNS_KEY = (matchId: string) => ['matches', 'analysis-runs', matchId];
export const RUN_REFRESH_MS = 10_000;
const MAX_PAGES = 20;

/** All runs of a match across pages. */
export async function loadAllRuns(adapter: ReturnType<typeof createAnalysisRunAdapter>, matchId: string): Promise<AnalysisRunSummary[]> {
  const all: AnalysisRunSummary[] = [];
  let token: string | undefined;
  for (let i = 0; i < MAX_PAGES; i++) {
    const page = await adapter.listByMatch(matchId, token ? { continuationToken: token } : undefined);
    all.push(...page.items);
    token = page.continuationToken;
    if (!token) break;
  }
  return all;
}

const isUnfinished = (run: AnalysisRunSummary) => run.state === 'Requested' || run.state === 'Running';

const cancelledBySupersession = (run: AnalysisRunSummary) => run.state === 'Cancelled' && run.cancellationCause === 'superseded';

const cancelled = (run: AnalysisRunSummary) => run.state === 'Cancelled' && Boolean(run.cancelledBy) && !cancelledBySupersession(run);

/** Finalized recording-set versions with the state of the analysis runs created from them (FR-022, FR-023); no results. */
export function AnalysisRunStates({ matchId, teamId }: { matchId: string; teamId?: string }) {
  const { t } = useTranslation('lineage');
  const { t: tc } = useTranslation('common');
  const { platform, session } = useSession();
  const queryClient = useQueryClient();
  const [target, setTarget] = useState<AnalysisRunSummary | undefined>();
  const [cancelFailure, setCancelFailure] = useState<PlatformFailure | undefined>();
  const member = session?.member;
  const canCancel = Boolean(member && teamId && canEditTeamMatches(member, teamId));
  const lineageAdapter = useMemo(() => createRecordingLineageAdapter(platform), [platform]);
  const runAdapter = useMemo(() => createAnalysisRunAdapter(platform), [platform]);
  const lineage = useQuery({ queryKey: LINEAGE_KEY(matchId), queryFn: () => lineageAdapter.getLineage(matchId), retry: false, refetchOnWindowFocus: false });
  const runs = useQuery({
    queryKey: RUNS_KEY(matchId),
    queryFn: () => loadAllRuns(runAdapter, matchId),
    retry: false,
    refetchOnWindowFocus: false,
    // TanStack Query pauses interval refetching while the page is hidden.
    refetchInterval: (query) => (query.state.data?.some(isUnfinished) ? RUN_REFRESH_MS : false),
    refetchIntervalInBackground: false,
  });
  const { refetch: refetchLineage } = lineage;
  const { refetch: refetchRuns } = runs;
  const cancel = useMutation({
    mutationFn: ({ run, reason }: { run: AnalysisRunSummary; reason?: string }) => runAdapter.cancel(run.runId, run.version, reason),
    onSuccess: (updated) => {
      setCancelFailure(undefined);
      setTarget(undefined);
      queryClient.setQueryData<AnalysisRunSummary[]>(RUNS_KEY(matchId), (old) => old?.map((r) => (r.runId === updated.runId ? updated : r)));
      void refetchRuns();
    },
    onError: (error) => {
      const failure = toPlatformFailure(error);
      setCancelFailure(failure);
      if (failure.kind !== 'validation') setTarget(undefined);
      void refetchRuns();
    },
  });
  const reasonError = useFieldError(cancelFailure, 'reason', tc);
  const closeDialog = () => {
    setTarget(undefined);
    setCancelFailure(undefined);
  };

  const sets = useMemo(
    () => [...(lineage.data?.recordingSetVersions ?? [])].sort((a, b) => b.finalizedAt.getTime() - a.finalizedAt.getTime()),
    [lineage.data],
  );
  const byVersion = useMemo(() => {
    const groups = new Map<string, AnalysisRunSummary[]>();
    for (const run of runs.data ?? []) groups.set(run.recordingSetVersionId, [...(groups.get(run.recordingSetVersionId) ?? []), run]);
    return groups;
  }, [runs.data]);
  const failure = lineage.error ?? runs.error;

  return (
    <section aria-label={t('runs.title')}>
      <Typography variant="h3" component="h3" sx={{ mt: 3 }}>
        {t('runs.title')}
      </Typography>
      {failure && (
        <FailureAlert
          failure={toPlatformFailure(failure)}
          onReload={() => {
            void refetchLineage();
            void runs.refetch();
          }}
        />
      )}
      {cancelFailure && cancelFailure.kind !== 'validation' && <FailureAlert failure={cancelFailure} onReload={() => void refetchRuns()} />}
      {lineage.data && sets.length === 0 && <Typography>{t('runs.empty')}</Typography>}
      {sets.length > 0 && (
        <ul aria-label={t('runs.listLabel')}>
          {sets.map((set) => {
            const setRuns = byVersion.get(set.id) ?? [];
            return (
              <li key={set.id}>
                <Typography>
                  {t('runs.setSummary', { time: formatKickoff(set.finalizedAt), count: set.members.length })}
                </Typography>
                <ol>
                  {set.members.map((m) => (
                    <li key={m.position}>
                      {t('runs.member', { id: m.recordingVersionId, content: m.recordingContentDigest, mapping: m.timelineMappingDigest })}
                    </li>
                  ))}
                </ol>
                {runs.data && setRuns.length === 0 && <Typography>{t('runs.noRuns')}</Typography>}
                {setRuns.length > 0 && (
                  <ul aria-label={t('runs.runsLabel')}>
                    {setRuns.map((run) => (
                      <li key={run.runId}>
                        <span role="status">
                          {cancelled(run)
                            ? t('runs.cancelledBy', { name: run.cancelledBy?.accountName })
                            : cancelledBySupersession(run)
                              ? t('runs.cancelledSuperseded')
                              : t('runs.stateLabel', { state: t(`runs.state.${run.state}`, { defaultValue: run.state }) })}
                          {cancelled(run) && run.terminatedAt && ` · ${formatKickoff(run.terminatedAt)}`}
                          {cancelled(run) && run.cancellationReason && ` · ${run.cancellationReason}`}
                          {run.isCurrent && ` · ${t('runs.current')}`}
                          {run.supersededBy &&
                            ` · ${t('runs.supersededBy', {
                              set: formatKickoff(run.supersededBy.recordingSetFinalizedAt),
                              time: run.supersededAt ? formatKickoff(run.supersededAt) : '',
                            })}`}
                        </span>
                        {' · '}
                        {t('runs.run', { workflow: run.workflowId, version: run.workflowVersion })}
                        {' · '}
                        {t('runs.created', { time: formatKickoff(run.createdAt) })}
                        {run.runningAt && ` · ${t('runs.started', { time: formatKickoff(run.runningAt) })}`}
                        {run.terminatedAt && !cancelled(run) && ` · ${t('runs.ended', { time: formatKickoff(run.terminatedAt) })}`}
                        {run.failureReason && ` · ${t('runs.failure', { reason: run.failureReason })}`}
                        {canCancel && isUnfinished(run) && (
                          <Button
                            size="small"
                            color="error"
                            sx={{ ml: 1 }}
                            onClick={() => {
                              setCancelFailure(undefined);
                              setTarget(run);
                            }}
                          >
                            {t('runs.cancelAction')}
                          </Button>
                        )}
                        {' · '}
                        <Link to={`/matches/${matchId}/analysis-runs/${run.runId}`}>{t('runs.openReview')}</Link>
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            );
          })}
        </ul>
      )}
      <CancelRunDialog
        open={Boolean(target)}
        busy={cancel.isPending}
        fieldError={reasonError}
        onConfirm={(reason) => target && cancel.mutate({ run: target, reason })}
        onClose={closeDialog}
      />
    </section>
  );
}
