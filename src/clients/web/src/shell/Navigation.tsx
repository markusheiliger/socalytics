import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import { useTranslation } from 'react-i18next';
import { NavLink } from 'react-router';
import { useSession } from '../session/sessionContext';
import { allowedNavItems } from './navItems';

/** Offers exactly the routes the member's club and team roles allow. */
export function Navigation() {
  const { t } = useTranslation('shell');
  const { session } = useSession();
  if (!session) return null;
  return (
    <nav aria-label={t('nav.label')}>
      <Stack component="ul" direction="row" spacing={1} sx={{ listStyle: 'none', m: 0, p: 0, flexWrap: 'wrap' }}>
        {allowedNavItems(session.member).map((item) => (
          <li key={item.to}>
            <Button
              component={NavLink}
              to={item.to}
              end
              sx={{ '&.active': { fontWeight: 700, textDecoration: 'underline' } }}
            >
              {t(`nav.${item.labelKey}`)}
            </Button>
          </li>
        ))}
      </Stack>
    </nav>
  );
}
