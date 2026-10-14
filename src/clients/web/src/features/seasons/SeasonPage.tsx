import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createSeasonAdapter } from '../../platform/adapters/seasonAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';
import { SEASONS_KEY } from './SeasonsPage';

/** One season: activate a draft, archive an active season. */
export function SeasonPage() {
  const { seasonId = '' } = useParams();
  const { t } = useTranslation('seasons');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createSeasonAdapter(platform), [platform]);
  const key = [...SEASONS_KEY, seasonId];
  const [done, setDone] = useState<'activated' | 'archived'>();
  const query = useQuery({ queryKey: key, queryFn: () => adapter.get(seasonId), retry: false });
  const transition = useMutation({
    mutationFn: (action: 'activate' | 'archive') => (action === 'activate' ? adapter.activate(seasonId) : adapter.archive(seasonId)),
    onSuccess: (season, action) => {
      queryClient.setQueryData(key, season);
      setDone(action === 'activate' ? 'activated' : 'archived');
      return queryClient.invalidateQueries({ queryKey: SEASONS_KEY, exact: true });
    },
  });
  const failure = transition.error ? toPlatformFailure(transition.error) : undefined;
  const season = query.data;
  const reload = () => {
    transition.reset();
    setDone(undefined);
    void query.refetch();
  };
  const run = (action: 'activate' | 'archive') => {
    setDone(undefined);
    transition.mutate(action);
  };

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{season?.name ?? t('seasons.title')}</Typography>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {failure && <FailureAlert failure={failure} onReload={reload} />}
      {done && (
        <Alert severity="success" role="status">
          {t(`seasons.${done}`)}
        </Alert>
      )}
      {season && (
        <>
          <Typography sx={{ mt: 1 }}>{t('seasons.stateLabel', { state: t(`seasons.state.${season.state}`) })}</Typography>
          {season.state === 'archived' && <Typography sx={{ mt: 1 }}>{t('seasons.readOnlyNote')}</Typography>}
          <Stack direction="row" spacing={1} sx={{ mt: 2 }}>
            {season.state === 'draft' && (
              <Button variant="contained" disabled={transition.isPending || isUnreachable} onClick={() => run('activate')}>
                {t('seasons.activate')}
              </Button>
            )}
            {season.state === 'active' && (
              <Button variant="contained" disabled={transition.isPending || isUnreachable} onClick={() => run('archive')}>
                {t('seasons.archive')}
              </Button>
            )}
            <Button component={Link} to="/seasons" variant="outlined">
              {t('seasons.back')}
            </Button>
          </Stack>
        </>
      )}
    </Container>
  );
}
