import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createClubAdapter } from '../../platform/adapters/clubAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { Club } from '../../platform/models/club';
import { useSession } from '../../session/sessionContext';
import { FailureAlert, useFieldError } from '../shared/FailureAlert';

const CLUB_KEY = ['club'];

function ClubForm({ club, onReload }: { club: Club; onReload: () => void }) {
  const { t } = useTranslation('club');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createClubAdapter(platform), [platform]);
  const [displayName, setDisplayName] = useState(club.displayName);
  const [saved, setSaved] = useState(false);
  const update = useMutation({
    mutationFn: () => adapter.update({ displayName }, club.version),
    onSuccess: (next) => {
      queryClient.setQueryData(CLUB_KEY, next);
      setSaved(true);
    },
  });
  const failure = update.error ? toPlatformFailure(update.error) : undefined;
  const nameError = useFieldError(failure, 'displayName', tc);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    update.mutate();
  };

  return (
    <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={onSubmit} noValidate>
      {saved && (
        <Alert severity="success" role="status">
          {t('club.saved')}
        </Alert>
      )}
      {failure && !(failure.kind === 'validation' && nameError) && <FailureAlert failure={failure} onReload={onReload} />}
      <TextField
        label={t('club.displayName')}
        name="displayName"
        value={displayName}
        onChange={(e) => setDisplayName(e.target.value)}
        error={Boolean(nameError)}
        helperText={nameError}
        required
      />
      <Button type="submit" variant="contained" disabled={update.isPending || isUnreachable}>
        {update.isPending ? t('club.saving') : t('club.save')}
      </Button>
    </Stack>
  );
}

/** Club Admins edit the club's details. */
export function ClubPage() {
  const { t } = useTranslation('club');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const adapter = useMemo(() => createClubAdapter(platform), [platform]);
  const query = useQuery({ queryKey: CLUB_KEY, queryFn: () => adapter.get(), retry: false, refetchOnWindowFocus: false });
  const [generation, setGeneration] = useState(0);
  const reload = () => {
    void query.refetch();
    setGeneration((g) => g + 1);
  };

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('club.title')}</Typography>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={reload} />}
      {query.data && !query.isFetching && (
        <ClubForm key={generation} club={query.data} onReload={reload} />
      )}
    </Container>
  );
}
