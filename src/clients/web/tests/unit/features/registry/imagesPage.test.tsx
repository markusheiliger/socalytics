import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { ImagesPage } from '../../../../src/features/registry/ImagesPage';
import { fakeRegistryAdapter, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeRegistryAdapter, path = '/registry/images') {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/registry/images', element: <ImagesPage /> }], { initialEntries: [path] });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('ImagesPage', () => {
  it('lists images with capability, version, shortened digest, and registration time', async () => {
    renderPage(fakeRegistryAdapter());
    const list = await screen.findByRole('list', { name: 'Analyst images' });
    const entries = within(list).getAllByRole('listitem', {}).filter((li) => li.querySelector('a'));
    expect(entries).toHaveLength(2);
    expect(within(list).getByRole('link', { name: 'ball-tracking 1.0.0' })).toHaveAttribute('href', expect.stringContaining('/registry/images/sha256%3Aaaaaaaa'));
    expect(within(list).getByText('sha256:aaaaaaaaaaaa')).toBeInTheDocument();
    expect(within(list).getAllByText(/Registered /)).toHaveLength(2);
  });

  it('filters by the capabilityId search parameter', async () => {
    const adapter = fakeRegistryAdapter();
    renderPage(adapter, '/registry/images?capabilityId=possession');
    expect(await screen.findByRole('link', { name: 'possession 2.1.0' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'ball-tracking 1.0.0' })).not.toBeInTheDocument();
    expect(adapter.listImages).toHaveBeenCalledWith({ capabilityId: 'possession' });
  });

  it('keeps the filter in the address and clears it', async () => {
    const router = renderPage(fakeRegistryAdapter());
    await screen.findByRole('list', { name: 'Analyst images' });
    await userEvent.type(screen.getByLabelText('Capability'), 'possession');
    await userEvent.click(screen.getByRole('button', { name: 'Apply filter' }));
    expect(router.state.location.search).toBe('?capabilityId=possession');
    await userEvent.click(screen.getByRole('button', { name: 'Clear filter' }));
    expect(router.state.location.search).toBe('');
  });

  it('shows empty states', async () => {
    renderPage(fakeRegistryAdapter({ listImages: vi.fn(async () => []) }));
    expect(await screen.findByText('No Analyst images are registered.')).toBeInTheDocument();
  });

  it('shows the filtered empty state', async () => {
    renderPage(fakeRegistryAdapter({ listImages: vi.fn(async () => []) }), '/registry/images?capabilityId=x');
    expect(await screen.findByText('No Analyst images match the filter.')).toBeInTheDocument();
  });
});
