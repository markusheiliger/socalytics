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

  it('offers a Club Admin club, seasons, teams, members, Analyst Managers, pairing, and account', async () => {
    expect(await navLinks(['ClubAdmin'], [])).toEqual(['Home', 'Club', 'Seasons', 'Teams', 'Members', 'Analyst Managers', 'Pair a Manager', 'Account']);
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
