import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { fakeAdapter, makeSession, renderApp } from '../session/helpers';
import type { ClubRole, TeamRole } from '../../../src/platform/models/session';

const team: TeamRole = { teamId: 't-1', teamName: 'First Team', seasonId: 's-1', role: 'Coach' };

async function navLinks(clubRoles: ClubRole[], teamRoles: TeamRole[]) {
  renderApp(fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles, teamRoles })) }));
  const nav = await screen.findByRole('navigation', { name: 'Main navigation' });
  return within(nav).getAllByRole('link').map((a) => a.textContent);
}

describe('role-based navigation', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ items: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } })));
  });
  afterEach(() => vi.unstubAllGlobals());

  it('offers a Club Admin club, seasons, teams, members, Analyst Managers, Analyst Registry, Operations, Agent catalog, pairing, and account', async () => {
    expect(await navLinks(['ClubAdmin'], [])).toEqual(['Home', 'Club', 'Seasons', 'Teams', 'Members', 'Analyst Managers', 'Analyst Registry', 'Operations', 'Agent catalog', 'Pair a Manager', 'Account']);
  });

  it('places Operations directly after Analyst Registry for a Club Admin', async () => {
    const links = await navLinks(['ClubAdmin'], []);
    expect(links.indexOf('Operations')).toBe(links.indexOf('Analyst Registry') + 1);
  });

  it.each([
    ['a Registrar', ['Registrar'] as ClubRole[], [] as TeamRole[]],
    ['a Coach', [] as ClubRole[], [team]],
    ['a Viewer', [] as ClubRole[], [{ ...team, role: 'Viewer' } as TeamRole]],
  ])('offers %s no Operations entry and the not-permitted view for /operations', async (_name, clubRoles, teamRoles) => {
    renderApp(fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles, teamRoles })) }), '/operations');
    expect(await screen.findByRole('alert')).toHaveTextContent('not permitted');
    expect(screen.queryByRole('link', { name: 'Operations' })).not.toBeInTheDocument();
  });

  it('offers a team member only teams and account', async () => {
    expect(await navLinks([], [team])).toEqual(['Home', 'Teams', 'Account']);
  });

  it('offers a Registrar without other roles only home, requests, pairing, and account', async () => {
    expect(await navLinks(['Registrar'], [])).toEqual(['Home', 'My registration requests', 'Pair a Manager', 'Account']);
  });

  it('shows the not-permitted view with the navigation for a disallowed address', async () => {
    renderApp(fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [team] })) }), '/members');
    expect(await screen.findByRole('alert')).toHaveTextContent('not permitted');
    expect(screen.getByRole('navigation', { name: 'Main navigation' })).toBeInTheDocument();
  });

  it.each(['/registry', '/registry/images', '/registry/images/sha256:abc', '/registry/profiles', '/registry/profiles/p-1/versions/1', '/registry/workflows', '/registry/workflows/w-1/versions/1.0.0'])(
    'shows the not-permitted view for %s to a member who is not a Club Admin',
    async (path) => {
      renderApp(fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['Registrar'], teamRoles: [team] })) }), path);
      expect(await screen.findByRole('alert')).toHaveTextContent('not permitted');
      expect(screen.queryByRole('link', { name: 'Analyst Registry' })).not.toBeInTheDocument();
    },
  );

  it.each([['Coach'], ['Viewer'], ['Registrar']])('does not offer the Agent catalog to a %s', async (role) => {
    const roles = role === 'Registrar' ? await navLinks(['Registrar'], []) : await navLinks([], [{ ...team, role: role as TeamRole['role'] }]);
    expect(roles).not.toContain('Agent catalog');
  });

  it('shows the not-found view for unknown addresses', async () => {
    renderApp(fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) }), '/nowhere');
    expect(await screen.findByRole('heading', { level: 2, name: 'This page does not exist.' })).toBeInTheDocument();
  });

  it('follows changed roles on the next action that reads the session', async () => {
    const restore = vi
      .fn()
      .mockResolvedValueOnce(makeSession({ clubRoles: ['ClubAdmin'] }))
      .mockResolvedValue(makeSession({ teamRoles: [team] }));
    renderApp(fakeAdapter({ restore }), '/account');
    await screen.findByRole('link', { name: 'Members' });
    await userEvent.type(screen.getByLabelText(/^Current password/), 'old-password-1');
    await userEvent.type(screen.getByLabelText(/^New password/), 'new-password-2');
    await userEvent.type(screen.getByLabelText(/^Repeat the new password/), 'new-password-2');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Members' })).not.toBeInTheDocument());
    expect(screen.getByRole('link', { name: 'Teams' })).toBeInTheDocument();
  });
});
