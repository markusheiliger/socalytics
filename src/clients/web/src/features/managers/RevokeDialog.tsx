import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import TextField from '@mui/material/TextField';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { matchesRevocation, normalizeFingerprint } from './fingerprint';
import { REASON_MAX_LENGTH } from './RejectDialog';

interface Props {
  open: boolean;
  busy: boolean;
  hostLabel?: string;
  fingerprint: string;
  activeAnalysts?: number;
  fieldError?: string;
  onConfirm: (reason: string) => void;
  onClose: () => void;
}

/** Revocation needs a reason of 1–500 characters and the host label (or, without one, the last 6 fingerprint characters) typed back (FR-012). */
export function RevokeDialog({ open, busy, hostLabel, fingerprint, activeAnalysts, fieldError, onConfirm, onClose }: Props) {
  const { t } = useTranslation('managers');
  const [reason, setReason] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const label = hostLabel?.trim() || undefined;
  const trimmed = reason.trim();
  const tooLong = trimmed.length > REASON_MAX_LENGTH;
  const confirmed = matchesRevocation(confirmation, label, fingerprint);
  const mismatch = normalizeFingerprint(confirmation) !== '' && !confirmed;
  const valid = trimmed.length >= 1 && !tooLong && confirmed;
  const close = () => {
    setReason('');
    setConfirmation('');
    onClose();
  };
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (valid && !busy) onConfirm(trimmed);
  };
  return (
    <Dialog open={open} onClose={close} aria-labelledby="revoke-title" component="form" onSubmit={submit}>
      <DialogTitle id="revoke-title">{t('revoke.title')}</DialogTitle>
      <DialogContent>
        <DialogContentText>{label ? t('revoke.host', { value: label }) : t('revoke.noHost')}</DialogContentText>
        <DialogContentText>
          {activeAnalysts === undefined ? t('revoke.activeUnknown') : t('revoke.active', { value: activeAnalysts })}
        </DialogContentText>
        <DialogContentText sx={{ mt: 1 }}>{t('revoke.consequences')}</DialogContentText>
        <TextField
          label={t('revoke.reason')}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          error={tooLong || Boolean(fieldError)}
          helperText={tooLong ? t('revoke.tooLong', { max: REASON_MAX_LENGTH }) : (fieldError ?? t('revoke.hint', { max: REASON_MAX_LENGTH }))}
          required
          multiline
          autoFocus
          fullWidth
          margin="normal"
        />
        <TextField
          label={label ? t('revoke.confirmationHost') : t('revoke.confirmationFingerprint')}
          value={confirmation}
          onChange={(e) => setConfirmation(e.target.value)}
          error={mismatch}
          helperText={mismatch ? t('revoke.mismatch') : undefined}
          autoComplete="off"
          fullWidth
          margin="normal"
        />
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{t('decision.cancel')}</Button>
        <Button type="submit" variant="contained" color="error" disabled={!valid || busy}>
          {t('revoke.confirm')}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
