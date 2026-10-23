import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { RetireAgentVersionDialog } from '../../../../src/features/agent-catalog/RetireAgentVersionDialog';
import { approvedTactics, fakeAgentCatalogAdapter, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({
  useSession: () => ({ platform: {}, session: { member: { id: 'someone' } } }),
}));

function renderDialog(adapter: FakeAgentCatalogAdapter) {
  state.adapter = adapter;
  const onClose = vi.fn();
  const onDone = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RetireAgentVersionDialog version={approvedTactics} open onClose={onClose} onDone={onDone} />
    </QueryClientProvider>,
  );
  return { onClose, onDone };
}

describe('RetireAgentVersionDialog', () => {
  it('states the effect and sends one request with the last seen etag', async () => {
    const adapter = fakeAgentCatalogAdapter();
    const { onDone } = renderDialog(adapter);
    expect(screen.getByText('Retire version 0.1.0 of specialist-tactics')).toBeInTheDocument();
    expect(screen.getByText(/can no longer be enabled\. Invocations that pinned it keep it/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Retire version' }));
    await waitFor(() => expect(onDone).toHaveBeenCalledTimes(1));
    expect(adapter.retireAgentVersion).toHaveBeenCalledTimes(1);
    expect(adapter.retireAgentVersion).toHaveBeenCalledWith('specialist-tactics', '0.1.0', approvedTactics.etag);
    expect(screen.getByText(/The version is retired/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retire version' })).not.toBeInTheDocument();
  });

  it('cancelling sends nothing', async () => {
    const adapter = fakeAgentCatalogAdapter();
    const { onClose } = renderDialog(adapter);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
    expect(adapter.retireAgentVersion).not.toHaveBeenCalled();
  });

  it('explains agent-version-enabled with the advice and never repeats the request', async () => {
    const adapter = fakeAgentCatalogAdapter({
      retireAgentVersion: vi.fn(async () => ({ kind: 'transitionRefused' as const, code: 'agent-version-enabled' })),
    });
    renderDialog(adapter);
    await userEvent.click(screen.getByRole('button', { name: 'Retire version' }));
    expect(await screen.findByText(/This version is enabled: enable another version or disable the agent first\./)).toBeInTheDocument();
    expect(screen.getByText(/Enable another version or disable the agent first, then retire/)).toBeInTheDocument();
    expect(adapter.retireAgentVersion).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Retire version' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Close' })).toBeInTheDocument();
  });

  it('explains agent-version-not-approved', async () => {
    const adapter = fakeAgentCatalogAdapter({
      retireAgentVersion: vi.fn(async () => ({ kind: 'transitionRefused' as const, code: 'agent-version-not-approved' })),
    });
    renderDialog(adapter);
    await userEvent.click(screen.getByRole('button', { name: 'Retire version' }));
    expect(await screen.findByText(/Only an approved agent version can be retired\./)).toBeInTheDocument();
    expect(adapter.retireAgentVersion).toHaveBeenCalledTimes(1);
  });

  it('shows the current state when the version changed and does not resend', async () => {
    const adapter = fakeAgentCatalogAdapter({
      retireAgentVersion: vi.fn(async () => ({ kind: 'changed' as const })),
      getAgentVersion: vi.fn(async () => ({ ...approvedTactics, state: 'retired', etag: '"tactics-0.1.0-3"' })),
    });
    renderDialog(adapter);
    await userEvent.click(screen.getByRole('button', { name: 'Retire version' }));
    expect(await screen.findByText(/has changed since you loaded it/)).toBeInTheDocument();
    expect(await screen.findByText('Current state: retired')).toBeInTheDocument();
    expect(adapter.retireAgentVersion).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Retire version' })).not.toBeInTheDocument();
  });
});
