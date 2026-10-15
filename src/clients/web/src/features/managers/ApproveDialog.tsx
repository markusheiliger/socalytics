import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import TextField from '@mui/material/TextField';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { matchesFingerprint, normalizeFingerprint } from './fingerprint';

/** Approval needs the last 6 characters of the fingerprint shown on the Manager's screen; a human check, not an authorization rule. */
export function ApproveDialog({ open, fingerprint, busy, onConfirm, onClose }: { open: boolean; fingerprint: string; busy: boolean; onConfirm: () => void; onClose: () => void }) {
  const { t } = useTranslation('managers');
  const [input, setInput] = useState('');
  const matches = matchesFingerprint(input, fingerprint);
  const mismatch = normalizeFingerprint(input) !== '' && !matches;
  const close = () => {
    setInput('');
    onClose();
  };
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (matches && !busy) onConfirm();
  };
  return (
    <Dialog open={open} onClose={close} aria-labelledby="approve-title" component="form" onSubmit={submit}>
      <DialogTitle id="approve-title">{t('approve.title')}</DialogTitle>
      <DialogContent>
        <DialogContentText>{t('approve.intro')}</DialogContentText>
        <TextField
          label={t('approve.confirmation')}
          value={input}
          onChange={(e) => setInput(e.target.value)}
          error={mismatch}
          helperText={mismatch ? t('approve.mismatch') : undefined}
          autoComplete="off"
          autoFocus
          fullWidth
          margin="normal"
        />
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{t('decision.cancel')}</Button>
        <Button type="submit" variant="contained" disabled={!matches || busy}>
          {t('approve.confirm')}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
