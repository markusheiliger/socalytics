import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const MEMBER = '00000000-0000-4000-8000-000000000021';
const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
const member = {
  id: MEMBER,
  accountName: 'ada',
  membershipStatus: 'active',
  passwordSet: true,
  lockedOut: false,
  lockoutEndsAt: null,
  twoFactorEnabled: false,
  clubRoles: [],
  teamRoles: [],
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
};
type Call = [string | Request, RequestInit?];
const calls = () => (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls as Call[];
const urlOf = (c: Call) => (typeof c[0] === 'string' ? c[0] : c[0].url);
const methodOf = (c: Call) => (typeof c[0] === 'string' ? c[1]?.method : c[0].method) ?? 'GET';

const stub = (email: () => Response, onWrite: (method: string) => Response) =>
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string | Request, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.url;
      const method = (typeof input === 'string' ? init?.method : input.method) ?? 'GET';
      if (url.endsWith(`/members/${MEMBER}/email`)) return method === 'GET' ? email() : onWrite(method);
      if (url.includes('/teams')) return json(200, { items: [], continuationToken: null });
      return json(200, member);
    }),
  );

const admin = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })) });

afterEach(() => vi.unstubAllGlobals());

describe('member email row', () => {
  it('shows that the member has no address and sets one', async () => {
    stub(
      () => json(404, { status: 404 }),
      () => json(200, { address: 'ada@example.org', state: 'unverified', verifiedAt: null, version: 1 }),
    );
    renderApp(admin(), `/members/${MEMBER}`);
    expect(await screen.findByText('This member has no email address.')).toBeInTheDocument();
    await userEvent.type(screen.getByRole('textbox', { name: /member's email address/i }), 'ada@example.org');
    await userEvent.click(screen.getByRole('button', { name: 'Set address' }));
    expect(await screen.findByText(/we sent a confirmation link/i)).toBeInTheDocument();
    const put = calls().find((c) => methodOf(c) === 'PUT');
    expect(put && urlOf(put)).toContain(`/api/v1/members/${MEMBER}/email`);
  });

  it('shows the state and sends the version to remove the address', async () => {
    stub(
      () => json(200, { address: 'ada@example.org', state: 'verified', verifiedAt: '2026-10-13T10:00:00Z', version: 4 }),
      () => new Response(null, { status: 204 }),
    );
    renderApp(admin(), `/members/${MEMBER}`);
    expect(await screen.findByText(/ada@example.org/)).toBeInTheDocument();
    expect(screen.getByText(/Confirmed\./)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Remove address' }));
    expect(await screen.findByText('Address removed.')).toBeInTheDocument();
    const del = calls().find((c) => methodOf(c) === 'DELETE');
    const headers = typeof del?.[0] === 'string' ? del[1]?.headers : (del?.[0] as Request).headers;
    expect(new Headers(headers as HeadersInit).get('If-Match')).toBe('"4"');
  });

  it('shows the refusal of an invalid address', async () => {
    stub(
      () => json(404, { status: 404 }),
      () => json(400, { status: 400, code: 'email-address-invalid' }),
    );
    renderApp(admin(), `/members/${MEMBER}`);
    await userEvent.type(await screen.findByRole('textbox', { name: /member's email address/i }), 'nope');
    await userEvent.click(screen.getByRole('button', { name: 'Set address' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('not a valid email address');
  });
});
