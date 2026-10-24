import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { ChangeEnablementDialog } from '../../../../src/features/agent-catalog/ChangeEnablementDialog';
import type { AgentGroup, Enablement } from '../../../../src/platform/models/agentCatalog';
import { enablements, fakeAgentCatalogAdapter, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({
  useSession: () => ({ platform: {}, session: { member: { id: 'someone' } } }),
}));

const tactics = enablements[0] as Enablement;
const none = enablements[2] as Enablement;

function renderDialog(
  adapter: FakeAgentCatalogAdapter,
  options: { current?: Enablement; nextVersion?: string | null; group?: AgentGroup } = {},
) {
  state.adapter = adapter;
  const onClose = vi.fn();
  const onDone = vi.fn();
  const current = options.current ?? tactics;
  render(
    <QueryClientProvider client={new QueryClient()}>
      <ChangeEnablementDialog
        agentId={current.agentId}
        group={options.group ?? 'specialist'}
        current={current}
        nextVersion={options.nextVersion === undefined ? '0.2.0' : options.nextVersion}
        open
        onClose={onClose}
        onDone={onDone}
      />
    </QueryClientProvider>,
  );
  return { onClose, onDone };
}

const refusal = (code: string, reason?: string) =>
  vi.fn(async () => ({ kind: 'enablementRefused' as const, code, ...(reason ? { reason } : {}) }));

describe('ChangeEnablementDialog', () => {
  it('states agent, current and new version and the effect, and sends one request with the last seen etag', async () => {
    const adapter = fakeAgentCatalogAdapter();
    const { onDone } = renderDialog(adapter);
    expect(screen.getByText('Agent: specialist-tactics')).toBeInTheDocument();
    expect(screen.getByText('Currently enabled: 0.1.0')).toBeInTheDocument();
    expect(screen.getByText('New enabled version: 0.2.0')).toBeInTheDocument();
    expect(screen.getByText(/Only questions submitted afterwards use the change/)).toBeInTheDocument();
    expect(screen.getByText(/keep their pinned versions/)).toBeInTheDocument();
    expect(screen.queryByText(/Coach Agents are not available/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Enable version' }));
    await waitFor(() => expect(onDone).toHaveBeenCalledTimes(1));
    expect(adapter.setEnablement).toHaveBeenCalledTimes(1);
    expect(adapter.setEnablement).toHaveBeenCalledWith('specialist-tactics', '0.2.0', tactics.etag);
    expect(screen.getByText(/The enablement was changed/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Enable version' })).not.toBeInTheDocument();
  });

  it('states that nothing is enabled yet', () => {
    renderDialog(fakeAgentCatalogAdapter(), { current: none });
    expect(screen.getByText('Currently enabled: none')).toBeInTheDocument();
  });

  it('when disabling, states that members can no longer add or ask the agent', async () => {
    const adapter = fakeAgentCatalogAdapter();
    renderDialog(adapter, { nextVersion: null });
    expect(screen.getByText('Disable specialist-tactics')).toBeInTheDocument();
    expect(screen.getByText(/the agent is disabled/)).toBeInTheDocument();
    expect(screen.getByText(/Members can no longer add or ask the agent/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Disable agent' }));
    await waitFor(() => expect(adapter.setEnablement).toHaveBeenCalledWith('specialist-tactics', null, tactics.etag));
    expect(adapter.setEnablement).toHaveBeenCalledTimes(1);
  });

  it('adds the Coach Agent note for a Coach Agent version', () => {
    renderDialog(fakeAgentCatalogAdapter(), { group: 'coach' });
    expect(screen.getByText(/may refuse while Coach Agents are not available in conversations/)).toBeInTheDocument();
  });

  it('cancelling sends nothing', async () => {
    const adapter = fakeAgentCatalogAdapter();
    const { onClose } = renderDialog(adapter);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
    expect(adapter.setEnablement).not.toHaveBeenCalled();
  });

  it.each([
    ['agent-version-not-enableable', 'not-approved', /This version cannot be enabled\./, /The version is not approved\./],
    ['agent-version-not-enableable', 'retired', /This version cannot be enabled\./, /The version is retired\./],
    ['agent-version-not-enableable', 'simulated-provider', /This version cannot be enabled\./, /simulated provider, which is permitted only in Development and Test/],
    ['agent-version-not-enableable', 'something-new', /This version cannot be enabled\./, /Reason: something-new/],
    ['coach-agent-enablement-unsupported', undefined, /does not support enabling Coach Agents yet/, undefined],
    ['agent-not-found', undefined, /does not know this agent/, undefined],
    ['unheard-of', undefined, /Refused with code unheard-of\./, undefined],
  ])('shows the refusal %s with reason %s and never repeats the request', async (code, reason, message, reasonText) => {
    const adapter = fakeAgentCatalogAdapter({ setEnablement: refusal(code, reason) });
    renderDialog(adapter);
    await userEvent.click(screen.getByRole('button', { name: 'Enable version' }));
    expect(await screen.findByText(message)).toBeInTheDocument();
    if (reasonText) expect(screen.getByText(reasonText)).toBeInTheDocument();
    expect(adapter.setEnablement).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Enable version' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Close' })).toBeInTheDocument();
  });

  it('shows the current enablement when the enablement changed and does not resend', async () => {
    const adapter = fakeAgentCatalogAdapter({
      setEnablement: vi.fn(async () => ({ kind: 'changed' as const })),
      listEnablements: vi.fn(async () => [{ ...tactics, enabledVersion: '0.3.0', etag: '"enablement-tactics-2"' }]),
    });
    renderDialog(adapter);
    await userEvent.click(screen.getByRole('button', { name: 'Enable version' }));
    expect(await screen.findByText(/has changed since you loaded it/)).toBeInTheDocument();
    expect(await screen.findByText('Currently enabled: 0.3.0', { selector: 'div div' })).toBeInTheDocument();
    expect(adapter.setEnablement).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Enable version' })).not.toBeInTheDocument();
  });
});
