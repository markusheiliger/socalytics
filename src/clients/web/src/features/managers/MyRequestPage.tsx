import Alert from '@mui/material/Alert';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, Navigate, useParams } from 'react-router';
import { createRegistrationAdapter } from '../../platform/adapters/registrationAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { isClubAdmin } from '../../session/roles';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';
import { MY_REQUESTS_KEY, REQUESTS_REFRESH_MS } from './MyRequestsPage';

/** One registration request of the signed-in Registrar, read-only; requests of others answer "not found". */
export function MyRequestPage() {
  const { registrationId = '' } = useParams();
  const { t } = useTranslation('managers');
  const { t: tc } = useTranslation('common');
  const { platform, session } = useSession();
  const adapter = useMemo(() => createRegistrationAdapter(platform), [platform]);
  const admin = session ? isClubAdmin(session.member) : false;
  const query = useQuery({
    queryKey: [...MY_REQUESTS_KEY, registrationId],
    queryFn: () => adapter.get(registrationId),
    retry: false,
    enabled: !admin,
    refetchInterval: REQUESTS_REFRESH_MS,
  });
  if (admin) return <Navigate to={`/analyst-managers/${registrationId}`} replace />;

  const failure = query.error ? toPlatformFailure(query.error) : undefined;
  const r = query.data;
  const none = t('pairing.notProvided');

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('requests.detailTitle')}</Typography>
      <Link to="/analyst-managers/requests">{t('requests.back')}</Link>
      {failure &&
        (failure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('requests.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => void query.refetch()} />
        ))}
      {r && (
        <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }} spacing={0.5}>
          <li>{t('pairing.hostLabel', { value: r.deviceMetadata.hostLabel ?? none })}</li>
          <li>{t('requests.fingerprint', { value: r.deviceFingerprint })}</li>
          <li>{t('pairing.state', { state: t(`state.${r.state}`) })}</li>
          {r.submittedAt && <li>{t('requests.submittedAt', { value: r.submittedAt.toLocaleString() })}</li>}
          {r.decidedAt && <li>{t('requests.decidedAt', { value: r.decidedAt.toLocaleString() })}</li>}
          {r.decisionReason && <li>{t('requests.reason', { value: r.decisionReason })}</li>}
          {r.approvalExpiresAt && <li>{t('requests.approvalDeadline', { value: r.approvalExpiresAt.toLocaleString() })}</li>}
          {r.activationExpiresAt && <li>{t('requests.activationDeadline', { value: r.activationExpiresAt.toLocaleString() })}</li>}
        </Stack>
      )}
    </Container>
  );
}
