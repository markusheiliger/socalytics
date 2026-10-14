import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useSession } from '../../session/sessionContext';
import { allowedNavItems } from '../../shell/navItems';
import { SignOutButton } from '../account/SignOutButton';

/** Role-dependent shortcuts to the views the member may open. */
export function HomePage() {
  const { t } = useTranslation('shell');
  const { t: tc } = useTranslation('common');
  const { session } = useSession();
  const shortcuts = session ? allowedNavItems(session.member).filter((item) => item.to !== '/') : [];
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('home.title')}</Typography>
      {session && <Typography sx={{ mt: 1 }}>{t('home.signedInAs', { name: session.member.accountName })}</Typography>}
      <Typography variant="h3" sx={{ mt: 3 }}>
        {t('home.shortcuts')}
      </Typography>
      <Stack component="ul" direction="row" spacing={1} sx={{ listStyle: 'none', m: 0, p: 0, mt: 1, flexWrap: 'wrap' }}>
        {shortcuts.map((item) => (
          <li key={item.to}>
            <Button component={Link} to={item.to} variant="outlined">
              {t(`nav.${item.labelKey}`)}
            </Button>
          </li>
        ))}
      </Stack>
      <Stack sx={{ mt: 3 }}>
        <SignOutButton />
      </Stack>
    </Container>
  );
}
