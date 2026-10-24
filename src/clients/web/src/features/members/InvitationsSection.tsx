import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createInvitationAdapter, type InvitationResult } from '../../platform/adapters/invitationAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { InvitationDetail } from '../../platform/models/invitation';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';

export const INVITATIONS_KEY = ['invitations'];

const STATES = ['pending', 'expired', 'accepted', 'revoked'] as const;
const known = <T extends string>(all: readonly T[], value: string): value is T => (all as readonly string[]).includes(value);

type Notice = { severity: 'success' | 'error'; text: string };

/** Club Admin list of invitations, newest first, with state filter, resend, and confirmed revoke. */
export function InvitationsSection() {
  const { t } = useTranslation('invitations');
  const { t: ti } = useTranslation('invitations', { keyPrefix: 'invite' });
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createInvitationAdapter(platform), [platform]);
  const [filter, setFilter] = useState('');
  const [revoking, setRevoking] = useState<InvitationDetail>();
  const [notice, setNotice] = useState<Notice>();

  const query = useQuery({
    queryKey: [...INVITATIONS_KEY, filter],
    queryFn: () => adapter.listInvitations(filter || undefined),
    retry: false,
  });

  const refusalText = (result: Exclude<InvitationResult<InvitationDetail>, { ok: true }>) => {
    switch (result.kind) {
      case 'notOpen':
        return t('list.notOpen');
      case 'stale':
        return t('list.stale');
      case 'rateLimited':
        return result.retryAfterSeconds ? t('list.rateLimitedWait', { seconds: result.retryAfterSeconds }) : t('list.rateLimited');
      default:
        return undefined;
    }
  };

  const operate = useMutation({
    mutationFn: ({ invitation, operation }: { invitation: InvitationDetail; operation: 'resend' | 'revoke' }) =>
      operation === 'resend' ? adapter.resendInvitation(invitation.id, invitation.version) : adapter.revokeInvitation(invitation.id, invitation.version),
    onSuccess: (result, { invitation, operation }) => {
      setRevoking(undefined);
      if (result.ok) {
        setNotice({ severity: 'success', text: t(operation === 'resend' ? 'list.resent' : 'list.revokedDone', { address: invitation.address }) });
      } else {
        const text = refusalText(result);
        setNotice(text ? { severity: 'error', text } : undefined);
      }
      // Conflicts leave the shown version stale, so the list always reloads.
      return queryClient.invalidateQueries({ queryKey: INVITATIONS_KEY });
    },
    onError: () => setRevoking(undefined),
  });
  const failure = operate.error ? toPlatformFailure(operate.error) : undefined;
  const busy = operate.isPending || isUnreachable;
  const run = (invitation: InvitationDetail, operation: 'resend' | 'revoke') => {
    setNotice(undefined);
    operate.mutate({ invitation, operation });
  };
  const when = (value?: Date) => value?.toLocaleString('en', { dateStyle: 'medium', timeStyle: 'short' }) ?? '';

  return (
    <section aria-labelledby="invitations-title">
      <Typography id="invitations-title" variant="h3" sx={{ mt: 3 }}>
        {t('list.title')}
      </Typography>
      <TextField
        select
        label={t('list.filterLabel')}
        name="invitationState"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
        slotProps={{ select: { native: true } }}
        size="small"
        sx={{ mt: 1 }}
      >
        <option value="">{t('list.filter.all')}</option>
        {STATES.map((s) => (
          <option key={s} value={s}>
            {t(`list.filter.${s}`)}
          </option>
        ))}
      </TextField>
      {notice && (
        <Alert severity={notice.severity} role={notice.severity === 'error' ? 'alert' : 'status'} sx={{ mt: 2 }}>
          {notice.text}
        </Alert>
      )}
      {failure && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {query.data &&
        (query.data.length === 0 ? (
          <Typography sx={{ mt: 1 }}>{t('list.empty')}</Typography>
        ) : (
          <Stack component="ul" aria-label={t('list.listLabel')} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
            {query.data.map((invitation) => {
              const open = invitation.state === 'pending' || invitation.state === 'expired';
              const roles = invitation.teamRoles.map((r) => t('list.teamRole', { team: r.teamName, role: ti(`teamRole.${r.role}`) })).join(', ');
              const delivery = invitation.deliveryState && known(['queued', 'sent', 'failed'] as const, invitation.deliveryState) ? invitation.deliveryState : undefined;
              return (
                <li key={invitation.id}>
                  <Stack spacing={0.5}>
                    <Typography component="span" sx={{ fontWeight: 600 }}>
                      {invitation.address}
                    </Typography>
                    <span>{t('list.accountName', { value: invitation.accountName })}</span>
                    <span>{t('list.stateLabel', { value: known(STATES, invitation.state) ? t(`list.state.${invitation.state}`) : invitation.state })}</span>
                    <span>{t('list.rolesLabel', { value: roles || t('list.noRoles') })}</span>
                    <span>{t('list.invitedBy', { name: invitation.invitedBy.accountName })}</span>
                    <span>{t('list.created', { value: when(invitation.createdAt) })}</span>
                    {invitation.state === 'pending' && invitation.linkExpiresAt && <span>{t('list.linkExpires', { value: when(invitation.linkExpiresAt) })}</span>}
                    {invitation.acceptedAt && <span>{t('list.accepted', { value: when(invitation.acceptedAt) })}</span>}
                    {invitation.revokedAt && <span>{t('list.revoked', { value: when(invitation.revokedAt) })}</span>}
                    {open && delivery && <span>{t('list.deliveryLabel', { value: t(`list.delivery.${delivery}`) })}</span>}
                    {invitation.acceptedMemberId && (
                      <Link to={`/members/${invitation.acceptedMemberId}`}>{t('list.acceptedMember', { name: invitation.accountName })}</Link>
                    )}
                    {open && (
                      <Stack direction="row" spacing={1}>
                        <Button size="small" disabled={busy} onClick={() => run(invitation, 'resend')}>
                          {t('list.resend', { address: invitation.address })}
                        </Button>
                        <Button size="small" color="error" disabled={busy} onClick={() => setRevoking(invitation)}>
                          {t('list.revoke', { address: invitation.address })}
                        </Button>
                      </Stack>
                    )}
                  </Stack>
                </li>
              );
            })}
          </Stack>
        ))}
      <Dialog open={Boolean(revoking)} onClose={() => setRevoking(undefined)} aria-labelledby="revoke-invitation-title">
        <DialogTitle id="revoke-invitation-title">{t('list.revokeTitle')}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('list.revokeText', { address: revoking?.address ?? '' })}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setRevoking(undefined)}>{t('list.cancel')}</Button>
          <Button variant="contained" color="error" disabled={busy} onClick={() => revoking && run(revoking, 'revoke')}>
            {t('list.revokeConfirm')}
          </Button>
        </DialogActions>
      </Dialog>
    </section>
  );
}
