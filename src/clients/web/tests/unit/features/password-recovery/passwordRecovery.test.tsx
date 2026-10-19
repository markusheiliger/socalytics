import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createPasswordRecoveryAdapter } from '../../../../src/platform/adapters/passwordRecoveryAdapter';
import { PlatformFailure } from '../../../../src/platform/failure';
import { bodyOf, ok, problem, setup } from '../../platform/adapters/support';
import { fakeAdapter, renderApp } from '../../session/helpers';

const signedOut = () => fakeAdapter({ restore: vi.fn().mockRejectedValue(new PlatformFailure('sessionEnded', { status: 401 })) });

const stubFetch = (handler: (url: string, init: RequestInit) => Response) =>
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string | Request, init: RequestInit = {}) => handler(typeof input === 'string' ? input : input.url, init)),
  );

const fetchCalls = () => (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls as [string | Request, RequestInit][];
const urlOf = (c: [string | Request, RequestInit]) => (typeof c[0] === 'string' ? c[0] : c[0].url);
const sentBody = (c: [string | Request, RequestInit] | undefined) => JSON.parse(new TextDecoder().decode(c?.[1].body as ArrayBuffer));

const json = (status: number, body?: unknown, headers: Record<string, string> = {}) =>
  new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });

const recoveryPath = (url: string) => /\/api\/v1\/password-recovery\/(requests|checks|completions)$/.exec(url)?.[1];
const callTo = (operation: string) => fetchCalls().find((c) => recoveryPath(urlOf(c)) === operation);

afterEach(() => vi.unstubAllGlobals());

describe('password recovery adapter mapping', () => {
  it('maps success, the neutral invalid answer, field problems, and the wait time', async () => {
    const accepted = createPasswordRecoveryAdapter(setup(() => ok(202)).api);
    expect(await accepted.requestRecovery('ada@example.org')).toEqual({ ok: true, value: undefined });

    const checked = setup(() => ok(200, { accountName: 'ada' }));
    expect(await createPasswordRecoveryAdapter(checked.api).checkRecovery('tok')).toEqual({ ok: true, value: 'ada' });
    expect(bodyOf(checked.calls[0])).toEqual({ token: 'tok' });

    const invalid = createPasswordRecoveryAdapter(setup(() => problem(400, 'password-recovery-invalid')).api);
    expect(await invalid.checkRecovery('tok')).toEqual({ ok: false, kind: 'invalid' });

    const policy = createPasswordRecoveryAdapter(
      setup(() => problem(400, 'validation-failed', [{ field: 'newPassword', code: 'password-policy' }])).api,
    );
    expect(await policy.completeRecovery('tok', 'x')).toEqual({
      ok: false,
      kind: 'fieldErrors',
      fieldErrors: [{ field: 'newPassword', code: 'password-policy' }],
    });

    const limited = createPasswordRecoveryAdapter(
      setup(
        () =>
          new Response(JSON.stringify({ status: 429 }), {
            status: 429,
            headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '30' },
          }),
      ).api,
    );
    expect(await limited.requestRecovery('ada@example.org')).toEqual({ ok: false, kind: 'rateLimited', retryAfterSeconds: 30 });
  });
});

