import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import { useTranslation } from 'react-i18next';

export type LifecycleAction = 'archive' | 'unarchive' | 'delete';

/** What the last attempt ended with; every kind except `none` leaves the conversation unchanged. */
export type LifecycleFailure = 'none' | 'notPermitted' | 'conflict' | 'unavailable' | 'unknownOutcome' | 'failed';

interface ConversationLifecycleDialogProps {
  action: LifecycleAction | undefined;
  busy: boolean;
  failure: LifecycleFailure;
  offline: boolean;
  onConfirm: () => void;
  onClose: () => void;
  onReload: () => void;
}

const failureKey: Record<Exclude<LifecycleFailure, 'none' | 'conflict'>, string> = {
  notPermitted: 'lifecycle.refusal.notPermitted',
  unavailable: 'lifecycle.unavailable.service',
  unknownOutcome: 'lifecycle.unknownOutcome',
  failed: 'lifecycle.refusal.failed',
};

/** Confirmation of archive, unarchive, and delete: names the effect, then shows loading and every refusal inside the dialog (FR-009). */
export function ConversationLifecycleDialog({ action, busy, failure, offline, onConfirm, onClose, onReload }: ConversationLifecycleDialogProps) {
  const { t } = useTranslation('triage');
  const open = action !== undefined;
  const shown = action ?? 'archive';
  const needsReload = failure === 'conflict' || failure === 'unknownOutcome';

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} aria-labelledby="lifecycle-title" aria-describedby="lifecycle-message">
      <DialogTitle id="lifecycle-title">{t(`lifecycle.confirm.${shown}.title`)}</DialogTitle>
      <DialogContent>
        <DialogContentText id="lifecycle-message">{t(`lifecycle.confirm.${shown}.message`)}</DialogContentText>
        {offline && (
          <Alert severity="warning" role="status" sx={{ mt: 2 }}>
            {t('lifecycle.offline')}
          </Alert>
        )}
        {failure === 'conflict' && (
          <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
            {t('lifecycle.conflict.message')}
          </Alert>
        )}
        {failure !== 'none' && failure !== 'conflict' && (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>
            {t(failureKey[failure])}
          </Alert>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>
          {t('lifecycle.confirm.cancel')}
        </Button>
        {needsReload ? (
          <Button onClick={onReload} variant="contained" disabled={busy}>
            {t('lifecycle.conflict.reload')}
          </Button>
        ) : (
          <Button onClick={onConfirm} variant="contained" color={shown === 'delete' ? 'error' : 'primary'} disabled={busy || offline}>
            {busy ? t(`lifecycle.confirm.working.${shown}`) : failure === 'unavailable' ? t('lifecycle.unavailable.retry') : t(`lifecycle.confirm.${shown}.confirm`)}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
