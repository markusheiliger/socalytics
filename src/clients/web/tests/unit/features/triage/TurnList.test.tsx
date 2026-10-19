import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '../../../../src/i18n';
import { TurnList, type PendingTurn } from '../../../../src/features/triage/TurnList';
import type { AgentConversationAdapter } from '../../../../src/platform/adapters/agentConversationAdapter';
import { toFailureReason, toTurnState, type Turn } from '../../../../src/platform/models/agents';

function turn(invocationId: string, state: string, extra: Partial<Turn> = {}): Turn {
  return {
    invocationId,
    agentId: 'specialist-tactics',
    question: `Question ${invocationId}`,
    state: toTurnState(state),
    submittedAt: '2026-10-19T10:00:00Z',
    ...extra,
  };
}

function setup(turns: Turn[], pending: PendingTurn[] = []) {
  const getInvocationStatus = vi.fn(() => new Promise<never>(() => {}));
  const listTurns = vi.fn(async () => ({ kind: 'changed' as const, etag: 'e', value: { turns, etag: 'e' } }));
  const adapter = { listTurns, getInvocationStatus } as unknown as AgentConversationAdapter;
  const onPendingResolved = vi.fn();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <TurnList adapter={adapter} conversationId="conv-1" pendingTurns={pending} onPendingResolved={onPendingResolved} />
    </QueryClientProvider>,
  );
  return { listTurns, onPendingResolved };
}

describe('TurnList', () => {
  it('shows the empty note without turns', async () => {
    setup([]);
    expect(await screen.findByText('No questions have been asked in this conversation yet.')).toBeInTheDocument();
  });

  it('shows history in order with advice, failure reasons, and unfinished states', async () => {
    setup([
      turn('inv-1', 'succeeded', {
        advice: { text: 'Press higher.', evidenceReferences: [{ reference: 'clip-1' }] },
      }),
      turn('inv-2', 'failed', { failureReason: toFailureReason('scope-lost') }),
      turn('inv-3', 'failed', { failureReason: toFailureReason('odd-reason') }),
      turn('inv-4', 'running'),
      turn('inv-5', 'queued'),
    ]);
    await screen.findByText('Question inv-5');
    const list = screen.getByRole('list', { name: 'Questions and advice' });
    const items = Array.from(list.querySelectorAll(':scope > li'));
    expect(items).toHaveLength(5);
    expect(items[0]).toHaveTextContent('Question inv-1');
    expect(items[0]).toHaveTextContent('Press higher.');
    expect(items[1]).toHaveTextContent('No advice: access to this team was lost');
    expect(items[2]).toHaveTextContent('No advice. Reason: odd-reason');
    expect(items[3]).toHaveTextContent('The agent is working on it');
    expect(items[4]).toHaveTextContent('Waiting for the agent');
  });

  it('keeps an accepted turn after the history until the history contains it', async () => {
    const pending: PendingTurn = {
      invocationId: 'inv-9',
      agentId: 'specialist-tactics',
      question: 'Who presses?',
      submittedAt: '2026-10-19T10:05:00Z',
      retryAfterSeconds: 2,
    };
    setup([turn('inv-1', 'succeeded', { advice: { text: 'A', evidenceReferences: [] } })], [pending]);
    await screen.findByText('Question inv-1');
    const list = screen.getByRole('list', { name: 'Questions and advice' });
    const items = Array.from(list.querySelectorAll(':scope > li'));
    expect(items).toHaveLength(2);
    expect(items[1]).toHaveTextContent('Who presses?');
    expect(items[1]).toHaveTextContent('Waiting for the agent');
  });

  it('shows each turn once and reports pending turns that the history contains', async () => {
    const pending: PendingTurn = {
      invocationId: 'inv-1',
      agentId: 'specialist-tactics',
      question: 'Question inv-1',
      submittedAt: '2026-10-19T10:00:00Z',
    };
    const { onPendingResolved } = setup([turn('inv-1', 'queued')], [pending]);
    await waitFor(() => expect(onPendingResolved).toHaveBeenCalledWith(['inv-1']));
    const list = screen.getByRole('list', { name: 'Questions and advice' });
    expect(list.querySelectorAll(':scope > li')).toHaveLength(1);
  });

  it('renders platform text as text', async () => {
    setup([turn('inv-1', 'queued', { question: '<img src=x onerror=alert(1)>' })]);
    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
  });

  it('announces state changes politely', async () => {
    setup([turn('inv-1', 'queued')]);
    const live = await screen.findByRole('status');
    expect(live).toHaveAttribute('aria-live', 'polite');
  });
});
