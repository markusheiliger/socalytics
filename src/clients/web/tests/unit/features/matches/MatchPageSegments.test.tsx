import { screen } from '@testing-library/react';
import { vi } from 'vitest';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';
import type { ClubRole, TeamRoleName } from '../../../../src/platform/models/session';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const MATCH = '00000000-0000-4000-8000-000000000021';
const team = { id: TEAM, seasonId: SEASON, seasonState: 'Active', name: 'First Team', createdAt: '2026-10-12T12:00:00Z', version: 1 };
const match = {
  id: MATCH,
  teamId: TEAM,
  opponent: { name: 'Rivals FC' },
  kickoffAt: '2026-11-01T15:00:00Z',
  homeAway: 'Home',
  competition: 'League',
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
};

const listSegments = vi.fn(async () => ({ matchId: MATCH, segments: [] }));
vi.mock('../../../../src/platform/adapters/segmentAdapter', () => ({
  createSegmentAdapter: () => ({ list: () => listSegments(), purge: vi.fn() }),
}));

const sessionFor = (options: { clubRoles?: ClubRole[]; teamRole?: TeamRoleName }) =>
  fakeAdapter({
    restore: vi.fn().mockResolvedValue(
      makeSession({
        clubRoles: options.clubRoles,
        teamRoles: options.teamRole ? [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role: options.teamRole }] : [],
      }),
    ),
  });

function stubPlatform(matchStatus = 200) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      if (url.endsWith(`/matches/${MATCH}`)) return matchStatus === 200 ? json(200, match) : json(matchStatus, { title: 'Forbidden', status: matchStatus });
      if (url.includes('/recording-lineage')) return json(200, { matchId: MATCH, teamId: TEAM, recordingVersions: [] });
      if (url.includes('/analysis')) return json(200, { items: [], continuationToken: null });
      return json(200, team);
    }),
  );
}

beforeEach(() => listSegments.mockClear());
afterEach(() => vi.unstubAllGlobals());

describe('match page segments area', () => {
  it('shows the area to a Club Admin and requests the segments', async () => {
    stubPlatform();
    renderApp(sessionFor({ clubRoles: ['ClubAdmin'] }), `/matches/${MATCH}`);
    expect(await screen.findByRole('heading', { name: 'Video segments' })).toBeInTheDocument();
    expect(listSegments).toHaveBeenCalled();
  });

  it.each<[string, TeamRoleName | undefined, ClubRole[] | undefined]>([
    ['a Coach', 'Coach', undefined],
    ['a Viewer', 'Viewer', undefined],
    ['a Registrar without team role', undefined, ['Registrar']],
  ])('shows %s no area and sends no segment request', async (_name, teamRole, clubRoles) => {
    stubPlatform();
    renderApp(sessionFor({ teamRole, clubRoles }), `/matches/${MATCH}`);
    if (teamRole) expect(await screen.findByRole('heading', { name: 'Rivals FC' })).toBeInTheDocument();
    else await screen.findByText('You are not permitted to open this page.');
    expect(screen.queryByRole('heading', { name: 'Video segments' })).toBeNull();
    expect(listSegments).not.toHaveBeenCalled();
  });

  it('shows a Club Admin of another club no area, because the match is refused', async () => {
    stubPlatform(403);
    renderApp(sessionFor({ clubRoles: ['ClubAdmin'] }), `/matches/${MATCH}`);
    await screen.findByText('You are not permitted to open this page.');
    expect(screen.queryByRole('heading', { name: 'Video segments' })).toBeNull();
    expect(listSegments).not.toHaveBeenCalled();
  });
});
