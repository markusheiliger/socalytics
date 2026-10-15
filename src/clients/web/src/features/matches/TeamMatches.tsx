import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { createMatchAdapter } from '../../platform/adapters/matchAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';
import { formatKickoff } from './MatchForm';
import { MATCHES_KEY } from './matchKeys';

/** The team's matches, newest kickoff first, loaded page by page. */
export function TeamMatches({ teamId, canCreate }: { teamId: string; canCreate: boolean }) {
  const { t } = useTranslation('matches');
  const { platform } = useSession();
  const adapter = useMemo(() => createMatchAdapter(platform), [platform]);
  const query = useInfiniteQuery({
    queryKey: [...MATCHES_KEY, 'team', teamId],
    queryFn: ({ pageParam }) => adapter.listByTeam(teamId, { continuationToken: pageParam }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.continuationToken,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const matches = useMemo(
    () => (query.data?.pages.flatMap((p) => p.items) ?? []).sort((a, b) => b.kickoffAt.getTime() - a.kickoffAt.getTime()),
    [query.data],
  );

  return (
    <section aria-labelledby="team-matches-heading">
      <Typography variant="h3" id="team-matches-heading" sx={{ mt: 3 }}>
        {t('matches.listTitle')}
      </Typography>
      {canCreate && (
        <Button component={Link} to={`/teams/${teamId}/matches/new`} variant="contained" sx={{ mt: 1 }}>
          {t('matches.newMatch')}
        </Button>
      )}
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {query.data && matches.length === 0 && <Typography sx={{ mt: 1 }}>{t('matches.empty')}</Typography>}
      {matches.length > 0 && (
        <Stack component="ul" aria-label={t('matches.listLabel')} spacing={1} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
          {matches.map((m) => (
            <li key={m.id}>
              <Link to={`/matches/${m.id}`}>{m.opponentName}</Link>
              {`, ${formatKickoff(m.kickoffAt)}, ${t(`matches.venueOption.${m.homeAway}`)}${m.competition ? `, ${m.competition}` : ''}`}
            </li>
          ))}
        </Stack>
      )}
      {query.hasNextPage && (
        <Button sx={{ mt: 1 }} variant="outlined" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
          {t('matches.loadMore')}
        </Button>
      )}
    </section>
  );
}
