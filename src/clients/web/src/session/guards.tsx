import CircularProgress from '@mui/material/CircularProgress';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, Outlet } from 'react-router';
import type { SessionMember } from '../platform/models/session';
import { useSession } from './sessionContext';

export function LoadingView() {
  const { t } = useTranslation('common');
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t('app.name')}</Typography>
      <CircularProgress aria-label={t('loading')} />
    </Container>
  );
}

export function NotPermittedView() {
  const { t } = useTranslation('common');
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t('app.name')}</Typography>
      <Typography role="alert">{t('notPermitted')}</Typography>
    </Container>
  );
}

/** Signed-in routes: waits for the session, sends signed-out members to sign-in and password-change members to their view. */
export function RequireSignedIn() {
  const { status } = useSession();
  if (status === 'unknown') return <LoadingView />;
  if (status === 'signedOut') return <Navigate to="/sign-in" replace />;
  if (status === 'passwordChangeRequired') return <Navigate to="/password-change" replace />;
  return <Outlet />;
}

/** Only `passwordChangeRequired` may open the route. */
export function RequirePasswordChange() {
  const { status } = useSession();
  if (status === 'unknown') return <LoadingView />;
  if (status === 'signedOut') return <Navigate to="/sign-in" replace />;
  if (status === 'signedIn') return <Navigate to="/" replace />;
  return <Outlet />;
}

/** Sign-in is for signed-out members only. */
export function RequireSignedOut() {
  const { status } = useSession();
  if (status === 'unknown') return <LoadingView />;
  if (status !== 'signedOut') return <Navigate to="/" replace />;
  return <Outlet />;
}

/** Renders the not-permitted view, without mounting the children, unless the member's roles allow the route. */
export function RequireRole({ allow, children }: { allow: (member: SessionMember) => boolean; children?: ReactNode }) {
  const { session } = useSession();
  if (!session || !allow(session.member)) return <NotPermittedView />;
  return children ?? <Outlet />;
}
