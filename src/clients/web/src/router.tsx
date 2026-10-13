import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import { createBrowserRouter, useParams, type RouteObject } from 'react-router';
import { AccountPage } from './features/account/AccountPage';
import { SetPasswordPage } from './features/set-password/SetPasswordPage';
import { PasswordChangePage } from './features/account/PasswordChangePage';
import { SignOutButton } from './features/account/SignOutButton';
import { SignInPage } from './features/sign-in/SignInPage';
import { useSession } from './session/sessionContext';
import { canViewTeam, canViewTeams, isClubAdmin } from './session/roles';
import { RequirePasswordChange, RequireRole, RequireSignedIn, RequireSignedOut } from './session/guards';

function Placeholder({ name }: { name: string }) {
  const { t } = useTranslation('common');
  const { status } = useSession();
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t('app.name')}</Typography>
      <Typography variant="h2">{name}</Typography>
      <Typography>{t('placeholder')}</Typography>
      {status === 'signedIn' && <SignOutButton />}
    </Container>
  );
}

function TeamDetailRoute() {
  const { teamId = '' } = useParams();
  return (
    <RequireRole allow={(member) => canViewTeam(member, teamId)}>
      <Placeholder name="Team" />
    </RequireRole>
  );
}

function NotFoundRoute() {
  const { t } = useTranslation('common');
  return <Placeholder name={t('notFound')} />;
}

const clubAdminOnly = (element: React.ReactNode) => <RequireRole allow={isClubAdmin}>{element}</RequireRole>;

/** Routes of contracts/routes-and-roles.md; the views are placeholders until their stories. */
export const appRoutes: RouteObject[] = [
  { element: <RequireSignedOut />, children: [{ path: '/sign-in', element: <SignInPage /> }] },
  { path: '/set-password', element: <SetPasswordPage /> },
  { element: <RequirePasswordChange />, children: [{ path: '/password-change', element: <PasswordChangePage /> }] },
  {
    element: <RequireSignedIn />,
    children: [
      { path: '/', element: <Placeholder name="Home" /> },
      { path: '/account', element: <AccountPage /> },
      { path: '/club', element: clubAdminOnly(<Placeholder name="Club" />) },
      { path: '/seasons', element: clubAdminOnly(<Placeholder name="Seasons" />) },
      { path: '/seasons/:seasonId', element: clubAdminOnly(<Placeholder name="Season" />) },
      { path: '/teams', element: <RequireRole allow={canViewTeams}><Placeholder name="Teams" /></RequireRole> },
      { path: '/teams/:teamId', element: <TeamDetailRoute /> },
      { path: '/members', element: clubAdminOnly(<Placeholder name="Members" />) },
      { path: '/members/:memberId', element: clubAdminOnly(<Placeholder name="Member" />) },
    ],
  },
  { path: '*', element: <NotFoundRoute /> },
];

export const createAppRouter = () => createBrowserRouter(appRoutes);
