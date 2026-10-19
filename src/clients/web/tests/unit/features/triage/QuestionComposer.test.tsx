import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import '../../../../src/i18n';
import { QuestionComposer } from '../../../../src/features/triage/QuestionComposer';
import { PlatformFailure } from '../../../../src/platform/failure';
import { TriageFailure, type AgentConversationAdapter } from '../../../../src/platform/adapters/agentConversationAdapter';
import type { ConversationSummary, SubmissionAccepted, TriageOutcome } from '../../../../src/platform/models/agents';

function conversation(participants: string[]): ConversationSummary {
  return {
    conversationId: 'conv-1',
    matchId: 'match-1',
    analysisRunId: 'run-1',
    teamId: 'team-1',
    analysisRunIsCurrent: true,
    state: 'active',
    participants: participants.map((agentId) => ({ agentId, addedAt: '2026-10-19T10:00:00Z' })),
    createdAt: '2026-10-19T09:00:00Z',
    version: 3,
  };
}

const accepted: SubmissionAccepted = {
  conversationId: 'conv-1',
  invocationId: 'inv-1',
  statusLocation: '/api/v1/agent-invocations/inv-1',
  retryAfterSeconds: 2,
};

function failure(outcome: TriageOutcome) {
  return new TriageFailure(outcome, new PlatformFailure('conflict', { status: 409 }));
}

function setup(submitQuestion: ReturnType<typeof vi.fn>, participants = ['specialist-tactics', 'specialist-fitness']) {
  const adapter = { submitQuestion } as unknown as AgentConversationAdapter;
  const onAccepted = vi.fn();
  const onOutcome = vi.fn();
  const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <QuestionComposer
        adapter={adapter}
        conversation={conversation(participants)}
        onAccepted={onAccepted}
        onOutcome={onOutcome}
      />
    </QueryClientProvider>,
  );
  return { onAccepted, onOutcome };
}

