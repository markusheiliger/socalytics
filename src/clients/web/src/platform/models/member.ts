import type { ClubRole, TeamRoleName } from './session';

export type MembershipStatus = 'active' | 'deactivated';

export interface MemberTeamRole {
  teamId: string;
  role: TeamRoleName;
}

export interface Member {
  id: string;
  accountName: string;
  status: MembershipStatus;
  passwordSet: boolean;
  lockedOut: boolean;
  lockoutEndsAt?: Date;
  twoFactorEnabled: boolean;
  clubRoles: ClubRole[];
  teamRoles: MemberTeamRole[];
  createdAt: Date;
  version: number;
}

/** A single-use code to hand over; the Web UI shows it once and never stores it. */
export interface CredentialCode {
  memberId: string;
  purpose: string;
  code: string;
  expiresAt: Date;
}
