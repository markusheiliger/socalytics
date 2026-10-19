import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { NotFoundPage } from '../../shell/NotFoundPage';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { useMatchContext } from '../matches/useMatchContext';
import { FailureAlert } from '../shared/FailureAlert';
import { analysisRunKey, runRefreshInterval } from './analysisKeys';
import { RunStatusSection } from './RunStatusSection';

/** Analysis review of one run of a match: run status and nodes (FR-001, FR-002, FR-003, FR-006, FR-017). */
export function AnalysisRunPage() {
  const { matchId = '', runId = '' } = useParams();
  const { t } = useTranslation('analysis');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const adapter = useMemo(() => createAnalysisRunAdapter(platform), [platform]);
  const { match, team, permitted } = useMatchContext(matchId);
  const run = useQuery({
    queryKey: analysisRunKey(runId),
    queryFn: () => adapter.getRun(runId),
    enabled: permitted,
    retry: false,
    refetchOnWindowFocus: false,
    // TanStack Query pauses interval refetching while the page is hidden.
    refetchInterval: (query) => runRefreshInterval(query.state.data?.kind === 'found' ? query.state.data.run : undefined),
    refetchIntervalInBackground: false,
  });

  const isNotPermitted = (error: unknown) => Boolean(error) && toPlatformFailure(error).kind === 'notPermitted';
  if ((match.data && !permitted) || isNotPermitted(match.error) || isNotPermitted(run.error)) return <NotPermittedPage />;

  const outcome = run.data;
  if (outcome?.kind === 'runNotFound' || (outcome?.kind === 'found' && outcome.run.matchId !== matchId)) return <NotFoundPage framed />;

  const error = match.error ?? team.error ?? run.error;
  const reload = () => {
    void match.refetch();
    void team.refetch();
    void run.refetch();
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('page.title')}</Typography>
      {error && <FailureAlert failure={toPlatformFailure(error)} onReload={reload} />}
      {error && (
        <Button variant="outlined" sx={{ mt: 1 }} onClick={reload}>
          {t('page.reload')}
        </Button>
      )}
      {!error && !outcome && <Typography>{t('page.loading')}</Typography>}
      {outcome?.kind === 'found' && <RunStatusSection run={outcome.run} />}
      <Button component={Link} to={`/matches/${matchId}`} variant="outlined" sx={{ mt: 2 }}>
        {t('page.back')}
      </Button>
    </Container>
  );
}
