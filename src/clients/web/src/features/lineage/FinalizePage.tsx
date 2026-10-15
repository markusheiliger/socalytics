import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import Container from '@mui/material/Container';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import FormControlLabel from '@mui/material/FormControlLabel';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router';
import { createRecordingLineageAdapter } from '../../platform/adapters/recordingLineageAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { FinalizeMember, RecordingSetVersion, TimelineMapping } from '../../platform/models/recordings';
import { useSession } from '../../session/sessionContext';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { formatKickoff } from '../matches/MatchForm';
import { useMatchContext } from '../matches/useMatchContext';
import { LINEAGE_KEY, RUNS_KEY } from './AnalysisRunStates';

/** The platform's default `RecordingSets:MaxMembers`; its refusal stays authoritative. */
export const DEFAULT_MAX_MEMBERS = 100;

const newestFirst = (a: TimelineMapping, b: TimelineMapping) => b.createdAt.getTime() - a.createdAt.getTime();

/** True when order, versions, and mappings equal the newest finalized set. */
export function isIdenticalToNewest(members: FinalizeMember[], sets: RecordingSetVersion[]): boolean {
  if (sets.length === 0) return false;
  const newest = [...sets].sort((a, b) => b.finalizedAt.getTime() - a.finalizedAt.getTime())[0];
  return (
    newest.members.length === members.length &&
    [...newest.members]
      .sort((a, b) => a.position - b.position)
      .every((m, i) => m.recordingVersionId === members[i].recordingVersionId && m.timelineMappingId === members[i].timelineMappingId)
  );
}

