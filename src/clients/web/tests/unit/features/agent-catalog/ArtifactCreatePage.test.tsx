import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { ArtifactCreatePage } from '../../../../src/features/agent-catalog/ArtifactCreatePage';
import { fakeAgentCatalogAdapter, promptRecord, toolRecord, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeAgentCatalogAdapter, path: string) {
  state.adapter = adapter;
  const router = createMemoryRouter(
    [
      { path: '/agent-catalog/artifacts/:kind/new', element: <ArtifactCreatePage /> },
      { path: '/agent-catalog/artifacts/:kind/:artifactId/versions/:version', element: <p>record opened</p> },
    ],
    { initialEntries: [path] },
  );
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

const START = '/agent-catalog/artifacts/prompt-templates/new';
const FROM = `${START}?fromId=specialist-prompt&fromVersion=0.1.0`;

describe('ArtifactCreatePage', () => {
  it('prefills every field from the record and proposes the next patch version', async () => {
    const adapter = fakeAgentCatalogAdapter({ listArtifacts: vi.fn(async () => [{ ...promptRecord }]) as never });
    renderPage(adapter, FROM);
    expect(await screen.findByLabelText(/Identifier/)).toHaveValue('specialist-prompt');
    expect(screen.getByLabelText(/^Prompt text/)).toHaveValue(promptRecord.document.text);
    await waitFor(() => expect(screen.getByLabelText(/^Version/)).toHaveValue('0.1.1'));
  });

  it('proposes 0.1.0 for a new identifier and registers once, then opens the record', async () => {
    const user = userEvent.setup();
    let release: () => void = () => {};
    const registerArtifact = vi.fn(
      () =>
        new Promise((resolve) => {
          release = () => resolve({ kind: 'created', value: { ...promptRecord, id: 'new-prompt', version: '0.1.0' } });
        }),
    ) as never;
    const adapter = fakeAgentCatalogAdapter({ registerArtifact });
    renderPage(adapter, START);
    await user.type(await screen.findByLabelText(/Identifier/), 'new-prompt');
    await waitFor(() => expect(screen.getByLabelText(/^Version/)).toHaveValue('0.1.0'));
    await user.type(screen.getByLabelText(/^Prompt text/), 'Hello');
    const submit = screen.getByRole('button', { name: 'Register' });
    await user.click(submit);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Working…' })).toBeDisabled());
    fireEvent.submit(screen.getByRole('button', { name: 'Working…' }).closest('form') as HTMLFormElement);
    expect(adapter.registerArtifact).toHaveBeenCalledTimes(1);
    expect(adapter.registerArtifact).toHaveBeenCalledWith(
      'prompt-template',
      expect.objectContaining({ kind: 'prompt-template', id: 'new-prompt', version: '0.1.0', text: 'Hello' }),
    );
    release();
    expect(await screen.findByText('record opened')).toBeInTheDocument();
  });

  it('says that a replay changed nothing', async () => {
    const user = userEvent.setup();
    const adapter = fakeAgentCatalogAdapter({
      registerArtifact: vi.fn(async () => ({ kind: 'replayed', value: promptRecord })) as never,
    });
    renderPage(adapter, FROM);
    await screen.findByDisplayValue('specialist-prompt');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByText(/Already registered, nothing changed/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Prompt text/)).toHaveValue(promptRecord.document.text);
  });

  it('refuses an existing version with different content and keeps the entered values', async () => {
    const user = userEvent.setup();
    const adapter = fakeAgentCatalogAdapter({ registerArtifact: vi.fn(async () => ({ kind: 'versionExists' })) as never });
    renderPage(adapter, START);
    await user.type(await screen.findByLabelText(/Identifier/), 'new-prompt');
    await user.type(screen.getByLabelText(/^Prompt text/), 'Kept text');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByText(/already registered with different content/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Prompt text/)).toHaveValue('Kept text');
    expect(screen.getByLabelText(/Identifier/)).toHaveValue('new-prompt');
  });

  it('shows violations at their fields and the rest in the summary, keeping every value', async () => {
    const user = userEvent.setup();
    const registerArtifact = vi.fn(async () => ({
      kind: 'validationFailed',
      violations: [
        { code: 'schema-violation', pointer: '/text', detail: 'too short', location: 'field:text' },
        { code: 'group-mismatch', pointer: '', detail: '', location: 'summary' },
      ],
    })) as never;
    renderPage(fakeAgentCatalogAdapter({ registerArtifact }), START);
    await user.type(await screen.findByLabelText(/Identifier/), 'new-prompt');
    await user.type(screen.getByLabelText(/^Prompt text/), 'Short');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByText('Problems without a field')).toBeInTheDocument();
    expect(screen.getByText('The group does not match the agent or the referenced artifact.')).toBeInTheDocument();
    expect(screen.getByText(/does not match its format: too short/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Prompt text/)).toHaveValue('Short');
  });

  it('registers a modifier policy and a dimension set through their forms', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/artifacts/modifier-policies/new');
    expect(await screen.findByRole('heading', { level: 2, name: /modifier policy/i })).toBeInTheDocument();
    expect(screen.queryByText(/not available in the Web UI yet/)).not.toBeInTheDocument();
  });

  it('shows the philosophy profile form with the registered dimension sets', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/artifacts/philosophy-profiles/new');
    expect(await screen.findByRole('heading', { level: 2, name: /philosophy profile/i })).toBeInTheDocument();
    expect(screen.queryByText(/not available in the Web UI yet/)).not.toBeInTheDocument();
  });

  it('offers only allowlisted tools for a tool policy and prefills from a record', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, '/agent-catalog/artifacts/tool-policies/new?fromId=specialist-tools&fromVersion=0.1.0');
    expect(await screen.findByRole('checkbox', { name: 'match.lookup' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'evidence.read' })).toBeChecked();
    expect(toolRecord.document.tools).toContain('run.summary');
  });

  it('shows not found without a request for an unknown kind or an invalid starting record', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, '/agent-catalog/artifacts/widgets/new');
    expect(await screen.findByText('This catalog record was not found.')).toBeInTheDocument();
    expect(adapter.listArtifacts).not.toHaveBeenCalled();
  });

  it('shows not found for an invalid starting record without a request', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderPage(adapter, `${START}?fromId=Bad_Id&fromVersion=1`);
    expect(await screen.findByText('This catalog record was not found.')).toBeInTheDocument();
    expect(adapter.getArtifact).not.toHaveBeenCalled();
  });
});
