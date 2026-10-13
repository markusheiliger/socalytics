import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link as RouterLink } from 'react-router';
import { SignOutButton } from '../account/SignOutButton';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { LoadingView } from '../../session/guards';

type Problem = 'mismatch' | 'policy' | 'codeInvalid' | 'refused' | 'unreachable' | 'error';

function problemOf(error: unknown): Problem {
  const failure = toPlatformFailure(error);
  if (failure.kind === 'unreachable') return 'unreachable';
  if (failure.kind === 'validation') {
    if (failure.code === 'credential-invalid') return 'codeInvalid';
    if (failure.fieldErrors.some((e) => e.field === 'newPassword' && e.code === 'password-policy')) return 'policy';
    return 'refused';
  }
  return 'error';
}

/** Redeems a single-use credential; the code is typed in and never part of an address. */
export function SetPasswordPage() {
  const { t } = useTranslation('account');
  const { status, redeemCredential } = useSession();
  const [accountName, setAccountName] = useState('');
  const [code, setCode] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [problem, setProblem] = useState<Problem>();

  if (status === 'unknown') return <LoadingView />;

  if (status !== 'signedOut') {
    return (
      <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
        <Typography variant="h1">{t('setPassword.title')}</Typography>
        <Alert severity="warning" sx={{ mt: 2, mb: 2 }}>
          {t('setPassword.signOutFirst')}
        </Alert>
        <SignOutButton />
      </Container>
    );
  }

  if (done) {
    return (
      <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
        <Typography variant="h1">{t('setPassword.title')}</Typography>
        <Alert severity="success" role="status" sx={{ mt: 2, mb: 2 }}>
          {t('setPassword.done')}
        </Alert>
        <Button component={RouterLink} to="/sign-in" variant="contained">
          {t('setPassword.toSignIn')}
        </Button>
      </Container>
    );
  }

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (newPassword !== confirmation) {
      setProblem('mismatch');
      return;
    }
    setProblem(undefined);
    setBusy(true);
    try {
      await redeemCredential(accountName.trim(), code.trim(), newPassword);
      setCode('');
      setNewPassword('');
      setConfirmation('');
      setDone(true);
    } catch (error) {
      setProblem(problemOf(error));
      setCode('');
      setNewPassword('');
      setConfirmation('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
      <Typography variant="h1">{t('setPassword.title')}</Typography>
      <Typography sx={{ mt: 1 }}>{t('setPassword.intro')}</Typography>
      <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={(e: FormEvent) => void onSubmit(e)} noValidate>
        {problem && (
          <Alert severity="error" role="alert">
            {t(`setPassword.${problem}`)}
          </Alert>
        )}
        <TextField
          label={t('setPassword.accountName')}
          name="accountName"
          autoComplete="username"
          value={accountName}
          onChange={(e) => setAccountName(e.target.value)}
          required
          autoFocus
        />
        <TextField
          label={t('setPassword.code')}
          name="code"
          type="password"
          autoComplete="off"
          value={code}
          onChange={(e) => setCode(e.target.value)}
          required
        />
        <TextField
          label={t('setPassword.newPassword')}
          name="newPassword"
          type="password"
          autoComplete="new-password"
          value={newPassword}
          onChange={(e) => setNewPassword(e.target.value)}
          required
        />
        <TextField
          label={t('setPassword.confirmPassword')}
          name="confirmation"
          type="password"
          autoComplete="new-password"
          value={confirmation}
          onChange={(e) => setConfirmation(e.target.value)}
          required
        />
        <Button type="submit" variant="contained" disabled={busy || !accountName || !code || !newPassword || !confirmation}>
          {busy ? t('setPassword.submitting') : t('setPassword.submit')}
        </Button>
      </Stack>
    </Container>
  );
}
