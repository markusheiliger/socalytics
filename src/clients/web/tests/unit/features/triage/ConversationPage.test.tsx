import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, Link, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../../src/i18n';
import { ConversationPage } from '../../../../src/features/triage/ConversationPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { TriageFailure } from '../../../../src/platform/adapters/agentConversationAdapter';
import type { TriageConversation } from '../../../../src/platform/models/agents';
import { ConnectivityContext } from '../../../../src/connectivity/connectivityContext';
import { SessionContext, type SessionApi } from '../../../../src/session/sessionContext';

const getConversation = vi.fn();
const archiveConversation = vi.fn();
const unarchiveConversation = vi.fn();
const deleteConversation = vi.fn();
const getMatch = vi.fn();
const getTeam = vi.fn();
const listByMatch = vi.fn();

vi.mock('../../../../src/platform/adapters/agentConversationAdapter', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../src/platform/adapters/agentConversationAdapter')>();
  return { ...actual, createAgentConversationAdapter: () => ({ getConversation, archiveConversation, unarchiveConversation, deleteConversation }) };
});
vi.mock('../../../../src/platform/adapters/matchAdapter', () => ({ createMatchAdapter: () => ({ get: getMatch }) }));
vi.mock('../../../../src/platform/adapters/teamAdapter', () => ({ createTeamAdapter: () => ({ get: getTeam }) }));
vi.mock('../../../../src/platform/adapters/analysisRunAdapter', () => ({ createAnalysisRunAdapter: () => ({ listByMatch }) }));
vi.mock('../../../../src/features/triage/ParticipantsPanel', () => ({
  ParticipantsPanel: ({ canEdit }: { canEdit: boolean }) => <div>participants {canEdit ? 'editable' : 'readonly'}</div>,
}));
vi.mock('../../../../src/features/triage/TurnList', () => ({
  TurnList: ({ pendingTurns, onNotFound }: { pendingTurns: { invocationId: string; question: string }[]; onNotFound: () => void }) => (
    <div>
      <ul>{pendingTurns.map((turn) => <li key={turn.invocationId}>pending {turn.question}</li>)}</ul>
      <button onClick={onNotFound}>poll not found</button>
    </div>
  ),
}));
vi.mock('../../../../src/features/triage/QuestionComposer', () => ({
  QuestionComposer: ({ onAccepted, onUnsentChange, onOutcome }: { onAccepted: (a: unknown, agent: string, q: string) => void; onUnsentChange: (unsent: boolean) => void; onOutcome: (o: unknown) => void }) => (
    <>
      <button onClick={() => onUnsentChange(true)}>type draft</button>
      <button onClick={() => onUnsentChange(false)}>clear draft</button>
      <button onClick={() => onOutcome({ kind: 'notPermitted' })}>refuse</button>
    <button onClick={() => onAccepted({ conversationId: 'conv-1', invocationId: 'inv-1', statusLocation: '', retryAfterSeconds: 2 }, 'specialist-tactics', 'Why?')}>
      composer
    </button>
    </>
  ),
}));

const conversation = (over: Partial<TriageConversation> = {}): TriageConversation => ({
  conversationId: 'conv-1',
  matchId: 'match-1',
  analysisRunId: 'run-1',
  teamId: 'team-1',
  analysisRunIsCurrent: true,
  state: 'active',
  participants: [{ agentId: 'specialist-tactics', addedAt: '2026-10-19T10:00:00Z' }],
  createdAt: '2026-10-19T09:00:00Z',
  version: 1,
  etag: 'e1',
  ...over,
});

