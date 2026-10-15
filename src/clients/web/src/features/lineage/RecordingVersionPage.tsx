import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { createRecordingLineageAdapter } from '../../platform/adapters/recordingLineageAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { TimelineSpan } from '../../platform/models/recordings';
import { useSession } from '../../session/sessionContext';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { formatKickoff } from '../matches/MatchForm';
import { useMatchContext } from '../matches/useMatchContext';
import { FailureAlert } from '../shared/FailureAlert';
import { formatBytes } from '../uploads/formatBytes';
import { createDefaultDraft, toTimelineSpans, type SpanDraft } from '../uploads/mappingDraft';
import { formatTime } from '../uploads/mappingTime';
import { TimelineMappingEditor } from '../uploads/TimelineMappingEditor';
import { LINEAGE_KEY } from './AnalysisRunStates';

/** One recording version with its timeline mappings; editors may add a revised mapping (FR-019, FR-021). */
export function RecordingVersionPage() {
  const { matchId = '', recordingVersionId = '' } = useParams();
  const { t } = useTranslation('lineage');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createRecordingLineageAdapter(platform), [platform]);
  const { match, team, permitted, archived, canEdit } = useMatchContext(matchId);
  const lineage = useQuery({ queryKey: LINEAGE_KEY(matchId), queryFn: () => adapter.getLineage(matchId), retry: false, refetchOnWindowFocus: false });
  const [dialogKey, setDialogKey] = useState<string>();
  const [draft, setDraft] = useState<SpanDraft[]>(createDefaultDraft());
  const [showErrors, setShowErrors] = useState(false);

  const version = lineage.data?.recordingVersions.find((v) => v.id === recordingVersionId);
  const mappings = useMemo(
    () =>
      (lineage.data?.timelineMappings ?? [])
        .filter((m) => m.recordingVersionId === recordingVersionId)
        .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime()),
    [lineage.data, recordingVersionId],
  );

  const revise = useMutation({
    mutationFn: ({ spans, key }: { spans: TimelineSpan[]; key: string }) => adapter.reviseMapping(matchId, recordingVersionId, spans, key),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: LINEAGE_KEY(matchId) });
      setDialogKey(undefined);
    },
  });

  const openDialog = () => {
    const newest = mappings[0];
    setDraft(
      newest
        ? newest.spans.map((s) => ({ mediaStart: formatTime(s.mediaStartSeconds), mediaEnd: formatTime(s.mediaEndSeconds), matchStart: formatTime(s.matchStartSeconds) }))
        : createDefaultDraft(),
    );
    setShowErrors(false);
    revise.reset();
    setDialogKey(crypto.randomUUID());
  };
  const closeDialog = () => {
    if (revise.isPending) return;
    revise.reset();
    setDialogKey(undefined);
  };
  const submit = () => {
    setShowErrors(true);
    const result = toTimelineSpans(draft);
    if (result.ok && dialogKey) revise.mutate({ spans: result.spans, key: dialogKey });
  };

  if (match.data && !permitted) return <NotPermittedPage />;
  if (match.error && toPlatformFailure(match.error).kind === 'notPermitted') return <NotPermittedPage />;
  const failure = revise.error ? toPlatformFailure(revise.error) : undefined;
  const loadError = match.error ?? team.error ?? lineage.error;

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('version.title')}</Typography>
      {loadError && (
        <FailureAlert
          failure={toPlatformFailure(loadError)}
          onReload={() => {
            void match.refetch();
            void lineage.refetch();
          }}
        />
      )}
      {lineage.data && !version && <Typography sx={{ mt: 1 }}>{t('version.notFound')}</Typography>}
      {version && (
        <>
          <Stack component="dl" spacing={1} sx={{ mt: 2 }}>
            <div>
              <dt>{t('version.name')}</dt>
              <dd>{version.recording.displayName}</dd>
            </div>
            <div>
              <dt>{t('version.size')}</dt>
              <dd>{formatBytes(version.totalSizeBytes)}</dd>
            </div>
            <div>
              <dt>{t('version.digest')}</dt>
              <dd>{version.contentDigest}</dd>
            </div>
            <div>
              <dt>{t('version.uploaded')}</dt>
              <dd>{formatKickoff(version.createdAt)}</dd>
            </div>
          </Stack>
          <section aria-label={t('version.mappingsLabel')}>
            <Typography variant="h3" component="h3" sx={{ mt: 2 }}>
              {t('version.mappingsTitle')}
            </Typography>
            <ul>
              {mappings.map((m, i) => (
                <li key={m.id}>
                  {t('version.mapping', { digest: m.digest, time: formatKickoff(m.createdAt) })}
                  {i === 0 ? ` (${t('version.newest')})` : ''}
                  <ul>
                    {m.spans.map((s, index) => (
                      <li key={index}>
                        {t('review.span', { start: formatTime(s.mediaStartSeconds), end: formatTime(s.mediaEndSeconds), match: formatTime(s.matchStartSeconds) })}
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>
          </section>
          {archived && <Typography sx={{ mt: 1 }}>{t('version.readOnlyNote')}</Typography>}
          {canEdit && (
            <Button variant="contained" onClick={openDialog} sx={{ mt: 2, mr: 1 }}>
              {t('version.revise')}
            </Button>
          )}
        </>
      )}
      <Button component={Link} to={`/matches/${matchId}`} variant="outlined" sx={{ mt: 2 }}>
        {t('version.back')}
      </Button>
      <Dialog open={dialogKey !== undefined} onClose={closeDialog} aria-labelledby="revise-title" fullWidth maxWidth="md">
        <DialogTitle id="revise-title">{t('version.reviseTitle')}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('version.reviseIntro')}</DialogContentText>
          <TimelineMappingEditor spans={draft} onChange={setDraft} showErrors={showErrors} failure={failure} disabled={revise.isPending} />
          {failure && <FailureAlert failure={failure} />}
        </DialogContent>
        <DialogActions>
          <Button onClick={closeDialog} disabled={revise.isPending}>
            {t('version.cancel')}
          </Button>
          <Button variant="contained" disabled={revise.isPending} onClick={submit}>
            {revise.isPending ? t('version.working') : failure ? t('version.retry') : t('version.submit')}
          </Button>
        </DialogActions>
      </Dialog>
    </Container>
  );
}
