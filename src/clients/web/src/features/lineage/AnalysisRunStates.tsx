import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import { createRecordingLineageAdapter } from '../../platform/adapters/recordingLineageAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { AnalysisRunSummary } from '../../platform/models/recordings';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';
import { formatKickoff } from '../matches/MatchForm';

export const LINEAGE_KEY = (matchId: string) => ['matches', 'lineage', matchId];
export const RUNS_KEY = (matchId: string) => ['matches', 'analysis-runs', matchId];
export const RUN_REFRESH_MS = 10_000;
const MAX_PAGES = 20;

const isUnfinished = (run: AnalysisRunSummary) => run.state === 'Requested' || run.state === 'Running';

/** Finalized recording-set versions with the state of the analysis runs created from them (FR-022, FR-023); no results. */
export function AnalysisRunStates({ matchId }: { matchId: string }) {
  const { t } = useTranslation('lineage');
  const { platform } = useSession();
  const lineageAdapter = useMemo(() => createRecordingLineageAdapter(platform), [platform]);
  const runAdapter = useMemo(() => createAnalysisRunAdapter(platform), [platform]);
  const lineage = useQuery({ queryKey: LINEAGE_KEY(matchId), queryFn: () => lineageAdapter.getLineage(matchId), retry: false, refetchOnWindowFocus: false });
  const runs = useQuery({
    queryKey: RUNS_KEY(matchId),
    queryFn: async () => {
      const all: AnalysisRunSummary[] = [];
      let token: string | undefined;
      for (let i = 0; i < MAX_PAGES; i++) {
        const page = await runAdapter.listByMatch(matchId, token ? { continuationToken: token } : undefined);
        all.push(...page.items);
        token = page.continuationToken;
        if (!token) break;
      }
      return all;
    },
    retry: false,
    refetchOnWindowFocus: false,
    // TanStack Query pauses interval refetching while the page is hidden.
    refetchInterval: (query) => (query.state.data?.some(isUnfinished) ? RUN_REFRESH_MS : false),
    refetchIntervalInBackground: false,
  });
  const { refetch: refetchLineage } = lineage;

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
                        <span role="status">{t('runs.stateLabel', { state: t(`runs.state.${run.state}`, { defaultValue: run.state }) })}</span>
                        {' · '}
                        {t('runs.run', { workflow: run.workflowId, version: run.workflowVersion })}
                        {' · '}
                        {t('runs.created', { time: formatKickoff(run.createdAt) })}
                        {run.runningAt && ` · ${t('runs.started', { time: formatKickoff(run.runningAt) })}`}
                        {run.terminatedAt && ` · ${t('runs.ended', { time: formatKickoff(run.terminatedAt) })}`}
                        {run.failureReason && ` · ${t('runs.failure', { reason: run.failureReason })}`}
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
