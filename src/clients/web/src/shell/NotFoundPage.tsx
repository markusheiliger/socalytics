import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import { SignOutButton } from '../features/account/SignOutButton';
import { useSession } from '../session/sessionContext';
import { ShellHeader } from './AppLayout';

export function NotFoundPage() {
  const { t } = useTranslation('common');
  const { t: ts } = useTranslation('shell');
  const { status } = useSession();
  const signedIn = status === 'signedIn';
  return (
    <>
      {signedIn && <ShellHeader />}
      <Container component="main" maxWidth="md" sx={{ py: 4 }}>
        <Typography variant="h1">{t('app.name')}</Typography>
        <Typography variant="h2">{ts('notFound')}</Typography>
        {signedIn && <SignOutButton />}
      </Container>
    </>
  );
}
