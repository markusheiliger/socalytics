import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import { ConnectivityProvider } from '../../../src/connectivity/ConnectivityProvider';
import { SessionProvider } from '../../../src/session/SessionProvider';
import { PlatformFailure } from '../../../src/platform/failure';
import { useSession } from '../../../src/session/sessionContext';
import { fakeAdapter, makeSession, renderApp } from './helpers';

const heading2 = (name: string) => screen.findByRole('heading', { level: 2, name });

describe('session states and guards', () => {
  it('shows loading while restoring, then sign-in when signed out', async () => {
    let reject!: (e: unknown) => void;
    const adapter = fakeAdapter({ restore: () => new Promise((_, r) => (reject = r)) });
    const { router } = renderApp(adapter, '/account');
    expect(screen.getByRole('progressbar')).toBeInTheDocument();
    await act(async () => reject(new PlatformFailure('sessionEnded', { status: 401 })));
    await screen.findByRole('heading', { level: 1, name: 'Sign in' });
    expect(router.state.location.pathname).toBe('/sign-in');
  });

  it('stays unknown with the failure when the platform is unreachable', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockRejectedValue(new PlatformFailure('unreachable')) });
    const { router } = renderApp(adapter, '/account');
    await waitFor(() => expect(adapter.restore).toHaveBeenCalled());
    expect(screen.getByRole('progressbar')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/account');
  });

  it('opens signed-in routes and redirects sign-in to home when signed in', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) });
    const { router } = renderApp(adapter, '/sign-in');
    await heading2('Home');
    expect(router.state.location.pathname).toBe('/');
  });

  it('routes every protected route to /password-change while a change is required', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ passwordChangeRequired: true })) });
    const { router } = renderApp(adapter, '/account');
    await screen.findByRole('heading', { level: 1, name: 'Change your password' });
    expect(router.state.location.pathname).toBe('/password-change');
    await act(() => router.navigate('/club'));
    expect(router.state.location.pathname).toBe('/password-change');
  });

  it('keeps /password-change for password-change members only', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) });
    const { router } = renderApp(adapter, '/password-change');
    await heading2('Home');
    expect(router.state.location.pathname).toBe('/');
  });

  it('shows the not-permitted view for routes the roles do not allow', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['Registrar'] })),
    });
    const { router } = renderApp(adapter, '/members');
    expect(await screen.findByRole('alert')).toHaveTextContent('not permitted');
    expect(screen.queryByRole('heading', { level: 2 })).not.toBeInTheDocument();
    await act(() => router.navigate('/teams'));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('allows Club Admins everywhere and team members on their own team only', async () => {
    const admin = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })) });
    const first = renderApp(admin, '/teams/t-9');
    await heading2('Team');
    expect(first.router.state.location.pathname).toBe('/teams/t-9');
  });

  it('limits Coach and Viewer to their teams', async () => {
    const teamRoles = [{ teamId: 't-1', teamName: 'U12', seasonId: 'se-1', role: 'Coach' as const }];
    const adapter = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles })) });
    const { router } = renderApp(adapter, '/teams/t-1');
    await heading2('Team');
    await act(() => router.navigate('/teams/t-2'));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    await act(() => router.navigate('/teams'));
    await heading2('Teams');
  });

  it('shows not found for unknown routes', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) });
    renderApp(adapter, '/nope');
    await heading2('This page does not exist.');
  });
});

describe('session provider', () => {
  let api: ReturnType<typeof useSession>;
  const renderProvider = (adapter: ReturnType<typeof fakeAdapter>, queryClient = new QueryClient()) => {
    function Probe() {
      api = useSession();
      return <span data-testid="status">{api.status}</span>;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <ConnectivityProvider>
          <SessionProvider adapter={adapter}>
            <Probe />
          </SessionProvider>
        </ConnectivityProvider>
      </QueryClientProvider>,
    );
    return queryClient;
  };

  it('signs in, clears the cache, and keeps the token out of storage', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockRejectedValue(new PlatformFailure('sessionEnded')),
      signIn: vi.fn().mockResolvedValue(makeSession({ token: 'secret-token' })),
    });
    const queryClient = renderProvider(adapter);
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('signedOut'));
    queryClient.setQueryData(['old'], 1);
    await act(() => api.signIn('ada', 'pw'));
    expect(screen.getByTestId('status')).toHaveTextContent('signedIn');
    expect(queryClient.getQueryData(['old'])).toBeUndefined();
    expect(JSON.stringify(localStorage) + JSON.stringify(sessionStorage) + document.cookie).not.toContain('secret-token');
  });

  it('enters passwordChangeRequired when the session requires it', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockResolvedValue(makeSession({ passwordChangeRequired: true })),
    });
    renderProvider(adapter);
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('passwordChangeRequired'));
  });

  it('re-reads the session after a password change', async () => {
    const restore = vi
      .fn()
      .mockResolvedValueOnce(makeSession({ passwordChangeRequired: true }))
      .mockResolvedValueOnce(makeSession());
    renderProvider(fakeAdapter({ restore }));
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('passwordChangeRequired'));
    await act(() => api.changeOwnPassword('old', 'new'));
    expect(screen.getByTestId('status')).toHaveTextContent('signedIn');
  });

  it('signs out, clears the cache, and signs out even when the session already ended', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockResolvedValue(makeSession()),
      signOut: vi.fn().mockRejectedValue(new PlatformFailure('sessionEnded')),
    });
    const queryClient = renderProvider(adapter);
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('signedIn'));
    queryClient.setQueryData(['x'], 1);
    await act(() => api.signOut());
    expect(screen.getByTestId('status')).toHaveTextContent('signedOut');
    expect(api.reason).toBeUndefined();
    expect(queryClient.getQueryData(['x'])).toBeUndefined();
  });

  it('keeps the member signed in and rethrows when sign-out fails otherwise', async () => {
    const adapter = fakeAdapter({
      restore: vi.fn().mockResolvedValue(makeSession()),
      signOut: vi.fn().mockRejectedValue(new PlatformFailure('unexpected')),
    });
    renderProvider(adapter);
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('signedIn'));
    await act(async () => {
      await expect(api.signOut()).rejects.toMatchObject({ kind: 'unexpected' });
    });
    expect(screen.getByTestId('status')).toHaveTextContent('signedIn');
  });

  it('retries restoring when connectivity returns', async () => {
    const restore = vi
      .fn()
      .mockRejectedValueOnce(new PlatformFailure('unreachable'))
      .mockResolvedValueOnce(makeSession());
    renderProvider(fakeAdapter({ restore }));
    await waitFor(() => expect(restore).toHaveBeenCalledTimes(1));
    expect(screen.getByTestId('status')).toHaveTextContent('unknown');
    await waitFor(() => expect(api.restoreFailure?.kind).toBe('unreachable'));
  });

  it('passes redeem through without changing the session', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockRejectedValue(new PlatformFailure('sessionEnded')) });
    renderProvider(adapter);
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('signedOut'));
    await act(() => api.redeemCredential('ada', 'code', 'new'));
    expect(adapter.redeemCredential).toHaveBeenCalledWith('ada', 'code', 'new');
    expect(screen.getByTestId('status')).toHaveTextContent('signedOut');
  });
});
