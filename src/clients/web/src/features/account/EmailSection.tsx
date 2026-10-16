import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createEmailAdapter, RateLimitedFailure } from '../../platform/adapters/emailAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';

const KEY = ['email', 'own'];

type Notice = 'saved' | 'removed' | 'resent';

/** The member's own email address: state, set, change, remove, and resend the confirmation link. */
export function EmailSection() {
  const { t } = useTranslation('email');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createEmailAdapter(platform), [platform]);
  const [address, setAddress] = useState('');
  const [notice, setNotice] = useState<Notice>();
  const [failure, setFailure] = useState<PlatformFailure>();
  const query = useQuery({ queryKey: KEY, queryFn: () => adapter.get(), retry: false });
  const current = query.data;

  const run = useMutation({
    mutationFn: async (action: 'set' | 'remove' | 'resend') => {
      if (action === 'set') return adapter.set(address.trim(), current?.version);
      if (action === 'remove') return adapter.remove(current?.version ?? 0);
      return adapter.resend();
    },
    onMutate: () => {
      setNotice(undefined);
      setFailure(undefined);
    },
    onSuccess: async (_result, action) => {
      setNotice(action === 'set' ? 'saved' : action === 'remove' ? 'removed' : 'resent');
      if (action !== 'resend') setAddress('');
      await queryClient.invalidateQueries({ queryKey: KEY });
    },
    onError: (error) => setFailure(toPlatformFailure(error)),
  });

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    run.mutate('set');
  };

  const problem = () => {
    if (!failure) return null;
    if (failure instanceof RateLimitedFailure) {
      return failure.retryAfterSeconds === undefined
        ? t('section.rateLimitedUnknown')
        : t('section.rateLimited', { seconds: failure.retryAfterSeconds });
    }
    if (failure.code === 'email-address-invalid' || failure.fieldErrors.some((e) => e.field === 'address')) return t('section.invalid');
    if (failure.code === 'email-already-verified') return t('section.alreadyVerified');
    return null;
  };
  const message = problem();
  const busy = run.isPending;

  return (
    <Stack component="section" aria-labelledby="email-title" spacing={2} sx={{ mt: 3 }}>
      <Typography id="email-title" variant="h3">
        {t('section.title')}
      </Typography>
      {query.isError && <FailureAlert failure={toPlatformFailure(query.error)} />}
      {query.isSuccess && (
        <Typography>
          {current ? t('section.current', { address: current.address }) : t('section.none')}{' '}
          {current && t(`section.state.${current.state}`)}
        </Typography>
      )}
      {notice && (
        <Alert severity="success" role="status">
          {t(`section.${notice}`)}
        </Alert>
      )}
      {message && (
        <Alert severity="error" role="alert">
          {message}
        </Alert>
      )}
      {failure && !message && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      <Stack component="form" spacing={2} onSubmit={onSubmit} noValidate>
        <TextField
          label={t('section.address')}
          name="emailAddress"
          type="email"
          autoComplete="email"
          value={address}
          onChange={(e) => setAddress(e.target.value)}
          error={message === t('section.invalid')}
          required
        />
        <Button type="submit" variant="contained" disabled={busy || isUnreachable || !query.isSuccess || !address.trim()}>
          {busy ? t('section.saving') : current ? t('section.change') : t('section.set')}
        </Button>
      </Stack>
      {current && (
        <Stack direction="row" spacing={2}>
          {current.state === 'unverified' && (
            <Button variant="outlined" disabled={busy || isUnreachable} onClick={() => run.mutate('resend')}>
              {t('section.resend')}
            </Button>
          )}
          <Button variant="outlined" color="error" disabled={busy || isUnreachable} onClick={() => run.mutate('remove')}>
            {t('section.remove')}
          </Button>
        </Stack>
      )}
    </Stack>
  );
}
