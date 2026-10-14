import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createSeasonAdapter } from '../../platform/adapters/seasonAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert, useFieldError } from '../shared/FailureAlert';

export const SEASONS_KEY = ['seasons'];

/** Seasons of the club with creation; Club Admins only. */
export function SeasonsPage() {
  const { t } = useTranslation('seasons');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createSeasonAdapter(platform), [platform]);
  const [name, setName] = useState('');
  const [created, setCreated] = useState(false);
  const query = useQuery({ queryKey: SEASONS_KEY, queryFn: () => adapter.list(), retry: false });
  const create = useMutation({
    mutationFn: () => adapter.create({ name }),
    onSuccess: () => {
      setName('');
      setCreated(true);
      return queryClient.invalidateQueries({ queryKey: SEASONS_KEY });
    },
  });
  const failure = create.error ? toPlatformFailure(create.error) : undefined;
  const nameError = useFieldError(failure, 'name', tc);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setCreated(false);
    create.mutate();
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('seasons.title')}</Typography>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {query.data?.length === 0 && <Typography sx={{ mt: 1 }}>{t('seasons.empty')}</Typography>}
      {query.data && query.data.length > 0 && (
        <Stack component="ul" aria-label={t('seasons.listLabel')} spacing={1} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
          {query.data.map((season) => (
            <li key={season.id}>
              <Link to={`/seasons/${season.id}`}>{season.name}</Link> <span>{t(`seasons.state.${season.state}`)}</span>
            </li>
          ))}
        </Stack>
      )}
      <Typography variant="h3" sx={{ mt: 3 }}>
        {t('seasons.createTitle')}
      </Typography>
      <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={onSubmit} noValidate>
        {created && (
          <Alert severity="success" role="status">
            {t('seasons.created')}
          </Alert>
        )}
        {failure && !(failure.kind === 'validation' && nameError) && <FailureAlert failure={failure} />}
        <TextField
          label={t('seasons.name')}
          name="name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          error={Boolean(nameError)}
          helperText={nameError}
          required
        />
        <Button type="submit" variant="contained" disabled={create.isPending || isUnreachable}>
          {create.isPending ? t('seasons.creating') : t('seasons.create')}
        </Button>
      </Stack>
    </Container>
  );
}
