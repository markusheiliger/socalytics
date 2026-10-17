import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PlatformFailure } from '../../../../src/platform/failure';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const ended = () => new PlatformFailure('sessionEnded', { status: 401 });

describe('sign-in view', () => {
  it('signs in and shows the home view', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockRejectedValue(ended()),
      signIn: vi.fn().mockResolvedValue(makeSession()),
    });
    const { router } = renderApp(adapter, '/sign-in');
    await userEvent.type(await screen.findByLabelText(/sign-in name/i), 'ada');
    await userEvent.type(screen.getByLabelText(/^password/i), 'secret-pw');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByRole('heading', { level: 2, name: 'Home' });
    expect(adapter.signIn).toHaveBeenCalledWith('ada', 'secret-pw');
    expect(router.state.location.pathname).toBe('/');
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain('secret-pw');
  });

  it('shows one generic refusal for every refusal reason and clears the password', async () => {
    const messages = new Set<string>();
    for (const status of [401, 403, 423, 400]) {
      const adapter = fakeAdapter({
        restore: vi.fn().mockRejectedValue(ended()),
        signIn: vi.fn().mockRejectedValue(new PlatformFailure(status === 401 ? 'sessionEnded' : 'notPermitted', { status })),
      });
      renderApp(adapter, '/sign-in');
      await userEvent.type(await screen.findByLabelText(/sign-in name/i), 'ada');
      await userEvent.type(screen.getByLabelText(/^password/i), 'wrong');
      await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
      messages.add((await screen.findByRole('alert')).textContent ?? '');
      expect(screen.getByLabelText(/^password/i)).toHaveValue('');
      cleanup();
    }
    expect(messages.size).toBe(1);
  });

  it('asks for cookies when the browser blocks them', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockRejectedValue(ended()), signIn: vi.fn() });
    const spy = vi.spyOn(navigator, 'cookieEnabled', 'get').mockReturnValue(false);
    renderApp(adapter, '/sign-in');
    await userEvent.type(await screen.findByLabelText(/sign-in name/i), 'ada');
    await userEvent.type(screen.getByLabelText(/^password/i), 'pw');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/cookies/i);
    expect(adapter.signIn).not.toHaveBeenCalled();
    spy.mockRestore();
  });
});

