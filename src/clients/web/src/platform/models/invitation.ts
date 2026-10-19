import type { TeamRoleName } from './session';

export interface InvitationTeamRole {
  teamId: string;
  role: TeamRoleName;
}

export interface Invitation {
  id: string;
  state: string;
  address: string;
  accountName: string;
  teamRoles: InvitationTeamRole[];
  version: number;
}

export interface InvitationCheck {
  clubName: string;
  accountName: string;
}
