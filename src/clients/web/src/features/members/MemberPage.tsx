import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createMemberAdapter, type MemberAdapter } from '../../platform/adapters/memberAdapter';
import { createTeamAdapter } from '../../platform/adapters/teamAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { Member } from '../../platform/models/member';
import type { CredentialCode } from '../../platform/models/member';
import type { ClubRole, TeamRoleName } from '../../platform/models/session';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';
import { TEAMS_KEY } from '../teams/TeamsPage';
import { CredentialDialog } from './CredentialDialog';
import { MemberFailureAlert } from './MemberFailureAlert';
import { MEMBERS_KEY } from './MembersPage';

const CLUB_ROLES: ClubRole[] = ['ClubAdmin', 'Registrar'];
const TEAM_ROLES: TeamRoleName[] = ['Coach', 'Viewer'];

type Action =
  | { kind: 'assignClubRole'; role: ClubRole }
  | { kind: 'removeClubRole'; role: ClubRole }
  | { kind: 'assignTeamRole'; teamId: string; role: TeamRoleName }
  | { kind: 'removeTeamRole'; teamId: string }
  | { kind: 'deactivate' }
  | { kind: 'reactivate' }
  | { kind: 'unlock' }
  | { kind: 'endSessions' };

const NOTICES: Record<Action['kind'], string> = {
  assignClubRole: 'roleAssigned',
  assignTeamRole: 'roleAssigned',
  removeClubRole: 'roleRemoved',
  removeTeamRole: 'roleRemoved',
  deactivate: 'deactivated',
  reactivate: 'reactivated',
  unlock: 'unlocked',
  endSessions: 'sessionsEnded',
};

async function perform(adapter: MemberAdapter, id: string, action: Action): Promise<Member | undefined> {
  switch (action.kind) {
    case 'assignClubRole':
      return adapter.assignClubRole(id, action.role);
    case 'removeClubRole':
      return adapter.removeClubRole(id, action.role);
    case 'assignTeamRole':
      return adapter.assignTeamRole(id, action.teamId, action.role);
    case 'removeTeamRole':
      return adapter.removeTeamRole(id, action.teamId);
    case 'deactivate':
      return adapter.deactivate(id);
    case 'reactivate':
      return adapter.reactivate(id);
    case 'unlock':
      return adapter.unlock(id);
    case 'endSessions':
      await adapter.endSessions(id);
      return undefined;
  }
}