function renderPage(role: 'Coach' | 'Viewer' | undefined, unreachable = false) {
  const member = {
    id: 'm1',
    accountName: 'a',
    membershipStatus: 'Active',
    clubRoles: [],
    teamRoles: role ? [{ teamId: 'team-1', teamName: 'First', seasonId: 's', role }] : [],
  };
  const session = { platform: {}, session: { member }, status: 'signedIn' } as unknown as SessionApi;
  const router = createMemoryRouter(
    [
      { path: '/matches/:matchId/triage/:conversationId', element: <><ConversationPage /><Link to="/elsewhere">elsewhere</Link></> },
      { path: '/elsewhere', element: <div>elsewhere page</div> },
      { path: '/matches/:matchId', element: <div>match page</div> },
    ],
    { initialEntries: ['/matches/match-1/triage/conv-1'] },
  );
  render(
    <QueryClientProvider client={new QueryClient()}>
      <ConnectivityContext.Provider value={{ status: unreachable ? 'unreachable' : 'online', isUnreachable: unreachable, reportNetworkFailure: () => {} }}>
        <SessionContext.Provider value={session}>
          <RouterProvider router={router} />
        </SessionContext.Provider>
      </ConnectivityContext.Provider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  getConversation.mockReset();
  archiveConversation.mockReset();
  unarchiveConversation.mockReset();
  deleteConversation.mockReset();
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

  it('shows the not-found view when a poll or read inside the history answers not found', async () => {
    renderPage('Coach');
    await userEvent.click(await screen.findByRole('button', { name: 'poll not found' }));
    expect(await screen.findByRole('heading', { name: 'This page does not exist.' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'poll not found' })).toBeNull();
  });

  it('switches to read-only when a submission answers not permitted', async () => {
    renderPage('Coach');
    await userEvent.click(await screen.findByRole('button', { name: 'refuse' }));
    expect(await screen.findByText(/only Club Admins and Coaches/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'composer' })).toBeNull();
  });

  it('shows the connection-required state while offline', async () => {
    renderPage('Coach', true);
    expect(await screen.findByText(/A connection is required/)).toBeInTheDocument();
  });

  it('asks before leaving with an unsent question and lets the member stay or leave', async () => {
    renderPage('Coach');
    await userEvent.click(await screen.findByRole('button', { name: 'type draft' }));
    await userEvent.click(screen.getByRole('link', { name: 'elsewhere' }));
    expect(await screen.findByText('Leave this conversation? Your unsent question will be lost.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Stay' }));
    expect(screen.queryByText('elsewhere page')).toBeNull();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await userEvent.click(screen.getByRole('link', { name: 'elsewhere' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Leave' }));
    expect(await screen.findByText('elsewhere page')).toBeInTheDocument();
  });

  it('does not ask before leaving without unsent input', async () => {
    renderPage('Coach');
    await userEvent.click(await screen.findByRole('button', { name: 'type draft' }));
    await userEvent.click(screen.getByRole('button', { name: 'clear draft' }));
    await userEvent.click(screen.getByRole('link', { name: 'elsewhere' }));
    expect(await screen.findByText('elsewhere page')).toBeInTheDocument();
  });

  it('shows the not-permitted view without a team role', async () => {
    renderPage(undefined);
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
  });

  describe('lifecycle actions', () => {
    const archivedConversation = () => ({ kind: 'changed', value: conversation({ state: 'archived', version: 2 }), etag: 'e2' });

    it('offers archive and delete to a Coach and no lifecycle action to a Viewer', async () => {
      renderPage('Viewer');
      await screen.findByText('participants readonly');
      expect(screen.queryByRole('button', { name: 'Archive' })).toBeNull();
      expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
      expect(screen.queryByRole('group', { name: 'Conversation actions' })).toBeNull();
    });

    it('confirms an archive naming its effect, sends the version, and reloads the archived state', async () => {
      archiveConversation.mockResolvedValue(conversation({ state: 'archived' }));
      renderPage('Coach');
      await userEvent.click(await screen.findByRole('button', { name: 'Archive' }));
      expect(await screen.findByText('Archive this conversation?')).toBeInTheDocument();
      expect(archiveConversation).not.toHaveBeenCalled();
      getConversation.mockResolvedValue(archivedConversation());
      await userEvent.click(screen.getByRole('button', { name: 'Archive conversation' }));
      expect(archiveConversation).toHaveBeenCalledWith('conv-1', 1);
      expect(await screen.findByText('The conversation was archived.')).toBeInTheDocument();
      expect(await screen.findByRole('button', { name: 'Unarchive' })).toBeInTheDocument();
      expect(screen.getByText(/This conversation is archived/)).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'composer' })).toBeNull();
      expect(screen.getByText('participants readonly')).toBeInTheDocument();
    });

    it('changes nothing when the confirmation is cancelled', async () => {
      renderPage('Coach');
      await userEvent.click(await screen.findByRole('button', { name: 'Delete' }));
      expect(await screen.findByText('Delete this conversation permanently?')).toBeInTheDocument();
      expect(screen.getByText(/Deleting is permanent/)).toBeInTheDocument();
      await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      expect(deleteConversation).not.toHaveBeenCalled();
      expect(archiveConversation).not.toHaveBeenCalled();
    });

    it('unarchives an archived conversation after confirmation', async () => {
      getConversation.mockResolvedValue(archivedConversation());
      unarchiveConversation.mockResolvedValue(conversation());
      renderPage('Coach');
      await userEvent.click(await screen.findByRole('button', { name: 'Unarchive' }));
      expect(await screen.findByText('Unarchive this conversation?')).toBeInTheDocument();
      getConversation.mockResolvedValue({ kind: 'changed', value: conversation({ version: 3 }), etag: 'e3' });
      await userEvent.click(screen.getByRole('button', { name: 'Unarchive conversation' }));
      expect(unarchiveConversation).toHaveBeenCalledWith('conv-1', 2);
      expect(await screen.findByText('The conversation was unarchived.')).toBeInTheDocument();
      expect(await screen.findByRole('button', { name: 'composer' })).toBeInTheDocument();
    });

    it('shows the loading state while the platform has not answered and no optimistic change', async () => {
      let resolve: (value: unknown) => void = () => {};
      archiveConversation.mockReturnValue(new Promise((r) => (resolve = r)));
      renderPage('Coach');
      await userEvent.click(await screen.findByRole('button', { name: 'Archive' }));
      await userEvent.click(await screen.findByRole('button', { name: 'Archive conversation' }));
      expect(await screen.findByRole('button', { name: 'Archiving…' })).toBeDisabled();
      expect(screen.queryByText('The conversation was archived.')).toBeNull();
      expect(screen.queryByText(/This conversation is archived/)).toBeNull();
      resolve(conversation({ state: 'archived' }));
      expect(await screen.findByText('The conversation was archived.')).toBeInTheDocument();
    });

    it('deletes after confirmation and shows the success view without the conversation', async () => {
      deleteConversation.mockResolvedValue(undefined);
      renderPage('Coach');
      await userEvent.click(await screen.findByRole('button', { name: 'Delete' }));
      await userEvent.click(await screen.findByRole('button', { name: 'Delete permanently' }));
      expect(deleteConversation).toHaveBeenCalledWith('conv-1', 1);
      expect(await screen.findByText('The conversation was deleted.')).toBeInTheDocument();
      expect(screen.queryByText('participants editable')).toBeNull();
      expect(screen.getByRole('link', { name: /match/i })).toHaveAttribute('href', '/matches/match-1');
    });

    it.each([
      ['not permitted', new TriageFailure({ kind: 'notPermitted' }, new PlatformFailure('notPermitted', { status: 403 })), /Only Club Admins and Coaches of the team can archive/],
      ['unavailable', new TriageFailure({ kind: 'platform', failureKind: 'unexpected' }, new PlatformFailure('unexpected', { status: 500 })), /platform is unavailable/],
      ['unknown outcome', new TriageFailure({ kind: 'unknownOutcome' }, new PlatformFailure('unreachable', {})), /could not confirm the result/],
      ['refusal', new TriageFailure({ kind: 'platform', failureKind: 'validation' }, new PlatformFailure('validation', { status: 400 })), /could not be changed. Try again/],
    ])('shows the %s state inside the dialog and leaves the conversation unchanged', async (_name, failure, message) => {
      archiveConversation.mockRejectedValue(failure);
      renderPage('Coach');
      await userEvent.click(await screen.findByRole('button', { name: 'Archive' }));
      await userEvent.click(await screen.findByRole('button', { name: 'Archive conversation' }));
      expect(await screen.findByText(message)).toBeInTheDocument();
      expect(screen.queryByText('The conversation was archived.')).toBeNull();
      expect(screen.getByText('participants editable')).toBeInTheDocument();
    });

    it('shows the conflict with a reload offer that rereads the conversation', async () => {
      archiveConversation.mockRejectedValue(new TriageFailure({ kind: 'staleVersion' }, new PlatformFailure('unexpected', { status: 412 })));
      renderPage('Coach');
      await userEvent.click(await screen.findByRole('button', { name: 'Archive' }));
      await userEvent.click(await screen.findByRole('button', { name: 'Archive conversation' }));
      expect(await screen.findByText(/changed in the meantime, so nothing was changed/)).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Archive conversation' })).toBeNull();
      const reads = getConversation.mock.calls.length;
      await userEvent.click(screen.getByRole('button', { name: 'Reload conversation' }));
      await waitFor(() => expect(getConversation.mock.calls.length).toBeGreaterThan(reads));
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    });

    it('shows the unavailable view when the platform answers not found to an action', async () => {
      deleteConversation.mockRejectedValue(new TriageFailure({ kind: 'notFound' }, new PlatformFailure('unexpected', { status: 404 })));
      renderPage('Coach');
      await userEvent.click(await screen.findByRole('button', { name: 'Delete' }));
      await userEvent.click(await screen.findByRole('button', { name: 'Delete permanently' }));
      expect(await screen.findByRole('heading', { name: 'This page does not exist.' })).toBeInTheDocument();
      expect(screen.getByText(/This conversation is not available/)).toBeInTheDocument();
    });

    it('disables the confirmation and says so while offline', async () => {
      renderPage('Coach', true);
      await userEvent.click(await screen.findByRole('button', { name: 'Archive' }));
      expect(await screen.findByText('Managing conversations needs a connection. Reconnect to continue.')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Archive conversation' })).toBeDisabled();
      expect(archiveConversation).not.toHaveBeenCalled();
    });

    it('shows the unavailable view without actions to a non-initiator whose read is answered not found', async () => {
      getConversation.mockRejectedValue(new TriageFailure({ kind: 'notFound' }, new PlatformFailure('unexpected', { status: 404 })));
      renderPage('Coach');
      expect(await screen.findByRole('heading', { name: 'This page does not exist.' })).toBeInTheDocument();
      expect(screen.getByText(/not one of your conversations/)).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Archive' })).toBeNull();
      expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
    });
  });
});
