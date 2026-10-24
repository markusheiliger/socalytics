import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const TEAM = '00000000-0000-4000-8000-000000000003';
const MEMBER = '00000000-0000-4000-8000-000000000021';
const idOf = (n: number) => `00000000-0000-4000-8000-0000000000e${n}`;

const wire = (n: number, over: Record<string, unknown> = {}) => ({
  id: idOf(n),
  state: 'pending',
  address: `invitee${n}@example.org`,
  accountName: `invitee${n}`,
  teamRoles: [{ teamId: TEAM, teamName: 'First Team', role: 'viewer' }],
  invitedBy: { id: MEMBER, accountName: 'ada' },
  createdAt: '2026-10-12T12:00:00Z',
  linkExpiresAt: '2026-10-15T12:00:00Z',
  deliveryState: 'sent',
  resendCount: 0,
  version: 1,
  ...over,
});

const json = (status: number, body?: unknown, headers: Record<string, string> = {}) =>
  new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });

type Call = [string | Request, RequestInit];
const urlOf = (c: Call) => (typeof c[0] === 'string' ? c[0] : c[0].url);
const methodOf = (c: Call) => (typeof c[0] === 'string' ? c[1]?.method : c[0].method) ?? 'GET';
const calls = () => (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls as Call[];
const operationCalls = (operation: string) => calls().filter((c) => new RegExp(`/api/v1/invitations/[^/]+/${operation}$`).test(urlOf(c)));
const listCalls = () => calls().filter((c) => /\/api\/v1\/invitations(\?|$)/.test(urlOf(c)) && methodOf(c) === 'GET');
const headerOf = (c: Call, name: string) => {
  const headers = (typeof c[0] === 'string' ? c[1]?.headers : c[0].headers) as Record<string, string> | Headers | undefined;
  if (!headers) return undefined;
  return headers instanceof Headers ? (headers.get(name) ?? undefined) : (headers[name] ?? headers[name.toLowerCase()]);
};

type Handler = (url: string, init: RequestInit) => Response | undefined;
const stubPlatform = (handler: Handler) =>
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string | Request, init: RequestInit = {}) => {
      const url = typeof input === 'string' ? input : input.url;
      const method = typeof input === 'string' ? init.method : input.method;
      const answered = handler(url, { ...init, method: method ?? init.method });
      if (answered) return answered;
      if (/\/api\/v1\/teams/.test(url)) return json(200, { items: [], continuationToken: null });
      if (/\/api\/v1\/members/.test(url)) return json(200, { items: [], continuationToken: null });
      return json(404, { status: 404 });
    }),
  );

const admin = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })) });
const isList = (url: string, init: RequestInit) => /\/api\/v1\/invitations(\?|$)/.test(url) && (init.method ?? 'GET') === 'GET';
const section = async () => within(await screen.findByRole('region', { name: 'Invitations' }));

afterEach(() => vi.unstubAllGlobals());

