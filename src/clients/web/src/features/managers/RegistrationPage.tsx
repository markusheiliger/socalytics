import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createRegistrationAdapter } from '../../platform/adapters/registrationAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert, useFieldError } from '../shared/FailureAlert';
import { ApproveDialog } from './ApproveDialog';
import { FLEET_KEY, FLEET_REFRESH_MS } from './FleetPage';
import { RejectDialog } from './RejectDialog';

type Decision = { kind: 'approve' } | { kind: 'reject'; reason: string };

/** One registration for a Club Admin: every field of FR-007, the warnings of FR-008, and the decisions of FR-009. */
export function RegistrationPage() {
  const { registrationId = '' } = useParams();
  const { t } = useTranslation('managers');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createRegistrationAdapter(platform), [platform]);
  const key = [...FLEET_KEY, registrationId];
  const [dialog, setDialog] = useState<'approve' | 'reject'>();
  const [notice, setNotice] = useState<'approved' | 'rejected'>();
  const query = useQuery({ queryKey: key, queryFn: () => adapter.get(registrationId), retry: false, refetchInterval: FLEET_REFRESH_MS });
  const decide = useMutation({
    mutationFn: (decision: Decision) =>
      decision.kind === 'approve' ? adapter.approve(registrationId) : adapter.reject(registrationId, decision.reason),
    onSuccess: (next, decision) => {
      queryClient.setQueryData(key, next);
      setNotice(decision.kind === 'approve' ? 'approved' : 'rejected');
      setDialog(undefined);
      return queryClient.invalidateQueries({ queryKey: FLEET_KEY });
    },
    onError: (error) => {
      const failure = toPlatformFailure(error);
      if (failure.kind === 'decisionConflict' || failure.kind === 'conflict') {
        setDialog(undefined);
        void queryClient.invalidateQueries({ queryKey: FLEET_KEY });
      }
    },
  });

  const r = query.data;
  const loadFailure = query.error ? toPlatformFailure(query.error) : undefined;
  const failure: PlatformFailure | undefined = decide.error ? toPlatformFailure(decide.error) : undefined;
  const reasonError = useFieldError(failure, 'reason', tc);
  const none = t('pairing.notProvided');
  const date = (value?: Date) => value?.toLocaleString();
  const pending = r?.state === 'pendingApproval';
  const busy = decide.isPending || isUnreachable;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('registration.title')}</Typography>
      <Link to="/analyst-managers">{t('registration.back')}</Link>
      {loadFailure &&
        (loadFailure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('requests.notFound')}</Alert>
        ) : (
          <FailureAlert failure={loadFailure} onReload={() => void query.refetch()} />
        ))}
      {failure &&
        (failure.kind === 'decisionConflict' ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{tc('failure.decisionConflict')}</Alert>
        ) : failure.kind === 'validation' ? null : (
          <FailureAlert failure={failure} onReload={() => void query.refetch()} />
        ))}
      {notice && (
        <Alert severity="success" role="status" sx={{ mt: 2 }}>{t(`registration.notice.${notice}`)}</Alert>
      )}
      {r && (
        <>
          <Stack component="ul" aria-label={t('registration.details')} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }} spacing={0.5}>
            <li>{t('pairing.state', { state: t(`state.${r.state}`) })}</li>
            <li>{t('pairing.hostLabel', { value: r.deviceMetadata.hostLabel ?? none })}</li>
            <li>{t('pairing.fingerprintValue', { value: r.deviceFingerprint })}</li>
            <li>{t('pairing.keyProtection', { value: t(`keyProtection.${r.deviceMetadata.claimedKeyProtection}`) })}</li>
            <li>{t('registration.os', { value: [r.deviceMetadata.os, r.deviceMetadata.osVersion].filter(Boolean).join(' ') || none })}</li>
            <li>{t('registration.architecture', { value: r.deviceMetadata.architecture ?? none })}</li>
            <li>{t('registration.managerVersion', { value: r.deviceMetadata.managerVersion ?? none })}</li>
            {r.pairingOrigin?.requestedAt && <li>{t('registration.requestedAt', { value: date(r.pairingOrigin.requestedAt) })}</li>}
            {r.pairingOrigin?.sourceAddress && <li>{t('registration.sourceAddress', { value: r.pairingOrigin.sourceAddress })}</li>}
            {r.submittedBy && <li>{t('registration.submittedBy', { value: r.submittedBy })}</li>}
            {r.submittedAt && <li>{t('requests.submittedAt', { value: date(r.submittedAt) })}</li>}
            {r.decidedBy && <li>{t('registration.decidedBy', { value: r.decidedBy })}</li>}
            {r.decidedAt && <li>{t('requests.decidedAt', { value: date(r.decidedAt) })}</li>}
            {r.decisionReason && <li>{t('requests.reason', { value: r.decisionReason })}</li>}
            {r.pairingExpiresAt && <li>{t('registration.pairingDeadline', { value: date(r.pairingExpiresAt) })}</li>}
            {r.approvalExpiresAt && <li>{t('requests.approvalDeadline', { value: date(r.approvalExpiresAt) })}</li>}
            {r.activationExpiresAt && <li>{t('requests.activationDeadline', { value: date(r.activationExpiresAt) })}</li>}
            {r.activatedAt && <li>{t('registration.activatedAt', { value: date(r.activatedAt) })}</li>}
          </Stack>
          {pending && (
            <Stack spacing={1} sx={{ mt: 2 }}>
              {r.approvalNotice && <Alert severity="info">{r.approvalNotice}</Alert>}
              <Alert severity="warning">{t('registration.physicalWarning')}</Alert>
              {r.deviceMetadata.claimedKeyProtection === 'software' && (
                <Alert severity="warning">{t('registration.softwareWarning')}</Alert>
              )}
              <Typography>{t('registration.claimedNotVerified')}</Typography>
              <Stack direction="row" spacing={1}>
                <Button variant="contained" disabled={busy} onClick={() => { decide.reset(); setNotice(undefined); setDialog('approve'); }}>
                  {t('approve.open')}
                </Button>
                <Button variant="outlined" color="error" disabled={busy} onClick={() => { decide.reset(); setNotice(undefined); setDialog('reject'); }}>
                  {t('reject.open')}
                </Button>
              </Stack>
            </Stack>
          )}
          <ApproveDialog
            key={`approve-${dialog === 'approve'}`}
            open={dialog === 'approve'}
            fingerprint={r.deviceFingerprint}
            busy={busy}
            onConfirm={() => decide.mutate({ kind: 'approve' })}
            onClose={() => setDialog(undefined)}
          />
          <RejectDialog
            key={`reject-${dialog === 'reject'}`}
            open={dialog === 'reject'}
            busy={busy}
            fieldError={reasonError}
            onConfirm={(reason) => decide.mutate({ kind: 'reject', reason })}
            onClose={() => setDialog(undefined)}
          />
        </>
      )}
    </Container>
  );
}
