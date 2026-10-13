import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { SessionProblemAlert } from './SessionProblemAlert';

/** Ends the session on the platform; the guards then return the browser to sign-in. */
export function SignOutButton() {
  const { t } = useTranslation('session');
  const { signOut } = useSession();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<'problem' | 'failed'>();

  const onClick = async () => {
    setBusy(true);
    setFailure(undefined);
    try {
      await signOut();
    } catch (error) {
      setFailure(toPlatformFailure(error).kind === 'sessionProblem' ? 'problem' : 'failed');
      setBusy(false);
    }
  };

  return (
    <Stack spacing={1} alignItems="flex-start">
      <Button variant="outlined" onClick={() => void onClick()} disabled={busy}>
        {t('signOut.label')}
      </Button>
      {failure === 'problem' && <SessionProblemAlert />}
      {failure === 'failed' && <Alert severity="error">{t('signOut.failed')}</Alert>}
    </Stack>
  );
}
