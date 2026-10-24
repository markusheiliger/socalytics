import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { AgentCatalogOverviewPage } from '../../../../src/features/agent-catalog/AgentCatalogOverviewPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { baseline, fakeAgentCatalogAdapter, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeAgentCatalogAdapter) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/agent-catalog', element: <AgentCatalogOverviewPage /> }], { initialEntries: ['/agent-catalog'] });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

function rowOf(agentId: string) {
  return screen.getByRole('link', { name: `Show the agent ${agentId}` }).closest('tr') as HTMLElement;
}

describe('AgentCatalogOverviewPage', () => {
  it('lists every baseline agent with its enablement, state counts, and offer', async () => {
    renderPage(fakeAgentCatalogAdapter());
    await screen.findByRole('table', { name: 'Catalog agents' });
    expect(screen.getAllByRole('row')).toHaveLength(baseline.agents.length + 1);

    const tactics = within(rowOf('specialist-tactics'));
    expect(tactics.getByText('Specialist Agent')).toBeInTheDocument();
    expect(tactics.getByText('tactics')).toBeInTheDocument();
    expect(tactics.getByText('Tactics Specialist')).toBeInTheDocument();
    expect(tactics.getByText('0.1.0')).toBeInTheDocument();
    expect(tactics.getByText('1 draft, 1 approved, 1 retired')).toBeInTheDocument();
    expect(tactics.getByText('Offered in triage conversations: 0.1.0')).toBeInTheDocument();

    const setPieces = within(rowOf('specialist-set-pieces'));
    expect(setPieces.getByText('none')).toBeInTheDocument();
    expect(setPieces.getByText('Not offered: no enabled version')).toBeInTheDocument();

    const coach = within(rowOf('coach-lead'));
    expect(coach.getByText('Coach Agent')).toBeInTheDocument();
    expect(coach.getByText('Not available in conversations yet')).toBeInTheDocument();
  });

  it('links to the agents, the artifact lists, and the baseline', async () => {
    renderPage(fakeAgentCatalogAdapter());
    await screen.findByRole('table', { name: 'Catalog agents' });
    expect(screen.getByRole('link', { name: 'Show the agent specialist-tactics' })).toHaveAttribute('href', '/agent-catalog/agents/specialist-tactics');
    expect(screen.getByRole('link', { name: 'Catalog baseline' })).toHaveAttribute('href', '/agent-catalog/baseline');
    expect(screen.getByRole('link', { name: 'Artifacts of kind Prompt templates' })).toHaveAttribute('href', '/agent-catalog/artifacts/prompt-templates');
    expect(screen.getByRole('link', { name: 'Artifacts of kind Modifier policies' })).toHaveAttribute('href', '/agent-catalog/artifacts/modifier-policies');
  });

  it('shows the loading state', () => {
    renderPage(fakeAgentCatalogAdapter({ getBaseline: vi.fn(() => new Promise<typeof baseline>(() => undefined)) }));
    expect(screen.getByRole('status')).toHaveTextContent('Loading the agent catalog…');
  });

  it('shows the empty state', async () => {
    renderPage(fakeAgentCatalogAdapter({ getBaseline: vi.fn(async () => ({ ...baseline, agents: [] })) }));
    expect(await screen.findByText('The catalog baseline lists no agents.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('shows an error', async () => {
    renderPage(fakeAgentCatalogAdapter({ listAvailableAgents: vi.fn(async () => { throw new PlatformFailure('unexpected', { status: 500 }); }) }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('shows the not-permitted page', async () => {
    renderPage(fakeAgentCatalogAdapter({ getBaseline: vi.fn(async () => { throw new PlatformFailure('notPermitted', { status: 403 }); }) }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});
