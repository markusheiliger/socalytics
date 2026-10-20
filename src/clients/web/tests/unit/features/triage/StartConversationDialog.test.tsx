import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { vi } from 'vitest';
import { StartConversationDialog } from '../../../../src/features/triage/StartConversationDialog';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { AnalysisRunSummary } from '../../../../src/platform/models/recordings';
import { SessionContext, type SessionApi } from '../../../../src/session/sessionContext';
import '../../../../src/i18n';

const createConversation = vi.fn();

vi.mock('../../../../src/platform/adapters/agentConversationAdapter', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../src/platform/adapters/agentConversationAdapter')>();
  return { ...actual, createAgentConversationAdapter: () => ({ createConversation }) };
});

const { TriageFailure } = await import('../../../../src/platform/adapters/agentConversationAdapter');

const run = (runId: string, state: string, createdAt: string): AnalysisRunSummary => ({
  runId,
  recordingSetVersionId: 'set-1',
  workflowId: 'wf',
  workflowVersion: '1',
  state,
  createdAt: new Date(createdAt),
});

const runs = [
  run('run-old', 'Completed', '2026-10-01T10:00:00Z'),
  run('run-failed', 'Failed', '2026-10-05T10:00:00Z'),
  run('run-new', 'PartiallyCompleted', '2026-10-03T10:00:00Z'),
  run('run-running', 'Running', '2026-10-06T10:00:00Z'),
];

function Location() {
  return <p data-testid="location">{useLocation().pathname}</p>;
}

function renderDialog(onClose = vi.fn()) {
  const session = { platform: {} } as unknown as SessionApi;
  render(
    <QueryClientProvider client={new QueryClient()}>
      <SessionContext.Provider value={session}>
        <MemoryRouter initialEntries={['/matches/match-1']}>
          <Routes>
            <Route path="*" element={<><StartConversationDialog open matchId="match-1" runs={runs} onClose={onClose} /><Location /></>} />
          </Routes>
        </MemoryRouter>
      </SessionContext.Provider>
    </QueryClientProvider>,
  );
  return onClose;
}

beforeEach(() => createConversation.mockReset());

describe('StartConversationDialog', () => {
  it('lists only completed and partially completed runs with the newest preselected, and starts the conversation', async () => {
    createConversation.mockResolvedValue({ conversationId: 'conv-1' });
    renderDialog();
    const choice = screen.getByRole('combobox', { name: 'Analysis run' });
    expect(choice).toHaveTextContent('Partially completed');
    await userEvent.click(choice);
    expect(screen.getAllByRole('option')).toHaveLength(2);
    expect(screen.queryByText(/Failed|Running/)).not.toBeInTheDocument();
    await userEvent.click(screen.getAllByRole('option')[0]!);
    await userEvent.click(screen.getByRole('button', { name: 'Start triage conversation' }));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/matches/match-1/triage/conv-1'));
    expect(createConversation).toHaveBeenCalledWith('match-1', 'run-new', expect.stringMatching(/^[0-9a-f-]{36}$/));
  });

  it('uses the chosen run', async () => {
    createConversation.mockResolvedValue({ conversationId: 'conv-2' });
    renderDialog();
    await userEvent.click(screen.getByRole('combobox', { name: 'Analysis run' }));
    await userEvent.click(screen.getAllByRole('option')[1]!);
    await userEvent.click(screen.getByRole('button', { name: 'Start triage conversation' }));
    await waitFor(() => expect(createConversation).toHaveBeenCalledWith('match-1', 'run-old', expect.any(String)));
  });

  it('shows a refusal and stays open', async () => {
    createConversation.mockRejectedValueOnce(new TriageFailure({ kind: 'runNotEligible' }, new PlatformFailure('conflict')));
    renderDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Start triage conversation' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('This analysis run cannot be used for triage. Choose another run.');
    expect(screen.getByTestId('location')).toHaveTextContent('/matches/match-1');
  });

  it('reuses the key when resending after an unknown outcome', async () => {
    createConversation.mockRejectedValueOnce(new TriageFailure({ kind: 'unknownOutcome' }, new PlatformFailure('unreachable')));
    createConversation.mockResolvedValueOnce({ conversationId: 'conv-3' });
    renderDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Start triage conversation' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('We could not confirm that the conversation was started.');
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(createConversation).toHaveBeenCalledTimes(2));
    expect(createConversation.mock.calls[1]![2]).toBe(createConversation.mock.calls[0]![2]);
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/matches/match-1/triage/conv-3'));
  });

  it('uses a new key when another run is chosen after an unknown outcome', async () => {
    createConversation.mockRejectedValueOnce(new TriageFailure({ kind: 'unknownOutcome' }, new PlatformFailure('unreachable')));
    createConversation.mockRejectedValueOnce(new TriageFailure({ kind: 'runNotEligible' }, new PlatformFailure('conflict')));
    renderDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Start triage conversation' }));
    await screen.findByRole('button', { name: 'Try again' });
    await userEvent.click(screen.getByRole('combobox', { name: 'Analysis run' }));
    await userEvent.click(screen.getAllByRole('option')[1]!);
    await userEvent.click(screen.getByRole('button', { name: 'Start triage conversation' }));
    await waitFor(() => expect(createConversation).toHaveBeenCalledTimes(2));
    expect(createConversation.mock.calls[1]![2]).not.toBe(createConversation.mock.calls[0]![2]);
  });

  it('shows the notPermitted message and stays open', async () => {
    createConversation.mockRejectedValueOnce(new TriageFailure({ kind: 'notPermitted' }, new PlatformFailure('notPermitted')));
    renderDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Start triage conversation' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Only Club Admins and Coaches of the team can start triage conversations.');
    expect(screen.getByTestId('location')).toHaveTextContent('/matches/match-1');
  });

  it('closes on cancel', async () => {
    const onClose = renderDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
  });
});
