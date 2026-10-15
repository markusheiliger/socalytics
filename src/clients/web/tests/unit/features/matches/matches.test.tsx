import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const OTHER_TEAM = '00000000-0000-4000-8000-000000000004';
const MATCH = '00000000-0000-4000-8000-000000000021';
const team = (over = {}) => ({ id: TEAM, seasonId: SEASON, seasonState: 'Active', name: 'First Team', createdAt: '2026-10-12T12:00:00Z', version: 1, ...over });
const match = (over = {}) => ({
  id: MATCH,
  teamId: TEAM,
  opponent: { name: 'Rivals FC' },
  kickoffAt: '2026-11-01T15:00:00Z',
  homeAway: 'Home',
  competition: 'League',
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
  ...over,
});
const withRole = (role: 'Coach' | 'Viewer', teamId = TEAM) =>
  fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId, teamName: 'First Team', seasonId: SEASON, role }] })) });
const admin = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })) });

const bodyOf = (init: RequestInit) => JSON.parse(new TextDecoder().decode(init.body as ArrayBuffer));

afterEach(() => vi.unstubAllGlobals());

describe('match views', () => {
  it('lists the team matches newest kickoff first with details and offers New match to editors', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) =>
        url.includes('/matches')
          ? json(200, {
              items: [
                match({ id: '00000000-0000-4000-8000-0000000000a1', opponent: { name: 'Older FC' }, kickoffAt: '2026-10-01T15:00:00Z', homeAway: 'Away', competition: null }),
                match({ id: '00000000-0000-4000-8000-0000000000b1', opponent: { name: 'Newer FC' }, kickoffAt: '2026-12-01T15:00:00Z', homeAway: 'Neutral' }),
              ],
              continuationToken: null,
            })
          : json(200, team()),
      ),
    );
    renderApp(withRole('Coach'), `/teams/${TEAM}`);
    const list = await screen.findByRole('list', { name: 'Matches' });
    const items = list.querySelectorAll('li');
    expect(items[0]).toHaveTextContent('Newer FC');
    expect(items[0]).toHaveTextContent('Neutral');
    expect(items[0]).toHaveTextContent('League');
    expect(items[1]).toHaveTextContent('Older FC');
    expect(items[1]).toHaveTextContent('Away');
    expect(screen.getByRole('link', { name: 'New match' })).toBeInTheDocument();
  });

  it('shows a Viewer matches without create or edit actions', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => (url.endsWith(`/matches/${MATCH}`) ? json(200, match()) : url.includes('/matches') ? json(200, { items: [match()], continuationToken: null }) : json(200, team()))),
    );
    renderApp(withRole('Viewer'), `/teams/${TEAM}`);
    expect(screen.queryByRole('link', { name: 'New match' })).toBeNull();
    await userEvent.click(await screen.findByRole('link', { name: 'Rivals FC' }));
    expect(await screen.findByText(/view this match but not change it/i)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Edit match' })).toBeNull();
  });

  it('creates a match, sends the opponent, and opens the match view', async () => {
    const posts: unknown[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          posts.push(bodyOf(init));
          return json(201, match());
        }
        if (url.endsWith(`/matches/${MATCH}`)) return json(200, match());
        if (url.includes('/matches')) return json(200, { items: [], continuationToken: null });
        return json(200, team());
      }),
    );
    renderApp(admin(), `/teams/${TEAM}/matches/new`);
    await userEvent.type(await screen.findByLabelText(/opponent/i), 'Rivals FC');
    await userEvent.type(screen.getByLabelText(/kickoff time/i), '2026-11-01T15:00');
    await userEvent.selectOptions(screen.getByLabelText('Venue'), 'away');
    await userEvent.type(screen.getByLabelText('Competition'), 'League');
    await userEvent.click(screen.getByRole('button', { name: 'Create match' }));
    expect(await screen.findByRole('heading', { level: 2, name: 'Rivals FC' })).toBeInTheDocument();
    expect(posts[0]).toMatchObject({ opponent: { name: 'Rivals FC' }, homeAway: 'away', competition: 'League' });
  });

  it('edits without sending the opponent, with If-Match, and refuses a stale save with a reload action', async () => {
    let stale = false;
    const puts: { init: RequestInit; body: Record<string, unknown> }[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        if (init?.method === 'PUT') {
          puts.push({ init, body: bodyOf(init) });
          return stale ? json(412, { status: 412 }) : json(200, match({ competition: 'Cup', version: 2 }));
        }
        return url.endsWith(`/matches/${MATCH}`) ? json(200, match()) : json(200, team());
      }),
    );
    renderApp(admin(), `/matches/${MATCH}/edit`);
    expect(await screen.findByLabelText(/opponent/i)).toHaveAttribute('readonly');
    const competition = screen.getByLabelText('Competition');
    await userEvent.clear(competition);
    await userEvent.type(competition, 'Cup');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('The match was saved.')).toBeInTheDocument();
    expect(new Headers(puts[0].init.headers).get('If-Match')).toBe('"1"');
    expect(puts[0].body).not.toHaveProperty('opponent');

    stale = true;
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/changed by someone else/i);
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
  });

  it('hides editor actions in an archived season and shows the platform refusal for a refused change', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => (url.endsWith(`/matches/${MATCH}`) ? json(200, match()) : json(200, team({ seasonState: 'Archived' })))));
    renderApp(admin(), `/matches/${MATCH}`);
    expect(await screen.findByText(/matches are read-only/i)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Edit match' })).toBeNull();
  });

  it('shows the season-archived reason when the platform refuses a save', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        if (init?.method === 'PUT') return json(409, { status: 409, code: 'season-archived' });
        return url.endsWith(`/matches/${MATCH}`) ? json(200, match()) : json(200, team());
      }),
    );
    renderApp(admin(), `/matches/${MATCH}/edit`);
    await userEvent.click(await screen.findByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/season is archived/i);
  });

  it('does not show a Coach of another team the match', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => (url.endsWith(`/matches/${MATCH}`) ? json(200, match()) : json(200, team()))));
    renderApp(withRole('Coach', OTHER_TEAM), `/matches/${MATCH}`);
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByText('Rivals FC')).toBeNull();
  });
});
