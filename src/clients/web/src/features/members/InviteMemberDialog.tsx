import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createInvitationAdapter, type InvitationResult } from '../../platform/adapters/invitationAdapter';
import { createTeamAdapter } from '../../platform/adapters/teamAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { Invitation } from '../../platform/models/invitation';
import type { TeamRoleName } from '../../platform/models/session';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';
import { TEAMS_KEY } from '../teams/TeamsPage';

const TEAM_ROLES: TeamRoleName[] = ['Coach', 'Viewer'];

interface Row {
  key: number;
  teamId: string;
  role: TeamRoleName;
}

/** Club Admin form that invites a person by email address with a reserved account name and team roles. */
export function InviteMemberDialog({
  open,
  onClose,
  onInvited,
}: {
  open: boolean;
  onClose: () => void;
  onInvited: (invitation: Invitation) => void;
}) {
  const { t } = useTranslation('invitations');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const adapter = useMemo(() => createInvitationAdapter(platform), [platform]);
  const teamAdapter = useMemo(() => createTeamAdapter(platform), [platform]);
  const [address, setAddress] = useState('');
  const [accountName, setAccountName] = useState('');
  const [rows, setRows] = useState<Row[]>([]);
  const [nextKey, setNextKey] = useState(1);
  const [refusal, setRefusal] = useState<Exclude<InvitationResult, { ok: true }>>();

  const teams = useQuery({
    queryKey: [...TEAMS_KEY, 'mine'],
    queryFn: () => teamAdapter.listMine(),
    retry: false,
    enabled: open,
  });
  const assignable = teams.data?.filter((x) => x.seasonState !== 'archived') ?? [];

  const invite = useMutation({
    mutationFn: () =>
      adapter.createInvitation(
        address.trim(),
        accountName.trim(),
        rows.map((r) => ({ teamId: r.teamId, role: r.role })),
      ),
    onSuccess: (result) => {
      if (result.ok) {
        setRefusal(undefined);
        setAddress('');
        setAccountName('');
        setRows([]);
        onInvited(result.value);
      } else {
        setRefusal(result);
      }
    },
  });

  const failure = invite.error ? toPlatformFailure(invite.error) : undefined;
  const fieldErrors = refusal && !refusal.ok && refusal.kind === 'fieldErrors' ? refusal.fieldErrors : [];
  const fieldMessage = (match: (field: string) => boolean) => {
    const error = fieldErrors.find((e) => match(e.field));
    if (!error) return undefined;
    return ['required', 'too-long'].includes(error.code) ? t(`invite.fieldError.${error.code}`) : t('invite.fieldError.other');
  };
  const rowField = (index: number, name: 'teamId' | 'role') => (field: string) => field.toLowerCase() === `teamroles[${index}].${name}`.toLowerCase();
  const isUnhandled = refusal && !refusal.ok && refusal.kind === 'fieldErrors' && fieldErrors.length === 0;

  const addRow = () => {
    const team = assignable[0];
    if (!team) return;
    setRows((current) => [...current, { key: nextKey, teamId: team.id, role: 'Coach' }]);
    setNextKey(nextKey + 1);
  };
  const updateRow = (key: number, change: Partial<Row>) => setRows((current) => current.map((r) => (r.key === key ? { ...r, ...change } : r)));

  const close = () => {
    setRefusal(undefined);
    invite.reset();
    onClose();
  };
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setRefusal(undefined);
    invite.mutate();
  };

  const conflictMessage = (() => {
    if (!refusal || refusal.ok) return undefined;
    switch (refusal.kind) {
      case 'emailAddressInUse':
        return t('invite.emailAddressInUse');
      case 'accountNameTaken':
        return t('invite.accountNameTaken');
      case 'rateLimited':
        return refusal.retryAfterSeconds ? t('invite.rateLimitedWait', { seconds: refusal.retryAfterSeconds }) : t('invite.rateLimited');
      case 'invalid':
        return tc('failure.validation');
      default:
        return isUnhandled ? tc('failure.validation') : undefined;
    }
  })();

  const addressError =
    fieldMessage((f) => f.toLowerCase() === 'address') ??
    (refusal && !refusal.ok && refusal.kind === 'emailAddressInUse' ? t('invite.emailAddressInUse') : undefined);
  const nameError =
    fieldMessage((f) => f.toLowerCase() === 'accountname') ??
    (refusal && !refusal.ok && refusal.kind === 'accountNameTaken' ? t('invite.accountNameTaken') : undefined);
  const showSummary = conflictMessage && !addressError && !nameError;

  return (
    <Dialog open={open} onClose={close} aria-labelledby="invite-title" fullWidth maxWidth="sm">
      <Stack component="form" onSubmit={onSubmit} noValidate>
        <DialogTitle id="invite-title">{t('invite.title')}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            <Typography>{t('invite.intro')}</Typography>
            {failure && <FailureAlert failure={failure} />}
            {showSummary && (
              <Alert severity="error" role="alert">
                {conflictMessage}
              </Alert>
            )}
            <TextField
              label={t('invite.address')}
              name="address"
              type="email"
              value={address}
              onChange={(e) => setAddress(e.target.value)}
              error={Boolean(addressError)}
              helperText={addressError}
              autoComplete="off"
              required
            />
            <TextField
              label={t('invite.accountName')}
              name="accountName"
              value={accountName}
              onChange={(e) => setAccountName(e.target.value)}
              error={Boolean(nameError)}
              helperText={nameError}
              autoComplete="off"
              required
            />
            <Typography variant="h3" sx={{ fontSize: '1.1rem' }}>
              {t('invite.teamRolesTitle')}
            </Typography>
            {rows.length === 0 && <Typography>{teams.isSuccess && assignable.length === 0 ? t('invite.noTeams') : t('invite.noRows')}</Typography>}
            {rows.map((row, index) => {
              const teamError = fieldMessage(rowField(index, 'teamId'));
              const roleError = fieldMessage(rowField(index, 'role'));
              return (
                <Stack
                  key={row.key}
                  direction="row"
                  spacing={2}
                  role="group"
                  aria-label={t('invite.teamRolesTitle')}
                  sx={{ alignItems: 'flex-start' }}
                >
                  <TextField
                    select
                    label={t('invite.team')}
                    value={row.teamId}
                    onChange={(e) => updateRow(row.key, { teamId: e.target.value })}
                    error={Boolean(teamError)}
                    helperText={teamError}
                    slotProps={{ select: { native: true } }}
                  >
                    {assignable.map((x) => (
                      <option key={x.id} value={x.id}>
                        {x.name}
                      </option>
                    ))}
                  </TextField>
                  <TextField
                    select
                    label={t('invite.role')}
                    value={row.role}
                    onChange={(e) =>
                      updateRow(row.key, {
                        role: e.target.value as TeamRoleName,
                      })
                    }
                    error={Boolean(roleError)}
                    helperText={roleError}
                    slotProps={{ select: { native: true } }}
                  >
                    {TEAM_ROLES.map((r) => (
                      <option key={r} value={r}>
                        {t(`invite.teamRole.${r}`)}
                      </option>
                    ))}
                  </TextField>
                  <Button onClick={() => setRows((current) => current.filter((r) => r.key !== row.key))}>
                    {t('invite.removeRow', { number: index + 1 })}
                  </Button>
                </Stack>
              );
            })}
            <div>
              <Button onClick={addRow} disabled={assignable.length === 0}>
                {t('invite.addRow')}
              </Button>
            </div>
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={close}>{t('invite.cancel')}</Button>
          <Button type="submit" variant="contained" disabled={invite.isPending || isUnreachable || !address.trim() || !accountName.trim()}>
            {invite.isPending ? t('invite.sending') : t('invite.send')}
          </Button>
        </DialogActions>
      </Stack>
    </Dialog>
  );
}
