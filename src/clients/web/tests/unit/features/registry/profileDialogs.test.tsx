import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { ApproveProfileDialog } from '../../../../src/features/registry/ApproveProfileDialog';
import { RetireProfileDialog } from '../../../../src/features/registry/RetireProfileDialog';
import { RegistryChangedError, RegistryRefusalError, type ProfileVersion } from '../../../../src/platform/models/registry';
import { ADMIN, approvedProfile, draftProfile, fakeRegistryAdapter, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown, memberId: '' }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({
  useSession: () => ({ platform: {}, session: { member: { id: state.memberId } } }),
}));

function renderDialog(adapter: FakeRegistryAdapter, kind: 'approve' | 'retire', profile: ProfileVersion, memberId = 'someone-else') {
  state.adapter = adapter;
  state.memberId = memberId;
  const onClose = vi.fn();
  const Dialog = kind === 'approve' ? ApproveProfileDialog : RetireProfileDialog;
  render(
    <QueryClientProvider client={new QueryClient()}>
      <Dialog profile={profile} open onClose={onClose} />
    </QueryClientProvider>,
  );
  return onClose;
}

describe('profile dialogs', () => {
  it('approval names the version and digests, states the consequence, and sends one request on confirm', async () => {
    const adapter = fakeRegistryAdapter();
    renderDialog(adapter, 'approve', draftProfile);
    const body = screen.getByText(/Approve ball-tracking-standard version 1/);
    expect(body).toHaveTextContent(draftProfile.imageDigest);
    expect(body).toHaveTextContent(draftProfile.content.modelDigest);
    expect(body).toHaveTextContent('Approved versions never change');
    expect(screen.queryByText(/approving your own work/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Approve version' }));
    await waitFor(() => expect(screen.getByText('Profile version approved.')).toBeInTheDocument());
    expect(adapter.approveProfile).toHaveBeenCalledTimes(1);
    expect(adapter.approveProfile).toHaveBeenCalledWith('ball-tracking-standard', 1, draftProfile.rowVersion);
  });

  it('approval adds the self-approval note for the creator', () => {
    renderDialog(fakeRegistryAdapter(), 'approve', draftProfile, ADMIN.id);
    expect(screen.getByText(/You are approving your own work/)).toBeInTheDocument();
  });

  it('retirement states what stays usable and sends one request on confirm', async () => {
    const adapter = fakeRegistryAdapter();
    renderDialog(adapter, 'retire', approvedProfile);
    const body = screen.getByText(/Retire possession-standard version 2/);
    expect(body).toHaveTextContent(approvedProfile.imageDigest);
    expect(body).toHaveTextContent('cannot be used in new workflow versions');
    expect(body).toHaveTextContent('Existing workflow versions and analysis runs keep it');
    await userEvent.click(screen.getByRole('button', { name: 'Retire version' }));
    await waitFor(() => expect(screen.getByText('Profile version retired.')).toBeInTheDocument());
    expect(adapter.retireProfile).toHaveBeenCalledTimes(1);
    expect(adapter.retireProfile).toHaveBeenCalledWith('possession-standard', 2, approvedProfile.rowVersion);
  });

  it('cancelling sends nothing', async () => {
    const adapter = fakeRegistryAdapter();
    const onClose = renderDialog(adapter, 'retire', approvedProfile);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
    expect(adapter.retireProfile).not.toHaveBeenCalled();
    expect(adapter.approveProfile).not.toHaveBeenCalled();
  });

  it('shows a refusal with the refusal alert', async () => {
    const refusal = { operation: 'retireAnalystProfile' as const, code: 'profile-in-default-workflow', details: [], retryable: false };
    const adapter = fakeRegistryAdapter({ retireProfile: vi.fn(async () => { throw new RegistryRefusalError(refusal); }) });
    renderDialog(adapter, 'retire', approvedProfile);
    await userEvent.click(screen.getByRole('button', { name: 'Retire version' }));
    expect(await screen.findByText('The profile version is used by the default workflow and cannot be retired.')).toBeInTheDocument();
    expect(adapter.retireProfile).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Retire version' })).not.toBeInTheDocument();
  });

  it('shows the has-changed message with the reloaded state', async () => {
    const adapter = fakeRegistryAdapter({
      approveProfile: vi.fn(async () => { throw new RegistryChangedError(); }),
      getProfile: vi.fn(async () => ({ ...draftProfile, state: 'retired' as const })),
    });
    renderDialog(adapter, 'approve', draftProfile);
    await userEvent.click(screen.getByRole('button', { name: 'Approve version' }));
    expect(await screen.findByText('The record has changed')).toBeInTheDocument();
    expect(await screen.findByText('Current state: Retired')).toBeInTheDocument();
    expect(adapter.approveProfile).toHaveBeenCalledTimes(1);
  });
});
