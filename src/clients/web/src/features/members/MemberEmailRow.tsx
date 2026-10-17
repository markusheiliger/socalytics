import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createEmailAdapter } from '../../platform/adapters/emailAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';

type Notice = 'saved' | 'removed';

/** A member's address as a Club Admin sees it: state, set, change, and remove. */
export function MemberEmailRow({ memberId }: { memberId: string }) {
  const { t } = useTranslation('email');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const operations = useMemo(() => createEmailAdapter(platform).forMember(memberId), [platform, memberId]);
  const key = ['email', 'member', memberId];
  const [address, setAddress] = useState('');
  const [notice, setNotice] = useState<Notice>();
  const [failure, setFailure] = useState<PlatformFailure>();
  const query = useQuery({ queryKey: key, queryFn: () => operations.get(), retry: false });
  const current = query.data;

  const run = useMutation({
    mutationFn: async (action: 'set' | 'remove') =>
      action === 'set' ? operations.set(address.trim(), current?.version) : operations.remove(current?.version ?? 0),
    onMutate: () => {
      setNotice(undefined);
      setFailure(undefined);
    },
    onSuccess: async (_result, action) => {
      setNotice(action === 'set' ? 'saved' : 'removed');
      setAddress('');
      await queryClient.invalidateQueries({ queryKey: key });
    },
    onError: (error) => setFailure(toPlatformFailure(error)),
  });

  const invalid = !!failure && (failure.code === 'email-address-invalid' || failure.fieldErrors.some((e) => e.field === 'address'));
  const busy = run.isPending;

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    run.mutate('set');
  };

  return (
    <Stack component="section" aria-labelledby="member-email-title" spacing={2} sx={{ mt: 3 }}>
      <Typography id="member-email-title" variant="h3">
        {t('member.title')}
      </Typography>
      {query.isError && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {query.isSuccess && (
        <Typography>
          {current ? t('member.current', { address: current.address }) : t('member.none')}{' '}
          {current && t(`member.state.${current.state}`)}
        </Typography>
      )}
      {notice && (
        <Alert severity="success" role="status">
          {t(`member.${notice}`)}
        </Alert>
      )}
      {invalid && (
        <Alert severity="error" role="alert">
          {t('section.invalid')}
        </Alert>
      )}
      {failure && !invalid && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      <Stack component="form" spacing={2} onSubmit={onSubmit} noValidate aria-label={t('member.formLabel')}>
        <TextField
          label={t('member.address')}
          name="memberEmailAddress"
          type="email"
          value={address}
          onChange={(e) => setAddress(e.target.value)}
          error={invalid}
          required
        />
        <Button type="submit" variant="contained" disabled={busy || isUnreachable || !query.isSuccess || !address.trim()}>
          {busy ? t('section.saving') : current ? t('section.change') : t('section.set')}
        </Button>
      </Stack>
      {current && (
        <Stack direction="row" spacing={2}>
          <Button variant="outlined" color="error" disabled={busy || isUnreachable} onClick={() => run.mutate('remove')}>
            {t('section.remove')}
          </Button>
        </Stack>
      )}
    </Stack>
  );
}
