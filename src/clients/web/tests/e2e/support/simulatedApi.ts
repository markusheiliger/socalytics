import { createHash } from 'node:crypto';
import type { Page, Route } from '@playwright/test';
import type {
  AnalysisRunSummaryDto,
  AnalystManagerRegistrationDto,
  AnalystManagerRegistrationPageDto,
  ClubDto,
  MatchDto,
  RecordingSetVersionDto,
  RecordingVersionDto,
  TimelineMappingDto,
  UploadSessionDto,
  CurrentMemberDto,
  MemberDto,
  MemberPageDto,
  SeasonDto,
  SeasonPageDto,
  SessionInfoDto,
  TeamDto,
  TeamPageDto,
} from '../../../src/generated/platform-api/models';

// Mirrors the platform's composite content digest over the declared part digests.
function compositeDigest(partSizeBytes: number, partDigests: string[]): string {
  const raw = Buffer.concat(partDigests.map((d) => Buffer.from(d.replace('sha-256:', ''), 'hex')));
  return `sha-256-parts:${partSizeBytes}:${partDigests.length}:${createHash('sha256').update(raw).digest('hex')}`;
}

/** Registry fixtures mirror the JSON of the 13 Registry operations (dates serialize through JSON). */
export interface RegistryImage {
  imageDigest: string;
  repository: string;
  manifestDigest: string;
  capability: { id: string; version: string; tier: string; executionScope: string };
  modelDigest: string;
  manifest: Record<string, unknown>;
  source: string;
  registeredAt: Date;
  registeredBy: string | null;
}

export interface RegistryProfile {
  profileId: string;
  version: number;
  profileDigest: string;
  imageDigest: string;
  state: 'draft' | 'approved' | 'retired';
  content: {
    capabilityId: string;
    capabilityVersion: string;
    modelDigest: string;
    runtimeConstraints: string[];
    accelerators: string[];
    resources: Record<string, string>;
    maxAttempts: number;
    leaseSeconds: number;
    staleTimeoutSeconds: number;
    executionTimeoutSeconds: number;
    videoPolicy: string;
    inputSchemaVersions: Record<string, string>;
    outputSchemaVersion: string;
  };
  createdAt: Date;
  createdBy: string;
  approvedAt?: Date;
  approvedBy?: string;
  retiredAt?: Date;
  retiredBy?: string;
  /** Sent as `ETag`; never part of the body. */
  rowVersion: number;
}

export interface RegistryWorkflow {
  workflowId: string;
  workflowVersion: string;
  definitionDigest: string;
  definition: {
    requestedCapabilities: string[];
    completionPolicy: string;
    segmentationPolicy: { version: number; durationSeconds: number; digest: string };
    nodes: { nodeKey: string; profileId: string; profileVersion: number; profileDigest: string }[];
    edges: {
      upstreamNodeKey: string;
      dependentNodeKey: string;
      kind: string;
      predicate?: { kind: string; capabilityId?: string; nodeKey?: string };
    }[];
  };
  createdAt: Date;
  createdBy: string | null;
}

export interface RegistryDefault {
  workflowId: string;
  workflowVersion: string;
  changedAt: Date;
  changedBy: string | null;
}

function imageRefusalStatus(code: string): number {
  if (code === 'reference-not-digest' || code === 'reference-not-allowed') return 400;
  if (code === 'image-not-found') return 404;
  if (code === 'registry-unavailable') return 503;
  if (code === 'digest-mismatch') return 502;
  return 422;
}

function profileDto({ rowVersion: _rowVersion, ...dto }: RegistryProfile) {
  return dto;
}

function digestOf(value: unknown): string {
  return `sha-256:${createHash('sha256').update(JSON.stringify(value)).digest('hex')}`;
}

export type SimulatedRole = 'ClubAdmin' | 'Registrar' | 'Coach' | 'Viewer';

export const ANTIFORGERY_TOKEN = 'simulated-antiforgery-token-7f3a9c';
/** Origin of the simulated identity provider's authorization endpoint; no request leaves the browser. */
export const SIMULATED_PROVIDER_ORIGIN = 'https://login.simulated.test';
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

/** Origin of the fake object store (tests/e2e/support/fakeObjectStorage.mjs); playwright.config.ts starts it on this port. */
export const FAKE_STORAGE_ORIGIN = 'http://127.0.0.1:4176';

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

