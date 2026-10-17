import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import type { IdentityProvider } from '../../platform/adapters/sessionAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';

type Problem = { key: 'cookiesRequired' | 'unreachable' | 'incompatible' | 'error' | 'refused' | 'externalFailed' | 'providerUnavailable'; reference?: string };

// Wrong credentials, locked, and deactivated accounts all end in the same refusal (FR-006).
function problemOf(failure: PlatformFailure): Problem {
  if (failure.code === 'provider-unavailable') return { key: 'providerUnavailable' };
  if (failure.kind === 'unreachable') return { key: 'unreachable' };
  if (failure.kind === 'incompatible' && failure.status === undefined) return { key: 'incompatible' };
  if (failure.kind === 'unexpected' || (failure.status !== undefined && failure.status >= 500)) {
    return { key: 'error', reference: failure.correlationId };
  }
  return { key: 'refused' };
}

export function SignInPage() {
  const { t } = useTranslation('session');
  const { signIn, listProviders, startExternalSignIn, reason, clearReason } = useSession();
  const [searchParams] = useSearchParams();
  const [providers, setProviders] = useState<IdentityProvider[]>([]);
  const [accountName, setAccountName] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem | undefined>(
    searchParams.get('external') === 'failed' ? { key: 'externalFailed' } : undefined,
  );

  // A provider list that cannot be read leaves the local form as the only way to sign in.
  useEffect(() => {
    let current = true;
    listProviders().then(
      (list) => current && setProviders(list),
      () => undefined,
    );
    return () => {
      current = false;
    };
    // Read once; the session API object changes identity with every session state change.
  }, []);

  const onProvider = async (provider: IdentityProvider) => {
    setProblem(undefined);
    if (!navigator.cookieEnabled) {
      setProblem({ key: 'cookiesRequired' });
      return;
    }
    setBusy(true);
    try {
      window.location.assign(await startExternalSignIn(provider.id));
    } catch (error) {
      setProblem(problemOf(toPlatformFailure(error)));
      setBusy(false);
    }
  };

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setProblem(undefined);
    if (!navigator.cookieEnabled) {
      setProblem({ key: 'cookiesRequired' });
      return;
    }
    setBusy(true);
    try {
      await signIn(accountName.trim(), password);
      clearReason();
    } catch (error) {
      setProblem(problemOf(toPlatformFailure(error)));
      setPassword('');
      setBusy(false);
    }
  };

  return (
    <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
      <Typography variant="h1">{t('signIn.title')}</Typography>
      <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={(e: FormEvent) => void onSubmit(e)} noValidate>
        {reason === 'sessionEnded' && !problem && <Alert severity="info">{t('sessionEnded')}</Alert>}
        {problem && (
          <Alert severity="error" role="alert">
            {t(`signIn.${problem.key}`)}
            {problem.reference && <Box component="span" sx={{ display: 'block' }}>{t('reference', { id: problem.reference })}</Box>}
          </Alert>
        )}
        <TextField
          label={t('signIn.accountName')}
          name="accountName"
          autoComplete="username"
          value={accountName}
          onChange={(e) => setAccountName(e.target.value)}
          required
          autoFocus
        />
        <TextField
          label={t('signIn.password')}
          name="password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        <Button type="submit" variant="contained" disabled={busy || !accountName.trim() || !password}>
          {busy ? t('signIn.submitting') : t('signIn.submit')}
        </Button>
        {providers.length > 0 && (
          <Stack component="section" spacing={1} aria-label={t('signIn.providersHeading')}>
            <Typography variant="body2">{t('signIn.providersHeading')}</Typography>
            {providers.map((provider) => (
              <Button key={provider.id} type="button" variant="outlined" disabled={busy} onClick={() => void onProvider(provider)}>
                {t('signIn.providerButton', { name: provider.displayName })}
              </Button>
            ))}
          </Stack>
        )}
      </Stack>
    </Container>
  );
}
