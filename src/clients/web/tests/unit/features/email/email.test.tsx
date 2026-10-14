import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });

const stubFetch = (handler: (url: string, init: RequestInit) => Response) =>
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string | Request, init: RequestInit = {}) => handler(typeof input === 'string' ? input : input.url, init)),
  );

const calls = () => (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls as [string | Request, RequestInit][];
const urlOf = (c: [string | Request, RequestInit]) => (typeof c[0] === 'string' ? c[0] : c[0].url);

describe('email section of the account view', () => {
  const signedIn = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) });

  it('offers to set an address when none exists and sends it', async () => {
    stubFetch((_url, init) =>
      init.method === 'PUT'
        ? json(200, { address: 'ada@example.org', state: 'unverified', verifiedAt: null, version: 1 })
        : json(404, { status: 404 }),
    );
    renderApp(signedIn(), '/account');
    expect(await screen.findByText('You have no email address yet.')).toBeInTheDocument();
    await userEvent.type(screen.getByRole('textbox', { name: /email address/i }), 'ada@example.org');
    await userEvent.click(screen.getByRole('button', { name: 'Set address' }));
    expect(await screen.findByText(/we sent a confirmation link/i)).toBeInTheDocument();
    const put = calls().find((c) => c[1].method === 'PUT');
    expect(put && urlOf(put)).toContain('/api/v1/me/email');
    expect(String(put?.[1].body ?? '')).not.toBe('');
  });

  it('shows the state, offers resend and remove, and reports the wait time when limited', async () => {
    stubFetch((url, init) => {
      if (url.endsWith('/confirmation-requests')) return json(429, { status: 429, code: 'email-confirmation-rate-limited' }, { 'Retry-After': '90' });
      return init.method === 'GET' || !init.method
        ? json(200, { address: 'ada@example.org', state: 'unverified', verifiedAt: null, version: 3 })
        : json(404, {});
    });
    renderApp(signedIn(), '/account');
    expect(await screen.findByText(/ada@example.org/)).toBeInTheDocument();
    expect(screen.getByText(/not confirmed yet/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove address' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Send a new link' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again in 90 seconds');
  });

  it('shows the field refusal of an invalid address', async () => {
    stubFetch((_url, init) =>
      init.method === 'PUT'
        ? json(400, { status: 400, code: 'email-address-invalid' })
        : json(404, { status: 404 }),
    );
    renderApp(signedIn(), '/account');
    await userEvent.type(await screen.findByRole('textbox', { name: /email address/i }), 'nope');
    await userEvent.click(screen.getByRole('button', { name: 'Set address' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('not a valid email address');
  });
});

describe('email confirmation page', () => {
  const signedOut = () => fakeAdapter({ restore: vi.fn().mockRejectedValue(Object.assign(new Error('x'), { kind: 'sessionEnded' })) });

  it('removes the token from the address and confirms only on the click', async () => {
    stubFetch(() => new Response(null, { status: 204 }));
    const { router } = renderApp(signedOut(), '/email/confirm#token=SECRET-TOKEN-1');
    const button = await screen.findByRole('button', { name: 'Confirm this address' });
    await waitFor(() => expect(router.state.location.hash).toBe(''));
    expect(window.location.href).not.toContain('SECRET-TOKEN-1');
    expect(calls()).toHaveLength(0);
    await userEvent.click(button);
    expect(await screen.findByText('Your address is confirmed.')).toBeInTheDocument();
    const post = calls().find((c) => c[1].method === 'POST');
    expect(post && urlOf(post)).toContain('/api/v1/email-confirmations');
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain('SECRET-TOKEN-1');
  });

  it('shows one generic message for an invalid link', async () => {
    stubFetch(() => json(400, { status: 400, code: 'email-confirmation-invalid' }));
    renderApp(signedOut(), '/email/confirm#token=OLD');
    await userEvent.click(await screen.findByRole('button', { name: 'Confirm this address' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/invalid, expired, or already used/i);
  });

  it('shows the wait time when rate limited', async () => {
    stubFetch(() => json(429, { status: 429 }, { 'Retry-After': '30' }));
    renderApp(signedOut(), '/email/confirm#token=T');
    await userEvent.click(await screen.findByRole('button', { name: 'Confirm this address' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again in 30 seconds');
  });
});
