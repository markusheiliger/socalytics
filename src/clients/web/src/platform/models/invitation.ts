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

export interface InvitationDetail {
  id: string;
  state: string;
  address: string;
  accountName: string;
  teamRoles: { teamId: string; teamName: string; role: TeamRoleName }[];
  invitedBy: { id: string; accountName: string };
  createdAt: Date;
  linkExpiresAt?: Date;
  deliveryState?: string;
  resendCount: number;
  revokedAt?: Date;
  acceptedAt?: Date;
  acceptedMemberId?: string;
  version: number;
}
