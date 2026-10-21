import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { WorkflowCreatePage } from '../../../../src/features/registry/WorkflowCreatePage';
import { RegistryRefusalError } from '../../../../src/platform/models/registry';
import { approvedProfile, draftProfile, fakeRegistryAdapter, newerWorkflowVersion, profiles, workflowVersion, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

const approvedTracking = { ...draftProfile, state: 'approved' as const };
const allApproved = [approvedTracking, approvedProfile, profiles[2]];

function renderPage(adapter: FakeRegistryAdapter, entry = '/registry/workflows/new') {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/registry/workflows/new', element: <WorkflowCreatePage /> }], { initialEntries: [entry] });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

const adapterWith = (overrides = {}) => fakeRegistryAdapter({ listProfiles: vi.fn(async () => allApproved), ...overrides });

describe('WorkflowCreatePage', () => {
  it('prefills the segmentation policy from the default and offers approved profile versions only', async () => {
    renderPage(adapterWith());
    expect(await screen.findByLabelText('Segment duration (seconds)')).toHaveValue('300');
    await userEvent.click(screen.getByRole('button', { name: 'Add node' }));
    const select = screen.getByLabelText('Approved profile version 1');
    const options = within(select).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['Choose a profile version', 'ball-tracking-standard version 1', 'possession-standard version 2']);
  });

  it('defines a version from approved profiles after the local check', async () => {
    const adapter = adapterWith({ defineWorkflow: vi.fn(async () => newerWorkflowVersion) });
    renderPage(adapter);
    await screen.findByLabelText('Workflow id');
    await userEvent.type(screen.getByLabelText('Workflow id'), 'standard-analysis');
    await userEvent.type(screen.getByLabelText('Workflow version'), '1.1.0');
    await userEvent.click(screen.getByRole('button', { name: 'Add node' }));
    await userEvent.type(screen.getByLabelText('Node key 1'), 'tracking');
    await userEvent.selectOptions(screen.getByLabelText('Approved profile version 1'), 'ball-tracking-standard|1');
    expect(screen.getByRole('checkbox', { name: 'ball-tracking' })).toBeChecked();
    await userEvent.click(screen.getByRole('button', { name: 'Define workflow version' }));
    expect(await screen.findByText('Workflow version defined')).toBeInTheDocument();
    expect(adapter.defineWorkflow).toHaveBeenCalledTimes(1);
    const draft = adapter.defineWorkflow.mock.calls[0][0];
    expect(draft.nodes).toEqual([{ nodeKey: 'tracking', profileId: 'ball-tracking-standard', profileVersion: 1 }]);
    expect(draft.requestedCapabilities).toEqual(['ball-tracking']);
    expect(draft.segmentationPolicy.durationSeconds).toBe(300);
  });

  it('blocks a draft with a flagged node without a request', async () => {
    // The tracking profile is a draft here, so its node is flagged.
    const adapter = adapterWith({ listProfiles: vi.fn(async () => [draftProfile, approvedProfile]) });
    renderPage(adapter, '/registry/workflows/new?fromId=standard-analysis&fromVersion=1.0.0');
    await screen.findByText(/Started from standard-analysis version 1.0.0/);
    await userEvent.type(screen.getByLabelText('Workflow version'), '2.0.0');
    await userEvent.click(screen.getByRole('button', { name: 'Define workflow version' }));
    expect((await screen.findAllByText(/no longer approved/)).length).toBeGreaterThan(0);
    expect(adapter.defineWorkflow).not.toHaveBeenCalled();
  });

  it('explains a refusal per violation', async () => {
    const adapter = adapterWith({
      defineWorkflow: vi.fn(async () => {
        throw new RegistryRefusalError({
          operation: 'defineWorkflow',
          code: 'workflow-invalid',
          retryable: false,
          details: [{ field: 'edges', code: 'cycle' }, { field: 'nodes.tracking', code: 'profile-not-approved' }],
        });
      }),
    });
    renderPage(adapter, `/registry/workflows/new?fromId=${workflowVersion.workflowId}&fromVersion=${workflowVersion.workflowVersion}`);
    await screen.findByText(/Started from/);
    await userEvent.type(screen.getByLabelText('Workflow version'), '2.0.0');
    await userEvent.click(screen.getByRole('button', { name: 'Define workflow version' }));
    await waitFor(() => expect(adapter.defineWorkflow).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('The workflow edges form a cycle.')).toBeInTheDocument();
    expect(screen.getByText('Node tracking: the profile version is not approved.')).toBeInTheDocument();
  });
});
