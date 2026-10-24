import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { ApproveAgentVersionDialog } from '../../../../src/features/agent-catalog/ApproveAgentVersionDialog';
import type { AgentVersion } from '../../../../src/platform/models/agentCatalog';
import { ADMIN, approvedTactics, draftCoach, draftTactics, fakeAgentCatalogAdapter, type FakeAgentCatalogAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown, memberId: 'someone' }));
vi.mock('../../../../src/platform/adapters/agentCatalogAdapter', () => ({ createAgentCatalogAdapter: () => state.adapter }));
vi.mock('../../../../src/session/sessionContext', () => ({
  useSession: () => ({ platform: {}, session: { member: { id: state.memberId } } }),
}));

function renderDialog(adapter: FakeAgentCatalogAdapter, version: AgentVersion = draftTactics, memberId = 'someone') {
  state.adapter = adapter;
  state.memberId = memberId;
  const onClose = vi.fn();
  const onDone = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <ApproveAgentVersionDialog version={version} open onClose={onClose} onDone={onDone} />
    </QueryClientProvider>,
  );
  return { onClose, onDone };
}

const approveButton = () => screen.getByRole('button', { name: 'Approve version' });

describe('ApproveAgentVersionDialog', () => {
  it('names agent, version, digests and the consequences, and sends one request with the last seen etag', async () => {
    const adapter = fakeAgentCatalogAdapter({ approveAgentVersion: vi.fn(async () => ({ ...draftTactics, state: 'approved' as const })) });
    const { onDone } = renderDialog(adapter);
    expect(screen.getByText('Agent: specialist-tactics')).toBeInTheDocument();
    expect(screen.getByText('Version: 0.1.1')).toBeInTheDocument();
    expect(screen.getByText(`Version digest: ${draftTactics.digest}`)).toBeInTheDocument();
    for (const a of draftTactics.artifacts) expect(screen.getByText(new RegExp(a.digest))).toBeInTheDocument();
    expect(screen.getByText('Approved versions never change.')).toBeInTheDocument();
    expect(screen.getByText(/accountable decision. It is not evaluation evidence/)).toBeInTheDocument();
    expect(screen.queryByText(/self-approval/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Profile distances below/)).not.toBeInTheDocument();
    await userEvent.click(approveButton());
    await waitFor(() => expect(onDone).toHaveBeenCalledTimes(1));
    expect(adapter.approveAgentVersion).toHaveBeenCalledTimes(1);
    expect(adapter.approveAgentVersion).toHaveBeenCalledWith('specialist-tactics', '0.1.1', draftTactics.etag, { differentiationReviewAcknowledged: undefined });
    expect(screen.queryByRole('button', { name: 'Approve version' })).not.toBeInTheDocument();
  });

  it('notes the self-approval without blocking it', async () => {
    const adapter = fakeAgentCatalogAdapter({ approveAgentVersion: vi.fn(async () => approvedTactics) });
    renderDialog(adapter, draftTactics, ADMIN.id);
    expect(screen.getByText(/recorded as a self-approval/)).toBeInTheDocument();
    await userEvent.click(approveButton());
    await waitFor(() => expect(adapter.approveAgentVersion).toHaveBeenCalledTimes(1));
  });

  it('lists review-required distances and needs the acknowledgement, which it sends', async () => {
    const adapter = fakeAgentCatalogAdapter({ approveAgentVersion: vi.fn(async () => approvedTactics) });
    renderDialog(adapter, draftCoach);
    expect(screen.getByText('Profile distances below 0.10')).toBeInTheDocument();
    expect(screen.getAllByRole('listitem').filter((li) => li.textContent?.includes('review required')).length).toBeGreaterThan(0);
    expect(approveButton()).toBeDisabled();
    await userEvent.click(screen.getByRole('checkbox', { name: 'I have reviewed the differentiation of these profiles' }));
    await userEvent.click(approveButton());
    await waitFor(() => expect(adapter.approveAgentVersion).toHaveBeenCalledTimes(1));
    expect(adapter.approveAgentVersion).toHaveBeenCalledWith('coach-lead', '0.2.0', draftCoach.etag, { differentiationReviewAcknowledged: true });
  });

  it('cancelling sends nothing', async () => {
    const adapter = fakeAgentCatalogAdapter();
    const { onClose } = renderDialog(adapter);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
    expect(adapter.approveAgentVersion).not.toHaveBeenCalled();
  });

  it.each([
    ['agent-version-not-draft', /Only a draft agent version can be approved\./],
    ['differentiation-review-required', /acknowledge the differentiation review/],
    ['unheard-of', /Refused with code unheard-of\./],
  ])('shows the refusal %s and never repeats the request', async (code, message) => {
    const adapter = fakeAgentCatalogAdapter({ approveAgentVersion: vi.fn(async () => ({ kind: 'transitionRefused' as const, code })) });
    renderDialog(adapter);
    await userEvent.click(approveButton());
    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(adapter.approveAgentVersion).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Approve version' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Close' })).toBeInTheDocument();
  });

  it('says the version changed, shows the current state and does not resend', async () => {
    const adapter = fakeAgentCatalogAdapter({
      approveAgentVersion: vi.fn(async () => ({ kind: 'changed' as const })),
      getAgentVersion: vi.fn(async () => ({ ...draftTactics, state: 'retired' as const })),
    });
    renderDialog(adapter);
    await userEvent.click(approveButton());
    expect(await screen.findByText(/has changed since you loaded it/)).toBeInTheDocument();
    expect(await screen.findByText('retired')).toBeInTheDocument();
    expect(adapter.approveAgentVersion).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Approve version' })).not.toBeInTheDocument();
  });
});
