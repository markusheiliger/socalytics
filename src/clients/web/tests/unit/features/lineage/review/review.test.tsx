import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import type { TeamRoleName } from '../../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const MATCH = '00000000-0000-4000-8000-000000000021';
const V1 = '00000000-0000-4000-8000-0000000000a1';
const M1 = '00000000-0000-4000-8000-0000000000b1';
const M2 = '00000000-0000-4000-8000-0000000000b2';
const SET = '00000000-0000-4000-8000-0000000000c1';
const digest = (c: string) => `sha256:${c.repeat(64)}`;

const version = {
  id: V1,
  matchId: MATCH,
  teamId: TEAM,
  uploadSessionId: '00000000-0000-4000-8000-0000000000e1',
  recording: { displayName: 'Main camera', contentType: 'video/mp4' },
  totalSizeBytes: 2048,
  partSizeBytes: 2048,
  partCount: 1,
  contentDigest: digest('a'),
  createdAt: '2026-10-12T12:00:00Z',
};
const mapping = (id: string, createdAt: string, d: string) => ({
  id,
  recordingVersionId: V1,
  matchId: MATCH,
  spans: [{ mediaStartSeconds: 0, mediaEndSeconds: 60, matchStartSeconds: 600 }],
  digest: digest(d),
  createdAt,
});
const set = {
  id: SET,
  matchId: MATCH,
  teamId: TEAM,
  finalizedAt: '2026-10-13T12:00:00Z',
  members: [{ position: 0, recordingVersionId: V1, recordingContentDigest: digest('a'), timelineMappingId: M1, timelineMappingDigest: digest('b') }],
};
const matchDto = {
  id: MATCH,
  teamId: TEAM,
  opponent: { name: 'Rivals FC' },
  kickoffAt: '2026-11-01T15:00:00Z',
  homeAway: 'Home',
  competition: 'League',
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
};
const team = { id: TEAM, seasonId: SEASON, seasonState: 'Active', name: 'First Team', createdAt: '2026-10-12T12:00:00Z', version: 1 };
const asRole = (role: TeamRoleName) =>
  fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role }] })) });

/** `refuse` answers a POST with a failure; `fail` throws once per call to simulate a lost connection. */
function stub(options: { refuse?: Response; failFirst?: boolean } = {}) {
  const posts: RequestInit[] = [];
  const mappings = [mapping(M1, '2026-10-12T12:00:00Z', 'b')];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      if (init.method === 'POST') {
        posts.push(init);
        if (options.failFirst && posts.length === 1) throw new TypeError('Failed to fetch');
        if (options.refuse) return options.refuse;
        const next = mapping(M2, '2026-10-14T12:00:00Z', 'c');
        mappings.push(next);
        return json(201, next);
      }
      if (url.includes('recording-lineage'))
        return json(200, { matchId: MATCH, teamId: TEAM, recordingVersions: [version], timelineMappings: mappings, recordingSetVersions: [set] });
      if (url.includes('analysis-runs')) return json(200, { items: [], continuationToken: null });
      if (url.includes('/matches/')) return json(200, matchDto);
      return json(200, team);
    }),
  );
  return posts;
}
const keyOf = (init: RequestInit) => new Headers(init.headers as Record<string, string>).get('Idempotency-Key');
const bodyOf = (init: RequestInit) => JSON.parse(new TextDecoder().decode(init.body as ArrayBuffer));

afterEach(() => vi.unstubAllGlobals());

describe('lineage section', () => {
  it.each<TeamRoleName>(['Viewer', 'Coach'])('shows versions, mappings, and set versions as %s without change or media actions', async (role) => {
    stub();
    renderApp(asRole(role), `/matches/${MATCH}`);
    const versions = await screen.findByRole('list', { name: 'Uploaded versions in the lineage' });
    expect(versions).toHaveTextContent('Main camera');
    expect(versions).toHaveTextContent(digest('a'));
    expect(versions).toHaveTextContent('2.0 KiB');
    expect(screen.getByRole('list', { name: 'Lineage timeline mappings' })).toHaveTextContent(digest('b'));
    expect(screen.getByRole('list', { name: 'Lineage timeline mappings' })).toHaveTextContent('00:00–01:00');
    const sets = screen.getByRole('list', { name: 'Lineage recording set versions' });
    expect(within(sets).getByRole('list', { name: 'Ordered members' })).toHaveTextContent('1. Main camera');
    const section = screen.getByRole('region', { name: 'Recording lineage' });
    expect(within(section).queryByRole('button')).toBeNull();
    expect(section.querySelector('video, audio, a[download]')).toBeNull();
  });
});

describe('recording version page', () => {
  it('offers no revision to a Viewer', async () => {
    stub();
    renderApp(asRole('Viewer'), `/matches/${MATCH}/recordings/${V1}`);
    expect(await screen.findByText(digest('a'))).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revise mapping' })).toBeNull();
  });

  it('adds a revised mapping with one key per dialog, reused on retry, and keeps the earlier mapping', async () => {
    const posts = stub({ failFirst: true });
    renderApp(asRole('Coach'), `/matches/${MATCH}/recordings/${V1}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Revise mapping' }));
    const start = screen.getByLabelText('Match time at media start (Span 1)');
    await userEvent.clear(start);
    await userEvent.type(start, '10:30');
    await userEvent.click(screen.getByRole('button', { name: 'Add revised mapping' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(posts).toHaveLength(2));
    expect(keyOf(posts[1])).toBe(keyOf(posts[0]));
    expect(bodyOf(posts[1])).toEqual({ spans: [{ mediaStartSeconds: 0, mediaEndSeconds: 60, matchStartSeconds: 630 }] });
    await waitFor(() => expect(screen.getAllByText(/Mapping digest/)).toHaveLength(2));
    expect(screen.getByText(new RegExp(digest('b')))).toBeInTheDocument();
  });

  it('shows the platform refusal and sends no request for invalid input', async () => {
    const posts = stub({ refuse: json(400, { code: 'validation-failed', errors: [{ field: 'timelineMapping.spans[0]', code: 'timeline-mapping-invalid' }] }) });
    renderApp(asRole('Coach'), `/matches/${MATCH}/recordings/${V1}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Revise mapping' }));
    const end = screen.getByLabelText('Media end (Span 1)');
    await userEvent.clear(end);
    await userEvent.click(screen.getByRole('button', { name: 'Add revised mapping' }));
    expect(posts).toHaveLength(0);
    expect(await screen.findByText('This time is required.')).toBeInTheDocument();
    await userEvent.type(end, '01:00');
    await userEvent.click(screen.getByRole('button', { name: 'Add revised mapping' }));
    expect(await screen.findByText('The platform refused this span.')).toBeInTheDocument();
    expect(posts).toHaveLength(1);
  });
});
