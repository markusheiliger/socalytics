import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { AgentPage } from '../../../../src/features/agent-catalog/AgentPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { fakeAgentCatalogAdapter, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/platform/adapters/memberAdapter', () => ({ createMemberAdapter: () => ({ list: async () => [] }) }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeAgentCatalogAdapter, entry: string) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/agent-catalog/agents/:agentId', element: <AgentPage /> }], { initialEntries: [entry] });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('AgentPage', () => {
  it('shows versions, the tool baseline, and the enablement', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, '/agent-catalog/agents/specialist-tactics');
    expect(await screen.findByRole('heading', { name: 'Agent specialist-tactics' })).toBeInTheDocument();
    const link = await screen.findByRole('link', { name: '0.1.0 (approved)' });
    expect(link).toHaveAttribute('href', '/agent-catalog/agents/specialist-tactics/versions/0.1.0');
    expect(screen.getByRole('link', { name: '0.1.1 (draft)' })).toBeInTheDocument();
    expect(screen.getAllByText('enabled')).toHaveLength(1);
    expect(screen.getAllByText(/^sha256:/).length).toBeGreaterThan(0);
    expect(screen.getByRole('list', { name: 'Permitted-tool baseline' })).toHaveTextContent('evidence.read');
    expect(screen.getByText('Enabled version: 0.1.0')).toBeInTheDocument();
    expect(screen.getByText(/^Changed .* by Development seed$/)).toBeInTheDocument();
    expect(adapter.listAgentVersions).toHaveBeenCalledWith({ agentId: 'specialist-tactics', state: undefined });
  });

  it('filters the versions by the state search parameter', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, '/agent-catalog/agents/specialist-tactics?state=draft');
    expect(await screen.findByRole('link', { name: '0.1.1 (draft)' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: '0.1.0 (approved)' })).not.toBeInTheDocument();
    expect(adapter.listAgentVersions).toHaveBeenCalledWith({ agentId: 'specialist-tactics', state: 'draft' });
  });

  it('changes the filter through the state selector', async () => {
    const router = renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/agents/specialist-tactics');
    await screen.findByRole('link', { name: '0.1.0 (approved)' });
    await userEvent.selectOptions(screen.getByLabelText('State'), 'retired');
    expect(router.state.location.search).toBe('?state=retired');
  });

  it('offers registering a version when none is registered', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/agents/specialist-set-pieces');
    expect(await screen.findByText(/^No versions are registered for this agent/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Register a version' })).toHaveAttribute('href', '/agent-catalog/agents/specialist-set-pieces/versions/new');
    expect(screen.getByText('No version is enabled.')).toBeInTheDocument();
  });

  it('shows not found for an agent outside the baseline', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/agents/unknown-agent');
    expect(await screen.findByText('This catalog record was not found.')).toBeInTheDocument();
  });

  it('shows a failure with reload', async () => {
    const adapter = fakeAgentCatalogAdapter({ getBaseline: vi.fn(async () => { throw new PlatformFailure('unexpected', { status: 500 }); }) });
    renderPage(adapter, '/agent-catalog/agents/specialist-tactics');
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
