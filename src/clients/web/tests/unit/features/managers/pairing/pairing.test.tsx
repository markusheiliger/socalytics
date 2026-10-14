import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { PlatformFailure } from '../../../../../src/platform/failure';
import type { ClubRole } from '../../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });
const registration = {
  registrationId: '00000000-0000-4000-8000-000000000031',
  version: 2,
  state: 'pending_approval',
  deviceFingerprint: 'ABCD-EF12-3456',
  deviceMetadata: { os: 'linux', claimedKeyProtection: 'software', hostLabel: 'lab-pc' },
  approvalExpiresAt: '2030-01-01T12:00:00Z',
};
const as = (...clubRoles: ClubRole[]) => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles })) });

async function submit(code: string, fingerprint: string) {
  await userEvent.type(await screen.findByLabelText(/pairing code/i), code);
  await userEvent.type(screen.getByLabelText(/device fingerprint/i), fingerprint);
  await userEvent.click(screen.getByRole('button', { name: 'Submit' }));
}

afterEach(() => vi.unstubAllGlobals());

describe('pairing page', () => {
  it.each([['Registrar'], ['ClubAdmin']] as const)('shows the pending request to a %s and clears the form', async (role) => {
    const fetchMock = vi.fn(async () => json(201, registration));
    vi.stubGlobal('fetch', fetchMock);
    const { router } = renderApp(as(role), '/analyst-managers/pair');
    await submit('  ABCD-1234 ', ' ABCD-EF12-3456 ');
    expect(await screen.findByText('State: Pending approval')).toBeInTheDocument();
    expect(screen.getByText('Host label: lab-pc')).toBeInTheDocument();
    expect(screen.getByText(/Claimed key protection: Software/)).toBeInTheDocument();
    expect(screen.getByText('Operating system: linux')).toBeInTheDocument();
    expect(screen.getByText('Device fingerprint: ABCD-EF12-3456')).toBeInTheDocument();
    expect(screen.getByText(/Open for approval until/)).toBeInTheDocument();
    expect(screen.getByLabelText(/pairing code/i)).toHaveValue('');
    expect(screen.getByLabelText(/device fingerprint/i)).toHaveValue('');
    const calls = fetchMock.mock.calls as unknown as [string, RequestInit][];
    const post = calls.find(([, init]) => init.method === 'POST')!;
    expect(JSON.parse(new TextDecoder().decode(post[1].body as ArrayBuffer))).toEqual({ pairingCode: 'ABCD-1234', deviceFingerprint: 'ABCD-EF12-3456' });
    expect(router.state.location.search).toBe('');
  });

  it('shows one generic refusal and lets the member retry', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(400, { code: 'analyst-manager-pairing-code-invalid' })));
    renderApp(as('Registrar'), '/analyst-managers/pair');
    await submit('WRONG', 'WRONG');
    expect(await screen.findByRole('alert')).toHaveTextContent('The pairing code or device fingerprint was not accepted.');
    await userEvent.type(screen.getByLabelText(/pairing code/i), 'AGAIN');
    await userEvent.type(screen.getByLabelText(/device fingerprint/i), 'AGAIN');
    expect(screen.getByRole('button', { name: 'Submit' })).toBeEnabled();
  });

  it('shows the rate limit with the wait', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(429, { code: 'analyst-manager-rate-limited' }, { 'Retry-After': '42' })));
    renderApp(as('ClubAdmin'), '/analyst-managers/pair');
    await submit('A', 'B');
    expect(await screen.findByRole('alert')).toHaveTextContent('Too many attempts. Try again in 42 seconds.');
  });

  it('shows the not-permitted view without a Registrar or Club Admin role', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    renderApp(as(), '/analyst-managers/pair');
    expect(await screen.findByRole('alert')).toHaveTextContent('not permitted');
    expect(screen.queryByLabelText(/pairing code/i)).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('sends a signed-out visitor to sign-in with the return path', async () => {
    const { router } = renderApp(fakeAdapter({ restore: vi.fn().mockRejectedValue(new PlatformFailure('sessionEnded', { status: 401 })) }), '/analyst-managers/pair');
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/sign-in');
    expect(router.state.location.search).toBe('?returnTo=%2Fanalyst-managers%2Fpair');
  });
});
