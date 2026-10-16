import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import { useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import { applyUpdate, getUpdateState, reloadApplication, subscribeUpdateState } from '../serviceWorker/registration';

/** Notice of a newer release: non-blocking while one is waiting, blocking while the platform requires it; "Update" activates it and reloads every window. */
export function UpdateNotice() {
  const { t } = useTranslation('shell');
  const state = useSyncExternalStore(subscribeUpdateState, getUpdateState);
  if (state === 'required' || state === 'reload-required') {
    return (
      <Dialog open role="alertdialog" aria-describedby="update-required-text" disableEscapeKeyDown>
        <DialogContent>
          <DialogContentText id="update-required-text">
            {state === 'required' ? t('update.required') : t('update.reloadRequired')}
          </DialogContentText>
        </DialogContent>
        {state === 'reload-required' && (
          <DialogActions>
            <Button onClick={reloadApplication}>{t('update.reload')}</Button>
          </DialogActions>
        )}
      </Dialog>
    );
  }
  if (state !== 'available') {
    return null;
  }
  return (
    <Alert
      role="status"
      severity="info"
      action={
        <Button color="inherit" size="small" onClick={applyUpdate}>
          {t('update.action')}
        </Button>
      }
    >
      {t('update.available')}
    </Alert>
  );
}
