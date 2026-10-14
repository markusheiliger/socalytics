import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router';
import { createMatchAdapter } from '../../platform/adapters/matchAdapter';
import { createTeamAdapter } from '../../platform/adapters/teamAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { TEAMS_KEY } from '../teams/TeamsPage';
import { FailureAlert } from '../shared/FailureAlert';
import { MatchForm } from './MatchForm';
import { MATCHES_KEY } from './matchKeys';

/** Creates a match for the team; not offered while the team's season is archived. */
export function MatchCreatePage() {
  const { teamId = '' } = useParams();
  const { t } = useTranslation('matches');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const teamAdapter = useMemo(() => createTeamAdapter(platform), [platform]);
  const adapter = useMemo(() => createMatchAdapter(platform), [platform]);
  const team = useQuery({ queryKey: [...TEAMS_KEY, 'item', teamId], queryFn: () => teamAdapter.get(teamId), retry: false, refetchOnWindowFocus: false });
  const create = useMutation({
    mutationFn: (input: Parameters<typeof adapter.create>[1]) => adapter.create(teamId, input),
    onSuccess: async (match) => {
      queryClient.setQueryData([...MATCHES_KEY, 'item', match.id], match);
      await queryClient.invalidateQueries({ queryKey: [...MATCHES_KEY, 'team', teamId] });
      void navigate(`/matches/${match.id}`);
    },
  });
  const archived = team.data?.seasonState === 'archived';

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('matches.createTitle')}</Typography>
      {team.error && <FailureAlert failure={toPlatformFailure(team.error)} onReload={() => void team.refetch()} />}
      {archived && <Typography sx={{ mt: 1 }}>{t('matches.readOnlyNote')}</Typography>}
      {team.data && !archived && (
        <MatchForm
          title={t('matches.createTitle')}
          submitLabel={t('matches.create')}
          submittingLabel={t('matches.creating')}
          submitting={create.isPending}
          failure={create.error ? toPlatformFailure(create.error) : undefined}
          onSubmit={(input) => create.mutate(input)}
        />
      )}
      <Button component={Link} to={`/teams/${teamId}`} variant="outlined" sx={{ mt: 2 }}>
        {t('matches.backToTeam')}
      </Button>
    </Container>
  );
}