describe('invitations section', () => {
  it('lists invitations with state, roles, inviting Club Admin, and delivery state', async () => {
    stubPlatform((url, init) =>
      isList(url, init)
        ? json(200, [
            wire(1, { deliveryState: 'queued' }),
            wire(2, { state: 'expired', deliveryState: 'failed' }),
            wire(3, { state: 'accepted', acceptedAt: '2026-10-13T12:00:00Z', acceptedMemberId: MEMBER, deliveryState: 'sent' }),
            wire(4, { state: 'revoked', revokedAt: '2026-10-13T12:00:00Z' }),
          ])
        : undefined,
    );
    renderApp(admin(), '/members');
    const list = within(await screen.findByRole('list', { name: 'Invitations' }));
    const items = list.getAllByRole('listitem');
    expect(items).toHaveLength(4);
    expect(items.map((i) => within(i).getByText(/^invitee\d@example\.org$/).textContent)).toEqual([
      'invitee1@example.org',
      'invitee2@example.org',
      'invitee3@example.org',
      'invitee4@example.org',
    ]);

    const first = within(items[0]);
    expect(first.getByText('State: Pending')).toBeInTheDocument();
    expect(first.getByText('Sign-in name: invitee1')).toBeInTheDocument();
    expect(first.getByText('Team roles: First Team: Viewer')).toBeInTheDocument();
    expect(first.getByText('Invited by ada')).toBeInTheDocument();
    expect(first.getByText('Delivery: Message queued')).toBeInTheDocument();

    expect(within(items[1]).getByText('State: Expired')).toBeInTheDocument();
    expect(within(items[1]).getByText('Delivery: Message could not be delivered')).toBeInTheDocument();

    const accepted = within(items[2]);
    expect(accepted.getByText('State: Accepted')).toBeInTheDocument();
    expect(accepted.getByRole('link', { name: 'Open the member invitee3' })).toHaveAttribute('href', `/members/${MEMBER}`);
    expect(accepted.queryByRole('button', { name: /Resend/ })).not.toBeInTheDocument();
    expect(accepted.queryByText(/Delivery:/)).not.toBeInTheDocument();

    const revoked = within(items[3]);
    expect(revoked.getByText('State: Revoked')).toBeInTheDocument();
    expect(revoked.queryByRole('button', { name: /Revoke invitation to/ })).not.toBeInTheDocument();
  });

  it('shows an empty list', async () => {
    stubPlatform((url, init) => (isList(url, init) ? json(200, []) : undefined));
    renderApp(admin(), '/members');
    expect(await screen.findByText('There are no invitations.')).toBeInTheDocument();
  });

  it('filters by state and asks the platform for that state', async () => {
    stubPlatform((url, init) => {
      if (!isList(url, init)) return undefined;
      return new URL(url, 'http://localhost').searchParams.get('state') === 'expired'
        ? json(200, [wire(2, { state: 'expired' })])
        : json(200, [wire(1), wire(2, { state: 'expired' })]);
    });
    renderApp(admin(), '/members');
    const list = await section();
    expect(await list.findByText('invitee1@example.org')).toBeInTheDocument();
    await userEvent.selectOptions(list.getByLabelText('Show invitations'), 'expired');
    await waitFor(() => expect(list.queryByText('invitee1@example.org')).not.toBeInTheDocument());
    expect(list.getByText('invitee2@example.org')).toBeInTheDocument();
    expect(listCalls().some((c) => new URL(urlOf(c), 'http://localhost').searchParams.get('state') === 'expired')).toBe(true);
  });

  it('resends conditionally on the shown version and reloads the list', async () => {
    let delivery = 'sent';
    stubPlatform((url, init) => {
      if (isList(url, init)) return json(200, [wire(1, { deliveryState: delivery, version: 3 })]);
      if (/\/resend$/.test(url)) {
        delivery = 'queued';
        return json(200, wire(1, { deliveryState: 'queued', version: 4 }), { ETag: '"4"' });
      }
      return undefined;
    });
    renderApp(admin(), '/members');
    await userEvent.click(await screen.findByRole('button', { name: 'Resend invitation to invitee1@example.org' }));
    expect(await screen.findByText('A new invitation was sent to invitee1@example.org. The previous link no longer works.')).toBeInTheDocument();
    const resend = operationCalls('resend');
    expect(resend).toHaveLength(1);
    expect(urlOf(resend[0])).toContain(`/api/v1/invitations/${idOf(1)}/resend`);
    expect(headerOf(resend[0], 'If-Match')).toBe('"3"');
    expect(await screen.findByText('Delivery: Message queued')).toBeInTheDocument();
  });

  it('revokes only after the confirmation', async () => {
    let revoked = false;
    stubPlatform((url, init) => {
      if (isList(url, init)) return json(200, [wire(1, revoked ? { state: 'revoked', revokedAt: '2026-10-13T12:00:00Z', version: 2 } : {})]);
      if (/\/revoke$/.test(url)) {
        revoked = true;
        return json(200, wire(1, { state: 'revoked', revokedAt: '2026-10-13T12:00:00Z', version: 2 }), { ETag: '"2"' });
      }
      return undefined;
    });
    renderApp(admin(), '/members');
    await userEvent.click(await screen.findByRole('button', { name: 'Revoke invitation to invitee1@example.org' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Revoke this invitation?' }));
    expect(dialog.getByText(/sign-in name and email address become available again/)).toBeInTheDocument();
    expect(operationCalls('revoke')).toHaveLength(0);

    await userEvent.click(dialog.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Revoke this invitation?' })).not.toBeInTheDocument());
    expect(operationCalls('revoke')).toHaveLength(0);

    await userEvent.click(screen.getByRole('button', { name: 'Revoke invitation to invitee1@example.org' }));
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Revoke this invitation?' })).getByRole('button', { name: 'Revoke invitation' }));
    expect(await screen.findByText('The invitation to invitee1@example.org was revoked.')).toBeInTheDocument();
    expect(operationCalls('revoke')).toHaveLength(1);
    expect(headerOf(operationCalls('revoke')[0], 'If-Match')).toBe('"1"');
    expect(await screen.findByText('State: Revoked')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revoke invitation to invitee1@example.org' })).not.toBeInTheDocument();
  });

  it('refreshes the list after a stale version', async () => {
    let version = 1;
    stubPlatform((url, init) => {
      if (isList(url, init)) return json(200, [wire(1, { version })]);
      if (/\/resend$/.test(url)) {
        version = 2;
        return json(412, { status: 412, code: 'precondition-failed' });
      }
      return undefined;
    });
    renderApp(admin(), '/members');
    await userEvent.click(await screen.findByRole('button', { name: 'Resend invitation to invitee1@example.org' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('This invitation was changed in the meantime. The list was refreshed.');
    await waitFor(() => expect(listCalls().length).toBeGreaterThanOrEqual(2));

    await userEvent.click(screen.getByRole('button', { name: 'Resend invitation to invitee1@example.org' }));
    await waitFor(() => expect(operationCalls('resend')).toHaveLength(2));
    expect(headerOf(operationCalls('resend')[1], 'If-Match')).toBe('"2"');
  });

  it('refreshes the list when the invitation is no longer open', async () => {
    let accepted = false;
    stubPlatform((url, init) => {
      if (isList(url, init)) return json(200, [wire(1, accepted ? { state: 'accepted', acceptedAt: '2026-10-13T12:00:00Z', acceptedMemberId: MEMBER, version: 2 } : {})]);
      if (/\/revoke$/.test(url)) {
        accepted = true;
        return json(409, { status: 409, code: 'invitation-not-open' });
      }
      return undefined;
    });
    renderApp(admin(), '/members');
    await userEvent.click(await screen.findByRole('button', { name: 'Revoke invitation to invitee1@example.org' }));
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Revoke this invitation?' })).getByRole('button', { name: 'Revoke invitation' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('This invitation was already accepted or revoked. The list was refreshed.');
    expect(await screen.findByText('State: Accepted')).toBeInTheDocument();
  });

  it('shows the wait time when resends are limited', async () => {
    stubPlatform((url, init) => {
      if (isList(url, init)) return json(200, [wire(1)]);
      if (/\/resend$/.test(url)) return json(429, { status: 429, code: 'rate-limited' }, { 'Retry-After': '1800' });
      return undefined;
    });
    renderApp(admin(), '/members');
    await userEvent.click(await screen.findByRole('button', { name: 'Resend invitation to invitee1@example.org' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Too many resends. Try again in 1800 seconds.');
    expect(operationCalls('resend')).toHaveLength(1);
  });
});
