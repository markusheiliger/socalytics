import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PlatformFailure } from '../../../../src/platform/failure';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const ended = () => new PlatformFailure('sessionEnded', { status: 401 });

async function fillSetPassword(code = 'CODE-123', password = 'New-Password-1!', confirmation = password) {
  await userEvent.type(await screen.findByLabelText(/sign-in name/i), 'ada');
  await userEvent.type(screen.getByLabelText(/^code/i), code);
  await userEvent.type(screen.getByLabelText(/^new password/i), password);
  await userEvent.type(screen.getByLabelText(/^repeat the new password/i), confirmation);
}

describe('set-password view', () => {
  const signedOut = (overrides = {}) => fakeAdapter({ restore: vi.fn().mockRejectedValue(ended()), ...overrides });

  it('redeems the code, keeps it out of the address, and offers sign-in', async () => {
    const adapter = signedOut();
    const { router } = renderApp(adapter, '/set-password');
    await fillSetPassword();
    await userEvent.click(screen.getByRole('button', { name: 'Set password' }));
    await screen.findByText(/your password is set/i);
    expect(adapter.redeemCredential).toHaveBeenCalledWith('ada', 'CODE-123', 'New-Password-1!');
    expect(router.state.location.pathname + router.state.location.search + router.state.location.hash).toBe('/set-password');
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain('CODE-123');
    expect(screen.getByRole('link', { name: 'Go to sign-in' })).toHaveAttribute('href', '/sign-in');
  });

  it('asks to contact a Club Admin when the code is expired, used, or revoked', async () => {
    const adapter = signedOut({
      redeemCredential: vi.fn().mockRejectedValue(new PlatformFailure('validation', { status: 400, code: 'credential-invalid' })),
    });
    renderApp(adapter, '/set-password');
    await fillSetPassword();
    await userEvent.click(screen.getByRole('button', { name: 'Set password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/expired, already used, or revoked.*Club Admin/i);
    expect(screen.getByLabelText(/^code/i)).toHaveValue('');
  });

  it('shows a password-policy refusal clearly', async () => {
    const adapter = signedOut({
      redeemCredential: vi.fn().mockRejectedValue(
        new PlatformFailure('validation', {
          status: 400,
          code: 'validation-failed',
          fieldErrors: [{ field: 'newPassword', code: 'password-policy' }],
        }),
      ),
    });
    renderApp(adapter, '/set-password');
    await fillSetPassword();
    await userEvent.click(screen.getByRole('button', { name: 'Set password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/password policy/i);
  });

  it('does not send mismatching passwords', async () => {
    const adapter = signedOut();
    renderApp(adapter, '/set-password');
    await fillSetPassword('CODE-123', 'New-Password-1!', 'Other-Password-2!');
    await userEvent.click(screen.getByRole('button', { name: 'Set password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/do not match/i);
    expect(adapter.redeemCredential).not.toHaveBeenCalled();
  });

  it('asks to sign out first while a member is signed in and shows no form', async () => {
    const adapter = fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) });
    renderApp(adapter, '/set-password');
    expect(await screen.findByText(/sign out before you set a password/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/^code/i)).toBeNull();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
    expect(adapter.redeemCredential).not.toHaveBeenCalled();
  });
});

describe('account view', () => {
  const signedIn = (overrides = {}) => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()), ...overrides });

  async function fill(current = 'Old-Password-1!', next = 'New-Password-1!', confirmation = next) {
    await userEvent.type(await screen.findByLabelText(/^current password/i), current);
    await userEvent.type(screen.getByLabelText(/^new password/i), next);
    await userEvent.type(screen.getByLabelText(/^repeat the new password/i), confirmation);
  }

  it('changes the own password and clears the fields', async () => {
    const adapter = signedIn();
    renderApp(adapter, '/account');
    await screen.findByRole('heading', { level: 2, name: 'Account' });
    await fill();
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    await screen.findByText('Your password was changed.');
    expect(adapter.changeOwnPassword).toHaveBeenCalledWith('Old-Password-1!', 'New-Password-1!');
    expect(screen.getByLabelText(/^current password/i)).toHaveValue('');
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain('New-Password-1!');
  });

  it('shows the platform refusal of a password that fails the policy', async () => {
    const adapter = signedIn({
      changeOwnPassword: vi.fn().mockRejectedValue(
        new PlatformFailure('validation', { status: 400, fieldErrors: [{ field: 'newPassword', code: 'password-policy' }] }),
      ),
    });
    renderApp(adapter, '/account');
    await fill();
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/password policy/i);
  });

  it('does not send mismatching passwords', async () => {
    const adapter = signedIn();
    renderApp(adapter, '/account');
    await fill('Old-Password-1!', 'New-Password-1!', 'Different-1!');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/do not match/i);
    expect(adapter.changeOwnPassword).not.toHaveBeenCalled();
  });

  it('shows a refused anti-forgery check as a session problem', async () => {
    const adapter = signedIn({
      changeOwnPassword: vi.fn().mockRejectedValue(new PlatformFailure('sessionProblem', { status: 403 })),
    });
    renderApp(adapter, '/account');
    await fill();
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByText(/could not be verified/i)).toBeInTheDocument();
  });
});