describe('sign-in link', () => {
  it('leads from the sign-in page to the request page', async () => {
    const { router } = renderApp(signedOut(), '/sign-in');
    await userEvent.click(await screen.findByRole('link', { name: 'Forgot your password?' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/password-recovery'));
    expect(await screen.findByRole('heading', { name: 'Reset your password' })).toBeInTheDocument();
  });
});

describe('password recovery request page', () => {
  it('sends the trimmed address and shows the generic confirmation', async () => {
    stubFetch(() => json(202));
    renderApp(signedOut(), '/password-recovery');
    await userEvent.type(await screen.findByLabelText(/email address/i), '  ada@example.org ');
    await userEvent.click(screen.getByRole('button', { name: 'Send reset link' }));
    expect(await screen.findByText(/If this address belongs to an account/)).toBeInTheDocument();
    expect(callTo('requests')?.[1].method).toBe('POST');
    expect(sentBody(callTo('requests'))).toEqual({ address: 'ada@example.org' });
  });

  it('shows a field problem for an invalid address', async () => {
    stubFetch(() => json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'address', code: 'invalid' }] }));
    renderApp(signedOut(), '/password-recovery');
    await userEvent.type(await screen.findByLabelText(/email address/i), 'nope');
    await userEvent.click(screen.getByRole('button', { name: 'Send reset link' }));
    expect(await screen.findByText('This is not a valid email address.')).toBeInTheDocument();
    expect(screen.queryByText(/If this address belongs to an account/)).not.toBeInTheDocument();
  });

  it('shows the wait time after a rate limit', async () => {
    stubFetch(() => json(429, { status: 429, code: 'password-recovery-rate-limited' }, { 'Retry-After': '42' }));
    renderApp(signedOut(), '/password-recovery');
    await userEvent.type(await screen.findByLabelText(/email address/i), 'ada@example.org');
    await userEvent.click(screen.getByRole('button', { name: 'Send reset link' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again in 42 seconds');
  });
});

describe('password recovery page', () => {
  const usable = (completion: () => Response = () => json(204)) =>
    stubFetch((url) => {
      const operation = recoveryPath(url);
      if (operation === 'checks') return json(200, { accountName: 'ada' });
      if (operation === 'completions') return completion();
      return json(404, { status: 404 });
    });

  const fill = async (password: string, confirmation: string) => {
    await userEvent.type(await screen.findByLabelText(/^new password/i), password);
    await userEvent.type(screen.getByLabelText(/^confirm new password/i), confirmation);
    await userEvent.click(screen.getByRole('button', { name: 'Set password' }));
  };

  it('removes the token from the address bar and shows the account name', async () => {
    usable();
    const { router } = renderApp(signedOut(), '/password-recovery/complete#token=secret-token');
    expect(await screen.findByLabelText(/account name/i)).toHaveValue('ada');
    expect(router.state.location.hash).toBe('');
    expect(window.location.hash).toBe('');
    expect(sentBody(callTo('checks'))).toEqual({ token: 'secret-token' });
  });

  it('reports a mismatch without calling the platform', async () => {
    usable();
    renderApp(signedOut(), '/password-recovery/complete#token=t');
    await fill('Secret-123', 'Secret-124');
    expect(await screen.findByRole('alert')).toHaveTextContent('The two passwords do not match.');
    expect(callTo('completions')).toBeUndefined();
  });

  it('reports a policy problem and keeps the form usable', async () => {
    usable(() => json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'newPassword', code: 'password-policy' }] }));
    renderApp(signedOut(), '/password-recovery/complete#token=t');
    await fill('short', 'short');
    expect(await screen.findByRole('alert')).toHaveTextContent('does not meet the password requirements');
    expect(screen.getByRole('button', { name: 'Set password' })).toBeInTheDocument();
  });

  it('confirms success and leads to sign-in', async () => {
    usable();
    const { router } = renderApp(signedOut(), '/password-recovery/complete#token=t');
    await fill('Secret-123', 'Secret-123');
    expect(await screen.findByText(/Your password has been changed/)).toBeInTheDocument();
    expect(sentBody(callTo('completions'))).toEqual({ token: 't', newPassword: 'Secret-123' });
    await userEvent.click(screen.getByRole('link', { name: 'Go to sign in' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/sign-in'));
  });

  it('shows one neutral message with a link to request a new one for an invalid link', async () => {
    stubFetch(() => json(400, { status: 400, code: 'password-recovery-invalid' }));
    renderApp(signedOut(), '/password-recovery/complete#token=old');
    expect(await screen.findByRole('alert')).toHaveTextContent('This link cannot be used.');
    expect(screen.getByRole('link', { name: 'Request a new link' })).toHaveAttribute('href', '/password-recovery');
    expect(screen.queryByLabelText(/^new password/i)).not.toBeInTheDocument();
  });

  it('treats a missing token as an invalid link without calling the platform', async () => {
    stubFetch(() => json(500, {}));
    renderApp(signedOut(), '/password-recovery/complete');
    expect(await screen.findByRole('alert')).toHaveTextContent('This link cannot be used.');
    expect(fetchCalls().some((c) => recoveryPath(urlOf(c)) !== undefined)).toBe(false);
  });
});
