import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { createRecordingLineageAdapter } from '../../platform/adapters/recordingLineageAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { useUploads } from '../../uploads/uploadContext';
import { AnalysisRunStates } from '../lineage/AnalysisRunStates';
import { LineageSection } from '../lineage/LineageSection';
import { FailureAlert } from '../shared/FailureAlert';
import { TriageSection } from '../triage/TriageSection';
import { AddRecordingsPanel } from '../uploads/AddRecordingsPanel';
import { formatBytes } from '../uploads/formatBytes';
import { PendingUploads } from '../uploads/PendingUploads';
import { UploadList } from '../uploads/UploadList';
import { formatKickoff } from './MatchForm';
import { useMatchContext } from './useMatchContext';

const NO_JOBS: readonly never[] = [];

/** The match's accepted recording versions and, for editors, the upload views (FR-005–FR-011). */
function RecordingsSection({ matchId, teamId, canUpload }: { matchId: string; teamId: string; canUpload: boolean }) {
  const { t } = useTranslation('uploads');
  const { platform } = useSession();
  const repository = useUploads();
  const adapter = useMemo(() => createRecordingLineageAdapter(platform), [platform]);
  const lineage = useQuery({ queryKey: ['matches', 'lineage', matchId], queryFn: () => adapter.getLineage(matchId), retry: false, refetchOnWindowFocus: false });
  const jobs = useSyncExternalStore(
    (listener) => repository?.subscribe(listener) ?? (() => undefined),
    () => repository?.getSnapshot() ?? NO_JOBS,
  );
  useEffect(() => {
    void repository?.loadMatch(matchId);
  }, [repository, matchId]);
  const matchJobs = useMemo(() => jobs.filter((job) => job.matchId === matchId), [jobs, matchId]);
  const completedCount = matchJobs.filter((job) => job.state === 'completed').length;
  const { refetch } = lineage;
  useEffect(() => {
    if (completedCount > 0) void refetch();
  }, [completedCount, refetch]);
  const versions = lineage.data?.recordingVersions ?? [];
  const knownDigests = useMemo(() => new Set(versions.map((v) => v.contentDigest)), [versions]);

  return (
    <section aria-label={t('recordings.title')}>
      <Typography variant="h3" component="h3" sx={{ mt: 3 }}>
        {t('recordings.title')}
      </Typography>
      {lineage.error && <FailureAlert failure={toPlatformFailure(lineage.error)} onReload={() => void refetch()} />}
      {lineage.data && versions.length === 0 && <Typography>{t('recordings.empty')}</Typography>}
      {versions.length > 0 && (
        <ul aria-label={t('recordings.listLabel')}>
          {versions.map((v) => (
            <li key={v.id}>
              {t('recordings.version', { name: v.recording.displayName, size: formatBytes(v.totalSizeBytes), digest: v.contentDigest })}
            </li>
          ))}
        </ul>
      )}
      {canUpload && repository && (
        <>
          <AddRecordingsPanel repository={repository} matchId={matchId} teamId={teamId} />
          <UploadList repository={repository} jobs={matchJobs} knownDigests={knownDigests} canUpload={canUpload} />
        </>
      )}
      {repository && <PendingUploads repository={repository} jobs={matchJobs} canUpload={canUpload} />}
    </section>
  );
}

/** One match: details for readers; edit actions only for editors while the season is not archived. */
export function MatchPage() {
  const { matchId = '' } = useParams();
  const { t } = useTranslation('matches');
  const { t: tc } = useTranslation('common');
  const { t: tl } = useTranslation('lineage');
  const { match, team, permitted, editor, archived, canEdit } = useMatchContext(matchId);
  const m = match.data;
  const error = match.error ?? team.error;
  if (m && !permitted) return <NotPermittedPage />;
  if (match.error && toPlatformFailure(match.error).kind === 'notPermitted') return <NotPermittedPage />;

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{m ? m.opponentName : t('matches.matchTitle')}</Typography>
      {error && (
        <FailureAlert
          failure={toPlatformFailure(error)}
          onReload={() => {
            void match.refetch();
            void team.refetch();
          }}
        />
      )}
      {m && (
        <>
          <Stack component="dl" spacing={1} sx={{ mt: 2 }} aria-label={t('matches.details')}>
            <div>
              <dt>{t('matches.opponent')}</dt>
              <dd>{m.opponentName}</dd>
            </div>
            <div>
              <dt>{t('matches.kickoff')}</dt>
              <dd>{formatKickoff(m.kickoffAt)}</dd>
            </div>
            <div>
              <dt>{t('matches.venue')}</dt>
              <dd>{t(`matches.venueOption.${m.homeAway}`)}</dd>
            </div>
            {m.competition && (
              <div>
                <dt>{t('matches.competition')}</dt>
                <dd>{m.competition}</dd>
              </div>
            )}
          </Stack>
          {archived && <Typography sx={{ mt: 1 }}>{t('matches.readOnlyNote')}</Typography>}
          {!editor && <Typography sx={{ mt: 1 }}>{t('matches.viewOnlyNote')}</Typography>}
          {canEdit && (
            <Button component={Link} to={`/matches/${m.id}/edit`} variant="contained" sx={{ mt: 2, mr: 1 }}>
              {t('matches.editAction')}
            </Button>
          )}
          {canEdit && (
            <Button component={Link} to={`/matches/${m.id}/finalize`} variant="contained" sx={{ mt: 2, mr: 1 }}>
              {tl('finalize.action')}
            </Button>
          )}
          <Button component={Link} to={`/teams/${m.teamId}`} variant="outlined" sx={{ mt: 2 }}>
            {t('matches.backToTeam')}
          </Button>
          <RecordingsSection matchId={m.id} teamId={m.teamId} canUpload={canEdit} />
          <AnalysisRunStates matchId={m.id} />
          <TriageSection matchId={m.id} canStart={editor} />
          <LineageSection matchId={m.id} />
        </>
      )}
    </Container>
  );
}
