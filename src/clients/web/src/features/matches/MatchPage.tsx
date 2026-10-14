import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { formatKickoff } from './MatchForm';
import { useMatchContext } from './useMatchContext';

/** One match: details for readers; edit actions only for editors while the season is not archived. */
export function MatchPage() {
  const { matchId = '' } = useParams();
  const { t } = useTranslation('matches');
  const { t: tc } = useTranslation('common');
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
          <Button component={Link} to={`/teams/${m.teamId}`} variant="outlined" sx={{ mt: 2 }}>
            {t('matches.backToTeam')}
          </Button>
        </>
      )}
    </Container>
  );
}
