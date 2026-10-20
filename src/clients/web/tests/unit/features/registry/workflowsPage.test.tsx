import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { WorkflowsPage } from '../../../../src/features/registry/WorkflowsPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { fakeRegistryAdapter, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/platform/adapters/memberAdapter', () => ({ createMemberAdapter: () => ({ list: async () => [] }) }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeRegistryAdapter) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/registry/workflows', element: <WorkflowsPage /> }], { initialEntries: ['/registry/workflows'] });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('WorkflowsPage', () => {
  it('lists versions with shortened digest, creation time, and the default marker', async () => {
    renderPage(fakeRegistryAdapter());
    const list = await screen.findByRole('list', { name: 'Workflow versions' });
    const first = within(list).getByRole('link', { name: 'standard-analysis version 1.0.0' });
    expect(first).toHaveAttribute('href', '/registry/workflows/standard-analysis/versions/1.0.0');
    expect(within(list).getByRole('link', { name: 'standard-analysis version 1.1.0' })).toBeInTheDocument();
    expect(within(list).getAllByText('sha256:111111111111')).toHaveLength(2);
    expect(within(list).getAllByText(/^Created /)).toHaveLength(2);
    expect(within(list).getAllByText('Default')).toHaveLength(1);
    expect(first.closest('li')).toHaveTextContent('Default');
  });

  it('marks no version when there is no default', async () => {
    renderPage(fakeRegistryAdapter({ getDefault: vi.fn(async () => null) }));
    await screen.findByRole('list', { name: 'Workflow versions' });
    expect(screen.queryByText('Default')).not.toBeInTheDocument();
  });

  it('shows the empty state', async () => {
    renderPage(fakeRegistryAdapter({ listWorkflows: vi.fn(async () => []) }));
    expect(await screen.findByText('No workflows are defined.')).toBeInTheDocument();
  });

  it('shows an error with reload', async () => {
    renderPage(fakeRegistryAdapter({ listWorkflows: vi.fn(async () => { throw new PlatformFailure('unexpected', { status: 500 }); }) }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('shows the not-permitted view for a refusal', async () => {
    renderPage(fakeRegistryAdapter({ listWorkflows: vi.fn(async () => { throw new PlatformFailure('notPermitted', { status: 403 }); }) }));
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
  });
});
