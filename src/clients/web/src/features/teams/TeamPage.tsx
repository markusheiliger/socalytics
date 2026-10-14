import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createTeamAdapter } from '../../platform/adapters/teamAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { Team } from '../../platform/models/club';
import { isClubAdmin } from '../../session/roles';
import { useSession } from '../../session/sessionContext';
import { FailureAlert, useFieldError } from '../shared/FailureAlert';
import { TEAMS_KEY } from './TeamsPage';

function TeamForm({ team, queryKey, onReload }: { team: Team; queryKey: unknown[]; onReload: () => void }) {
  const { t } = useTranslation('teams');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createTeamAdapter(platform), [platform]);
  const [name, setName] = useState(team.name);
  const [saved, setSaved] = useState(false);
  const update = useMutation({
    mutationFn: () => adapter.update(team.id, { name }, team.version),
    onSuccess: (next) => {
      queryClient.setQueryData(queryKey, next);
      setSaved(true);
    },
  });
  const failure = update.error ? toPlatformFailure(update.error) : undefined;
  const nameError = useFieldError(failure, 'name', tc);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    update.mutate();
  };

  return (
    <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={onSubmit} noValidate aria-label={t('teams.editTitle')}>
      {saved && (
        <Alert severity="success" role="status">
          {t('teams.saved')}
        </Alert>
      )}
      {failure && !(failure.kind === 'validation' && nameError) && <FailureAlert failure={failure} onReload={onReload} />}
      <TextField
        label={t('teams.name')}
        name="name"
        value={name}
        onChange={(e) => setName(e.target.value)}
        error={Boolean(nameError)}
        helperText={nameError}
        required
      />
      <Button type="submit" variant="contained" disabled={update.isPending || isUnreachable}>
        {update.isPending ? t('teams.saving') : t('teams.save')}
      </Button>
    </Stack>
  );
}

/** One team: Club Admins edit it while its season is not archived; other members view it read-only. */
export function TeamPage() {
  const { teamId = '' } = useParams();
  const { t } = useTranslation('teams');
  const { t: tc } = useTranslation('common');
  const { platform, session } = useSession();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createTeamAdapter(platform), [platform]);
  const key = [...TEAMS_KEY, 'item', teamId];
  const query = useQuery({ queryKey: key, queryFn: () => adapter.get(teamId), retry: false, refetchOnWindowFocus: false });
  const [generation, setGeneration] = useState(0);
  const reload = () => {
    void query.refetch();
    setGeneration((g) => g + 1);
  };
  const team = query.data;
  const admin = session ? isClubAdmin(session.member) : false;
  const editable = admin && team?.seasonState !== 'archived';
  // The list shows names, so a saved edit must refresh it when the member goes back.
  const refreshLists = () => void queryClient.invalidateQueries({ queryKey: [...TEAMS_KEY], predicate: (q) => q.queryKey[1] !== 'item' });

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{team?.name ?? t('teams.teamTitle')}</Typography>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={reload} />}
      {team && !query.isFetching && (
        <>
          <Typography sx={{ mt: 1 }}>{t('teams.seasonState', { state: t(`teams.state.${team.seasonState}`) })}</Typography>
          {team.seasonState === 'archived' && <Typography sx={{ mt: 1 }}>{t('teams.readOnlyNote')}</Typography>}
          {!admin && <Typography sx={{ mt: 1 }}>{t('teams.viewOnlyNote')}</Typography>}
          {editable && <TeamForm key={generation} team={team} queryKey={key} onReload={reload} />}
          <Button component={Link} to="/teams" variant="outlined" sx={{ mt: 2 }} onClick={refreshLists}>
            {t('teams.back')}
          </Button>
        </>
      )}
    </Container>
  );
}
