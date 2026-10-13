export type ClubRole = 'ClubAdmin' | 'Registrar';
export type TeamRoleName = 'Coach' | 'Viewer';

export interface TeamRole {
  teamId: string;
  teamName: string;
  seasonId: string;
  role: TeamRoleName;
}

export interface SessionMember {
  id: string;
  accountName: string;
  membershipStatus: string;
  clubRoles: ClubRole[];
  teamRoles: TeamRole[];
}

export interface Session {
  sessionId: string;
  idleExpiresAt: Date;
  absoluteExpiresAt: Date;
  /** Kept in memory only; sent as `X-CSRF-Token` on state-changing requests. */
  antiforgeryToken: string;
  passwordChangeRequired: boolean;
  member: SessionMember;
}