export const match: MatchDto = {
  id: '00000000-0000-4000-8000-000000000021',
  teamId: ids.team,
  opponent: { name: 'Rivals FC' },
  kickoffAt: later(48),
  homeAway: 'home',
  competition: 'League',
  createdAt: now,
  version: 1,
};

export const analysisRun: AnalysisRunSummaryDto = {
  runId: '00000000-0000-4000-8000-000000000031',
  teamId: ids.team,
  matchId: match.id,
  recordingSetVersionId: null,
  workflowId: 'default',
  workflowVersion: '1',
  state: 'Queued',
  createdAt: now,
  runningAt: null,
  terminatedAt: null,
  failureReason: null,
  version: 1,
};

let nextId = 100;
const newId = () => `00000000-0000-4000-8000-${String(++nextId).padStart(12, '0')}`;

/** The base64 SHA-256 a grant requires: the declared part digest may be hex or prefixed hex. */
function checksumHeader(digest: string | undefined): string {
  const hex = (digest ?? '').replace(/^sha-?256[:=]/i, '');
  return /^[0-9a-f]{64}$/i.test(hex) ? Buffer.from(hex, 'hex').toString('base64') : (digest ?? '');
}

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
  /** Content type of every request body sent to `/api/v1`; SC-003 asserts that all of them are JSON. */
  readonly bodyContentTypes: string[] = [];
  readonly matches: MatchDto[] = [match];
  readonly uploadSessions: (UploadSessionDto & { partDigests: string[] })[] = [];
  readonly recordingVersions: RecordingVersionDto[] = [];
  readonly timelineMappings: TimelineMappingDto[] = [];
  readonly recordingSetVersions: RecordingSetVersionDto[] = [];
  readonly analysisRuns: AnalysisRunSummaryDto[] = [];

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

  /** Identity providers the simulated platform lists; empty means none is configured. */
  providers: { id: string; displayName: string }[] = [];
  /** When true, starting an external sign-in answers 503 `provider-unavailable`. */
  providerUnavailable = false;
  /** Bodies of the external sign-in starts the simulated platform received, in order. */
  readonly externalStarts: Record<string, unknown>[] = [];
  /** Link codes the simulated platform accepts; a start that carries any other code answers the generic 400. */
  readonly linkCodes = new Set<string>();

  /**
   * Simulates the provider and the platform callback: the browser navigating to the authorization URL
   * is redirected as the callback would, to the Web UI home (signed in as `role`) or to `?external=failed`.
   */
  async simulateProviderRedirect(outcome: 'success' | 'failed', role: SimulatedRole = 'ClubAdmin'): Promise<void> {
    await this.page.route(`${SIMULATED_PROVIDER_ORIGIN}/**`, (route) => {
      const target = new URL(outcome === 'success' ? '/' : '/sign-in?external=failed', this.page.url());
      if (outcome === 'success') this.signInAs(role);
      return route.fulfill({ status: 302, headers: { Location: target.toString() } });
    });
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

  /** Registered Analyst images (stored manifests included), in list order; tests may add, replace, or clear them. */
  registryImages: RegistryImage[] = [];
  /** Images the simulated image registry can serve; registering a reference to one of their digests registers it. */
  readonly registryCatalog = new Map<string, RegistryImage>();
  /** Scripted image refusals by reference (or digest); the first registration with that reference is refused. */
  readonly imageRefusals = new Map<string, { code: string; errors?: { field: string; code: string }[] }>();
  /** Profile versions with the row version the platform sends as `ETag`. */
  registryProfiles: RegistryProfile[] = [];
  registryWorkflows: RegistryWorkflow[] = [];
  /** The default workflow; `null` answers 404 and a first change needs no `If-Match`. */
  registryDefault: (RegistryDefault & { rowVersion: number }) | null = null;

  seedImage(image: RegistryImage): RegistryImage {
    this.registryImages.push(image);
    return image;
  }

  seedProfile(profile: RegistryProfile): RegistryProfile {
    this.registryProfiles.push(profile);
    return profile;
  }

  seedWorkflow(workflow: RegistryWorkflow): RegistryWorkflow {
    this.registryWorkflows.push(workflow);
    return workflow;
  }

  seedDefault(target: { workflowId: string; workflowVersion: string }, rowVersion = 1): void {
    this.registryDefault = {
      ...target,
      changedAt: now,
      changedBy: String(ids.admin),
      rowVersion,
    };
  }

  private async handleRegistry(
    route: Route,
    method: string,
    path: string,
    role: SimulatedRole,
    json: (status: number, body: unknown) => Promise<void>,
  ): Promise<void> {
    if (role !== 'ClubAdmin') return json(403, problem(403, 'Forbidden', { code: 'forbidden' }));
    const request = route.request();
    const url = new URL(request.url());
    const refuse = (status: number, code: string, errors?: { field: string; code: string }[]) =>
      json(status, problem(status, code, { code, ...(errors ? { errors } : {}) }));
    const respond = (status: number, body: unknown, rowVersion?: number) =>
      route.fulfill({
        status,
        contentType: 'application/json',
        headers: rowVersion === undefined ? {} : { ETag: `"${rowVersion}"` },
        body: JSON.stringify(body),
      });
    let body: Record<string, any> = {};
    try {
      body = (request.postDataJSON() ?? {}) as Record<string, any>;
    } catch {
      body = {};
    }
    // Returns null when the precondition holds, else the refusal.
    const precondition = (current: number): Promise<void> | null => {
      const header = request.headers()['if-match'];
      if (header === undefined) return refuse(428, 'version-required');
      return header.replace(/"/g, '') === String(current) ? null : refuse(412, 'version-mismatch');
    };
    const seg = path.split('/').map(decodeURIComponent);

    if (path === '/registry/analyst-images') {
      if (method === 'GET') {
        const capabilityId = url.searchParams.get('capabilityId');
        const items = this.registryImages
          .filter((i) => !capabilityId || i.capability.id === capabilityId)
          .map((i) => ({
            imageDigest: i.imageDigest,
            capabilityId: i.capability.id,
            capabilityVersion: i.capability.version,
            registeredAt: i.registeredAt,
          }));
        return respond(200, { items });
      }
      if (method !== 'POST') return json(404, problem(404, 'Not found'));
      const reference = typeof body.reference === 'string' ? body.reference.trim() : '';
      if (!reference) return refuse(400, 'validation-failed', [{ field: 'reference', code: 'required' }]);
      const digest = /@(sha256:[0-9a-f]{64})$/i.exec(reference)?.[1];
      const scripted = this.imageRefusals.get(reference) ?? (digest ? this.imageRefusals.get(`sha-256:${digest.slice(7)}`) : undefined);
      if (scripted) return refuse(imageRefusalStatus(scripted.code), scripted.code, scripted.errors);
      if (!digest) return refuse(400, 'reference-not-digest');
      const imageDigest = `sha-256:${digest.slice(7).toLowerCase()}`;
      const existing = this.registryImages.find((i) => i.imageDigest === imageDigest);
      if (existing) return respond(200, existing);
      const offered = this.registryCatalog.get(imageDigest);
      if (!offered) return refuse(404, 'image-not-found');
      const created = { ...offered, registeredAt: new Date(), registeredBy: String(ids.admin) };
      this.registryImages.push(created);
      return respond(201, created);
    }
    if (seg[2] === 'analyst-images' && seg.length === 4 && method === 'GET') {
      const found = this.registryImages.find((i) => i.imageDigest === seg[3]);
      return found ? respond(200, found) : refuse(404, 'not-found');
    }

    if (path === '/registry/analyst-profiles') {
      if (method === 'GET') {
        const state = url.searchParams.get('state');
        const capabilityId = url.searchParams.get('capabilityId');
        const items = this.registryProfiles
          .filter((p) => (!state || p.state === state) && (!capabilityId || p.content.capabilityId === capabilityId))
          .map(profileDto);
        return respond(200, { items });
      }
      if (method !== 'POST') return json(404, problem(404, 'Not found'));
      const content = (body.content ?? null) as RegistryProfile['content'] | null;
      if (!body.profileId || !body.imageDigest || !content) {
        return refuse(400, 'validation-failed', [{ field: 'profileId', code: 'required' }]);
      }
      const image = this.registryImages.find((i) => i.imageDigest === body.imageDigest);
      if (!image) return refuse(404, 'image-not-registered');
      const contradictions: { field: string; code: string }[] = [];
      if (content.capabilityId !== image.capability.id) contradictions.push({ field: 'capabilityId', code: 'differs' });
      if (content.capabilityVersion !== image.capability.version) {
        contradictions.push({ field: 'capabilityVersion', code: 'differs' });
      }
      if (content.modelDigest !== image.modelDigest) contradictions.push({ field: 'modelDigest', code: 'differs' });
      if (contradictions.length > 0) return refuse(422, 'profile-contradicts-declaration', contradictions);
      const sameContent = this.registryProfiles.find(
        (p) =>
          p.profileId === body.profileId &&
          p.imageDigest === body.imageDigest &&
          JSON.stringify(p.content) === JSON.stringify(content),
      );
      if (sameContent) {
        if (sameContent.state === 'retired') return refuse(409, 'profile-content-exists');
        return respond(200, profileDto(sameContent), sameContent.rowVersion);
      }
      const version =
        Math.max(0, ...this.registryProfiles.filter((p) => p.profileId === body.profileId).map((p) => p.version)) + 1;
      const profile: RegistryProfile = {
        profileId: String(body.profileId),
        version,
        profileDigest: digestOf({ profileId: body.profileId, version, imageDigest: body.imageDigest, content }),
        imageDigest: String(body.imageDigest),
        state: 'draft',
        content,
        createdAt: new Date(),
        createdBy: String(ids.admin),
        rowVersion: 1,
      };
      this.registryProfiles.push(profile);
      return respond(201, profileDto(profile), profile.rowVersion);
    }
    const profileMatch = /^\/registry\/analyst-profiles\/([^/]+)\/versions\/(\d+)(?:\/(approval|retirement))?$/.exec(path);
    if (profileMatch) {
      const profile = this.registryProfiles.find(
        (p) => p.profileId === decodeURIComponent(profileMatch[1]) && p.version === Number(profileMatch[2]),
      );
      if (!profile) return refuse(404, 'not-found');
      const action = profileMatch[3];
      if (!action) return method === 'GET' ? respond(200, profileDto(profile), profile.rowVersion) : json(404, problem(404, 'Not found'));
      if (method !== 'POST') return json(404, problem(404, 'Not found'));
      const failed = precondition(profile.rowVersion);
      if (failed) return failed;
      if (action === 'approval') {
        if (profile.state !== 'draft') return refuse(409, 'profile-not-draft');
        Object.assign(profile, { state: 'approved', approvedAt: new Date(), approvedBy: String(ids.admin) });
      } else {
        if (profile.state === 'retired') return refuse(409, 'profile-already-retired');
        const inDefault = this.registryDefault
          ? this.registryWorkflows
              .find(
                (w) =>
                  w.workflowId === this.registryDefault?.workflowId &&
                  w.workflowVersion === this.registryDefault?.workflowVersion,
              )
              ?.definition.nodes.some((n) => n.profileId === profile.profileId && n.profileVersion === profile.version)
          : false;
        if (inDefault) return refuse(409, 'profile-in-default-workflow');
        Object.assign(profile, { state: 'retired', retiredAt: new Date(), retiredBy: String(ids.admin) });
      }
      profile.rowVersion += 1;
      return respond(200, profileDto(profile), profile.rowVersion);
    }

    if (path === '/registry/workflows') {
      if (method === 'GET') {
        return respond(200, {
          items: this.registryWorkflows.map(({ workflowId, workflowVersion, definitionDigest, createdAt }) => ({
            workflowId,
            workflowVersion,
            definitionDigest,
            createdAt,
          })),
        });
      }
      if (method !== 'POST') return json(404, problem(404, 'Not found'));
      return this.defineRegistryWorkflow(body, refuse, respond);
    }
    const workflowMatch = /^\/registry\/workflows\/([^/]+)\/versions\/([^/]+)$/.exec(path);
    if (workflowMatch && method === 'GET') {
      const found = this.registryWorkflows.find(
        (w) => w.workflowId === decodeURIComponent(workflowMatch[1]) && w.workflowVersion === decodeURIComponent(workflowMatch[2]),
      );
      return found ? respond(200, found) : refuse(404, 'not-found');
    }

    if (path === '/registry/default-workflow') {
      if (method === 'GET') {
        if (!this.registryDefault) return refuse(404, 'not-found');
        const { rowVersion, ...dto } = this.registryDefault;
        return respond(200, dto, rowVersion);
      }
      if (method !== 'PUT') return json(404, problem(404, 'Not found'));
      if (!body.workflowId || !body.workflowVersion) {
        return refuse(400, 'validation-failed', [{ field: 'workflowId', code: 'required' }]);
      }
      if (this.registryDefault) {
        const failed = precondition(this.registryDefault.rowVersion);
        if (failed) return failed;
      }
      const target = this.registryWorkflows.find(
        (w) => w.workflowId === body.workflowId && w.workflowVersion === body.workflowVersion,
      );
      if (!target) return refuse(422, 'workflow-not-resolvable');
      this.registryDefault = {
        workflowId: target.workflowId,
        workflowVersion: target.workflowVersion,
        changedAt: new Date(),
        changedBy: String(ids.admin),
        rowVersion: (this.registryDefault?.rowVersion ?? 0) + 1,
      };
      const { rowVersion, ...dto } = this.registryDefault;
      return respond(200, dto, rowVersion);
    }
    return json(404, problem(404, 'Not found'));
  }

  private defineRegistryWorkflow(
    body: Record<string, any>,
    refuse: (status: number, code: string, errors?: { field: string; code: string }[]) => Promise<void>,
    respond: (status: number, body: unknown, rowVersion?: number) => Promise<void>,
  ): Promise<void> {
    const nodes = (Array.isArray(body.nodes) ? body.nodes : []) as { nodeKey: string; profileId: string; profileVersion: number }[];
    const edges = (Array.isArray(body.edges) ? body.edges : []) as RegistryWorkflow['definition']['edges'];
    const requested = (Array.isArray(body.requestedCapabilities) ? body.requestedCapabilities : []) as string[];
    if (!body.workflowId || !body.workflowVersion || nodes.length === 0) {
      return refuse(400, 'validation-failed', [{ field: 'workflowId', code: 'required' }]);
    }
    if (this.registryWorkflows.some((w) => w.workflowId === body.workflowId && w.workflowVersion === body.workflowVersion)) {
      return refuse(409, 'workflow-version-exists');
    }
    const violations: { field: string; code: string }[] = [];
    const resolved = nodes.map((n) => {
      const profile = this.registryProfiles.find((p) => p.profileId === n.profileId && p.version === n.profileVersion);
      if (!profile) violations.push({ field: `nodes.${n.nodeKey}`, code: 'profile-not-found' });
      else if (profile.state !== 'approved') violations.push({ field: `nodes.${n.nodeKey}`, code: 'profile-not-approved' });
      return { ...n, profileDigest: profile?.profileDigest ?? '', capabilityId: profile?.content.capabilityId };
    });
    if (body.completionPolicy !== 'all-requested-capabilities') {
      violations.push({ field: 'workflow', code: 'unknown-completion-policy' });
    }
    const keys = new Set(nodes.map((n) => n.nodeKey));
    if (edges.some((e) => !keys.has(e.upstreamNodeKey) || !keys.has(e.dependentNodeKey))) {
      violations.push({ field: 'workflow', code: 'invalid-graph' });
    }
    if (requested.some((c) => !resolved.some((n) => n.capabilityId === c))) {
      violations.push({ field: 'workflow', code: 'requested-capability-missing' });
    }
    if (violations.length > 0) return refuse(422, 'workflow-invalid', violations);
    const definition = {
      requestedCapabilities: requested,
      completionPolicy: String(body.completionPolicy),
      segmentationPolicy: body.segmentationPolicy,
      nodes: resolved.map(({ nodeKey, profileId, profileVersion, profileDigest }) => ({
        nodeKey,
        profileId,
        profileVersion,
        profileDigest,
      })),
      edges,
    };
    const workflow: RegistryWorkflow = {
      workflowId: String(body.workflowId),
      workflowVersion: String(body.workflowVersion),
      definitionDigest: digestOf(definition),
      definition,
      createdAt: new Date(),
      createdBy: String(ids.admin),
    };
    this.registryWorkflows.push(workflow);
    return respond(201, workflow);
  }

  /** When set, every simulated API response carries `SocAlytics-Minimum-Web-Version` with this value. */
  minimumWebVersion: string | null = null;

  private async handle(route: Route): Promise<void> {
    if (this.minimumWebVersion) {
      const version = this.minimumWebVersion;
      const fulfill = route.fulfill.bind(route);
      route.fulfill = (options) => fulfill({ ...options, headers: { ...options?.headers, 'SocAlytics-Minimum-Web-Version': version } });
    }
    const request = route.request();
    const path = new URL(request.url()).pathname.replace(/^\/api\/v1/, '');
    const method = request.method();
    this.requests.push({ method, path, headers: request.headers() });
    if (request.postDataBuffer()) this.bodyContentTypes.push(request.headers()['content-type'] ?? '');

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
    if (method === 'GET' && path === '/identity-providers') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: { 'Cache-Control': 'no-store' },
        body: JSON.stringify({ providers: this.providers }),
      });
    }
    if (method === 'POST' && path === '/session/external') {
      if (this.providerUnavailable) return json(503, problem(503, 'Provider unavailable', { code: 'provider-unavailable' }));
      const started = (route.request().postDataJSON?.() ?? {}) as Record<string, unknown>;
      this.externalStarts.push(started);
      if (typeof started.linkCredential === 'string' && !this.linkCodes.has(started.linkCredential)) {
        return json(400, problem(400, 'Sign-in failed', { code: 'sign-in-failed' }));
      }
      return json(200, { authorizationUrl: `${SIMULATED_PROVIDER_ORIGIN}/authorize?state=simulated` });
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
    if (path.startsWith('/registry/')) {
      return this.handleRegistry(route, method, path, role, json);
    }
    if (method === 'GET') {
      if (path === '/me/notification-preferences') {
        const items = [{ type: 'email-confirmation', enabled: true, accountMessage: true }];
        if (role === 'ClubAdmin') items.push({ type: 'registration-pending', enabled: true, accountMessage: false });
        return json(200, { items });
      }
      if (path === '/club') return json(200, club);
      if (path === '/seasons') {
        return json(200, { items: [season], continuationToken: null } satisfies SeasonPageDto);
      }
      if (path === '/teams') {
        return json(200, { items: [team], continuationToken: null } satisfies TeamPageDto);
      }
      const found = this.handleMatchReads(path, json);
      if (found) return found;
      if (path === '/members') {
        if (role !== 'ClubAdmin') return json(403, problem(403, 'Forbidden'));
        return json(200, {
          items: [members.ClubAdmin, members.Coach, members.Viewer].map(memberListEntry),
          continuationToken: null,
        } satisfies MemberPageDto);
      }
    }
    if (method === 'POST' || method === 'PUT') {
      if (role === 'Viewer') return json(403, problem(403, 'Forbidden'));
      const result = this.handleMatchWrites(method, path, request.postDataJSON?.() ?? null, json);
      if (result) return result;
    }
    return json(404, problem(404, 'Not found'));
  }

  private handleMatchReads(path: string, json: (status: number, body: unknown) => Promise<void>) {
    const matchId = /^\/matches\/([^/]+)/.exec(path)?.[1];
    if (/^\/teams\/[^/]+\/matches$/.test(path)) return json(200, { items: this.matches, continuationToken: null });
    if (!matchId) return null;
    const known = this.matches.find((m) => m.id === matchId);
    if (!known) return json(404, problem(404, 'Not found'));
    if (path === `/matches/${matchId}`) return json(200, known);
    if (path === `/matches/${matchId}/recording-lineage`) {
      return json(200, {
        matchId,
        teamId: known.teamId,
        recordingVersions: this.recordingVersions.filter((v) => v.matchId === matchId),
        timelineMappings: this.timelineMappings.filter((m) => m.matchId === matchId),
        recordingSetVersions: this.recordingSetVersions.filter((v) => v.matchId === matchId),
      });
    }
    if (path === `/matches/${matchId}/analysis-runs`) {
      return json(200, { items: this.analysisRuns.filter((r) => r.matchId === matchId), continuationToken: null });
    }
    const upload = /^\/matches\/[^/]+\/upload-sessions\/([^/]+)$/.exec(path)?.[1];
    if (upload) {
      const found = this.uploadSessions.find((u) => u.id === upload);
      return found ? json(200, this.publicSession(found)) : json(404, problem(404, 'Not found'));
    }
    return null;
  }

  private publicSession(session: UploadSessionDto & { partDigests: string[] }): UploadSessionDto {
    const { partDigests: _digests, ...rest } = session;
    return rest;
  }

  private grantsFor(session: UploadSessionDto & { partDigests: string[] }, partNumbers: number[]) {
    return {
      expiresAt: new Date(Date.now() + 3_600_000),
      parts: partNumbers.map((partNumber) => ({
        partNumber,
        method: 'PUT',
        url: `${FAKE_STORAGE_ORIGIN}/bucket/${session.id}/${partNumber}?partNumber=${partNumber}&X-Amz-Signature=simulated`,
        requiredHeaders: { 'x-amz-checksum-sha256': checksumHeader(session.partDigests[partNumber - 1]) },
      })),
    };
  }

  private handleMatchWrites(
    method: string,
    path: string,
    body: any,
    json: (status: number, body: unknown) => Promise<void>,
  ) {
    const matchPath = /^\/matches\/([^/]+)\/(.+)$/.exec(path);
    if (method === 'POST' && /^\/teams\/[^/]+\/matches$/.test(path)) {
      const created: MatchDto = {
        id: newId(),
        teamId: ids.team,
        opponent: { name: body?.opponent?.name ?? '' },
        kickoffAt: new Date(body?.kickoffAt ?? now),
        homeAway: body?.homeAway ?? 'home',
        competition: body?.competition ?? null,
        createdAt: now,
        version: 1,
      };
      this.matches.push(created);
      return json(201, created);
    }
    if (!matchPath) return null;
    const [, matchId, rest] = matchPath;
    const known = this.matches.find((m) => m.id === matchId);
    if (!known) return json(404, problem(404, 'Not found'));

    if (method === 'POST' && rest === 'upload-sessions') {
      const total = Number(body?.totalSizeBytes ?? 0);
      const partSize = Number(body?.partSizeBytes ?? 1);
      const partCount = Math.ceil(total / partSize);
      const session = {
        id: newId(),
        matchId,
        teamId: known.teamId,
        state: 'pending',
        recording: body?.recording ?? { displayName: null, description: null, contentType: null },
        totalSizeBytes: total,
        partSizeBytes: partSize,
        partCount,
        contentDigest: compositeDigest(partSize, body?.partDigests ?? []),
        createdAt: now,
        expiresAt: later(24),
        completedAt: null,
        expiredAt: null,
        version: 1,
        partDigests: body?.partDigests ?? [],
      } satisfies UploadSessionDto & { partDigests: string[] };
      this.uploadSessions.push(session);
      const grants = this.grantsFor(session, Array.from({ length: Math.min(partCount, 8) }, (_, i) => i + 1));
      return json(201, { session: this.publicSession(session), grants });
    }
    const grantsMatch = /^upload-sessions\/([^/]+)\/grants$/.exec(rest ?? '');
    if (method === 'POST' && grantsMatch) {
      const session = this.uploadSessions.find((u) => u.id === grantsMatch[1]);
      if (!session) return json(404, problem(404, 'Not found'));
      return json(200, this.grantsFor(session, (body?.partNumbers as number[] | undefined) ?? []));
    }
    const completion = /^upload-sessions\/([^/]+)\/completion$/.exec(rest ?? '');
    if (method === 'POST' && completion) {
      const session = this.uploadSessions.find((u) => u.id === completion[1]);
      if (!session) return json(404, problem(404, 'Not found'));
      session.state = 'completed';
      session.completedAt = now;
      const recordingVersion: RecordingVersionDto = {
        id: newId(),
        matchId,
        teamId: known.teamId,
        uploadSessionId: session.id,
        recording: session.recording,
        totalSizeBytes: session.totalSizeBytes,
        partSizeBytes: session.partSizeBytes,
        partCount: session.partCount,
        contentDigest: session.contentDigest,
        createdAt: now,
      };
      const timelineMapping: TimelineMappingDto = {
        id: newId(),
        recordingVersionId: recordingVersion.id,
        matchId,
        spans: body?.timelineMapping?.spans ?? [],
        digest: `sha256:${'1'.repeat(64)}`,
        createdAt: now,
      };
      this.recordingVersions.push(recordingVersion);
      this.timelineMappings.push(timelineMapping);
      return json(201, { recordingVersion, timelineMapping });
    }
    if (method === 'POST' && rest === 'recording-set-versions') {
      const members = (body?.members ?? []) as { recordingVersionId: string; timelineMappingId: string }[];
      const set: RecordingSetVersionDto = {
        id: newId(),
        matchId,
        teamId: known.teamId,
        finalizedAt: now,
        members: members.map((m, position) => ({
          position,
          recordingVersionId: m.recordingVersionId,
          recordingContentDigest: this.recordingVersions.find((v) => v.id === m.recordingVersionId)?.contentDigest ?? '',
          timelineMappingId: m.timelineMappingId,
          timelineMappingDigest: this.timelineMappings.find((t) => t.id === m.timelineMappingId)?.digest ?? '',
        })),
      };
      this.recordingSetVersions.push(set);
      this.analysisRuns.push({ ...analysisRun, runId: newId(), matchId, recordingSetVersionId: set.id });
      return json(201, set);
    }
    return null;
  }
}
