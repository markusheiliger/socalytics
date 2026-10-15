import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import { useState, useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { useUploads } from '../../uploads/uploadContext';
import { shouldGuardLeave } from '../../uploads/useLeaveGuard';
import { SessionProblemAlert } from './SessionProblemAlert';

const NO_JOBS: readonly never[] = [];

/** Ends the session on the platform; the guards then return the browser to sign-in. */
export function SignOutButton() {
  const { t } = useTranslation('session');
  const { t: tu } = useTranslation('uploads');
  const repository = useUploads();
  const jobs = useSyncExternalStore(
    (listener) => repository?.subscribe(listener) ?? (() => undefined),
    () => repository?.getSnapshot() ?? NO_JOBS,
  );
  const [confirming, setConfirming] = useState(false);
  const { signOut } = useSession();
  const { isUnreachable } = useConnectivity();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<'problem' | 'failed'>();

  const onClick = async () => {
    setConfirming(false);
    setBusy(true);
    setFailure(undefined);
    try {
      // An explicit sign-out forgets the member's remembered uploads; a session ended by the platform keeps them.
      await repository?.purgeMember();
      await signOut();
    } catch (error) {
      setFailure(toPlatformFailure(error).kind === 'sessionProblem' ? 'problem' : 'failed');
      setBusy(false);
    }
  };

  return (
    <Stack spacing={1} alignItems="flex-start">
      <Button variant="outlined" onClick={() => (shouldGuardLeave(jobs) ? setConfirming(true) : void onClick())} disabled={busy || isUnreachable}>
        {t('signOut.label')}
      </Button>
      <Dialog open={confirming} onClose={() => setConfirming(false)} aria-labelledby="sign-out-confirm-title">
        <DialogTitle id="sign-out-confirm-title">{tu('signOut.title')}</DialogTitle>
        <DialogContent>
          <DialogContentText>{tu('signOut.body')}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirming(false)}>{tu('signOut.cancel')}</Button>
          <Button variant="contained" onClick={() => void onClick()}>
            {tu('signOut.confirm')}
          </Button>
        </DialogActions>
      </Dialog>
      {failure === 'problem' && <SessionProblemAlert />}
      {failure === 'failed' && <Alert severity="error">{t('signOut.failed')}</Alert>}
    </Stack>
  );
}
