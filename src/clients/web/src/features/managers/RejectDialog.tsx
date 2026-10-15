import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import TextField from '@mui/material/TextField';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';

export const REASON_MAX_LENGTH = 500;

/** Rejection needs a reason of 1–500 characters. */
export function RejectDialog({ open, busy, fieldError, onConfirm, onClose }: { open: boolean; busy: boolean; fieldError?: string; onConfirm: (reason: string) => void; onClose: () => void }) {
  const { t } = useTranslation('managers');
  const [reason, setReason] = useState('');
  const trimmed = reason.trim();
  const tooLong = trimmed.length > REASON_MAX_LENGTH;
  const valid = trimmed.length >= 1 && !tooLong;
  const close = () => {
    setReason('');
    onClose();
  };
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (valid && !busy) onConfirm(trimmed);
  };
  return (
    <Dialog open={open} onClose={close} aria-labelledby="reject-title" component="form" onSubmit={submit}>
      <DialogTitle id="reject-title">{t('reject.title')}</DialogTitle>
      <DialogContent>
        <DialogContentText>{t('reject.intro')}</DialogContentText>
        <TextField
          label={t('reject.reason')}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          error={tooLong || Boolean(fieldError)}
          helperText={tooLong ? t('reject.tooLong', { max: REASON_MAX_LENGTH }) : (fieldError ?? t('reject.hint', { max: REASON_MAX_LENGTH }))}
          required
          multiline
          autoFocus
          fullWidth
          margin="normal"
        />
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{t('decision.cancel')}</Button>
        <Button type="submit" variant="contained" color="error" disabled={!valid || busy}>
          {t('reject.confirm')}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
