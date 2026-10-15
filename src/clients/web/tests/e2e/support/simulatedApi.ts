import { createHash } from 'node:crypto';
import type { Page, Route } from '@playwright/test';
import type {
  AnalysisRunSummaryDto,
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

/** Origin of the fake object store (tests/e2e/support/fakeObjectStorage.mjs); playwright.config.ts starts it on this port. */
export const FAKE_STORAGE_ORIGIN = 'http://127.0.0.1:4175';

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

  private async handle(route: Route): Promise<void> {
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
    if (method === 'GET') {
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
          items: Object.values(members).map(memberListEntry),
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
