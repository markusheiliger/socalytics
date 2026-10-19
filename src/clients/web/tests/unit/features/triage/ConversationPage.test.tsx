import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../../src/i18n';
import { ConversationPage } from '../../../../src/features/triage/ConversationPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { TriageFailure } from '../../../../src/platform/adapters/agentConversationAdapter';
import type { TriageConversation } from '../../../../src/platform/models/agents';
import { SessionContext, type SessionApi } from '../../../../src/session/sessionContext';

const getConversation = vi.fn();
const getMatch = vi.fn();
const getTeam = vi.fn();
const listByMatch = vi.fn();

vi.mock('../../../../src/platform/adapters/agentConversationAdapter', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../src/platform/adapters/agentConversationAdapter')>();
  return { ...actual, createAgentConversationAdapter: () => ({ getConversation }) };
});
vi.mock('../../../../src/platform/adapters/matchAdapter', () => ({ createMatchAdapter: () => ({ get: getMatch }) }));
vi.mock('../../../../src/platform/adapters/teamAdapter', () => ({ createTeamAdapter: () => ({ get: getTeam }) }));
vi.mock('../../../../src/platform/adapters/analysisRunAdapter', () => ({ createAnalysisRunAdapter: () => ({ listByMatch }) }));
vi.mock('../../../../src/features/triage/ParticipantsPanel', () => ({
  ParticipantsPanel: ({ canEdit }: { canEdit: boolean }) => <div>participants {canEdit ? 'editable' : 'readonly'}</div>,
}));
vi.mock('../../../../src/features/triage/TurnList', () => ({
  TurnList: ({ pendingTurns }: { pendingTurns: { invocationId: string; question: string }[] }) => (
    <ul>{pendingTurns.map((turn) => <li key={turn.invocationId}>pending {turn.question}</li>)}</ul>
  ),
}));
vi.mock('../../../../src/features/triage/QuestionComposer', () => ({
  QuestionComposer: ({ onAccepted }: { onAccepted: (a: unknown, agent: string, q: string) => void }) => (
    <button onClick={() => onAccepted({ conversationId: 'conv-1', invocationId: 'inv-1', statusLocation: '', retryAfterSeconds: 2 }, 'specialist-tactics', 'Why?')}>
      composer
    </button>
  ),
}));

const conversation = (over: Partial<TriageConversation> = {}): TriageConversation => ({
  conversationId: 'conv-1',
  matchId: 'match-1',
  analysisRunId: 'run-1',
  teamId: 'team-1',
  analysisRunIsCurrent: true,
  state: 'Open',
  participants: [{ agentId: 'specialist-tactics', addedAt: '2026-10-19T10:00:00Z' }],
  createdAt: '2026-10-19T09:00:00Z',
  version: 1,
  etag: 'e1',
  ...over,
});

function renderPage(role: 'Coach' | 'Viewer' | undefined) {
  const member = {
    id: 'm1',
    accountName: 'a',
    membershipStatus: 'Active',
    clubRoles: [],
    teamRoles: role ? [{ teamId: 'team-1', teamName: 'First', seasonId: 's', role }] : [],
  };
  const session = { platform: {}, session: { member }, status: 'signedIn' } as unknown as SessionApi;
  render(
    <QueryClientProvider client={new QueryClient()}>
      <SessionContext.Provider value={session}>
        <MemoryRouter initialEntries={['/matches/match-1/triage/conv-1']}>
          <Routes>
            <Route path="/matches/:matchId/triage/:conversationId" element={<ConversationPage />} />
          </Routes>
        </MemoryRouter>
      </SessionContext.Provider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  getConversation.mockReset();
  getMatch.mockReset();
  getTeam.mockReset();
  listByMatch.mockReset();
  getMatch.mockResolvedValue({ id: 'match-1', teamId: 'team-1', opponentName: 'Rivals FC', kickoffAt: new Date(), homeAway: 'Home' });
  getTeam.mockResolvedValue({ id: 'team-1', seasonState: 'active' });
  listByMatch.mockResolvedValue({ items: [{ runId: 'run-1', state: 'Completed', createdAt: new Date('2026-10-01T10:00:00Z') }] });
  getConversation.mockResolvedValue({ kind: 'changed', value: conversation(), etag: 'e1' });
});

describe('ConversationPage', () => {
  it('shows the bound run with a review link, participants, and for Editors the composer feeding pending turns', async () => {
    renderPage('Coach');
    const link = await screen.findByRole('link', { name: 'Open the analysis review' });
    expect(link).toHaveAttribute('href', '/matches/match-1/analysis-runs/run-1');
    expect(screen.getByText('participants editable')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'composer' }));
    expect(await screen.findByText('pending Why?')).toBeInTheDocument();
  });

  it('shows the superseded-run notice', async () => {
    getConversation.mockResolvedValue({ kind: 'changed', value: conversation({ analysisRunIsCurrent: false }), etag: 'e1' });
    renderPage('Coach');
    expect(await screen.findByText(/no longer the match's current run/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'composer' })).toBeInTheDocument();
    expect(screen.getByText('participants editable')).toBeInTheDocument();
    expect(screen.queryByText(/only Club Admins and Coaches/i)).toBeNull();
  });

  it('shows no superseded-run notice while the bound run is current', async () => {
    renderPage('Coach');
    await screen.findByText('participants editable');
    expect(screen.queryByText(/no longer the match's current run/i)).toBeNull();
    expect(screen.queryByText(/only Club Admins and Coaches/i)).toBeNull();
  });

  it('shows both notes for a Viewer of a superseded conversation without any submit control', async () => {
    getConversation.mockResolvedValue({ kind: 'changed', value: conversation({ analysisRunIsCurrent: false }), etag: 'e1' });
    renderPage('Viewer');
    expect(await screen.findByText(/no longer the match's current run/i)).toBeInTheDocument();
    expect(screen.getByText(/only Club Admins and Coaches/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'composer' })).toBeNull();
  });

  it('shows a Viewer the conversation read-only without the composer', async () => {
    renderPage('Viewer');
    expect(await screen.findByText('participants readonly')).toBeInTheDocument();
    expect(screen.getByText(/only Club Admins and Coaches/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'composer' })).toBeNull();
  });

  it('shows the not-found view when the platform does not return the conversation', async () => {
    getConversation.mockRejectedValue(new TriageFailure({ kind: 'notFound' }, new PlatformFailure('unexpected', { status: 404 })));
    renderPage('Coach');
    expect(await screen.findByRole('heading', { name: 'This page does not exist.' })).toBeInTheDocument();
    expect(screen.queryByText('participants editable')).toBeNull();
  });

  it('shows the not-found view for a conversation of another match', async () => {
    getConversation.mockResolvedValue({ kind: 'changed', value: conversation({ matchId: 'other' }), etag: 'e1' });
    renderPage('Coach');
    expect(await screen.findByRole('heading', { name: 'This page does not exist.' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Open the analysis review' })).toBeNull();
  });

  it('shows the not-permitted view without a team role', async () => {
    renderPage(undefined);
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
  });
});
