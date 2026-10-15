import { createBrowserRouter, useParams, type RouteObject } from 'react-router';
import { AccountPage } from './features/account/AccountPage';
import { SetPasswordPage } from './features/set-password/SetPasswordPage';
import { PasswordChangePage } from './features/account/PasswordChangePage';
import { ClubPage } from './features/club/ClubPage';
import { HomePage } from './features/home/HomePage';
import { FinalizePage } from './features/lineage/FinalizePage';
import { RecordingVersionPage } from './features/lineage/RecordingVersionPage';
import { MatchCreatePage } from './features/matches/MatchCreatePage';
import { MatchEditPage } from './features/matches/MatchEditPage';
import { MatchPage } from './features/matches/MatchPage';
import { MemberPage } from './features/members/MemberPage';
import { MembersPage } from './features/members/MembersPage';
import { SeasonPage } from './features/seasons/SeasonPage';
import { SeasonsPage } from './features/seasons/SeasonsPage';
import { SignInPage } from './features/sign-in/SignInPage';
import { TeamPage } from './features/teams/TeamPage';
import { TeamsPage } from './features/teams/TeamsPage';
import { AppLayout } from './shell/AppLayout';
import { NotFoundPage } from './shell/NotFoundPage';
import { canEditTeamMatches, canViewTeam, canViewTeams, isClubAdmin } from './session/roles';
import { RequirePasswordChange, RequireRole, RequireSignedIn, RequireSignedOut } from './session/guards';

function TeamDetailRoute() {
  const { teamId = '' } = useParams();
  return (
    <RequireRole allow={(member) => canViewTeam(member, teamId)}>
      <TeamPage />
    </RequireRole>
  );
}

function MatchCreateRoute() {
  const { teamId = '' } = useParams();
  return (
    <RequireRole allow={(member) => canEditTeamMatches(member, teamId)}>
      <MatchCreatePage />
    </RequireRole>
  );
}

const clubAdminOnly = (element: React.ReactNode) => <RequireRole allow={isClubAdmin}>{element}</RequireRole>;

/** Routes of contracts/routes-and-roles.md; every view is implemented. */
export const appRoutes: RouteObject[] = [
  { element: <RequireSignedOut />, children: [{ path: '/sign-in', element: <SignInPage /> }] },
  { path: '/set-password', element: <SetPasswordPage /> },
  { element: <RequirePasswordChange />, children: [{ path: '/password-change', element: <PasswordChangePage /> }] },
  {
    element: <RequireSignedIn />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { path: '/', element: <HomePage /> },
          { path: '/account', element: <AccountPage /> },
          { path: '/club', element: clubAdminOnly(<ClubPage />) },
          { path: '/seasons', element: clubAdminOnly(<SeasonsPage />) },
          { path: '/seasons/:seasonId', element: clubAdminOnly(<SeasonPage />) },
          { path: '/teams', element: <RequireRole allow={canViewTeams}><TeamsPage /></RequireRole> },
          { path: '/teams/:teamId', element: <TeamDetailRoute /> },
          { path: '/teams/:teamId/matches/new', element: <MatchCreateRoute /> },
          { path: '/matches/:matchId', element: <MatchPage /> },
          { path: '/matches/:matchId/edit', element: <MatchEditPage /> },
          { path: '/matches/:matchId/finalize', element: <FinalizePage /> },
          { path: '/matches/:matchId/recordings/:recordingVersionId', element: <RecordingVersionPage /> },
          { path: '/members', element: clubAdminOnly(<MembersPage />) },
          { path: '/members/:memberId', element: clubAdminOnly(<MemberPage />) },
        ],
      },
    ],
  },
  { path: '*', element: <NotFoundPage /> },
];

export const createAppRouter = () => createBrowserRouter(appRoutes);
