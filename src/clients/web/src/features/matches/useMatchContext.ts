import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { createMatchAdapter } from '../../platform/adapters/matchAdapter';
import { createTeamAdapter } from '../../platform/adapters/teamAdapter';
import { canEditTeamMatches, canViewTeam } from '../../session/roles';
import { useSession } from '../../session/sessionContext';
import { TEAMS_KEY } from '../teams/TeamsPage';
import { MATCHES_KEY } from './matchKeys';

/** Loads the match first, then its team, and derives what the member may do (contracts/routes-and-roles.md). */
export function useMatchContext(matchId: string) {
  const { platform, session } = useSession();
  const matchAdapter = useMemo(() => createMatchAdapter(platform), [platform]);
  const teamAdapter = useMemo(() => createTeamAdapter(platform), [platform]);
  const matchKey = [...MATCHES_KEY, 'item', matchId];
  const match = useQuery({ queryKey: matchKey, queryFn: () => matchAdapter.get(matchId), retry: false, refetchOnWindowFocus: false });
  const teamId = match.data?.teamId;
  const team = useQuery({
    queryKey: [...TEAMS_KEY, 'item', teamId],
    queryFn: () => teamAdapter.get(teamId as string),
    enabled: Boolean(teamId),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const member = session?.member;
  const permitted = Boolean(member && teamId && canViewTeam(member, teamId));
  const editor = Boolean(member && teamId && canEditTeamMatches(member, teamId));
  const archived = team.data?.seasonState === 'archived';
  return { match, team, matchKey, permitted, editor, archived, canEdit: editor && Boolean(team.data) && !archived };
}
