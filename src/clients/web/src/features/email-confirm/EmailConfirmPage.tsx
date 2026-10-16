import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link as RouterLink, useLocation, useNavigate } from 'react-router';
import { createEmailAdapter, RateLimitedFailure } from '../../platform/adapters/emailAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';

type Outcome = { kind: 'success' | 'invalid' | 'unreachable' | 'error' } | { kind: 'rateLimited'; seconds?: number };

/** Confirms an address on a click, so a mail scanner that only reads the link verifies nothing. */
export function EmailConfirmPage() {
  const { t } = useTranslation('email');
  const { status, platform } = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  const adapter = useMemo(() => createEmailAdapter(platform), [platform]);
  // The token lives only in this ref: never in state, storage, or the address.
  const token = useRef('');
  const fromFragment = new URLSearchParams(location.hash.replace(/^#/, '')).get('token');
  if (fromFragment) token.current = fromFragment;
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>();

  useLayoutEffect(() => {
    if (!window.location.hash && !location.hash) return;
    window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search);
    void navigate({ pathname: location.pathname, search: location.search }, { replace: true, state: location.state });
  }, [location.hash, location.pathname, location.search, location.state, navigate]);

  const signedIn = status === 'signedIn';

  const onConfirm = async () => {
    setBusy(true);
    try {
      await adapter.confirm(token.current);
      setOutcome({ kind: 'success' });
    } catch (error) {
      const failure = toPlatformFailure(error);
      if (failure instanceof RateLimitedFailure) setOutcome({ kind: 'rateLimited', seconds: failure.retryAfterSeconds });
      else if (failure.kind === 'unreachable') setOutcome({ kind: 'unreachable' });
      else if (failure.kind === 'validation') setOutcome({ kind: 'invalid' });
      else setOutcome({ kind: 'error' });
    } finally {
      setBusy(false);
    }
  };

  const done = outcome?.kind === 'success';

  return (
    <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
      <Typography variant="h1">{t('confirm.title')}</Typography>
      {done ? (
        <Alert severity="success" role="status" sx={{ mt: 2, mb: 2 }}>
          {t('confirm.success')}
        </Alert>
      ) : (
        <>
          <Typography sx={{ mt: 1 }}>{t('confirm.intro')}</Typography>
          {outcome?.kind === 'invalid' && (
            <Alert severity="error" role="alert" sx={{ mt: 2 }}>
              {t('confirm.invalid')} {signedIn ? t('confirm.invalidSignedIn') : t('confirm.invalidSignedOut')}
            </Alert>
          )}
          {outcome?.kind === 'rateLimited' && (
            <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
              {outcome.seconds === undefined ? t('confirm.rateLimitedUnknown') : t('confirm.rateLimited', { seconds: outcome.seconds })}
            </Alert>
          )}
          {(outcome?.kind === 'unreachable' || outcome?.kind === 'error') && (
            <Alert severity="error" role="alert" sx={{ mt: 2 }}>
              {t(`confirm.${outcome.kind}`)}
            </Alert>
          )}
          <Button variant="contained" sx={{ mt: 2 }} disabled={busy} onClick={() => void onConfirm()}>
            {busy ? t('confirm.busy') : t('confirm.button')}
          </Button>
        </>
      )}
      {(done || outcome?.kind === 'invalid') && status !== 'unknown' && (
        <Button component={RouterLink} to={signedIn ? '/account' : '/sign-in'} sx={{ mt: 2, ml: done ? 0 : 2 }}>
          {signedIn ? t('confirm.toAccount') : t('confirm.toSignIn')}
        </Button>
      )}
    </Container>
  );
}