describe('QuestionComposer', () => {
  it('keeps send disabled for empty text or without an agent', async () => {
    const user = userEvent.setup();
    setup(vi.fn());
    const send = screen.getByRole('button', { name: 'Send question' });
    expect(send).toBeDisabled();
    await user.type(screen.getByLabelText('Your question'), '   ');
    await user.selectOptions(screen.getByLabelText('Ask this agent'), 'specialist-fitness');
    expect(send).toBeDisabled();
    await user.type(screen.getByLabelText('Your question'), 'Who presses?');
    expect(send).toBeEnabled();
  });

  it('submits to the chosen participant, clears on acceptance, and reports it', async () => {
    const user = userEvent.setup();
    const submitQuestion = vi.fn(async () => accepted);
    const { onAccepted } = setup(submitQuestion);
    await user.selectOptions(screen.getByLabelText('Ask this agent'), 'specialist-fitness');
    await user.type(screen.getByLabelText('Your question'), 'How is the load?');
    await user.click(screen.getByRole('button', { name: 'Send question' }));

    await waitFor(() => expect(onAccepted).toHaveBeenCalledWith(accepted, 'specialist-fitness', 'How is the load?'));
    expect(submitQuestion).toHaveBeenCalledWith(
      'conv-1',
      'specialist-fitness',
      'How is the load?',
      expect.stringMatching(/.+/),
    );
    await waitFor(() => expect(screen.getByLabelText('Your question')).toHaveValue(''));
    expect(screen.getByLabelText('Ask this agent')).toHaveValue('specialist-fitness');
  });

  it('shows Sending… while the submission is in flight', async () => {
    const user = userEvent.setup();
    let resolve: (value: SubmissionAccepted) => void = () => {};
    setup(vi.fn(() => new Promise<SubmissionAccepted>((r) => (resolve = r))), ['specialist-tactics']);
    await user.type(screen.getByLabelText('Your question'), 'Question');
    await user.click(screen.getByRole('button', { name: 'Send question' }));
    expect(await screen.findByRole('button', { name: 'Sending…' })).toBeDisabled();
    resolve(accepted);
    expect(await screen.findByRole('button', { name: 'Send question' })).toBeInTheDocument();
  });

  it('preselects the only participant', () => {
    setup(vi.fn(), ['specialist-tactics']);
    expect(screen.getByLabelText('Ask this agent')).toHaveValue('specialist-tactics');
  });

  it('says an agent must be added first when nobody participates', () => {
    setup(vi.fn(), []);
    expect(screen.getByText('Add an agent to the conversation before asking a question.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send question' })).not.toBeInTheDocument();
  });

  it('keeps the question after a refusal and shows its message', async () => {
    const user = userEvent.setup();
    const submitQuestion = vi.fn(async () => {
      throw failure({ kind: 'tooManyUnfinished' });
    });
    const { onOutcome, onAccepted } = setup(submitQuestion, ['specialist-tactics']);
    await user.type(screen.getByLabelText('Your question'), 'Keep me');
    await user.click(screen.getByRole('button', { name: 'Send question' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Too many of your questions are still being answered');
    expect(screen.getByLabelText('Your question')).toHaveValue('Keep me');
    expect(onOutcome).toHaveBeenCalledWith({ kind: 'tooManyUnfinished' });
    expect(onAccepted).not.toHaveBeenCalled();
  });

  it('shows the invalid question refusal next to the input', async () => {
    const user = userEvent.setup();
    const submitQuestion = vi.fn(async () => {
      throw failure({ kind: 'invalidQuestion', fieldErrors: [{ field: 'question', code: 'too-long' }] });
    });
    setup(submitQuestion, ['specialist-tactics']);
    await user.type(screen.getByLabelText('Your question'), 'Long');
    await user.click(screen.getByRole('button', { name: 'Send question' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The question is too long');
  });

  it.each([
    [{ kind: 'agentNotAvailable' }, 'This agent is no longer available on this platform.'],
    [{ kind: 'agentNotParticipant' }, 'Add this agent to the conversation before asking it.'],
    [
      { kind: 'notPermitted' },
      'You can read this conversation, but only Club Admins and Coaches of the team can ask questions.',
    ],
  ] as [TriageOutcome, string][])('shows the %j refusal, keeps the draft, and reports it', async (outcome, message) => {
    const user = userEvent.setup();
    const { onOutcome } = setup(
      vi.fn(async () => {
        throw failure(outcome);
      }),
      ['specialist-tactics'],
    );
    await user.type(screen.getByLabelText('Your question'), 'Keep me');
    await user.click(screen.getByRole('button', { name: 'Send question' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(message);
    expect(screen.getByLabelText('Your question')).toHaveValue('Keep me');
    expect(onOutcome).toHaveBeenCalledWith(outcome);
  });

  it('clears the refusal message once the draft changes', async () => {
    const user = userEvent.setup();
    setup(
      vi.fn(async () => {
        throw failure({ kind: 'tooManyUnfinished' });
      }),
      ['specialist-tactics'],
    );
    await user.type(screen.getByLabelText('Your question'), 'Q');
    await user.click(screen.getByRole('button', { name: 'Send question' }));
    await screen.findByRole('alert');
    await user.type(screen.getByLabelText('Your question'), 'x');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('offers to send again after an unknown outcome and reuses the key', async () => {
    const user = userEvent.setup();
    const submitQuestion = vi
      .fn()
      .mockRejectedValueOnce(failure({ kind: 'unknownOutcome' }))
      .mockResolvedValueOnce(accepted);
    const { onAccepted } = setup(submitQuestion, ['specialist-tactics']);
    await user.type(screen.getByLabelText('Your question'), 'Maybe received');
    await user.click(screen.getByRole('button', { name: 'Send question' }));
    expect(await screen.findByText(/could not confirm that your question was received/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Send again' }));

    await waitFor(() => expect(onAccepted).toHaveBeenCalledTimes(1));
    expect(submitQuestion.mock.calls[1]?.[3]).toBe(submitQuestion.mock.calls[0]?.[3]);
  });

  it('creates a new key after the question changed', async () => {
    const user = userEvent.setup();
    const submitQuestion = vi
      .fn()
      .mockRejectedValueOnce(failure({ kind: 'unknownOutcome' }))
      .mockResolvedValueOnce(accepted);
    setup(submitQuestion, ['specialist-tactics']);
    await user.type(screen.getByLabelText('Your question'), 'First');
    await user.click(screen.getByRole('button', { name: 'Send question' }));
    await screen.findByRole('button', { name: 'Send again' });
    await user.type(screen.getByLabelText('Your question'), ' edited');
    await user.click(screen.getByRole('button', { name: 'Send question' }));
    await waitFor(() => expect(submitQuestion).toHaveBeenCalledTimes(2));
    expect(submitQuestion.mock.calls[1]?.[3]).not.toBe(submitQuestion.mock.calls[0]?.[3]);
  });
});
