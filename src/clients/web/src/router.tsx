import { createBrowserRouter, useParams, type RouteObject } from 'react-router';
import { AccountPage } from './features/account/AccountPage';
import { EmailConfirmPage } from './features/email-confirm/EmailConfirmPage';
import { LinkAccountPage } from './features/link-account/LinkAccountPage';
import { SetPasswordPage } from './features/set-password/SetPasswordPage';
import { PasswordChangePage } from './features/account/PasswordChangePage';
import { AnalysisRunPage } from './features/analysis/AnalysisRunPage';
import { ClubPage } from './features/club/ClubPage';
import { HomePage } from './features/home/HomePage';
import { InvitationAcceptPage } from './features/invitations/InvitationAcceptPage';
import { ConversationPage } from './features/triage/ConversationPage';
import { FinalizePage } from './features/lineage/FinalizePage';
import { RecordingVersionPage } from './features/lineage/RecordingVersionPage';
import { MatchCreatePage } from './features/matches/MatchCreatePage';
import { MatchEditPage } from './features/matches/MatchEditPage';
import { MatchPage } from './features/matches/MatchPage';
import { FleetPage } from './features/managers/FleetPage';
import { MyRequestPage } from './features/managers/MyRequestPage';
import { MyRequestsPage } from './features/managers/MyRequestsPage';
import { PairingPage } from './features/managers/PairingPage';
import { RegistrationPage } from './features/managers/RegistrationPage';
import { ImagePage } from './features/registry/ImagePage';
import { ImagesPage } from './features/registry/ImagesPage';
import { ProfileCreatePage } from './features/registry/ProfileCreatePage';
import { ProfilesPage } from './features/registry/ProfilesPage';
import { ProfileVersionPage } from './features/registry/ProfileVersionPage';
import { RegisterImagePage } from './features/registry/RegisterImagePage';
import { RegistryOverviewPage } from './features/registry/RegistryOverviewPage';
import { WorkflowCreatePage } from './features/registry/WorkflowCreatePage';
import { WorkflowsPage } from './features/registry/WorkflowsPage';
import { WorkflowVersionPage } from './features/registry/WorkflowVersionPage';
import { NotificationReceiptsPage } from './features/operations/NotificationReceiptsPage';
import { OperationsSummaryPage } from './features/operations/OperationsSummaryPage';
import { OutboxMessagePage } from './features/operations/OutboxMessagePage';
import { OutboxMessagesPage } from './features/operations/OutboxMessagesPage';
import { MemberPage } from './features/members/MemberPage';
import { MembersPage } from './features/members/MembersPage';
import { SeasonPage } from './features/seasons/SeasonPage';
import { SeasonsPage } from './features/seasons/SeasonsPage';
import { PasswordRecoveryCompletePage } from './features/password-recovery/PasswordRecoveryCompletePage';
import { PasswordRecoveryRequestPage } from './features/password-recovery/PasswordRecoveryRequestPage';
import { SignInPage } from './features/sign-in/SignInPage';
import { TeamPage } from './features/teams/TeamPage';
import { TeamsPage } from './features/teams/TeamsPage';
import { AppLayout } from './shell/AppLayout';
import { NotFoundPage } from './shell/NotFoundPage';
import { canEditTeamMatches, canPairManagers, canViewTeam, canViewTeams, isClubAdmin } from './session/roles';
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
  {
    element: <RequireSignedOut routes={() => appRoutes} />,
    children: [
      { path: '/sign-in', element: <SignInPage /> },
      { path: '/password-recovery', element: <PasswordRecoveryRequestPage /> },
    ],
  },
  { path: '/password-recovery/complete', element: <PasswordRecoveryCompletePage /> },
  { path: '/invitations/accept', element: <InvitationAcceptPage /> },
  { path: '/set-password', element: <SetPasswordPage /> },
  { path: '/link-account', element: <LinkAccountPage /> },
  { path: '/email/confirm', element: <EmailConfirmPage /> },
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
          { path: '/matches/:matchId/analysis-runs/:runId', element: <AnalysisRunPage /> },
          { path: '/matches/:matchId/recordings/:recordingVersionId', element: <RecordingVersionPage /> },
          { path: '/matches/:matchId/triage/:conversationId', element: <ConversationPage /> },
          { path: '/members', element: clubAdminOnly(<MembersPage />) },
          { path: '/members/:memberId', element: clubAdminOnly(<MemberPage />) },
          { path: '/analyst-managers', element: clubAdminOnly(<FleetPage />) },
          { path: '/analyst-managers/pair', element: <RequireRole allow={canPairManagers}><PairingPage /></RequireRole> },
          { path: '/analyst-managers/requests', element: <RequireRole allow={canPairManagers}><MyRequestsPage /></RequireRole> },
          { path: '/analyst-managers/requests/:registrationId', element: <RequireRole allow={canPairManagers}><MyRequestPage /></RequireRole> },
          { path: '/analyst-managers/:registrationId', element: clubAdminOnly(<RegistrationPage />) },
          { path: '/operations', element: clubAdminOnly(<OperationsSummaryPage />) },
          { path: '/operations/outbox-messages', element: clubAdminOnly(<OutboxMessagesPage />) },
          { path: '/operations/outbox-messages/:messageId', element: clubAdminOnly(<OutboxMessagePage />) },
          { path: '/operations/notification-receipts', element: clubAdminOnly(<NotificationReceiptsPage />) },
          { path: '/registry', element: clubAdminOnly(<RegistryOverviewPage />) },
          { path: '/registry/images', element: clubAdminOnly(<ImagesPage />) },
          { path: '/registry/images/register', element: clubAdminOnly(<RegisterImagePage />) },
          { path: '/registry/images/:digest', element: clubAdminOnly(<ImagePage />) },
          { path: '/registry/profiles', element: clubAdminOnly(<ProfilesPage />) },
          { path: '/registry/profiles/new', element: clubAdminOnly(<ProfileCreatePage />) },
          { path: '/registry/profiles/:profileId/versions/:version', element: clubAdminOnly(<ProfileVersionPage />) },
          { path: '/registry/workflows', element: clubAdminOnly(<WorkflowsPage />) },
          { path: '/registry/workflows/new', element: clubAdminOnly(<WorkflowCreatePage />) },
          { path: '/registry/workflows/:workflowId/versions/:version', element: clubAdminOnly(<WorkflowVersionPage />) },
        ],
      },
    ],
  },
  { path: '*', element: <NotFoundPage /> },
];

export const createAppRouter = () => createBrowserRouter(appRoutes);
