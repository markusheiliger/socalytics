import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PlatformFailure } from '../../../../src/platform/failure';
import { fakeAdapter, renderApp } from '../../session/helpers';

const ended = () => new PlatformFailure('sessionEnded', { status: 401 });
const providers = [{ id: 'club-entra', displayName: 'Club account' }];
const signedOut = (overrides = {}) => fakeAdapter({ restore: vi.fn().mockRejectedValue(ended()), ...overrides });

afterEach(() => vi.unstubAllGlobals());

describe('link-account view', () => {
  it('starts the link redemption with the typed code and keeps it out of the address', async () => {
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign });
    const adapter = signedOut({
      listProviders: vi.fn().mockResolvedValue(providers),
      startExternalSignIn: vi.fn().mockResolvedValue('https://login.example/authorize?state=1'),
    });
    const { router } = renderApp(adapter, '/link-account');
    await userEvent.type(await screen.findByLabelText(/sign-in name/i), 'j.doe');
    await userEvent.type(screen.getByLabelText(/link code/i), 'LINK-CODE');
    await userEvent.click(screen.getByRole('button', { name: 'Continue to provider' }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://login.example/authorize?state=1'));
    expect(adapter.startExternalSignIn).toHaveBeenCalledWith('club-entra', { accountName: 'j.doe', linkCredential: 'LINK-CODE' });
    expect(router.state.location.search + router.state.location.pathname).not.toContain('LINK-CODE');
  });

  it('shows a generic failure and clears the code', async () => {
    const adapter = signedOut({
      listProviders: vi.fn().mockResolvedValue(providers),
      startExternalSignIn: vi.fn().mockRejectedValue(new PlatformFailure('validation', { status: 400 })),
    });
    renderApp(adapter, '/link-account');
    await userEvent.type(await screen.findByLabelText(/sign-in name/i), 'j.doe');
    await userEvent.type(screen.getByLabelText(/link code/i), 'BAD');
    await userEvent.click(screen.getByRole('button', { name: 'Continue to provider' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The account was not linked');
    expect(screen.getByLabelText(/link code/i)).toHaveValue('');
  });

  it('shows the generic failure after a failed callback', async () => {
    renderApp(signedOut({ listProviders: vi.fn().mockResolvedValue(providers) }), '/link-account?external=failed');
    expect(await screen.findByRole('alert')).toHaveTextContent('The account was not linked');
  });

  it('says so when no provider is configured', async () => {
    renderApp(signedOut(), '/link-account');
    expect(await screen.findByText(/No sign-in provider is configured/)).toBeInTheDocument();
  });
});
