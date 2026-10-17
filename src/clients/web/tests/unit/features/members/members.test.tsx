import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const MEMBER = '00000000-0000-4000-8000-000000000021';
const TEAM = '00000000-0000-4000-8000-000000000003';
const member = (over = {}) => ({
  id: MEMBER,
  accountName: 'ada',
  membershipStatus: 'active',
  passwordSet: true,
  lockedOut: false,
  lockoutEndsAt: null,
  twoFactorEnabled: false,
  clubRoles: ['club-admin'],
  teamRoles: [],
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
  ...over,
});
const team = { id: TEAM, seasonId: '00000000-0000-4000-8000-000000000002', seasonState: 'Active', name: 'First Team', createdAt: '2026-10-12T12:00:00Z', version: 1 };
const admin = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })) });

afterEach(() => vi.unstubAllGlobals());

describe('members views', () => {
  it('creates a member and shows the credential code once', async () => {
    const members = [member({ accountName: 'ada' })];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          members.push(member({ id: '00000000-0000-4000-8000-000000000022', accountName: 'bob', passwordSet: false }));
          return json(201, {
            member: members[1],
            setPasswordCredential: { memberId: members[1].id, purpose: 'set-password', credential: 'ONCE-CODE', expiresAt: '2026-10-13T12:00:00Z' },
          });
        }
        return json(200, { items: members, continuationToken: null });
      }),
    );
    renderApp(admin(), '/members');
    expect(await screen.findByRole('link', { name: 'ada (Active)' })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/sign-in name/i), 'bob');
    await userEvent.click(screen.getByRole('button', { name: 'Create member' }));
    expect(await screen.findByText('ONCE-CODE')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'I have handed over the code' }));
    expect(screen.queryByText('ONCE-CODE')).not.toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'bob (Active)' })).toBeInTheDocument();
  });

  it('shows the last Club Admin refusal clearly', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        if (url.endsWith('/email')) return json(404, { status: 404 });
        if (init?.method === 'DELETE') return json(409, { status: 409, code: 'last-club-admin' });
        if (url.includes('/teams')) return json(200, { items: [team], continuationToken: null });
        return json(200, member());
      }),
    );
    renderApp(admin(), `/members/${MEMBER}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Remove Club Admin' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('at least one active Club Admin');
  });

  it('assigns a team role and deactivates the member', async () => {
    let current = member();
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/email')) return json(404, { status: 404 });
      if (init?.method === 'PUT') current = member({ teamRoles: [{ teamId: TEAM, role: 'coach' }] });
      else if (url.endsWith('/deactivate')) current = member({ membershipStatus: 'deactivated' });
      else if (url.includes('/teams')) return json(200, { items: [team], continuationToken: null });
      return json(200, current);
    });
    vi.stubGlobal('fetch', fetchMock);
    renderApp(admin(), `/members/${MEMBER}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Assign team role' }));
    expect(await screen.findByText('First Team: Coach')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Deactivate' }));
    expect(await screen.findByText('Status: Deactivated')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reactivate' })).toBeInTheDocument();
  });

  it('offers the credential choice only when providers exist and creates with a link code', async () => {
    const bodies: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          bodies.push(new TextDecoder().decode(init.body as ArrayBuffer));
          return json(201, {
            member: member({ id: '00000000-0000-4000-8000-000000000022', accountName: 'bob', passwordSet: false }),
            setPasswordCredential: { memberId: MEMBER, purpose: 'identity-link', credential: 'LINK-CODE', expiresAt: '2026-10-13T12:00:00Z' },
          });
        }
        return json(200, { items: [], continuationToken: null });
      }),
    );
    const adapter = fakeAdapter({
      restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })),
      listProviders: vi.fn().mockResolvedValue([{ id: 'club-entra', displayName: 'Club account' }]),
    });
    renderApp(adapter, '/members');
    await userEvent.type(await screen.findByLabelText(/sign-in name/i), 'bob');
    await userEvent.selectOptions(await screen.findByLabelText('First credential'), 'club-entra');
    await userEvent.click(screen.getByRole('button', { name: 'Create member' }));
    expect(await screen.findByText('LINK-CODE')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Link code' })).toBeInTheDocument();
    expect(bodies[0]).toContain('"kind":"identity-link"');
  });

  it('shows no credential choice without providers', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, { items: [], continuationToken: null })));
    renderApp(admin(), '/members');
    await screen.findByLabelText(/sign-in name/i);
    expect(screen.queryByLabelText('First credential')).not.toBeInTheDocument();
  });

  it('shows linked providers, issues a link code, and unlinks after confirmation', async () => {
    let current = member({ externalIdentities: [{ provider: 'club-entra', displayName: 'Club account', linkedAt: '2026-10-13T12:00:00Z', providerConfigured: false }] });
    const calls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        calls.push(`${init?.method ?? 'GET'} ${new URL(url, 'http://x').pathname}`);
        if (url.endsWith('/email')) return json(404, { status: 404 });
        if (url.includes('/teams')) return json(200, { items: [team], continuationToken: null });
        if (init?.method === 'DELETE') {
          current = member();
          return new Response(null, { status: 204 });
        }
        if (init?.method === 'POST') return json(201, { memberId: MEMBER, purpose: 'identity-link', credential: 'LINK-CODE', expiresAt: '2026-10-13T12:00:00Z' });
        return json(200, current);
      }),
    );
    const adapter = fakeAdapter({
      restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })),
      listProviders: vi.fn().mockResolvedValue([{ id: 'club-entra', displayName: 'Club account' }]),
    });
    renderApp(adapter, `/members/${MEMBER}`);
    expect(await screen.findByText(/Club account, linked .*no longer configured/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Unlink Club account' }));
    expect(calls.some((c) => c.startsWith('DELETE'))).toBe(false);
    await userEvent.click(await screen.findByRole('button', { name: 'Unlink' }));
    expect(await screen.findByText('No provider account is linked.')).toBeInTheDocument();
    expect(calls).toContain(`DELETE /api/v1/members/${MEMBER}/external-identities/club-entra`);
    await userEvent.click(await screen.findByRole('button', { name: 'Issue link code for Club account' }));
    expect(await screen.findByText('LINK-CODE')).toBeInTheDocument();
  });

  it('keeps the members views from other members', async () => {
    renderApp(fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) }), '/members');
    expect(await screen.findByText(/not permitted/i)).toBeInTheDocument();
  });
});
