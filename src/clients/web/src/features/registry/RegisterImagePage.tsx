import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import { RegistryRefusalError } from '../../platform/models/registry';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { RefusalAlert } from './RefusalAlert';
import { useRegisterImage } from './registryKeys';

const DIGEST_REFERENCE = /@sha256:[0-9a-fA-F]{64}$/;

/** Registers an Analyst image by digest reference; explains a reference without a digest before sending (FR-011, FR-012). */
export function RegisterImagePage() {
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  const navigate = useNavigate();
  const [reference, setReference] = useState('');
  const [invalid, setInvalid] = useState(false);
  const register = useRegisterImage();
  const error = register.error;
  const refusal = error instanceof RegistryRefusalError ? error.refusal : undefined;
  const failure = error && !refusal ? toPlatformFailure(error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (register.isPending) return;
    const value = reference.trim();
    if (!DIGEST_REFERENCE.test(value)) {
      setInvalid(true);
      return;
    }
    setInvalid(false);
    register.mutate(value, {
      onSuccess: ({ declaration, created }) =>
        navigate(`/registry/images/${encodeURIComponent(declaration.imageDigest)}`, { state: created ? undefined : { alreadyRegistered: true } }),
    });
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('register.title')}</Typography>
      <Link to="/registry/images">{t('register.back')}</Link>
      <Typography sx={{ mt: 2 }}>{t('register.intro')}</Typography>
      <Stack component="form" spacing={2} onSubmit={submit} noValidate sx={{ mt: 2, alignItems: 'flex-start' }}>
        <TextField
          fullWidth
          label={t('register.reference')}
          value={reference}
          onChange={(e) => setReference(e.target.value)}
          error={invalid}
          helperText={invalid ? t('register.referenceInvalid') : t('register.referenceHint')}
          slotProps={{ htmlInput: { autoComplete: 'off', spellCheck: false } }}
        />
        <Button type="submit" variant="contained" disabled={register.isPending}>
          {register.isPending ? t('register.submitting') : t('register.submit')}
        </Button>
      </Stack>
      {register.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('register.submitting')}</Typography>}
      {refusal && <RefusalAlert refusal={refusal} />}
      {failure && <FailureAlert failure={failure} />}
    </Container>
  );
}
