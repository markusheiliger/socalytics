import type { Page, Route } from '@playwright/test';
import type {
  AnalystManagerRegistrationDto,
  AnalystManagerRegistrationPageDto,
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

export type SimulatedRole = 'ClubAdmin' | 'Registrar' | 'Coach' | 'Viewer';

export const ANTIFORGERY_TOKEN = 'simulated-antiforgery-token-7f3a9c';
export const SIMULATED_PASSWORD = 'Simulated-Password-1!';

const ids = {
  club: '00000000-0000-4000-8000-000000000001',
  season: '00000000-0000-4000-8000-000000000002',
  team: '00000000-0000-4000-8000-000000000003',
  session: '00000000-0000-4000-8000-000000000004',
  admin: '00000000-0000-4000-8000-000000000011',
  coach: '00000000-0000-4000-8000-000000000012',
  registrar: '00000000-0000-4000-8000-000000000014',
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
  Registrar: {
    id: ids.registrar,
    accountName: 'registrar',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: ['Registrar'],
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

  /** Registrations the simulated platform knows, in list order; tests may add, replace, or clear them. */
  registrations: AnalystManagerRegistrationDto[] = [];
  /** Pairing codes (as typed, upper-case, with dashes) the platform accepts, mapped to the registration they pair. */
  readonly pairingCodes = new Map<string, string>();
  /** When set, every code submission answers 429 with this `Retry-After` in seconds. */
  rateLimitSubmissionsFor: number | null = null;

  /** Adds a registration, and optionally a pairing code that submits it. */
  addRegistration(registration: AnalystManagerRegistrationDto, pairingCode?: string): AnalystManagerRegistrationDto {
    this.registrations.push(registration);
    if (pairingCode) this.pairingCodes.set(pairingCode, String(registration.registrationId));
    return registration;
  }

  private async handleRegistrations(
    route: Route,
    method: string,
    path: string,
    role: SimulatedRole,
    json: (status: number, body: unknown) => Promise<void>,
  ): Promise<void> {
    const request = route.request();
    const isAdmin = role === 'ClubAdmin';
    if (!isAdmin && role !== 'Registrar') return json(403, problem(403, 'Forbidden'));
    const self = String(members[role].id);
    const visible = (r: AnalystManagerRegistrationDto) => isAdmin || String(r.submittedBy) === self;
    // Status and connectivity belong to the Club Admin fleet view only.
    const shape = (r: AnalystManagerRegistrationDto): AnalystManagerRegistrationDto => {
      if (isAdmin) return r;
      const { status: _status, connectivity: _connectivity, ...rest } = r;
      return rest as AnalystManagerRegistrationDto;
    };
    const entity = (r: AnalystManagerRegistrationDto, status = 200) =>
      route.fulfill({
        status,
        contentType: 'application/json',
        headers: { ETag: `"${r.version}"` },
        body: JSON.stringify(shape(r)),
      });
    let body: Record<string, unknown> = {};
    try {
      body = (request.postDataJSON() ?? {}) as Record<string, unknown>;
    } catch {
      body = {};
    }

    if (path === '/analyst-manager-registrations') {
      if (method === 'POST') {
        if (this.rateLimitSubmissionsFor !== null) {
          return route.fulfill({
            status: 429,
            contentType: 'application/problem+json',
            headers: { 'Retry-After': String(this.rateLimitSubmissionsFor) },
            body: JSON.stringify(problem(429, 'Too many requests', { code: 'analyst-manager-rate-limited' })),
          });
        }
        const normalize = (value: unknown) => String(value ?? '').replace(/[^0-9a-z]/gi, '').toUpperCase();
        const code = [...this.pairingCodes.entries()].find(([c]) => normalize(c) === normalize(body.pairingCode));
        const found = code && this.registrations.find((r) => String(r.registrationId) === code[1]);
        if (
          !found ||
          found.state !== 'pairing' ||
          normalize(found.deviceFingerprint) !== normalize(body.deviceFingerprint)
        ) {
          return json(400, problem(400, 'Pairing code invalid', { code: 'analyst-manager-pairing-code-invalid' }));
        }
        Object.assign(found, {
          state: 'pending_approval',
          submittedBy: self,
          submittedAt: now,
          version: Number(found.version) + 1,
        });
        this.pairingCodes.delete(code[0]);
        return entity(found, 201);
      }
      if (method === 'GET') {
        const state = new URL(request.url()).searchParams.get('state');
        const items = this.registrations.filter((r) => visible(r) && (!state || r.state === state)).map(shape);
        return json(200, { items, continuationToken: null } satisfies AnalystManagerRegistrationPageDto);
      }
      return json(404, problem(404, 'Not found'));
    }

    const match = /^\/analyst-manager-registrations\/([^/]+)(?:\/(approval|rejection|revocation))?$/.exec(path);
    const target = match && this.registrations.find((r) => String(r.registrationId) === match[1]);
    if (!match || !target || !visible(target)) return json(404, problem(404, 'Not found'));
    const action = match[2];
    if (!action && method === 'GET') return entity(target);
    if (!action || method !== 'POST') return json(404, problem(404, 'Not found'));
    if (!isAdmin) return json(403, problem(403, 'Forbidden'));

    const required = action === 'approval' || action === 'rejection' ? 'pending_approval' : 'active';
    if (action !== 'approval') {
      const reason = typeof body.reason === 'string' ? body.reason.trim() : '';
      if (reason.length < 1 || reason.length > 500) {
        return json(400, problem(400, 'Validation failed', { errors: [{ field: 'reason', code: 'invalid' }] }));
      }
    }
    if (target.state !== required) {
      return json(409, problem(409, 'Invalid state transition', { code: 'invalid-state-transition' }));
    }
    if (
      action !== 'revocation' &&
      target.approvalExpiresAt &&
      new Date(target.approvalExpiresAt).getTime() < Date.now()
    ) {
      return json(409, problem(409, 'Registration expired', { code: 'analyst-manager-registration-expired' }));
    }
    const decided = { decidedBy: self, decidedAt: new Date(), version: Number(target.version) + 1 };
    if (action === 'approval') Object.assign(target, decided, { state: 'approved' });
    if (action === 'rejection') {
      Object.assign(target, decided, { state: 'rejected', decisionReason: body.reason, endedAt: decided.decidedAt });
    }
    if (action === 'revocation') {
      Object.assign(target, {
        state: 'revoked',
        revokedBy: self,
        endedAt: decided.decidedAt,
        endReason: body.reason,
        connectivity: 'not-applicable',
        version: decided.version,
      });
    }
    return entity(target);
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
    if (path === '/me' && method === 'GET') {
      return json(200, { ...members[role], passwordChangeRequired });
    }
    if (path === '/me/password' && method === 'POST') {
      this.current = { role, passwordChangeRequired: false };
      return route.fulfill({ status: 204 });
    }
    if (passwordChangeRequired) {
      return json(403, problem(403, 'Password change required', { code: 'password_change_required' }));
    }
    if (path.startsWith('/analyst-manager-registrations')) {
      return this.handleRegistrations(route, method, path, role, json);
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
          items: [members.ClubAdmin, members.Coach, members.Viewer].map(memberListEntry),
          continuationToken: null,
        } satisfies MemberPageDto);
      }
    }
    return json(404, problem(404, 'Not found'));
  }
}
