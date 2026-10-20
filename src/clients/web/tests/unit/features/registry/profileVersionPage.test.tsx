import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { ProfileVersionPage } from '../../../../src/features/registry/ProfileVersionPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { approvedProfile, draftProfile, fakeRegistryAdapter, retiredProfile, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/platform/adapters/memberAdapter', () => ({
  createMemberAdapter: () => ({ list: async () => [{ id: '00000000-0000-4000-8000-000000000021', accountName: 'ada' }] }),
}));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeRegistryAdapter, profileId: string, version: string) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/registry/profiles/:profileId/versions/:version', element: <ProfileVersionPage /> }], {
    initialEntries: [`/registry/profiles/${profileId}/versions/${version}`],
  });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('ProfileVersionPage', () => {
  it('shows every field with lifecycle times and member names', async () => {
    const adapter = fakeRegistryAdapter();
    renderPage(adapter, approvedProfile.profileId, String(approvedProfile.version));
    expect(await screen.findByText('possession-standard version 2')).toBeInTheDocument();
    expect(adapter.getProfile).toHaveBeenCalledWith('possession-standard', 2);
    expect(screen.getByText('Approved')).toBeInTheDocument();
    expect(screen.getByText('Forbidden')).toBeInTheDocument();
    expect(screen.getByText('linux/amd64')).toBeInTheDocument();
    expect(screen.getByText('cpu: 2')).toBeInTheDocument();
    expect(screen.getByText('ball-tracking: 1.0.0')).toBeInTheDocument();
    expect(screen.getByText('2.0.0')).toBeInTheDocument();
    expect(screen.getByText('900')).toBeInTheDocument();
    expect(screen.getByText('Approved at')).toBeInTheDocument();
    expect(screen.queryByText('Retired at')).not.toBeInTheDocument();
    expect((await screen.findAllByText('ada')).length).toBeGreaterThanOrEqual(2);
  });

  it('shows retirement and the development seed', async () => {
    renderPage(fakeRegistryAdapter(), retiredProfile.profileId, String(retiredProfile.version));
    expect(await screen.findByText('Retired at')).toBeInTheDocument();
    expect(screen.getByText('Development seed')).toBeInTheDocument();
  });

  it('shows no approval fields for a draft', async () => {
    renderPage(fakeRegistryAdapter(), draftProfile.profileId, '1');
    expect(await screen.findByText('Draft')).toBeInTheDocument();
    expect(screen.queryByText('Approved at')).not.toBeInTheDocument();
  });

  it('shows unknown values raw', async () => {
    const odd = { ...draftProfile, state: 'suspended', content: { ...draftProfile.content, videoPolicy: 'sometimes' } };
    renderPage(fakeRegistryAdapter({ getProfile: vi.fn(async () => odd) }), draftProfile.profileId, '1');
    expect(await screen.findByText('suspended')).toBeInTheDocument();
    expect(screen.getByText('sometimes')).toBeInTheDocument();
  });

  it('shows not-found for a non-integer version without a request', () => {
    const adapter = fakeRegistryAdapter();
    renderPage(adapter, 'ball-tracking-standard', '1.5');
    expect(screen.getByText('This profile version was not found.')).toBeInTheDocument();
    expect(adapter.getProfile).not.toHaveBeenCalled();
  });

  it('shows not-found for a 404', async () => {
    const getProfile = vi.fn(async () => { throw new PlatformFailure('unexpected', { status: 404 }); });
    renderPage(fakeRegistryAdapter({ getProfile }), 'missing', '9');
    expect(await screen.findByText('This profile version was not found.')).toBeInTheDocument();
  });

  it('shows the not-permitted view for a refusal', async () => {
    const getProfile = vi.fn(async () => { throw new PlatformFailure('notPermitted', { status: 403 }); });
    renderPage(fakeRegistryAdapter({ getProfile }), 'x', '1');
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
  });
});
