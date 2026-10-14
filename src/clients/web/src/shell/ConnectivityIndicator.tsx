import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../connectivity/useConnectivity';

/** Announces while the platform is not reachable; state-changing actions are disabled meanwhile. */
export function ConnectivityIndicator() {
  const { t } = useTranslation('shell');
  const { isUnreachable } = useConnectivity();
  return (
    <Box role="status">
      {isUnreachable && <Alert severity="warning">{t('connectivity.unreachable')}</Alert>}
    </Box>
  );
}
