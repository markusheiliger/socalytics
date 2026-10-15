import type { SessionMember } from '../platform/models/session';

export const isClubAdmin = (member: SessionMember) => member.clubRoles.includes('ClubAdmin');

export const isRegistrar = (member: SessionMember) => member.clubRoles.includes('Registrar');

export const canPairManagers = (member: SessionMember) => isClubAdmin(member) || isRegistrar(member);

export const hasAnyTeamRole = (member: SessionMember) => member.teamRoles.length > 0;

export const canViewTeam = (member: SessionMember, teamId: string) =>
  isClubAdmin(member) || member.teamRoles.some((r) => r.teamId === teamId);

export const canViewTeams = (member: SessionMember) => isClubAdmin(member) || hasAnyTeamRole(member);

/** Club Admins and Coaches of the team edit its matches and recordings; the platform stays authoritative. */
export const canEditTeamMatches = (member: SessionMember, teamId: string) =>
  isClubAdmin(member) || member.teamRoles.some((r) => r.teamId === teamId && r.role === 'Coach');
