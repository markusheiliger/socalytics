import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createMemberAdapter } from '../../platform/adapters/memberAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { CredentialCode } from '../../platform/models/member';
import { useSession } from '../../session/sessionContext';
import { FailureAlert, useFieldError } from '../shared/FailureAlert';
import { CredentialDialog } from './CredentialDialog';
import { InviteMemberDialog } from './InviteMemberDialog';
import { MemberFailureAlert } from './MemberFailureAlert';

export const MEMBERS_KEY = ['members'];

/** Members of the club: list and create; creating shows the first set-password code once. */
export function MembersPage() {
  const { t } = useTranslation('members');
  const { t: tc } = useTranslation('common');
  const { t: ti } = useTranslation('invitations');
  const { platform, listProviders } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createMemberAdapter(platform), [platform]);
  const [accountName, setAccountName] = useState('');
  const [created, setCreated] = useState<{ name: string; credential: CredentialCode }>();
  const [notice, setNotice] = useState(false);
  const [inviting, setInviting] = useState(false);
  const [invitedAddress, setInvitedAddress] = useState<string>();
  const [linkProvider, setLinkProvider] = useState('');
  const providers = useQuery({ queryKey: ['identity-providers'], queryFn: () => listProviders(), retry: false });
  const query = useQuery({ queryKey: MEMBERS_KEY, queryFn: () => adapter.list(), retry: false });
  const create = useMutation({
    mutationFn: () => adapter.create({ accountName, linkProvider: linkProvider || undefined }),
    onSuccess: (result) => {
      setCreated({ name: result.member.accountName, credential: result.credential });
      setNotice(true);
      setAccountName('');
      create.reset();
      return queryClient.invalidateQueries({ queryKey: MEMBERS_KEY });
    },
  });
  const failure = create.error ? toPlatformFailure(create.error) : undefined;
  const nameError = useFieldError(failure, 'accountName', tc);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setNotice(false);
    create.mutate();
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('members.title')}</Typography>
      <Button
        variant="contained"
        sx={{ mt: 1 }}
        onClick={() => {
          setInvitedAddress(undefined);
          setInviting(true);
        }}
      >
        {ti('invite.action')}
      </Button>
      {invitedAddress && (
        <Alert severity="success" role="status" sx={{ mt: 2 }}>
          {ti('invite.sent', { address: invitedAddress })}
        </Alert>
      )}
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {query.data &&
        (query.data.length === 0 ? (
          <Typography sx={{ mt: 1 }}>{t('members.empty')}</Typography>
        ) : (
          <Stack component="ul" aria-label={t('members.listLabel')} spacing={1} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
            {query.data.map((member) => (
              <li key={member.id}>
                <Link to={`/members/${member.id}`}>
                  {t('members.listEntry', { name: member.accountName, status: t(`members.status.${member.status}`) })}
                </Link>
              </li>
            ))}
          </Stack>
        ))}
      <Typography variant="h3" sx={{ mt: 3 }}>
        {t('members.createTitle')}
      </Typography>
      <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={onSubmit} noValidate>
        {notice && (
          <Alert severity="success" role="status">
            {t('members.created')}
          </Alert>
        )}
        {failure && !(failure.kind === 'validation' && nameError) && <MemberFailureAlert failure={failure} />}
        <TextField
          label={t('members.accountName')}
          name="accountName"
          value={accountName}
          onChange={(e) => setAccountName(e.target.value)}
          error={Boolean(nameError)}
          helperText={nameError}
          autoComplete="off"
          required
        />
        {providers.data && providers.data.length > 0 && (
          <TextField
            select
            label={t('members.credentialChoice')}
            name="credentialChoice"
            value={linkProvider}
            onChange={(e) => setLinkProvider(e.target.value)}
            slotProps={{ select: { native: true } }}
          >
            <option value="">{t('members.choiceSetPassword')}</option>
            {providers.data.map((p) => (
              <option key={p.id} value={p.id}>
                {t('members.choiceLink', { name: p.displayName })}
              </option>
            ))}
          </TextField>
        )}
        <Button type="submit" variant="contained" disabled={create.isPending || isUnreachable}>
          {create.isPending ? t('members.creating') : t('members.create')}
        </Button>
      </Stack>
      <InviteMemberDialog
        open={inviting}
        onClose={() => setInviting(false)}
        onInvited={(invitation) => {
          setInviting(false);
          setInvitedAddress(invitation.address);
        }}
      />
      <CredentialDialog credential={created?.credential} accountName={created?.name ?? ''} onClose={() => setCreated(undefined)} />
    </Container>
  );
}
