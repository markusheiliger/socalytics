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
import { createTeamAdapter } from '../../platform/adapters/teamAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { Team } from '../../platform/models/club';
import { isClubAdmin } from '../../session/roles';
import { useSession } from '../../session/sessionContext';
import { FailureAlert, useFieldError } from '../shared/FailureAlert';
import { SEASONS_KEY } from '../seasons/SeasonsPage';

export const TEAMS_KEY = ['teams'];

function TeamList({ teams }: { teams: Team[] }) {
  const { t } = useTranslation('teams');
  if (teams.length === 0) return <Typography sx={{ mt: 1 }}>{t('teams.empty')}</Typography>;
  return (
    <Stack component="ul" aria-label={t('teams.listLabel')} spacing={1} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
      {teams.map((team) => (
        <li key={team.id}>
          <Link to={`/teams/${team.id}`}>{team.name}</Link>
        </li>
      ))}
    </Stack>
  );
}

/** Coach and Viewer see their teams read-only. */
function MyTeams() {
  const { platform } = useSession();
  const adapter = useMemo(() => createTeamAdapter(platform), [platform]);
  const query = useQuery({ queryKey: [...TEAMS_KEY, 'mine'], queryFn: () => adapter.listMine(), retry: false });
  return (
    <>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {query.data && <TeamList teams={query.data} />}
    </>
  );
}

/** Club Admins list the teams of a season and create new ones. */
function SeasonTeams() {
  const { t } = useTranslation('teams');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const seasonAdapter = useMemo(() => createSeasonAdapter(platform), [platform]);
  const adapter = useMemo(() => createTeamAdapter(platform), [platform]);
  const [chosen, setChosen] = useState<string>();
  const [name, setName] = useState('');
  const [created, setCreated] = useState(false);
  const seasons = useQuery({ queryKey: SEASONS_KEY, queryFn: () => seasonAdapter.list(), retry: false });
  const season = seasons.data?.find((s) => s.id === chosen) ?? seasons.data?.find((s) => s.state === 'active') ?? seasons.data?.[0];
  const seasonId = season?.id;
  const teams = useQuery({
    queryKey: [...TEAMS_KEY, 'season', seasonId],
    queryFn: () => adapter.listBySeason(seasonId as string),
    enabled: Boolean(seasonId),
    retry: false,
  });
  const create = useMutation({
    mutationFn: () => adapter.create(seasonId as string, { name }),
    onSuccess: () => {
      setName('');
      setCreated(true);
      return queryClient.invalidateQueries({ queryKey: TEAMS_KEY });
    },
  });
  const failure = create.error ? toPlatformFailure(create.error) : undefined;
  const nameError = useFieldError(failure, 'name', tc);
  const archived = season?.state === 'archived';

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setCreated(false);
    create.mutate();
  };

  return (
    <>
      {seasons.error && <FailureAlert failure={toPlatformFailure(seasons.error)} onReload={() => void seasons.refetch()} />}
      {teams.error && <FailureAlert failure={toPlatformFailure(teams.error)} onReload={() => void teams.refetch()} />}
      {seasons.data && seasons.data.length > 0 && (
        <TextField
          select
          label={t('teams.season')}
          value={seasonId ?? ''}
          onChange={(e) => {
            setChosen(e.target.value);
            setCreated(false);
            create.reset();
          }}
          slotProps={{ select: { native: true } }}
          sx={{ mt: 2, minWidth: 240 }}
        >
          {seasons.data.map((s) => (
            <option key={s.id} value={s.id}>
              {t('teams.seasonOption', { name: s.name, state: t(`teams.state.${s.state}`) })}
            </option>
          ))}
        </TextField>
      )}
      {teams.data && <TeamList teams={teams.data} />}
      {archived && <Typography sx={{ mt: 2 }}>{t('teams.readOnlyNote')}</Typography>}
      {season && !archived && (
        <>
          <Typography variant="h3" sx={{ mt: 3 }}>
            {t('teams.createTitle')}
          </Typography>
          <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={onSubmit} noValidate>
            {created && (
              <Alert severity="success" role="status">
                {t('teams.created')}
              </Alert>
            )}
            {failure && !(failure.kind === 'validation' && nameError) && <FailureAlert failure={failure} />}
            <TextField
              label={t('teams.name')}
              name="name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              error={Boolean(nameError)}
              helperText={nameError}
              required
            />
            <Button type="submit" variant="contained" disabled={create.isPending || isUnreachable}>
              {create.isPending ? t('teams.creating') : t('teams.create')}
            </Button>
          </Stack>
        </>
      )}
    </>
  );
}

/** Teams of the club: administrators manage them per season, other members see their own. */
export function TeamsPage() {
  const { t } = useTranslation('teams');
  const { t: tc } = useTranslation('common');
  const { session } = useSession();
  const admin = session ? isClubAdmin(session.member) : false;
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('teams.title')}</Typography>
      {admin ? <SeasonTeams /> : <MyTeams />}
    </Container>
  );
}
