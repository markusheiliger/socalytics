import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { ProfilesPage } from '../../../../src/features/registry/ProfilesPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { approvedProfile, draftProfile, fakeRegistryAdapter, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/platform/adapters/memberAdapter', () => ({ createMemberAdapter: () => ({ list: async () => [] }) }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeRegistryAdapter, path = '/registry/profiles') {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/registry/profiles', element: <ProfilesPage /> }], { initialEntries: [path] });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('ProfilesPage', () => {
  it('groups versions by profile id with state, capability, and shortened image digest', async () => {
    renderPage(fakeRegistryAdapter());
    const list = await screen.findByRole('list', { name: 'Analyst profile versions' });
    const group = within(list).getByRole('list', { name: 'Profile possession-standard' });
    expect(within(group).getByRole('link', { name: 'Version 2 – Approved' })).toHaveAttribute('href', '/registry/profiles/possession-standard/versions/2');
    expect(within(group).getByText('possession 2.1.0')).toBeInTheDocument();
    expect(within(group).getByText('sha256:bbbbbbbbbbbb')).toBeInTheDocument();
    expect(within(list).getByRole('list', { name: 'Profile ball-tracking-standard' })).toBeInTheDocument();
    expect(within(list).getByRole('link', { name: 'Version 3 – Retired' })).toBeInTheDocument();
  });

  it('filters by the state and capabilityId search parameters', async () => {
    const adapter = fakeRegistryAdapter();
    renderPage(adapter, '/registry/profiles?state=approved&capabilityId=possession');
    expect(await screen.findByRole('link', { name: 'Version 2 – Approved' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Version 1 – Draft' })).not.toBeInTheDocument();
    expect(adapter.listProfiles).toHaveBeenCalledWith({ state: 'approved', capabilityId: 'possession' });
  });

  it('keeps the filters in the address and clears them', async () => {
    const router = renderPage(fakeRegistryAdapter());
    await screen.findByRole('list', { name: 'Analyst profile versions' });
    await userEvent.selectOptions(screen.getByLabelText('State'), 'draft');
    await userEvent.type(screen.getByLabelText('Capability'), 'ball-tracking');
    await userEvent.click(screen.getByRole('button', { name: 'Apply filters' }));
    expect(router.state.location.search).toBe('?state=draft&capabilityId=ball-tracking');
    expect(await screen.findByRole('link', { name: `Version ${draftProfile.version} – Draft` })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: `Version ${approvedProfile.version} – Approved` })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(router.state.location.search).toBe('');
  });

  it('shows an unknown state raw', async () => {
    const odd = { ...draftProfile, state: 'suspended' };
    renderPage(fakeRegistryAdapter({ listProfiles: vi.fn(async () => [odd]) }));
    expect(await screen.findByRole('link', { name: 'Version 1 – suspended' })).toBeInTheDocument();
  });

  it('shows empty states', async () => {
    renderPage(fakeRegistryAdapter({ listProfiles: vi.fn(async () => []) }));
    expect(await screen.findByText('No Analyst profiles exist.')).toBeInTheDocument();
  });

  it('shows the filtered empty state', async () => {
    renderPage(fakeRegistryAdapter({ listProfiles: vi.fn(async () => []) }), '/registry/profiles?state=retired');
    expect(await screen.findByText('No profile versions match the filters.')).toBeInTheDocument();
  });

  it('shows the not-permitted view for a refusal', async () => {
    renderPage(fakeRegistryAdapter({ listProfiles: vi.fn(async () => { throw new PlatformFailure('notPermitted', { status: 403 }); }) }));
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
  });
});
