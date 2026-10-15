import type { SessionMember } from '../platform/models/session';
import { canPairManagers, canViewTeams, isClubAdmin, isRegistrar } from '../session/roles';

export interface NavItem {
  to: string;
  labelKey: 'home' | 'club' | 'seasons' | 'teams' | 'members' | 'analystManagers' | 'myRequests' | 'pairManager' | 'account';
  allow: (member: SessionMember) => boolean;
}

/** The routes of contracts/routes-and-roles.md that appear in navigation, in display order. */
export const navItems: NavItem[] = [
  { to: '/', labelKey: 'home', allow: () => true },
  { to: '/club', labelKey: 'club', allow: isClubAdmin },
  { to: '/seasons', labelKey: 'seasons', allow: isClubAdmin },
  { to: '/teams', labelKey: 'teams', allow: canViewTeams },
  { to: '/members', labelKey: 'members', allow: isClubAdmin },
  { to: '/analyst-managers', labelKey: 'analystManagers', allow: isClubAdmin },
  { to: '/analyst-managers/requests', labelKey: 'myRequests', allow: (m) => isRegistrar(m) && !isClubAdmin(m) },
  { to: '/analyst-managers/pair', labelKey: 'pairManager', allow: canPairManagers },
  { to: '/account', labelKey: 'account', allow: () => true },
];

export const allowedNavItems = (member: SessionMember) => navItems.filter((item) => item.allow(member));