describe('external sign-in', () => {
  const signedOut = (overrides = {}) =>
    fakeAdapter({ restore: vi.fn().mockRejectedValue(ended()), ...overrides });

  it('shows no provider buttons when none is configured', async () => {
    renderApp(signedOut(), '/sign-in');
    await screen.findByLabelText(/sign-in name/i);
    expect(screen.queryByRole('button', { name: /Sign in with/ })).not.toBeInTheDocument();
  });

  it('shows one button per provider and navigates to the authorization URL', async () => {
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign });
    const adapter = signedOut({
      listProviders: vi.fn().mockResolvedValue([
        { id: 'club-entra', displayName: 'Club account' },
        { id: 'other', displayName: 'Other' },
      ]),
      startExternalSignIn: vi.fn().mockResolvedValue('https://login.example/authorize?state=1'),
    });
    renderApp(adapter, '/sign-in');
    await screen.findByRole('button', { name: 'Sign in with Other' });
    await userEvent.click(screen.getByRole('button', { name: 'Sign in with Club account' }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://login.example/authorize?state=1'));
    expect(adapter.startExternalSignIn).toHaveBeenCalledWith('club-entra');
    vi.unstubAllGlobals();
  });

  it('shows the generic refusal for ?external=failed', async () => {
    renderApp(signedOut(), '/sign-in?external=failed');
    expect(await screen.findByRole('alert')).toHaveTextContent(/Sign-in was not possible/);
  });

  it('shows a distinct message when the provider is unavailable and keeps the local form usable', async () => {
    const adapter = signedOut({
      listProviders: vi.fn().mockResolvedValue([{ id: 'club-entra', displayName: 'Club account' }]),
      startExternalSignIn: vi.fn().mockRejectedValue(new PlatformFailure('unexpected', { status: 503, code: 'provider-unavailable' })),
    });
    renderApp(adapter, '/sign-in');
    await userEvent.click(await screen.findByRole('button', { name: 'Sign in with Club account' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/currently unavailable/i);
    expect(screen.getByLabelText(/sign-in name/i)).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Sign in with Club account' })).toBeEnabled();
  });
});

describe('password-change and sign-out views', () => {
  it('shows only the password-change view and returns home after the change', async () => {
    const restore = vi.fn().mockResolvedValue(makeSession({ passwordChangeRequired: true }));
    const adapter = fakeAdapter({ restore });
    const { router } = renderApp(adapter, '/');
    await screen.findByRole('heading', { level: 1, name: 'Change your password' });
    expect(router.state.location.pathname).toBe('/password-change');
    restore.mockResolvedValue(makeSession());
    await userEvent.type(screen.getByLabelText(/^Current password/), 'old');
    await userEvent.type(screen.getByLabelText(/^New password/), 'new-password-1');
    await userEvent.type(screen.getByLabelText(/^Repeat the new password/), 'new-password-1');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    await screen.findByRole('heading', { level: 2, name: 'Home' });
    expect(adapter.changeOwnPassword).toHaveBeenCalledWith('old', 'new-password-1');
  });

  it('rejects a mismatching confirmation without calling the platform', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ passwordChangeRequired: true })) });
    renderApp(adapter, '/password-change');
    await userEvent.type(await screen.findByLabelText(/^Current password/), 'old');
    await userEvent.type(screen.getByLabelText(/^New password/), 'a');
    await userEvent.type(screen.getByLabelText(/^Repeat the new password/), 'b');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/do not match/i);
    expect(adapter.changeOwnPassword).not.toHaveBeenCalled();
  });

  it('shows a refused anti-forgery check as a session problem', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockResolvedValue(makeSession({ passwordChangeRequired: true })),
      changeOwnPassword: vi.fn().mockRejectedValue(new PlatformFailure('sessionProblem', { status: 403 })),
    });
    renderApp(adapter, '/password-change');
    await userEvent.type(await screen.findByLabelText(/^Current password/), 'old');
    await userEvent.type(screen.getByLabelText(/^New password/), 'n');
    await userEvent.type(screen.getByLabelText(/^Repeat the new password/), 'n');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByText(/could not be verified/i)).toBeInTheDocument();
  });

  it('signs out, returns to sign-in, and shows no member data after going back', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) });
    const { router, queryClient } = renderApp(adapter, '/');
    queryClient.setQueryData(['member-data'], { secret: 1 });
    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    await screen.findByRole('heading', { level: 1, name: 'Sign in' });
    expect(adapter.signOut).toHaveBeenCalled();
    expect(queryClient.getQueryData(['member-data'])).toBeUndefined();
    await act(async () => {
      await router.navigate('/');
    });
    await screen.findByRole('heading', { level: 1, name: 'Sign in' });
    expect(screen.queryByRole('heading', { level: 2, name: 'Home' })).not.toBeInTheDocument();
  });

  it('keeps the member signed in and says so when sign-out fails', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockResolvedValue(makeSession()),
      signOut: vi.fn().mockRejectedValue(new PlatformFailure('unreachable')),
    });
    renderApp(adapter, '/');
    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    expect(await screen.findByText('Signing out failed. Try again.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('heading', { level: 2, name: 'Home' })).toBeInTheDocument());
  });
});

describe('sign-in return', () => {
  const signInTo = async (path: string) => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockRejectedValue(ended()),
      signIn: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })),
    });
    const { router } = renderApp(adapter, path);
    await userEvent.type(await screen.findByLabelText(/sign-in name/i), 'ada');
    await userEvent.type(screen.getByLabelText(/^password/i), 'secret-pw');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(router.state.location.pathname).not.toBe('/sign-in'));
    return router;
  };

  it('sends a signed-out member from a guarded route to sign-in and back', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockRejectedValue(ended()),
      signIn: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })),
    });
    const { router } = renderApp(adapter, '/members');
    await screen.findByLabelText(/sign-in name/i);
    expect(router.state.location.pathname).toBe('/sign-in');
    expect(router.state.location.search).toBe('?returnTo=%2Fmembers');
    await userEvent.type(screen.getByLabelText(/sign-in name/i), 'ada');
    await userEvent.type(screen.getByLabelText(/^password/i), 'secret-pw');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/members'));
  });

  it.each(['//evil.example/members', 'https://evil.example/', '/no/such/route'])('goes home for %s', async (value) => {
    const router = await signInTo(`/sign-in?returnTo=${encodeURIComponent(value)}`);
    expect(router.state.location.pathname).toBe('/');
  });
});
