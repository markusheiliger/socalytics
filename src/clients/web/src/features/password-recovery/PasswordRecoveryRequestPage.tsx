import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link as RouterLink } from 'react-router';
import { createPasswordRecoveryAdapter } from '../../platform/adapters/passwordRecoveryAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';

type Outcome =
  | { kind: 'sent' }
  | { kind: 'fieldError'; code?: string }
  | { kind: 'rateLimited'; seconds?: number }
  | { kind: 'failure'; failure: PlatformFailure };

/** Asks for a recovery link; the answer is the same for every well-formed address. */
export function PasswordRecoveryRequestPage() {
  const { t } = useTranslation('passwordRecovery');
  const { platform } = useSession();
  const adapter = useMemo(() => createPasswordRecoveryAdapter(platform), [platform]);
  const [address, setAddress] = useState('');
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<Outcome>();

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setOutcome(undefined);
    try {
      const result = await adapter.requestRecovery(address.trim());
      if (result.ok) setOutcome({ kind: 'sent' });
      else if (result.kind === 'rateLimited') setOutcome({ kind: 'rateLimited', seconds: result.retryAfterSeconds });
      else setOutcome({ kind: 'fieldError', code: result.kind === 'fieldErrors' ? result.fieldErrors.find((e) => e.field === 'address')?.code : undefined });
    } catch (error) {
      setOutcome({ kind: 'failure', failure: toPlatformFailure(error) });
    } finally {
      setBusy(false);
    }
  };

  const fieldProblem =
    outcome?.kind === 'fieldError'
      ? t(`request.fieldError.${outcome.code === 'required' || outcome.code === 'invalid' ? outcome.code : 'other'}`)
      : undefined;

  return (
    <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
      <Typography variant="h1">{t('request.title')}</Typography>
      {outcome?.kind === 'sent' ? (
        <Stack spacing={2} sx={{ mt: 2 }}>
          <Alert severity="success" role="status">
            {t('request.sent')}
          </Alert>
          <Button component={RouterLink} to="/sign-in" variant="contained">
            {t('request.backToSignIn')}
          </Button>
        </Stack>
      ) : (
        <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={(e: FormEvent) => void onSubmit(e)} noValidate>
          <Typography>{t('request.intro')}</Typography>
          {outcome?.kind === 'rateLimited' && (
            <Alert severity="warning" role="alert">
              {outcome.seconds === undefined ? t('request.rateLimitedUnknown') : t('request.rateLimited', { seconds: outcome.seconds })}
            </Alert>
          )}
          {outcome?.kind === 'failure' && (
            <Alert severity="error" role="alert">
              {outcome.failure.kind === 'unreachable' ? t('request.unreachable') : t('request.error')}
              {outcome.failure.kind !== 'unreachable' && outcome.failure.correlationId
                ? ` ${t('request.reference', { id: outcome.failure.correlationId })}`
                : ''}
            </Alert>
          )}
          <TextField
            label={t('request.address')}
            name="address"
            type="email"
            autoComplete="email"
            value={address}
            onChange={(e) => setAddress(e.target.value)}
            error={fieldProblem !== undefined}
            helperText={fieldProblem}
            required
            autoFocus
          />
          <Button type="submit" variant="contained" disabled={busy || !address.trim()}>
            {busy ? t('request.submitting') : t('request.submit')}
          </Button>
          <Button component={RouterLink} to="/sign-in">
            {t('request.backToSignIn')}
          </Button>
        </Stack>
      )}
    </Container>
  );
}
