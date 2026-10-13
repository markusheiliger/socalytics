import type { Page, Route } from '@playwright/test';
import type {
  ClubDto,
  CurrentMemberDto,
  MemberDto,
  MemberPageDto,
  SeasonDto,
  SeasonPageDto,
  SessionInfoDto,
  TeamDto,
  TeamPageDto,
} from '../../../src/generated/platform-api/models';

export type SimulatedRole = 'ClubAdmin' | 'Coach' | 'Viewer';

export const ANTIFORGERY_TOKEN = 'simulated-antiforgery-token-7f3a9c';
export const SIMULATED_PASSWORD = 'Simulated-Password-1!';

const ids = {
  club: '00000000-0000-4000-8000-000000000001',
  season: '00000000-0000-4000-8000-000000000002',
  team: '00000000-0000-4000-8000-000000000003',
  session: '00000000-0000-4000-8000-000000000004',
  admin: '00000000-0000-4000-8000-000000000011',
  coach: '00000000-0000-4000-8000-000000000012',
  viewer: '00000000-0000-4000-8000-000000000013',
};

const now = new Date('2026-10-12T12:00:00Z');
const later = (hours: number) => new Date(now.getTime() + hours * 3_600_000);

export const club: ClubDto = { id: ids.club, displayName: 'Simulated Club', version: 1 };

export const season: SeasonDto = {
  id: ids.season,
  name: '2026/27',
  state: 'Active',
  createdAt: now,
  activatedAt: now,
  archivedAt: null,
  version: 1,
};

export const team: TeamDto = {
  id: ids.team,
  seasonId: ids.season,
  seasonState: 'Active',
  name: 'First Team',
  createdAt: now,
  version: 1,
};

const members: Record<SimulatedRole, CurrentMemberDto> = {
  ClubAdmin: {
    id: ids.admin,
    accountName: 'admin',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: ['ClubAdmin'],
    teamRoles: [],
  },
  Coach: {
    id: ids.coach,
    accountName: 'coach',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: [],
    teamRoles: [{ teamId: ids.team, teamName: team.name, seasonId: ids.season, role: 'Coach' }],
  },
  Viewer: {
    id: ids.viewer,
    accountName: 'viewer',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: [],
    teamRoles: [{ teamId: ids.team, teamName: team.name, seasonId: ids.season, role: 'Viewer' }],
  },
};

export function sessionFor(role: SimulatedRole, passwordChangeRequired = false): SessionInfoDto {
  return {
    sessionId: ids.session,
    idleExpiresAt: later(1),
    absoluteExpiresAt: later(8),
    passwordChangeRequired,
    antiforgeryToken: ANTIFORGERY_TOKEN,
    member: { ...members[role], passwordChangeRequired },
  };
}

function memberListEntry(member: CurrentMemberDto): MemberDto {
  return {
    id: member.id,
    accountName: member.accountName,
    membershipStatus: member.membershipStatus,
    passwordSet: true,
    lockedOut: false,
    lockoutEndsAt: null,
    twoFactorEnabled: false,
    clubRoles: member.clubRoles,
    teamRoles: (member.teamRoles ?? []).map((r) => ({ teamId: r.teamId, role: r.role })),
    createdAt: now,
    version: 1,
  };
}

export function problem(status: number, title: string, extra: Record<string, unknown> = {}) {
  return { type: 'about:blank', title, status, ...extra };
}

type Handler = (route: Route, body: unknown) => Promise<void> | void;

/** Answers `/api/v1/**` with typed fixtures. Tests may override any route with `on`. */
export class SimulatedApi {
  private current: { role: SimulatedRole; passwordChangeRequired: boolean } | null = null;
  private readonly overrides = new Map<string, Handler>();
  readonly requests: { method: string; path: string; headers: Record<string, string> }[] = [];

  constructor(private readonly page: Page) {}

  async install(): Promise<void> {
    await this.page.route('**/api/v1/**', (route) => this.handle(route));
  }

  /** Subsequent requests are answered by the signed-in state; sign-in succeeds for this role. */
  signInAs(role: SimulatedRole, options: { passwordChangeRequired?: boolean } = {}): void {
    this.current = { role, passwordChangeRequired: options.passwordChangeRequired ?? false };
  }

  /** Simulates a session ended on the platform, for example by a Club Admin: every request answers 401. */
  endSession(): void {
    this.current = null;
  }

  /** Overrides one route, for example `on('POST', '/session', ...)`. */
  on(method: string, path: string, handler: Handler): void {
    this.overrides.set(`${method.toUpperCase()} ${path}`, handler);
  }

  private async handle(route: Route): Promise<void> {
    const request = route.request();
    const path = new URL(request.url()).pathname.replace(/^\/api\/v1/, '');
    const method = request.method();
    this.requests.push({ method, path, headers: request.headers() });

    const override = this.overrides.get(`${method} ${path}`);
    if (override) {
      let body: unknown = null;
      try {
        body = request.postDataJSON();
      } catch {
        body = null;
      }
      await override(route, body);
      return;
    }

    const json = (status: number, body: unknown) =>
      route.fulfill({
        status,
        contentType: status >= 400 ? 'application/problem+json' : 'application/json',
        body: JSON.stringify(body),
      });
    const unauthorized = () => json(401, problem(401, 'Unauthorized'));

    if (method === 'POST' && path === '/session') {
      if (!this.current) {
        return json(401, problem(401, 'Invalid credentials'));
      }
      return json(200, sessionFor(this.current.role, this.current.passwordChangeRequired));
    }
    if (method === 'POST' && path === '/credentials/redeem') {
      return route.fulfill({ status: 204 });
    }
    if (!this.current) {
      return unauthorized();
    }
    const { role, passwordChangeRequired } = this.current;

    if (path === '/session' && method === 'GET') {
      return json(200, sessionFor(role, passwordChangeRequired));
    }
    if (path === '/session' && method === 'DELETE') {
      this.current = null;
      return route.fulfill({ status: 204 });
    }
    if (path === '/me/password' && method === 'POST') {
      this.current = { role, passwordChangeRequired: false };
      return route.fulfill({ status: 204 });
    }
    if (passwordChangeRequired) {
      return json(403, problem(403, 'Password change required', { code: 'password_change_required' }));
    }
    if (method === 'GET') {
      if (path === '/club') return json(200, club);
      if (path === '/seasons') {
        return json(200, { items: [season], continuationToken: null } satisfies SeasonPageDto);
      }
      if (path === '/teams') {
        return json(200, { items: [team], continuationToken: null } satisfies TeamPageDto);
      }
      if (path === '/members') {
        if (role !== 'ClubAdmin') return json(403, problem(403, 'Forbidden'));
        return json(200, {
          items: Object.values(members).map(memberListEntry),
          continuationToken: null,
        } satisfies MemberPageDto);
      }
    }
    return json(404, problem(404, 'Not found'));
  }
}
