import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { SessionProblemAlert } from './SessionProblemAlert';
import { SignOutButton } from './SignOutButton';

type Problem = 'mismatch' | 'refused' | 'unreachable' | 'error' | 'sessionProblem';

/** The only view a member with a required password change can reach. */
export function PasswordChangePage() {
  const { t } = useTranslation('session');
  const { changeOwnPassword } = useSession();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem>();

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (newPassword !== confirmation) {
      setProblem('mismatch');
      return;
    }
    setProblem(undefined);
    setBusy(true);
    try {
      await changeOwnPassword(currentPassword, newPassword);
    } catch (error) {
      const kind = toPlatformFailure(error).kind;
      setProblem(
        kind === 'sessionProblem' ? 'sessionProblem' : kind === 'unreachable' ? 'unreachable' : kind === 'validation' || kind === 'conflict' ? 'refused' : 'error',
      );
      setBusy(false);
    }
  };

  return (
    <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
      <Typography variant="h1">{t('passwordChange.title')}</Typography>
      <Typography sx={{ mt: 1 }}>{t('passwordChange.intro')}</Typography>
      <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={(e: FormEvent) => void onSubmit(e)} noValidate>
        {problem === 'sessionProblem' && <SessionProblemAlert />}
        {problem && problem !== 'sessionProblem' && (
          <Alert severity="error" role="alert">
            {t(`passwordChange.${problem}`)}
          </Alert>
        )}
        <TextField
          label={t('passwordChange.currentPassword')}
          name="currentPassword"
          type="password"
          autoComplete="current-password"
          value={currentPassword}
          onChange={(e) => setCurrentPassword(e.target.value)}
          required
          autoFocus
        />
        <TextField
          label={t('passwordChange.newPassword')}
          name="newPassword"
          type="password"
          autoComplete="new-password"
          value={newPassword}
          onChange={(e) => setNewPassword(e.target.value)}
          required
        />
        <TextField
          label={t('passwordChange.confirmPassword')}
          name="confirmation"
          type="password"
          autoComplete="new-password"
          value={confirmation}
          onChange={(e) => setConfirmation(e.target.value)}
          required
        />
        <Button type="submit" variant="contained" disabled={busy || !currentPassword || !newPassword || !confirmation}>
          {busy ? t('passwordChange.submitting') : t('passwordChange.submit')}
        </Button>
      </Stack>
      <Stack sx={{ mt: 3 }}>
        <SignOutButton />
      </Stack>
    </Container>
  );
}
