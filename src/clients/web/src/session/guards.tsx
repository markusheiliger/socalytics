import CircularProgress from '@mui/material/CircularProgress';
import Alert from '@mui/material/Alert';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, Outlet, useLocation, useSearchParams, type RouteObject } from 'react-router';
import { useConnectivity } from '../connectivity/useConnectivity';
import type { SessionMember } from '../platform/models/session';
import { NotPermittedPage } from '../shell/NotPermittedPage';
import { RETURN_TO_PARAM, safeReturnTo, signInPathFor } from './returnTo';
import { useSession } from './sessionContext';

/** Start-up placeholder; without a connection it shows the connection-required state instead of waiting for the session. */
export function LoadingView() {
  const { t } = useTranslation('common');
  const { t: tShell } = useTranslation('shell');
  const { isUnreachable } = useConnectivity();
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t('app.name')}</Typography>
      {isUnreachable ? (
        <Alert severity="warning" role="status" sx={{ mt: 2 }}>
          {tShell('connectivity.connectionRequired')}
        </Alert>
      ) : (
        <CircularProgress aria-label={t('loading')} />
      )}
    </Container>
  );
}

/** Signed-in routes: waits for the session, sends signed-out members to sign-in and password-change members to their view. */
export function RequireSignedIn() {
  const { status } = useSession();
  const location = useLocation();
  // Only a member who arrives signed out returns to the route; ending a session that was open goes to plain sign-in.
  const wasSignedIn = useRef(false);
  if (status === 'signedIn' || status === 'passwordChangeRequired') wasSignedIn.current = true;
  if (status === 'unknown') return <LoadingView />;
  if (status === 'signedOut') return <Navigate to={wasSignedIn.current ? '/sign-in' : signInPathFor(location)} replace />;
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

/** Sign-in is for signed-out members only; once signed in, a safe `returnTo` path is opened instead of home. */
export function RequireSignedOut({ routes }: { routes: () => RouteObject[] }) {
  const { status } = useSession();
  const [params] = useSearchParams();
  if (status === 'unknown') return <LoadingView />;
  if (status !== 'signedOut') {
    return <Navigate to={safeReturnTo(params.get(RETURN_TO_PARAM), routes()) ?? '/'} replace />;
  }
  return <Outlet />;
}

/** Renders the not-permitted view, without mounting the children, unless the member's roles allow the route. */
export function RequireRole({ allow, children }: { allow: (member: SessionMember) => boolean; children?: ReactNode }) {
  const { session } = useSession();
  if (!session || !allow(session.member)) return <NotPermittedPage />;
  return children ?? <Outlet />;
}
