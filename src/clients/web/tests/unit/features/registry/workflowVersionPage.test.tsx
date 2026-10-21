import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { WorkflowVersionPage } from '../../../../src/features/registry/WorkflowVersionPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { fakeRegistryAdapter, workflowVersion, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/platform/adapters/memberAdapter', () => ({
  createMemberAdapter: () => ({ list: async () => [{ id: '00000000-0000-4000-8000-000000000021', accountName: 'ada' }] }),
}));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeRegistryAdapter, version = '1.0.0') {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/registry/workflows/:workflowId/versions/:version', element: <WorkflowVersionPage /> }], {
    initialEntries: [`/registry/workflows/standard-analysis/versions/${version}`],
  });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('WorkflowVersionPage', () => {
  it('shows the definition fields, nodes linked to profile versions, and edges in words', async () => {
    const adapter = fakeRegistryAdapter();
    renderPage(adapter);
    expect(await screen.findByText('standard-analysis version 1.0.0')).toBeInTheDocument();
    expect(adapter.getWorkflow).toHaveBeenCalledWith('standard-analysis', '1.0.0');
    expect(screen.getByText('ball-tracking, possession')).toBeInTheDocument();
    expect(screen.getByText('All requested capabilities must complete')).toBeInTheDocument();
    expect(screen.getByText('300')).toBeInTheDocument();
    expect(screen.getByText('Development seed')).toBeInTheDocument();

    const nodes = screen.getByRole('list', { name: 'Nodes' });
    expect(within(nodes).getByText('Node tracking')).toBeInTheDocument();
    expect(within(nodes).getByRole('link', { name: 'ball-tracking-standard version 1' })).toHaveAttribute(
      'href',
      '/registry/profiles/ball-tracking-standard/versions/1',
    );
    expect(within(nodes).getByRole('link', { name: 'possession-standard version 2' })).toBeInTheDocument();

    const edges = screen.getByRole('list', { name: 'Edges' });
    expect(within(edges).getByText('tracking → possession (Required)')).toBeInTheDocument();
    expect(within(edges).getByText('only when capability possession is requested')).toBeInTheDocument();
    expect(within(edges).getByText('only when node tracking is accepted')).toBeInTheDocument();
  });

  it('marks the current default', async () => {
    renderPage(fakeRegistryAdapter());
    expect(await screen.findByText('This is the default workflow version.')).toBeInTheDocument();
  });

  it('does not mark a version that is not the default', async () => {
    renderPage(fakeRegistryAdapter(), '1.1.0');
    expect(await screen.findByText('standard-analysis version 1.1.0')).toBeInTheDocument();
    expect(await screen.findByText('ada')).toBeInTheDocument();
    expect(screen.queryByText('This is the default workflow version.')).not.toBeInTheDocument();
  });

  it('shows an unknown edge kind raw and a workflow without edges', async () => {
    const odd = { ...workflowVersion, edges: [{ upstreamNodeKey: 'tracking', dependentNodeKey: 'possession', kind: 'weird' }] };
    renderPage(fakeRegistryAdapter({ getWorkflow: vi.fn(async () => odd) }));
    expect(await screen.findByText('tracking → possession (weird)')).toBeInTheDocument();
  });

  it('shows a message for no edges', async () => {
    renderPage(fakeRegistryAdapter({ getWorkflow: vi.fn(async () => ({ ...workflowVersion, edges: [] })) }));
    expect(await screen.findByText('This workflow has no edges.')).toBeInTheDocument();
  });

  it('shows not-found for a 404', async () => {
    renderPage(fakeRegistryAdapter({ getWorkflow: vi.fn(async () => { throw new PlatformFailure('unexpected', { status: 404 }); }) }), '9.9.9');
    expect(await screen.findByText('This workflow version was not found.')).toBeInTheDocument();
  });

  it('shows the not-permitted view for a refusal', async () => {
    renderPage(fakeRegistryAdapter({ getWorkflow: vi.fn(async () => { throw new PlatformFailure('notPermitted', { status: 403 }); }) }));
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
  });
});
