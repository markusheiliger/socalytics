import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { AgentVersionPage } from '../../../../src/features/agent-catalog/AgentVersionPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { approvedCoach, approvedTactics, draftCoach, fakeAgentCatalogAdapter, retiredTactics, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/platform/adapters/memberAdapter', () => ({ createMemberAdapter: () => ({ list: async () => [] }) }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeAgentCatalogAdapter, agentId: string, version: string) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/agent-catalog/agents/:agentId/versions/:version', element: <AgentVersionPage /> }], {
    initialEntries: [`/agent-catalog/agents/${agentId}/versions/${version}`],
  });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('AgentVersionPage', () => {
  it('shows a Specialist Agent version with linked artifacts and the enabled marker', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, approvedTactics.agentId, approvedTactics.version);
    expect(await screen.findByText('specialist-tactics version 0.1.0')).toBeInTheDocument();
    expect(adapter.getAgentVersion).toHaveBeenCalledWith('specialist-tactics', '0.1.0');
    expect(screen.getByText('approved')).toBeInTheDocument();
    expect(screen.getByText('Tactics Specialist')).toBeInTheDocument();
    expect(screen.getByText('This version is enabled.')).toBeInTheDocument();
    expect(screen.getByText(/^Registered by Development seed/)).toBeInTheDocument();
    expect(screen.queryByText('Simulation disclosure')).not.toBeInTheDocument();
    expect(screen.queryByText('Profile distances')).not.toBeInTheDocument();
    const link = screen.getByRole('link', { name: 'Prompt template specialist-prompt version 0.1.0' });
    expect(link).toHaveAttribute('href', '/agent-catalog/artifacts/prompt-templates/specialist-prompt/versions/0.1.0');
    expect(screen.getAllByText(/^sha256:/)).toHaveLength(approvedTactics.artifacts.length + 1);
  });

  it('shows retirement and the self-approval marker', async () => {
    renderPage(fakeAgentCatalogAdapter(), retiredTactics.agentId, retiredTactics.version);
    expect(await screen.findByText(/^Retired by ada/)).toBeInTheDocument();
    expect(screen.getByText(/^Approved by ada/)).toBeInTheDocument();
    expect(screen.getByText(/self-approval/)).toBeInTheDocument();
    expect(screen.getByText('This version is not enabled.')).toBeInTheDocument();
  });

  it('shows the disclosure, the acknowledgement marker, and the distances of a Coach Agent version', async () => {
    renderPage(fakeAgentCatalogAdapter(), approvedCoach.agentId, approvedCoach.version);
    expect(await screen.findByText('Simulation disclosure')).toBeInTheDocument();
    expect(screen.getByLabelText('Simulation disclosure')).toHaveTextContent(approvedCoach.disclosure!);
    expect(screen.getByText('The approval acknowledged a differentiation review.')).toBeInTheDocument();
    expect(screen.getByText('Profile distances')).toBeInTheDocument();
    expect(screen.getAllByRole('listitem').some((li) => li.textContent?.includes('possession-first 1.0.0: 42'))).toBe(true);
  });

  it('marks the distances that require a review and shows no approval for a draft', async () => {
    renderPage(fakeAgentCatalogAdapter(), draftCoach.agentId, draftCoach.version);
    expect(await screen.findByText(/differentiation review required/)).toBeInTheDocument();
    expect(screen.queryByText(/^Approved by/)).not.toBeInTheDocument();
    expect(screen.getByText(/^Registered by grace/)).toBeInTheDocument();
  });

  it('shows an unknown state raw', async () => {
    const adapter = fakeAgentCatalogAdapter({ getAgentVersion: vi.fn(async () => ({ ...approvedTactics, state: 'suspended' })) });
    renderPage(adapter, approvedTactics.agentId, approvedTactics.version);
    expect(await screen.findByText('suspended')).toBeInTheDocument();
  });

  it('shows not found for a missing version', async () => {
    const adapter = fakeAgentCatalogAdapter({
      getAgentVersion: vi.fn(async () => {
        throw new PlatformFailure('unexpected', { status: 404 });
      }),
    });
    renderPage(adapter, 'specialist-tactics', '9.9.9');
    expect(await screen.findByText('This catalog record was not found.')).toBeInTheDocument();
  });
});
