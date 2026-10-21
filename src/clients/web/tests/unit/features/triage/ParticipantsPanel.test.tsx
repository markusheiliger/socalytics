import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import '../../../../src/i18n';
import { ParticipantsPanel } from '../../../../src/features/triage/ParticipantsPanel';
import { PlatformFailure } from '../../../../src/platform/failure';
import { TriageFailure, type AgentConversationAdapter } from '../../../../src/platform/adapters/agentConversationAdapter';
import { toAgentGroup, type ConversationSummary, type EnabledAgent, type TriageConversation } from '../../../../src/platform/models/agents';

const agents: EnabledAgent[] = [
  { agentId: 'specialist-tactics', group: toAgentGroup('specialist'), domain: 'Tactics' },
  { agentId: 'specialist-fitness', group: toAgentGroup('specialist'), domain: 'Fitness' },
];

function conversation(participants: string[], version = 3): ConversationSummary {
  return {
    conversationId: 'conv-1',
    matchId: 'match-1',
    analysisRunId: 'run-1',
    teamId: 'team-1',
    analysisRunIsCurrent: true,
    state: 'active',
    participants: participants.map((agentId) => ({ agentId, addedAt: '2026-10-19T10:00:00Z' })),
    createdAt: '2026-10-19T09:00:00Z',
    version,
  };
}

function setup(options: { participants: string[]; canEdit: boolean; addParticipant?: ReturnType<typeof vi.fn> }) {
  const addParticipant =
    options.addParticipant ??
    vi.fn(async (): Promise<TriageConversation> => ({ ...conversation(['specialist-tactics'], 4), etag: 'e4' }));
  const listAgents = vi.fn(async () => agents);
  const adapter = { listAgents, addParticipant } as unknown as AgentConversationAdapter;
  const onAdded = vi.fn();
  const onConflict = vi.fn();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ParticipantsPanel
        adapter={adapter}
        conversation={conversation(options.participants)}
        canEdit={options.canEdit}
        onAdded={onAdded}
        onConflict={onConflict}
      />
    </QueryClientProvider>,
  );
  return { addParticipant, onAdded, onConflict, listAgents };
}

describe('ParticipantsPanel', () => {
  it('shows each participant with readable name, identifier, and domain', async () => {
    setup({ participants: ['specialist-tactics'], canEdit: false });
    expect(await screen.findByText('Tactics specialist (specialist-tactics)')).toBeInTheDocument();
    expect(await screen.findByText('Domain: Tactics')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add agent' })).not.toBeInTheDocument();
  });

  it('shows the empty note without participants', async () => {
    setup({ participants: [], canEdit: false });
    expect(await screen.findByText('No agents take part in this conversation yet.')).toBeInTheDocument();
  });

  it('lists only enabled agents not yet participating and adds with the shown version', async () => {
    const { addParticipant, onAdded } = setup({ participants: ['specialist-tactics'], canEdit: true });
    const select = await screen.findByLabelText('Agent to add');
    await waitFor(() => expect(screen.getByRole('option', { name: /specialist-fitness/ })).toBeInTheDocument());
    expect(screen.queryByRole('option', { name: /specialist-tactics/ })).not.toBeInTheDocument();
    const button = screen.getByRole('button', { name: 'Add agent' });
    expect(button).toBeDisabled();

    await userEvent.selectOptions(select, 'specialist-fitness');
    await userEvent.click(button);

    await waitFor(() => expect(addParticipant).toHaveBeenCalledWith('conv-1', 'specialist-fitness', 3));
    await waitFor(() => expect(onAdded).toHaveBeenCalled());
    expect(await screen.findByText('Fitness specialist was added to the conversation')).toBeInTheDocument();
  });

  it('shows the note when all agents already participate', async () => {
    setup({ participants: ['specialist-tactics', 'specialist-fitness'], canEdit: true });
    expect(await screen.findByText('All available agents already take part in this conversation.')).toBeInTheDocument();
    expect(screen.queryByLabelText('Agent to add')).not.toBeInTheDocument();
  });

  it('reports a stale version and asks the owner to reload', async () => {
    const failure = new PlatformFailure('conflict');
    const addParticipant = vi.fn(async () => {
      throw new TriageFailure({ kind: 'staleVersion' }, failure);
    });
    const { onConflict } = setup({ participants: ['specialist-tactics'], canEdit: true, addParticipant });
    await userEvent.selectOptions(await screen.findByLabelText('Agent to add'), 'specialist-fitness');
    await userEvent.click(screen.getByRole('button', { name: 'Add agent' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The conversation changed. It has been reloaded; choose again.');
    expect(onConflict).toHaveBeenCalledWith({ kind: 'staleVersion' });
  });

  it('reloads and asks to choose again when the participant already exists', async () => {
    const addParticipant = vi.fn(async () => {
      throw new TriageFailure({ kind: 'participantExists' }, new PlatformFailure('conflict'));
    });
    const { onConflict } = setup({ participants: ['specialist-tactics'], canEdit: true, addParticipant });
    const select = await screen.findByLabelText('Agent to add');
    await userEvent.selectOptions(select, 'specialist-fitness');
    await userEvent.click(screen.getByRole('button', { name: 'Add agent' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The conversation changed. It has been reloaded; choose again.');
    expect(onConflict).toHaveBeenCalledWith({ kind: 'participantExists' });
    expect(select).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Add agent' })).toBeDisabled();
  });

  it('reloads the agents and the conversation when the agent is no longer available', async () => {
    const addParticipant = vi.fn(async () => {
      throw new TriageFailure({ kind: 'agentNotAvailable' }, new PlatformFailure('conflict'));
    });
    const { onConflict, listAgents } = setup({ participants: ['specialist-tactics'], canEdit: true, addParticipant });
    await userEvent.selectOptions(await screen.findByLabelText('Agent to add'), 'specialist-fitness');
    expect(listAgents).toHaveBeenCalledTimes(1);
    await userEvent.click(screen.getByRole('button', { name: 'Add agent' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('This agent is no longer available on this platform.');
    expect(onConflict).toHaveBeenCalledWith({ kind: 'agentNotAvailable' });
    await waitFor(() => expect(listAgents).toHaveBeenCalledTimes(2));
    expect(screen.getByLabelText('Agent to add')).toHaveValue('');
  });
});
