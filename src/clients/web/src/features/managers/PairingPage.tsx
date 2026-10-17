import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createRegistrationAdapter } from '../../platform/adapters/registrationAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';

function PairingFailure({ failure }: { failure: PlatformFailure }) {
  const { t } = useTranslation('common');
  if (failure.kind === 'pairingRefused') return <Alert severity="error" role="alert">{t('failure.pairingRefused')}</Alert>;
  if (failure.kind === 'rateLimited') {
    return (
      <Alert severity="error" role="alert">
        {failure.retryAfterSeconds === undefined
          ? t('failure.rateLimitedUnknown')
          : t('failure.rateLimited', { seconds: failure.retryAfterSeconds })}
      </Alert>
    );
  }
  return <FailureAlert failure={failure} />;
}

/** Submits the pairing code and device fingerprint of a Manager; both live only in this form and are cleared on submission. */
export function PairingPage() {
  const { t } = useTranslation('managers');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const adapter = useMemo(() => createRegistrationAdapter(platform), [platform]);
  const [code, setCode] = useState('');
  const [fingerprint, setFingerprint] = useState('');
  const submit = useMutation({ mutationFn: (input: { code: string; fingerprint: string }) => adapter.submitPairingCode(input.code, input.fingerprint) });
  const failure = submit.error ? toPlatformFailure(submit.error) : undefined;
  const result = submit.data;
  const canSubmit = code.trim() !== '' && fingerprint.trim() !== '' && !submit.isPending && !isUnreachable;
  const none = t('pairing.notProvided');

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (!canSubmit) return;
    submit.mutate({ code: code.trim(), fingerprint: fingerprint.trim() });
    setCode('');
    setFingerprint('');
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('pairing.title')}</Typography>
      {result && (
        <Stack component="section" aria-labelledby="pairing-result" spacing={1} sx={{ mt: 2 }}>
          <Alert severity="success" role="status">
            <Typography id="pairing-result" variant="h3" component="h3">
              {t('pairing.resultTitle')}
            </Typography>
            {t('pairing.resultIntro')}
          </Alert>
          <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }} spacing={0.5}>
            <li>{t('pairing.state', { state: t(`state.${result.state}`) })}</li>
            <li>{t('pairing.hostLabel', { value: result.deviceMetadata.hostLabel ?? none })}</li>
            <li>{t('pairing.keyProtection', { value: t(`keyProtection.${result.deviceMetadata.claimedKeyProtection}`) })}</li>
            <li>{t('pairing.profile', { value: t(`applicationProfile.${result.deviceMetadata.claimedApplicationProfile ?? 'notReported'}`) })}</li>
            <li>{t('pairing.os', { value: result.deviceMetadata.os ?? none })}</li>
            <li>{t('pairing.fingerprintValue', { value: result.deviceFingerprint })}</li>
            {result.approvalExpiresAt && <li>{t('pairing.deadline', { value: result.approvalExpiresAt.toLocaleString() })}</li>}
          </Stack>
        </Stack>
      )}
      <Typography sx={{ mt: 2 }}>{t('pairing.intro')}</Typography>
      <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={onSubmit} noValidate>
        {failure && <PairingFailure failure={failure} />}
        <TextField
          label={t('pairing.code')}
          name="pairingCode"
          value={code}
          onChange={(e) => setCode(e.target.value)}
          autoComplete="off"
          slotProps={{ htmlInput: { autoCapitalize: 'characters', spellCheck: false } }}
          required
        />
        <TextField
          label={t('pairing.fingerprint')}
          name="deviceFingerprint"
          value={fingerprint}
          onChange={(e) => setFingerprint(e.target.value)}
          autoComplete="off"
          slotProps={{ htmlInput: { spellCheck: false } }}
          required
        />
        <Button type="submit" variant="contained" disabled={!canSubmit}>
          {submit.isPending ? t('pairing.submitting') : t('pairing.submit')}
        </Button>
      </Stack>
    </Container>
  );
}
