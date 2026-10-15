import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { createMatchAdapter } from '../../platform/adapters/matchAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { MatchInput } from '../../platform/models/matches';
import { useSession } from '../../session/sessionContext';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { MatchForm } from './MatchForm';
import { MATCHES_KEY } from './matchKeys';
import { useMatchContext } from './useMatchContext';

/** Edits kickoff time, venue, and competition with the loaded version; the opponent is never sent. */
export function MatchEditPage() {
  const { matchId = '' } = useParams();
  const { t } = useTranslation('matches');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createMatchAdapter(platform), [platform]);
  const { match, team, matchKey, permitted, editor, archived, canEdit } = useMatchContext(matchId);
  const [saved, setSaved] = useState(false);
  const [generation, setGeneration] = useState(0);
  const m = match.data;
  const update = useMutation({
    mutationFn: (input: MatchInput) => adapter.update(matchId, input, (m as NonNullable<typeof m>).version),
    onSuccess: (next) => {
      queryClient.setQueryData(matchKey, next);
      void queryClient.invalidateQueries({ queryKey: [...MATCHES_KEY, 'team', next.teamId] });
      setSaved(true);
    },
  });
  const failure = update.error ? toPlatformFailure(update.error) : undefined;
  const reload = () => {
    update.reset();
    setSaved(false);
    void match.refetch().then(() => setGeneration((g) => g + 1));
  };

  if (m && (!permitted || !editor)) return <NotPermittedPage />;
  if (match.error && toPlatformFailure(match.error).kind === 'notPermitted') return <NotPermittedPage />;

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('matches.editTitle')}</Typography>
      {(match.error ?? team.error) && <FailureAlert failure={toPlatformFailure(match.error ?? team.error)} onReload={reload} />}
      {archived && <Typography sx={{ mt: 1 }}>{t('matches.readOnlyNote')}</Typography>}
      {saved && (
        <Alert severity="success" role="status" sx={{ mt: 2 }}>
          {t('matches.saved')}
        </Alert>
      )}
      {m && canEdit && !match.isFetching && (
        <MatchForm
          key={`${m.version}-${generation}`}
          match={m}
          title={t('matches.editTitle')}
          submitLabel={t('matches.save')}
          submittingLabel={t('matches.saving')}
          submitting={update.isPending}
          failure={failure}
          onSubmit={(input) => {
            setSaved(false);
            update.mutate(input);
          }}
          onReload={reload}
        />
      )}
      <Button component={Link} to={`/matches/${matchId}`} variant="outlined" sx={{ mt: 2 }}>
        {t('matches.backToMatch')}
      </Button>
    </Container>
  );
}
