import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Box from '@mui/material/Box';
import { useTranslation } from 'react-i18next';
import type { RegistryRefusal } from '../../platform/models/registry';
import { refusalMessages } from './refusalText';

/** The platform's refusal as text: the explanation, the listed details, and "try again later" when retryable (FR-012). */
export function RefusalAlert({ refusal }: { refusal: RegistryRefusal }) {
  const { t } = useTranslation('registry');
  const messages = refusalMessages(refusal).map(({ key, values, defaultValue }) => t(key, { ...values, defaultValue }));
  const retryLater = t('refusal.retryLater');
  return (
    <Alert severity="error" role="alert" sx={{ mt: 2 }}>
      <AlertTitle>{t('refusal.title')}</AlertTitle>
      {messages.map((message, index) => (
        <Box key={index}>{message}</Box>
      ))}
      {refusal.details.length > 0 && (
        <Box component="ul" sx={{ m: 0, pl: 2 }}>
          {refusal.details.map((detail, index) => (
            <li key={index}>{t('refusal.detail', { field: detail.field, code: detail.code })}</li>
          ))}
        </Box>
      )}
      {refusal.retryable && !messages.some((message) => message.includes(retryLater)) && <Box>{retryLater}</Box>}
    </Alert>
  );
}
