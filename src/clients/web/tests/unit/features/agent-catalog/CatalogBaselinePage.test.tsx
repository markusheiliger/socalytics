import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { CatalogBaselinePage } from '../../../../src/features/agent-catalog/CatalogBaselinePage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { baseline, fakeAgentCatalogAdapter, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeAgentCatalogAdapter) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/agent-catalog/baseline', element: <CatalogBaselinePage /> }], {
    initialEntries: ['/agent-catalog/baseline'],
  });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('CatalogBaselinePage', () => {
  it('shows the tool allowlist and the permitted tools of every agent, read-only', async () => {
    renderPage(fakeAgentCatalogAdapter());
    const allowlist = await screen.findByRole('list', { name: 'Tool allowlist' });
    expect(within(allowlist).getAllByRole('listitem').map((li) => li.textContent)).toEqual(baseline.tools);
    expect(screen.getByText('Baseline version 1.0.0')).toBeInTheDocument();
    for (const agent of baseline.agents) {
      const list = screen.getByRole('list', { name: `Permitted tools of ${agent.agentId}` });
      expect(within(list).getAllByRole('listitem').map((li) => li.textContent)).toEqual(agent.toolBaseline);
      expect(screen.getByRole('link', { name: agent.agentId })).toHaveAttribute('href', `/agent-catalog/agents/${agent.agentId}`);
    }
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
  });

  it('shows the loading state', () => {
    renderPage(fakeAgentCatalogAdapter({ getBaseline: vi.fn(() => new Promise<typeof baseline>(() => undefined)) }));
    expect(screen.getByRole('status')).toHaveTextContent('Loading the agent catalog…');
  });

  it('shows an error', async () => {
    renderPage(fakeAgentCatalogAdapter({ getBaseline: vi.fn(async () => { throw new PlatformFailure('unexpected', { status: 500 }); }) }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('shows the not-permitted page', async () => {
    renderPage(fakeAgentCatalogAdapter({ getBaseline: vi.fn(async () => { throw new PlatformFailure('notPermitted', { status: 403 }); }) }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByText('Tool allowlist')).not.toBeInTheDocument();
  });
});
