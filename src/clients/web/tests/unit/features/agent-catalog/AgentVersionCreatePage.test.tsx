import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { AgentVersionCreatePage } from '../../../../src/features/agent-catalog/AgentVersionCreatePage';
import { fakeAgentCatalogAdapter, modifierRecord, promptRecord, safetyRecord, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeAgentCatalogAdapter, path: string) {
  state.adapter = adapter;
  const router = createMemoryRouter(
    [
      { path: '/agent-catalog/agents/:agentId/versions/new', element: <AgentVersionCreatePage /> },
      { path: '/agent-catalog/agents/:agentId/versions/:version', element: <p>version opened</p> },
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

const START = '/agent-catalog/agents/specialist-tactics/versions/new';
const FROM = `${START}?from=0.1.0`;

describe('AgentVersionCreatePage', () => {
  it('prefills the display name from the baseline and proposes the next version', async () => {
    renderPage(fakeAgentCatalogAdapter(), START);
    expect(await screen.findByLabelText(/Display name/)).toHaveValue('Tactics Specialist');
    expect(screen.getByLabelText('Agent')).toHaveValue('specialist-tactics');
    await waitFor(() => expect(screen.getByLabelText(/^Version/)).toHaveValue('0.1.2'));
  });

  it('offers only prompt templates and safety policies of the agent\u2019s group', async () => {
    const coachPrompt = { ...promptRecord, id: 'coach-prompt', document: { ...promptRecord.document, group: 'coach' as const } };
    const adapter = fakeAgentCatalogAdapter({
      listArtifacts: vi.fn(async (kind: string) =>
        kind === 'prompt-template'
          ? [promptRecord, coachPrompt]
          : kind === 'safety-policy'
            ? [safetyRecord]
            : [],
      ) as never,
      getArtifact: vi.fn(async (_kind: string, id: string) => (id === 'coach-prompt' ? coachPrompt : promptRecord)) as never,
    });
    renderPage(adapter, START);
    const select = await screen.findByLabelText(/^Prompt template/);
    await waitFor(() => expect(screen.queryByRole('option', { name: /coach-prompt/ })).not.toBeInTheDocument());
    expect(select).toContainElement(screen.getByRole('option', { name: 'specialist-prompt@0.1.0' }));
  });

  it('prefills every reference from the chosen version and registers once, then opens the version', async () => {
    const user = userEvent.setup();
    let release: () => void = () => {};
    const registerAgentVersion = vi.fn(
      () =>
        new Promise((resolve) => {
          release = () => resolve({ kind: 'created', value: { agentId: 'specialist-tactics', version: '0.1.2' } });
        }),
    ) as never;
    const adapter = fakeAgentCatalogAdapter({
      registerAgentVersion,
      listArtifacts: vi.fn(async () => []) as never,
    });
    renderPage(adapter, FROM);
    expect(await screen.findByLabelText(/^Prompt template/)).toHaveValue('specialist-prompt@0.1.0');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Working…' })).toBeDisabled());
    fireEvent.submit(screen.getByRole('button', { name: 'Working…' }).closest('form') as HTMLFormElement);
    expect(adapter.registerAgentVersion).toHaveBeenCalledTimes(1);
    expect(adapter.registerAgentVersion).toHaveBeenCalledWith(
      expect.objectContaining({
        kind: 'agent-version',
        agentId: 'specialist-tactics',
        group: 'specialist',
        displayName: 'Tactics Specialist',
        version: '0.1.2',
        artifacts: expect.objectContaining({ promptTemplate: expect.objectContaining({ id: 'specialist-prompt', version: '0.1.0' }) }),
      }),
    );
    release();
    expect(await screen.findByText('version opened')).toBeInTheDocument();
  });

  it('says that a replay changed nothing and keeps the values', async () => {
    const user = userEvent.setup();
    const adapter = fakeAgentCatalogAdapter({
      registerAgentVersion: vi.fn(async () => ({ kind: 'replayed', value: { agentId: 'specialist-tactics', version: '0.1.0' } })) as never,
    });
    renderPage(adapter, FROM);
    await screen.findByDisplayValue('Tactics Specialist');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByText(/Already registered, nothing changed/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Display name/)).toHaveValue('Tactics Specialist');
  });

  it('explains an existing version with different content', async () => {
    const user = userEvent.setup();
    const adapter = fakeAgentCatalogAdapter({ registerAgentVersion: vi.fn(async () => ({ kind: 'versionExists' })) as never });
    renderPage(adapter, FROM);
    await screen.findByDisplayValue('Tactics Specialist');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByText(/already registered with different content/)).toBeInTheDocument();
  });

  it('shows violations next to the reference and the rest in the summary', async () => {
    const user = userEvent.setup();
    const adapter = fakeAgentCatalogAdapter({
      registerAgentVersion: vi.fn(async () => ({
        kind: 'validationFailed',
        violations: [
          { code: 'artifact-not-found', pointer: '/artifacts/promptTemplate', detail: '', location: 'reference:promptTemplate' },
          { code: 'schema-violation', pointer: '', detail: 'odd', location: 'summary' },
        ],
      })) as never,
    });
    renderPage(adapter, FROM);
    await screen.findByDisplayValue('Tactics Specialist');
    await user.click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByText('The referenced artifact is not registered.')).toBeInTheDocument();
    expect(screen.getByText('Problems without a field')).toBeInTheDocument();
    expect(screen.getByLabelText(/Display name/)).toHaveValue('Tactics Specialist');
  });

  it('asks for a disclosure and the coach references for a Coach Agent', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/agents/coach-lead/versions/new');
    expect(await screen.findByLabelText(/Disclosure statement/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Philosophy profile/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Modifier policy/)).toBeInTheDocument();
  });

  it('lists the modifier policies matching the prompt template\u2019s dimension set major first', async () => {
    const coachPrompt = { ...promptRecord, id: 'coach-prompt', document: { ...promptRecord.document, group: 'coach' as const, dimensionSetMajor: 2 } };
    const other = { ...modifierRecord, id: 'a-other', document: { ...modifierRecord.document, dimensionSetMajor: 1 } };
    const matching = { ...modifierRecord, id: 'z-matching', document: { ...modifierRecord.document, dimensionSetMajor: 2 } };
    const all = [coachPrompt, other, matching] as never[];
    const adapter = fakeAgentCatalogAdapter({
      listArtifacts: vi.fn(async (kind: string) => (all as { kind: string }[]).filter((r) => r.kind === kind)) as never,
      getArtifact: vi.fn(async (kind: string, id: string) => (all as { kind: string; id: string }[]).find((r) => r.kind === kind && r.id === id)) as never,
    });
    renderPage(adapter, '/agent-catalog/agents/coach-lead/versions/new');
    const select = await screen.findByLabelText(/^Modifier policy/);
    await userEvent.selectOptions(await screen.findByLabelText(/^Prompt template/), 'coach-prompt@0.1.0');
    await waitFor(() => expect(screen.getByText(/Listed first: matches the dimension set major version 2/)).toBeInTheDocument());
    const options = Array.from(select.querySelectorAll('option')).map((o) => o.textContent);
    expect(options.slice(1)).toEqual(['z-matching@0.1.0 (matches the prompt template)', 'a-other@0.1.0']);
  });

  it('reports an unknown agent', async () => {
    renderPage(fakeAgentCatalogAdapter(), '/agent-catalog/agents/unknown-agent/versions/new');
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