/** One member: roles, credential, deactivation, unlock, and ending sessions. */
export function MemberPage() {
  const { memberId = '' } = useParams();
  const { t } = useTranslation('members');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createMemberAdapter(platform), [platform]);
  const teamAdapter = useMemo(() => createTeamAdapter(platform), [platform]);
  const key = [...MEMBERS_KEY, memberId];
  const [notice, setNotice] = useState<string>();
  const [credential, setCredential] = useState<CredentialCode>();
  const [teamId, setTeamId] = useState('');
  const [teamRole, setTeamRole] = useState<TeamRoleName>('Coach');
  const query = useQuery({ queryKey: key, queryFn: () => adapter.get(memberId), retry: false });
  const teams = useQuery({ queryKey: [...TEAMS_KEY, 'mine'], queryFn: () => teamAdapter.listMine(), retry: false });
  const change = useMutation({
    mutationFn: (action: Action) => perform(adapter, memberId, action),
    onSuccess: (next, action) => {
      if (next) queryClient.setQueryData(key, next);
      else void queryClient.invalidateQueries({ queryKey: key });
      setNotice(NOTICES[action.kind]);
      return queryClient.invalidateQueries({ queryKey: MEMBERS_KEY, exact: true });
    },
  });
  const issue = useMutation({
    mutationFn: () => adapter.issueCredential(memberId),
    onSuccess: (result) => {
      setCredential(result);
      issue.reset();
    },
  });
  const failure = change.error ? toPlatformFailure(change.error) : issue.error ? toPlatformFailure(issue.error) : undefined;
  const member = query.data;
  const busy = change.isPending || issue.isPending || isUnreachable;
  const run = (action: Action) => {
    setNotice(undefined);
    issue.reset();
    change.mutate(action);
  };
  const teamName = (id: string) => teams.data?.find((x) => x.id === id)?.name ?? t('members.unknownTeam');
  const assignable = teams.data?.filter((x) => x.seasonState !== 'archived') ?? [];
  const chosenTeam = teamId || assignable[0]?.id || '';

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{member?.accountName ?? t('members.memberTitle')}</Typography>
      {query.error && <FailureAlert failure={toPlatformFailure(query.error)} onReload={() => void query.refetch()} />}
      {failure && <MemberFailureAlert failure={failure} />}
      {notice && (
        <Alert severity="success" role="status">
          {t(`members.notice.${notice}`)}
        </Alert>
      )}
      {member && (
        <>
          <Typography sx={{ mt: 1 }}>{t('members.statusLabel', { status: t(`members.status.${member.status}`) })}</Typography>
          <Typography>
            {t('members.passwordSetLabel', { value: member.passwordSet ? t('members.passwordSet') : t('members.passwordNotSet') })}
          </Typography>
          {member.lockedOut && <Typography>{t('members.lockedOut')}</Typography>}

          <Typography variant="h3" sx={{ mt: 3 }}>
            {t('members.clubRolesTitle')}
          </Typography>
          {member.clubRoles.length === 0 && <Typography>{t('members.noClubRoles')}</Typography>}
          <Stack direction="row" spacing={1} useFlexGap flexWrap="wrap" sx={{ mt: 1 }}>
            {CLUB_ROLES.map((role) => {
              const has = member.clubRoles.includes(role);
              const label = t(`members.clubRole.${role}`);
              return (
                <Button
                  key={role}
                  variant="outlined"
                  disabled={busy}
                  onClick={() => run({ kind: has ? 'removeClubRole' : 'assignClubRole', role })}
                >
                  {t(has ? 'members.removeClubRole' : 'members.assignClubRole', { role: label })}
                </Button>
              );
            })}
          </Stack>

          <Typography variant="h3" sx={{ mt: 3 }}>
            {t('members.teamRolesTitle')}
          </Typography>
          {member.teamRoles.length === 0 && <Typography>{t('members.noTeamRoles')}</Typography>}
          <Stack component="ul" spacing={1} sx={{ listStyle: 'none', m: 0, p: 0, mt: 1 }}>
            {member.teamRoles.map((r) => (
              <li key={r.teamId}>
                <Stack direction="row" spacing={2} alignItems="center">
                  <Typography>{t('members.teamRoleEntry', { team: teamName(r.teamId), role: t(`members.teamRole.${r.role}`) })}</Typography>
                  <Button
                    size="small"
                    variant="outlined"
                    disabled={busy}
                    onClick={() => run({ kind: 'removeTeamRole', teamId: r.teamId })}
                  >
                    {t('members.removeTeamRole', { role: t(`members.teamRole.${r.role}`), team: teamName(r.teamId) })}
                  </Button>
                </Stack>
              </li>
            ))}
          </Stack>
          {assignable.length === 0 ? (
            <Typography sx={{ mt: 1 }}>{t('members.noTeams')}</Typography>
          ) : (
            <Stack
              component="form"
              direction="row"
              spacing={2}
              sx={{ mt: 2 }}
              aria-label={t('members.assignTeamRoleTitle')}
              onSubmit={(e) => {
                e.preventDefault();
                run({ kind: 'assignTeamRole', teamId: chosenTeam, role: teamRole });
              }}
            >
              <TextField
                select
                label={t('members.team')}
                value={chosenTeam}
                onChange={(e) => setTeamId(e.target.value)}
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
                label={t('members.role')}
                value={teamRole}
                onChange={(e) => setTeamRole(e.target.value as TeamRoleName)}
                slotProps={{ select: { native: true } }}
              >
                {TEAM_ROLES.map((r) => (
                  <option key={r} value={r}>
                    {t(`members.teamRole.${r}`)}
                  </option>
                ))}
              </TextField>
              <Button type="submit" variant="contained" disabled={busy}>
                {t('members.assignTeamRole')}
              </Button>
            </Stack>
          )}

          <Typography variant="h3" sx={{ mt: 3 }}>
            {t('members.actionsTitle')}
          </Typography>
          <Stack direction="row" spacing={1} useFlexGap flexWrap="wrap" sx={{ mt: 1 }}>
            <Button variant="outlined" disabled={busy} onClick={() => issue.mutate()}>
              {t('members.issueCredential')}
            </Button>
            {member.status === 'active' ? (
              <Button variant="outlined" disabled={busy} onClick={() => run({ kind: 'deactivate' })}>
                {t('members.deactivate')}
              </Button>
            ) : (
              <Button variant="outlined" disabled={busy} onClick={() => run({ kind: 'reactivate' })}>
                {t('members.reactivate')}
              </Button>
            )}
            {member.lockedOut && (
              <Button variant="outlined" disabled={busy} onClick={() => run({ kind: 'unlock' })}>
                {t('members.unlock')}
              </Button>
            )}
            <Button variant="outlined" disabled={busy} onClick={() => run({ kind: 'endSessions' })}>
              {t('members.endSessions')}
            </Button>
          </Stack>
        </>
      )}
      <Button component={Link} to="/members" variant="outlined" sx={{ mt: 3 }}>
        {t('members.back')}
      </Button>
      <CredentialDialog credential={credential} accountName={member?.accountName ?? ''} onClose={() => setCredential(undefined)} />
    </Container>
  );
}
