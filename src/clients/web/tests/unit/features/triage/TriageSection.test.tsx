import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { vi } from 'vitest';
import { TriageSection } from '../../../../src/features/triage/TriageSection';
import type { ConversationSummary } from '../../../../src/platform/models/agents';
import type { AnalysisRunSummary } from '../../../../src/platform/models/recordings';
import { SessionContext, type SessionApi } from '../../../../src/session/sessionContext';
import '../../../../src/i18n';

const listConversations = vi.fn();
const listByMatch = vi.fn();

vi.mock('../../../../src/platform/adapters/agentConversationAdapter', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../src/platform/adapters/agentConversationAdapter')>();
  return { ...actual, createAgentConversationAdapter: () => ({ listConversations }) };
});
vi.mock('../../../../src/platform/adapters/analysisRunAdapter', () => ({
  createAnalysisRunAdapter: () => ({ listByMatch }),
}));

const run = (runId: string, state: string, createdAt: string): AnalysisRunSummary => ({
  runId,
  recordingSetVersionId: 'set-1',
  workflowId: 'wf',
  workflowVersion: '1',
  state,
  createdAt: new Date(createdAt),
});

const conversation = (id: string, createdAt: string, current = true): ConversationSummary => ({
  conversationId: id,
  matchId: 'match-1',
  analysisRunId: 'run-1',
  teamId: 'team-1',
  analysisRunIsCurrent: current,
  state: 'Open',
  participants: [{ agentId: 'specialist-tactics', addedAt: createdAt }],
  createdAt,
  version: 1,
});

function renderSection(canStart: boolean) {
  const session = { platform: {} } as unknown as SessionApi;
  render(
    <QueryClientProvider client={new QueryClient()}>
      <SessionContext.Provider value={session}>
        <MemoryRouter>
          <TriageSection matchId="match-1" canStart={canStart} />
        </MemoryRouter>
      </SessionContext.Provider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  listConversations.mockReset();
  listByMatch.mockReset();
  listByMatch.mockResolvedValue({ items: [run('run-1', 'Completed', '2026-10-01T10:00:00Z')] });
});

describe('TriageSection', () => {
  it('lists the conversations newest first with links', async () => {
    listConversations.mockResolvedValue([
      conversation('conv-old', '2026-10-02T10:00:00Z'),
      conversation('conv-new', '2026-10-03T10:00:00Z', false),
    ]);
    renderSection(false);
    const links = await screen.findAllByRole('link');
    expect(links).toHaveLength(2);
    expect(links[0]).toHaveAttribute('href', '/matches/match-1/triage/conv-new');
    expect(links[1]).toHaveAttribute('href', '/matches/match-1/triage/conv-old');
    expect(screen.getByText(/No longer current run/)).toBeInTheDocument();
    expect(screen.getAllByText(/Tactics specialist/)).toHaveLength(2);
  });

  it('shows the bound run, participants, creation time, and the not-current marker for each conversation', async () => {
    listConversations.mockResolvedValue([
      conversation('conv-1', '2026-10-02T10:00:00Z'),
      { ...conversation('conv-2', '2026-10-03T10:00:00Z', false), analysisRunId: 'run-9' },
    ]);
    renderSection(false);
    const items = await screen.findAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('Analysis run run-9');
    expect(items[0]).toHaveTextContent('No longer current run');
    expect(items[0]).toHaveTextContent('Participants: Tactics specialist');
    expect(items[1]).toHaveTextContent('Analysis run created');
    expect(items[1]).not.toHaveTextContent('No longer current run');
    expect(items[1]).toHaveTextContent('Created');
  });

  it('says so when there are no conversations and offers no start to viewers', async () => {
    listConversations.mockResolvedValue([]);
    renderSection(false);
    expect(await screen.findByText('You have no triage conversations for this match yet.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Start triage conversation' })).not.toBeInTheDocument();
    expect(screen.queryByText('Triage needs a finished analysis of this match first.')).not.toBeInTheDocument();
  });

  it('offers the start action to editors when an eligible run exists', async () => {
    listConversations.mockResolvedValue([]);
    renderSection(true);
    expect(await screen.findByRole('button', { name: 'Start triage conversation' })).toBeInTheDocument();
    expect(screen.queryByText('Triage needs a finished analysis of this match first.')).not.toBeInTheDocument();
  });

  it('shows the no-eligible-run note to editors without a finished run', async () => {
    listConversations.mockResolvedValue([]);
    listByMatch.mockResolvedValue({ items: [run('run-2', 'Running', '2026-10-01T10:00:00Z')] });
    renderSection(true);
    expect(await screen.findByText('Triage needs a finished analysis of this match first.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Start triage conversation' })).not.toBeInTheDocument();
  });
});
