import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { PlatformFailure } from '../../../../src/platform/failure';
import { ArtifactPage } from '../../../../src/features/agent-catalog/ArtifactPage';
import { fakeAgentCatalogAdapter, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeAgentCatalogAdapter, path: string) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/agent-catalog/artifacts/:kind/:artifactId/versions/:version', element: <ArtifactPage /> }], {
    initialEntries: [path],
  });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('ArtifactPage', () => {
  it('shows a prompt template with its text verbatim', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, '/agent-catalog/artifacts/prompt-templates/specialist-prompt/versions/0.1.0');
    expect(await screen.findByLabelText('Prompt text')).toHaveTextContent('<b>Never</b> interpret markup: {{evidence_context}}');
    expect(adapter.getArtifact).toHaveBeenCalledWith('prompt-template', 'specialist-prompt', '0.1.0');
    expect(screen.getByText('sha256:111111111111')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Prompt templates' })).toHaveAttribute('href', '/agent-catalog/artifacts/prompt-templates');
  });

  it('links to a new version prefilled from the record', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/artifacts/prompt-templates/specialist-prompt/versions/0.1.0');
    expect(await screen.findByRole('link', { name: 'New version from this' })).toHaveAttribute(
      'href',
      '/agent-catalog/artifacts/prompt-templates/new?fromId=specialist-prompt&fromVersion=0.1.0',
    );
  });

  it('shows a tool policy, a model selection, and a safety policy', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/artifacts/tool-policies/specialist-tools/versions/0.1.0');
    expect(await screen.findByText('evidence.read')).toBeInTheDocument();
    expect(screen.getByText('run.summary')).toBeInTheDocument();
  });

  it('shows a model selection', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/artifacts/model-selections/simulated-model/versions/0.1.0');
    expect(await screen.findByText('simulated-1')).toBeInTheDocument();
  });

  it('shows a safety policy', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/artifacts/safety-policies/specialist-safety/versions/0.1.0');
    expect(await screen.findByText(/Never reveal other teams/)).toBeInTheDocument();
  });

  it('shows kinds without a view as their raw document', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/artifacts/dimension-sets/football-dimensions/versions/1.0.0');
    expect(await screen.findByText(/no dedicated view yet/)).toBeInTheDocument();
    expect(screen.getByLabelText('Stored document')).toHaveTextContent('"meaning": "Intensity of pressing"');
  });

  it('shows not found for a missing record', async () => {
    const getArtifact = vi.fn(async () => {
      throw new PlatformFailure('unexpected', { status: 404 });
    }) as never;
    renderPage(fakeAgentCatalogAdapter({ getArtifact }), '/agent-catalog/artifacts/prompt-templates/unknown-prompt/versions/0.1.0');
    expect(await screen.findByText('This catalog record was not found.')).toBeInTheDocument();
  });

  it.each([
    ['an unknown kind', '/agent-catalog/artifacts/widgets/specialist-prompt/versions/0.1.0'],
    ['an identifier outside the kebab-case form', '/agent-catalog/artifacts/prompt-templates/Specialist_Prompt/versions/0.1.0'],
    ['a version that is not SemVer', '/agent-catalog/artifacts/prompt-templates/specialist-prompt/versions/1.0'],
  ])('shows not found without a request for %s', async (_name, path) => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, path);
    expect(await screen.findByText('This catalog record was not found.')).toBeInTheDocument();
    expect(adapter.getArtifact).not.toHaveBeenCalled();
  });
});
