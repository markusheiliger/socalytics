import Alert from '@mui/material/Alert';
import { useTranslation } from 'react-i18next';

/** Shown when the platform refused the anti-forgery check of a state-changing request. */
export function SessionProblemAlert() {
  const { t } = useTranslation('session');
  return <Alert severity="error">{t('sessionProblem')}</Alert>;
}
