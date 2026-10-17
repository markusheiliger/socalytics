import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import type { IdentityProvider } from '../../platform/adapters/sessionAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { LoadingView } from '../../session/guards';
import { SignOutButton } from '../account/SignOutButton';

type Problem = 'failed' | 'unavailable' | 'unreachable' | 'error';

function problemOf(error: unknown): Problem {
  const failure = toPlatformFailure(error);
  if (failure.code === 'provider-unavailable') return 'unavailable';
  if (failure.kind === 'unreachable') return 'unreachable';
  if (failure.kind === 'unexpected' || (failure.status !== undefined && failure.status >= 500)) return 'error';
  return 'failed';
}

/** Redeems a single-use link code through the provider; the code is typed in and never part of an address. */
export function LinkAccountPage() {
  const { t } = useTranslation('account');
  const { status, listProviders, startExternalSignIn } = useSession();
  const [searchParams] = useSearchParams();
  const [providers, setProviders] = useState<IdentityProvider[]>();
  const [provider, setProvider] = useState('');
  const [accountName, setAccountName] = useState('');
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem | undefined>(searchParams.get('external') === 'failed' ? 'failed' : undefined);

  useEffect(() => {
    let current = true;
    listProviders().then(
      (list) => current && setProviders(list),
      () => current && setProviders([]),
    );
    return () => {
      current = false;
    };
    // Read once; the session API object changes identity with every session state change.
  }, []);

  if (status === 'unknown' || !providers) return <LoadingView />;

  if (status !== 'signedOut') {
    return (
      <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
        <Typography variant="h1">{t('linkAccount.title')}</Typography>
        <Alert severity="warning" sx={{ mt: 2, mb: 2 }}>
          {t('linkAccount.signOutFirst')}
        </Alert>
        <SignOutButton />
      </Container>
    );
  }

  if (providers.length === 0) {
    return (
      <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
        <Typography variant="h1">{t('linkAccount.title')}</Typography>
        <Alert severity="info" sx={{ mt: 2 }}>
          {t('linkAccount.noProviders')}
        </Alert>
      </Container>
    );
  }

  const chosen = provider || providers[0]!.id;

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setProblem(undefined);
    setBusy(true);
    try {
      const url = await startExternalSignIn(chosen, { accountName: accountName.trim(), linkCredential: code.trim() });
      setCode('');
      window.location.assign(url);
    } catch (error) {
      setProblem(problemOf(error));
      setCode('');
      setBusy(false);
    }
  };

  return (
    <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
      <Typography variant="h1">{t('linkAccount.title')}</Typography>
      <Typography sx={{ mt: 1 }}>{t('linkAccount.intro')}</Typography>
      <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={(e: FormEvent) => void onSubmit(e)} noValidate>
        {problem && (
          <Alert severity="error" role="alert">
            {t(`linkAccount.${problem}`)}
          </Alert>
        )}
        <TextField
          label={t('linkAccount.accountName')}
          name="accountName"
          autoComplete="username"
          value={accountName}
          onChange={(e) => setAccountName(e.target.value)}
          required
          autoFocus
        />
        <TextField
          label={t('linkAccount.code')}
          name="code"
          type="password"
          autoComplete="off"
          value={code}
          onChange={(e) => setCode(e.target.value)}
          required
        />
        <TextField
          select
          label={t('linkAccount.provider')}
          name="provider"
          value={chosen}
          onChange={(e) => setProvider(e.target.value)}
          slotProps={{ select: { native: true } }}
        >
          {providers.map((p) => (
            <option key={p.id} value={p.id}>
              {p.displayName}
            </option>
          ))}
        </TextField>
        <Button type="submit" variant="contained" disabled={busy || !accountName || !code}>
          {busy ? t('linkAccount.submitting') : t('linkAccount.submit')}
        </Button>
      </Stack>
    </Container>
  );
}
