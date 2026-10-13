import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';

export function NotPermittedPage() {
  const { t } = useTranslation('common');
  const { t: ts } = useTranslation('shell');
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t('app.name')}</Typography>
      <Typography role="alert">{ts('notPermitted')}</Typography>
    </Container>
  );
}
