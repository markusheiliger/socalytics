import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { NavLink } from 'react-router';
import { PENDING_COUNT_REFRESH_MS, PENDING_KEY } from '../features/managers/FleetPage';
import { createRegistrationAdapter } from '../platform/adapters/registrationAdapter';
import { isClubAdmin } from '../session/roles';
import { useSession } from '../session/sessionContext';
import { allowedNavItems } from './navItems';

/** Offers exactly the routes the member's club and team roles allow. */
export function Navigation() {
  const { t } = useTranslation('shell');
  const { session, platform } = useSession();
  const admin = session ? isClubAdmin(session.member) : false;
  const adapter = useMemo(() => createRegistrationAdapter(platform), [platform]);
  const pending = useQuery({
    queryKey: PENDING_KEY,
    queryFn: () => adapter.listAll({ state: 'pendingApproval' }),
    retry: false,
    enabled: admin,
    refetchInterval: PENDING_COUNT_REFRESH_MS,
  });
  const pendingCount = admin ? (pending.data?.length ?? 0) : 0;
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
              {item.labelKey === 'analystManagers' && pendingCount > 0
                ? t('nav.analystManagersPending', { count: pendingCount })
                : t(`nav.${item.labelKey}`)}
            </Button>
          </li>
        ))}
      </Stack>
    </nav>
  );
}
