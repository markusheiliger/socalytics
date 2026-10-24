import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { ArtifactsPage } from '../../../../src/features/agent-catalog/ArtifactsPage';
import { fakeAgentCatalogAdapter, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeAgentCatalogAdapter, path = '/agent-catalog/artifacts/prompt-templates') {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/agent-catalog/artifacts/:kind', element: <ArtifactsPage /> }], { initialEntries: [path] });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('ArtifactsPage', () => {
  it('lists the records of the kind with identifier, version, shortened digest, and registration time', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter);
    const list = await screen.findByRole('list', { name: 'Prompt templates records' });
    const link = within(list).getByRole('link', { name: 'specialist-prompt version 0.1.0' });
    expect(link).toHaveAttribute('href', '/agent-catalog/artifacts/prompt-templates/specialist-prompt/versions/0.1.0');
    expect(within(list).getByText('sha256:111111111111')).toBeInTheDocument();
    expect(within(list).getByText(/Registered /)).toBeInTheDocument();
    expect(adapter.listArtifacts).toHaveBeenCalledWith('prompt-template', undefined);
  });

  it('offers a kind switcher to the other kinds', async () => {
    renderPage(fakeAgentCatalogAdapter());
    const nav = await screen.findByRole('navigation', { name: 'Artifact kind' });
    expect(within(nav).getAllByRole('link')).toHaveLength(6);
    expect(within(nav).getByText('Prompt templates')).toHaveAttribute('aria-current', 'page');
    expect(within(nav).getByRole('link', { name: 'Tool policies' })).toHaveAttribute('href', '/agent-catalog/artifacts/tool-policies');
  });

  it('filters by the id search parameter', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, '/agent-catalog/artifacts/prompt-templates?id=specialist-prompt');
    expect(await screen.findByRole('link', { name: 'specialist-prompt version 0.1.0' })).toBeInTheDocument();
    expect(adapter.listArtifacts).toHaveBeenCalledWith('prompt-template', { id: 'specialist-prompt' });
  });

  it('keeps the filter in the address and clears it', async () => {
    const router = renderPage(fakeAgentCatalogAdapter());
    await screen.findByRole('list', { name: 'Prompt templates records' });
    await userEvent.type(screen.getByLabelText('Identifier'), 'specialist-prompt');
    await userEvent.click(screen.getByRole('button', { name: 'Apply filter' }));
    expect(router.state.location.search).toBe('?id=specialist-prompt');
    await userEvent.click(screen.getByRole('button', { name: 'Clear filter' }));
    expect(router.state.location.search).toBe('');
  });

  it('shows the empty text and the filtered empty text', async () => {
    const adapter = fakeAgentCatalogAdapter({ listArtifacts: vi.fn(async () => []) });
    renderPage(adapter);
    expect(await screen.findByText(/No records of this kind are registered/)).toBeInTheDocument();
  });

  it('shows the filtered empty text', async () => {
    renderPage(fakeAgentCatalogAdapter({ listArtifacts: vi.fn(async () => []) }), '/agent-catalog/artifacts/prompt-templates?id=x');
    expect(await screen.findByText('No records match the identifier filter.')).toBeInTheDocument();
  });

  it('shows not found for an unknown kind without a request', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, '/agent-catalog/artifacts/widgets');
    expect(await screen.findByText('This catalog record was not found.')).toBeInTheDocument();
    expect(adapter.listArtifacts).not.toHaveBeenCalled();
  });
});