/** Select, order, and confirm the recording versions of a new recording-set version (FR-020). */
export function FinalizePage() {
  const { matchId = '' } = useParams();
  const { t } = useTranslation('lineage');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createRecordingLineageAdapter(platform), [platform]);
  const { match, team, permitted, editor, archived, canEdit } = useMatchContext(matchId);
  const lineage = useQuery({ queryKey: LINEAGE_KEY(matchId), queryFn: () => adapter.getLineage(matchId), retry: false, refetchOnWindowFocus: false });
  const [order, setOrder] = useState<string[]>([]);
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const [dialogKey, setDialogKey] = useState<string>();

  const versions = lineage.data?.recordingVersions ?? [];
  const mappingsOf = (versionId: string) => (lineage.data?.timelineMappings ?? []).filter((m) => m.recordingVersionId === versionId).sort(newestFirst);
  const mappingFor = (versionId: string) => chosen[versionId] ?? mappingsOf(versionId)[0]?.id ?? '';
  const members: FinalizeMember[] = order.map((id) => ({ recordingVersionId: id, timelineMappingId: mappingFor(id) }));
  const identical = isIdenticalToNewest(members, lineage.data?.recordingSetVersions ?? []);
  const nameOf = (id: string) => versions.find((v) => v.id === id)?.recording.displayName ?? id;
  const mappingTime = (id: string) => {
    const mapping = lineage.data?.timelineMappings.find((m) => m.id === id);
    return mapping ? formatKickoff(mapping.createdAt) : id;
  };

  const finalize = useMutation({
    mutationFn: (key: string) => adapter.finalize(matchId, members, key),
    onSuccess: async () => {
      await Promise.all([queryClient.invalidateQueries({ queryKey: LINEAGE_KEY(matchId) }), queryClient.invalidateQueries({ queryKey: RUNS_KEY(matchId) })]);
      void navigate(`/matches/${matchId}`);
    },
  });

  const tooMany = order.length > DEFAULT_MAX_MEMBERS;
  const missingMapping = members.some((m) => !m.timelineMappingId);
  const valid = order.length > 0 && !tooMany && !missingMapping;

  const toggle = (id: string, on: boolean) => setOrder((current) => (on ? [...current, id] : current.filter((x) => x !== id)));
  const move = (index: number, delta: number) =>
    setOrder((current) => {
      const next = [...current];
      [next[index], next[index + delta]] = [next[index + delta], next[index]];
      return next;
    });
  const openDialog = () => {
    finalize.reset();
    setDialogKey(crypto.randomUUID());
  };
  const closeDialog = () => {
    if (finalize.isPending) return;
    finalize.reset();
    setDialogKey(undefined);
  };

  if (match.data && (!permitted || !editor)) return <NotPermittedPage />;
  if (match.error && toPlatformFailure(match.error).kind === 'notPermitted') return <NotPermittedPage />;
  const failure = finalize.error ? toPlatformFailure(finalize.error) : undefined;
  const loadError = match.error ?? team.error ?? lineage.error;

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('finalize.title')}</Typography>
      {loadError && (
        <FailureAlert
          failure={toPlatformFailure(loadError)}
          onReload={() => {
            void match.refetch();
            void lineage.refetch();
          }}
        />
      )}
      {archived && <Typography sx={{ mt: 1 }}>{t('finalize.readOnlyNote')}</Typography>}
      {canEdit && lineage.data && (
        <>
          <Typography sx={{ mt: 1 }}>{t('finalize.intro')}</Typography>
          {versions.length === 0 && <Typography sx={{ mt: 2 }}>{t('finalize.noVersions')}</Typography>}
          {versions.length > 0 && (
            <section aria-label={t('finalize.availableLabel')}>
              <Typography variant="h3" component="h3" sx={{ mt: 2 }}>
                {t('finalize.available')}
              </Typography>
              <ul style={{ listStyle: 'none', padding: 0 }}>
                {versions.map((v) => (
                  <li key={v.id}>
                    <FormControlLabel
                      control={<Checkbox checked={order.includes(v.id)} onChange={(e) => toggle(v.id, e.target.checked)} />}
                      label={t('finalize.include', { name: v.recording.displayName })}
                    />
                  </li>
                ))}
              </ul>
            </section>
          )}
          <section aria-label={t('finalize.selectedLabel')}>
            <Typography variant="h3" component="h3" sx={{ mt: 2 }}>
              {t('finalize.selected')}
            </Typography>
            {order.length === 0 && <Typography>{t('finalize.nothingSelected')}</Typography>}
            <ol>
              {order.map((id, index) => (
                <li key={id} style={{ marginBottom: 12 }}>
                  <Typography component="span">{nameOf(id)}</Typography>{' '}
                  <Button size="small" disabled={index === 0} onClick={() => move(index, -1)} aria-label={t('finalize.moveUp', { name: nameOf(id) })}>
                    {t('finalize.upSymbol')}
                  </Button>
                  <Button size="small" disabled={index === order.length - 1} onClick={() => move(index, 1)} aria-label={t('finalize.moveDown', { name: nameOf(id) })}>
                    {t('finalize.downSymbol')}
                  </Button>
                  <TextField
                    select
                    fullWidth
                    size="small"
                    label={t('finalize.mapping', { name: nameOf(id) })}
                    value={mappingFor(id)}
                    onChange={(e) => setChosen((current) => ({ ...current, [id]: e.target.value }))}
                    slotProps={{ select: { native: true } }}
                  >
                    {mappingsOf(id).map((m, i) => (
                      <option key={m.id} value={m.id}>
                        {t('finalize.mappingOption', { time: formatKickoff(m.createdAt), count: m.spans.length, digest: m.digest })}
                        {i === 0 ? ` (${t('finalize.newestMapping')})` : ''}
                      </option>
                    ))}
                  </TextField>
                </li>
              ))}
            </ol>
            {tooMany && (
              <Alert severity="error" role="alert">
                {t('finalize.tooMany', { max: DEFAULT_MAX_MEMBERS })}
              </Alert>
            )}
          </section>
          <Button variant="contained" disabled={!valid} onClick={openDialog} sx={{ mt: 2, mr: 1 }}>
            {t('finalize.submit')}
          </Button>
        </>
      )}
      <Button component={Link} to={`/matches/${matchId}`} variant="outlined" sx={{ mt: 2 }}>
        {t('finalize.back')}
      </Button>
      <Dialog open={dialogKey !== undefined} onClose={closeDialog} aria-labelledby="finalize-title">
        <DialogTitle id="finalize-title">{t('finalize.confirmTitle')}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('finalize.confirmIntro')}</DialogContentText>
          <ol>
            {members.map((m, i) => (
              <li key={m.recordingVersionId}>{t('finalize.confirmItem', { position: i + 1, name: nameOf(m.recordingVersionId), time: mappingTime(m.timelineMappingId) })}</li>
            ))}
          </ol>
          {identical && (
            <Alert severity="warning" role="alert">
              {t('finalize.identicalWarning')}
            </Alert>
          )}
          {failure && <FailureAlert failure={failure} />}
        </DialogContent>
        <DialogActions>
          <Button onClick={closeDialog} disabled={finalize.isPending}>
            {t('finalize.cancel')}
          </Button>
          <Button variant="contained" disabled={finalize.isPending} onClick={() => dialogKey && finalize.mutate(dialogKey)}>
            {finalize.isPending ? t('finalize.working') : failure ? t('finalize.retry') : identical ? t('finalize.confirmAgain') : t('finalize.confirm')}
          </Button>
        </DialogActions>
      </Dialog>
    </Container>
  );
}
