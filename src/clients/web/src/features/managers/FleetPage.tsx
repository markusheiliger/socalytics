import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { createRegistrationAdapter } from '../../platform/adapters/registrationAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';

export const FLEET_KEY = ['analyst-managers'];
export const PENDING_KEY = [...FLEET_KEY, 'pending'];
export const FLEET_REFRESH_MS = 30_000;
export const PENDING_COUNT_REFRESH_MS = 60_000;

/** The requests waiting for a Club Admin's decision; shares its query with the navigation's pending count. */
export function FleetPage() {
  const { t } = useTranslation('managers');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const adapter = useMemo(() => createRegistrationAdapter(platform), [platform]);
  const query = useQuery({
    queryKey: PENDING_KEY,
    queryFn: () => adapter.listAll({ state: 'pendingApproval' }),
    retry: false,
    refetchInterval: FLEET_REFRESH_MS,
  });
  const none = t('pairing.notProvided');

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('fleet.title')}</Typography>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {query.data &&
        (query.data.length === 0 ? (
          <Typography sx={{ mt: 1 }}>{t('fleet.noPending')}</Typography>
        ) : (
          <>
            <Typography variant="h3" sx={{ mt: 2 }}>{t('fleet.pendingHeading')}</Typography>
            <Stack component="ul" aria-label={t('fleet.pendingHeading')} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 1 }}>
              {query.data.map((r) => (
                <li key={r.registrationId}>
                  <Link to={`/analyst-managers/${r.registrationId}`}>
                    {t('requests.listEntry', { host: r.deviceMetadata.hostLabel ?? none, state: t(`state.${r.state}`) })}
                  </Link>
                  <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                    <li>{t('requests.fingerprint', { value: r.deviceFingerprint })}</li>
                    {r.submittedAt && <li>{t('requests.submittedAt', { value: r.submittedAt.toLocaleString() })}</li>}
                    {r.approvalExpiresAt && <li>{t('requests.approvalDeadline', { value: r.approvalExpiresAt.toLocaleString() })}</li>}
                  </Stack>
                </li>
              ))}
            </Stack>
          </>
        ))}
    </Container>
  );
}
