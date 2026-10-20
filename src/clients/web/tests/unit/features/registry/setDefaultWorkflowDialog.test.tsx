import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { SetDefaultWorkflowDialog } from '../../../../src/features/registry/SetDefaultWorkflowDialog';
import { RegistryChangedError, RegistryRefusalError, type DefaultWorkflow } from '../../../../src/platform/models/registry';
import { defaultWorkflow, fakeRegistryAdapter, newerWorkflowVersion, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({
  useSession: () => ({ platform: {}, session: { member: { id: 'someone' } } }),
}));

function renderDialog(adapter: FakeRegistryAdapter, current: DefaultWorkflow | null) {
  state.adapter = adapter;
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <SetDefaultWorkflowDialog
        workflowId={newerWorkflowVersion.workflowId}
        workflowVersion={newerWorkflowVersion.workflowVersion}
        current={current}
        open
        onClose={onClose}
      />
    </QueryClientProvider>,
  );
  return onClose;
}

describe('SetDefaultWorkflowDialog', () => {
  it('names the version and the current default, states the consequence, and sends one request with the rowVersion', async () => {
    const adapter = fakeRegistryAdapter();
    renderDialog(adapter, defaultWorkflow);
    const body = screen.getByText(/Make .* the default workflow\?/);
    expect(body).toHaveTextContent(`${newerWorkflowVersion.workflowId} version 1.1.0`);
    expect(body).toHaveTextContent(`The current default is ${defaultWorkflow.workflowId} version 1.0.0`);
    expect(body).toHaveTextContent('Only analysis runs created afterwards use it; existing runs keep their snapshot');
    expect(screen.queryByText(/No default workflow exists yet/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Make default' }));
    await waitFor(() => expect(screen.getByText('The workflow version is now the default.')).toBeInTheDocument());
    expect(adapter.setDefault).toHaveBeenCalledTimes(1);
    expect(adapter.setDefault).toHaveBeenCalledWith(
      { workflowId: newerWorkflowVersion.workflowId, workflowVersion: '1.1.0' },
      defaultWorkflow.rowVersion,
    );
  });

  it('without a default says so, notes that new runs become possible, and sends no rowVersion', async () => {
    const adapter = fakeRegistryAdapter();
    renderDialog(adapter, null);
    expect(screen.getByText(/The current default is none/)).toBeInTheDocument();
    expect(screen.getByText(/new analysis runs can be created/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Make default' }));
    await waitFor(() => expect(adapter.setDefault).toHaveBeenCalledTimes(1));
    expect(adapter.setDefault).toHaveBeenCalledWith(
      { workflowId: newerWorkflowVersion.workflowId, workflowVersion: '1.1.0' },
      undefined,
    );
  });

  it('cancelling sends nothing', async () => {
    const adapter = fakeRegistryAdapter();
    const onClose = renderDialog(adapter, defaultWorkflow);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
    expect(adapter.setDefault).not.toHaveBeenCalled();
  });

  it('shows workflow-not-resolvable with the refusal alert', async () => {
    const refusal = { operation: 'setDefaultWorkflow' as const, code: 'workflow-not-resolvable', details: [], retryable: false };
    const adapter = fakeRegistryAdapter({ setDefault: vi.fn(async () => { throw new RegistryRefusalError(refusal); }) });
    renderDialog(adapter, defaultWorkflow);
    await userEvent.click(screen.getByRole('button', { name: 'Make default' }));
    expect(await screen.findByText('The workflow version cannot be resolved and cannot become the default.')).toBeInTheDocument();
    expect(adapter.setDefault).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Make default' })).not.toBeInTheDocument();
  });

  it('shows the has-changed message with the current default', async () => {
    const adapter = fakeRegistryAdapter({
      setDefault: vi.fn(async () => { throw new RegistryChangedError(); }),
      getDefault: vi.fn(async () => ({ ...defaultWorkflow, workflowVersion: '0.9.0', rowVersion: 5 })),
    });
    renderDialog(adapter, defaultWorkflow);
    await userEvent.click(screen.getByRole('button', { name: 'Make default' }));
    expect(await screen.findByText('The record has changed')).toBeInTheDocument();
    expect(await screen.findByText(`Current default: ${defaultWorkflow.workflowId} version 0.9.0`)).toBeInTheDocument();
    expect(adapter.setDefault).toHaveBeenCalledTimes(1);
  });
});
