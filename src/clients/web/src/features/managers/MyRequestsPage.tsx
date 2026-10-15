import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, Navigate } from 'react-router';
import { createRegistrationAdapter } from '../../platform/adapters/registrationAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { isClubAdmin } from '../../session/roles';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';

export const MY_REQUESTS_KEY = ['analyst-manager-requests'];
export const REQUESTS_REFRESH_MS = 30_000;

/** The registration requests the signed-in Registrar submitted; Club Admins use the fleet instead. */
export function MyRequestsPage() {
  const { t } = useTranslation('managers');
  const { t: tc } = useTranslation('common');
  const { platform, session } = useSession();
  const adapter = useMemo(() => createRegistrationAdapter(platform), [platform]);
  const admin = session ? isClubAdmin(session.member) : false;
  const query = useQuery({
    queryKey: MY_REQUESTS_KEY,
    queryFn: () => adapter.listAll(),
    retry: false,
    enabled: !admin,
    refetchInterval: REQUESTS_REFRESH_MS,
  });
  if (admin) return <Navigate to="/analyst-managers" replace />;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('requests.title')}</Typography>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {query.data &&
        (query.data.length === 0 ? (
          <Typography sx={{ mt: 1 }}>{t('requests.empty')}</Typography>
        ) : (
          <Stack component="ul" aria-label={t('requests.listLabel')} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
            {query.data.map((r) => (
              <li key={r.registrationId}>
                <Link to={`/analyst-managers/requests/${r.registrationId}`}>
                  {t('requests.listEntry', { host: r.deviceMetadata.hostLabel ?? t('pairing.notProvided'), state: t(`state.${r.state}`) })}
                </Link>
                <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                  <li>{t('requests.fingerprint', { value: r.deviceFingerprint })}</li>
                  {r.submittedAt && <li>{t('requests.submittedAt', { value: r.submittedAt.toLocaleString() })}</li>}
                  {r.decidedAt && <li>{t('requests.decidedAt', { value: r.decidedAt.toLocaleString() })}</li>}
                  {r.decisionReason && <li>{t('requests.reason', { value: r.decisionReason })}</li>}
                  {r.approvalExpiresAt && <li>{t('requests.approvalDeadline', { value: r.approvalExpiresAt.toLocaleString() })}</li>}
                  {r.activationExpiresAt && <li>{t('requests.activationDeadline', { value: r.activationExpiresAt.toLocaleString() })}</li>}
                </Stack>
              </li>
            ))}
          </Stack>
        ))}
    </Container>
  );
}
