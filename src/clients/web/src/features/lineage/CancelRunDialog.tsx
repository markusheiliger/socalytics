import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';

export const CANCEL_REASON_MAX_LENGTH = 500;

interface Props {
  open: boolean;
  busy: boolean;
  fieldError?: string;
  onConfirm: (reason?: string) => void;
  onClose: () => void;
}

/** Confirms the cancellation of an analysis run with an optional reason of at most 500 characters (FR-018, FR-020). */
export function CancelRunDialog({ open, busy, fieldError, onConfirm, onClose }: Props) {
  const { t } = useTranslation('lineage');
  const [reason, setReason] = useState('');
  const trimmed = reason.trim();
  const tooLong = trimmed.length > CANCEL_REASON_MAX_LENGTH;
  const close = () => {
    if (busy) return;
    setReason('');
    onClose();
  };
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!tooLong && !busy) onConfirm(trimmed === '' ? undefined : trimmed);
  };
  const counts = { count: trimmed.length, max: CANCEL_REASON_MAX_LENGTH };
  return (
    <Dialog open={open} onClose={close} aria-labelledby="cancel-run-title" component="form" onSubmit={submit}>
      <DialogTitle id="cancel-run-title">{t('cancelRun.title')}</DialogTitle>
      <DialogContent>
        <DialogContentText>{t('cancelRun.consequences')}</DialogContentText>
        <TextField
          label={t('cancelRun.reason')}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          error={tooLong || Boolean(fieldError)}
          helperText={tooLong ? t('cancelRun.tooLong', counts) : (fieldError ?? t('cancelRun.hint', counts))}
          multiline
          minRows={3}
          fullWidth
          margin="normal"
          disabled={busy}
        />
        <Typography role="status" variant="body2">
          {busy ? t('cancelRun.working') : ''}
        </Typography>
      </DialogContent>
      <DialogActions>
        <Button onClick={close} autoFocus disabled={busy}>
          {t('cancelRun.dismiss')}
        </Button>
        <Button type="submit" variant="contained" color="error" disabled={tooLong || busy}>
          {busy ? t('cancelRun.working') : t('cancelRun.confirm')}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
