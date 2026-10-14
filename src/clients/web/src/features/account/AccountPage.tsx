import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { SessionProblemAlert } from './SessionProblemAlert';
import { SignOutButton } from './SignOutButton';

type Problem = 'mismatch' | 'policy' | 'refused' | 'unreachable' | 'error' | 'sessionProblem';

function problemOf(error: unknown): Problem {
  const failure = toPlatformFailure(error);
  if (failure.kind === 'sessionProblem') return 'sessionProblem';
  if (failure.kind === 'unreachable') return 'unreachable';
  if (failure.kind === 'validation' || failure.kind === 'conflict') {
    return failure.fieldErrors.some((e) => e.field === 'newPassword' && e.code === 'password-policy') ? 'policy' : 'refused';
  }
  return 'error';
}

/** The member's own account: who is signed in, change own password, sign out. */
export function AccountPage() {
  const { t } = useTranslation('account');
  const { t: tc } = useTranslation('common');
  const { session, changeOwnPassword } = useSession();
  const { isUnreachable } = useConnectivity();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [problem, setProblem] = useState<Problem>();

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setDone(false);
    if (newPassword !== confirmation) {
      setProblem('mismatch');
      return;
    }
    setProblem(undefined);
    setBusy(true);
    try {
      await changeOwnPassword(currentPassword, newPassword);
      setDone(true);
    } catch (error) {
      setProblem(problemOf(error));
    } finally {
      setCurrentPassword('');
      setNewPassword('');
      setConfirmation('');
      setBusy(false);
    }
  };

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('account.title')}</Typography>
      {session && <Typography sx={{ mt: 1 }}>{t('account.signedInAs', { name: session.member.accountName })}</Typography>}
      <Typography variant="h3" sx={{ mt: 3 }}>
        {t('account.changePassword')}
      </Typography>
      <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={(e: FormEvent) => void onSubmit(e)} noValidate>
        {done && (
          <Alert severity="success" role="status">
            {t('account.done')}
          </Alert>
        )}
        {problem === 'sessionProblem' && <SessionProblemAlert />}
        {problem && problem !== 'sessionProblem' && (
          <Alert severity="error" role="alert">
            {t(`account.${problem}`)}
          </Alert>
        )}
        <TextField
          label={t('account.currentPassword')}
          name="currentPassword"
          type="password"
          autoComplete="current-password"
          value={currentPassword}
          onChange={(e) => setCurrentPassword(e.target.value)}
          required
        />
        <TextField
          label={t('account.newPassword')}
          name="newPassword"
          type="password"
          autoComplete="new-password"
          value={newPassword}
          onChange={(e) => setNewPassword(e.target.value)}
          required
        />
        <TextField
          label={t('account.confirmPassword')}
          name="confirmation"
          type="password"
          autoComplete="new-password"
          value={confirmation}
          onChange={(e) => setConfirmation(e.target.value)}
          required
        />
        <Button type="submit" variant="contained" disabled={busy || isUnreachable || !currentPassword || !newPassword || !confirmation}>
          {busy ? t('account.submitting') : t('account.submit')}
        </Button>
      </Stack>
      <Stack sx={{ mt: 3 }}>
        <SignOutButton />
      </Stack>
    </Container>
  );
}
