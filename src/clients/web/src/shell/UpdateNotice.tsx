import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import { useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { applyUpdate, getUpdateState, subscribeUpdateState } from '../serviceWorker/registration';

/** Non-blocking notice that a newer release is waiting; "Update" activates it and reloads every window. */
export function UpdateNotice() {
  const { t } = useTranslation('shell');
  const state = useSyncExternalStore(subscribeUpdateState, getUpdateState);
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
