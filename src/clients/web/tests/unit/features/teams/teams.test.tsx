import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const season = (over = {}) => ({
  id: SEASON,
  name: '2026/27',
  state: 'Active',
  createdAt: '2026-10-12T12:00:00Z',
  activatedAt: null,
  archivedAt: null,
  version: 1,
  ...over,
});
const team = (over = {}) => ({
  id: TEAM,
  seasonId: SEASON,
  seasonState: 'Active',
  name: 'First Team',
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
  ...over,
});
const admin = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })) });
const coach = () =>
  fakeAdapter({
    restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role: 'Coach' }] })),
  });

afterEach(() => vi.unstubAllGlobals());

describe('teams views', () => {
  it('lets a Club Admin create a team in the chosen season', async () => {
    const teams = [team()];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          teams.push(team({ id: '00000000-0000-4000-8000-000000000005', name: 'Second Team' }));
          return json(201, teams[1]);
        }
        if (url.includes('/seasons/')) return json(200, { items: teams, continuationToken: null });
        return json(200, { items: [season()], continuationToken: null });
      }),
    );
    renderApp(admin(), '/teams');
    expect(await screen.findByRole('link', { name: 'First Team' })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/team name/i), 'Second Team');
    await userEvent.click(screen.getByRole('button', { name: 'Create team' }));
    expect(await screen.findByRole('link', { name: 'Second Team' })).toBeInTheDocument();
    expect(screen.getByLabelText(/team name/i)).toHaveValue('');
  });

  it('shows the platform reason for a refused team name', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) =>
        init?.method === 'POST'
          ? json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'name', code: 'required' }] })
          : json(200, { items: url.includes('/seasons/') ? [] : [season()], continuationToken: null }),
      ),
    );
    renderApp(admin(), '/teams');
    await userEvent.click(await screen.findByRole('button', { name: 'Create team' }));
    expect(await screen.findByText('This field is required.')).toBeInTheDocument();
  });

  it('offers no creation for an archived season', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) =>
        json(200, { items: url.includes('/seasons/') ? [team({ seasonState: 'Archived' })] : [season({ state: 'Archived' })], continuationToken: null }),
      ),
    );
    renderApp(admin(), '/teams');
    expect(await screen.findByRole('link', { name: 'First Team' })).toBeInTheDocument();
    expect(screen.getByText(/teams are read-only/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Create team' })).toBeNull();
  });

  it('edits a team with the loaded version and refuses a stale save with a reload action', async () => {
    let stale = false;
    const puts: RequestInit[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'PUT') {
          puts.push(init);
          return stale ? json(412, { status: 412 }) : json(200, team({ name: 'Renamed', version: 2 }));
        }
        return json(200, team());
      }),
    );
    renderApp(admin(), `/teams/${TEAM}`);
    const name = await screen.findByLabelText(/team name/i);
    await userEvent.clear(name);
    await userEvent.type(name, 'Renamed');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('The team was saved.')).toBeInTheDocument();
    expect(new Headers(puts[0].headers).get('If-Match')).toBe('"1"');

    stale = true;
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/changed by someone else/i);
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
  });

  it('shows a Coach their teams read-only', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => json(200, url.endsWith(TEAM) ? team() : { items: [team()], continuationToken: null })));
    renderApp(coach(), '/teams');
    await userEvent.click(await screen.findByRole('link', { name: 'First Team' }));
    expect(await screen.findByText(/view this team but not change it/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/team name/i)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();
  });
});
