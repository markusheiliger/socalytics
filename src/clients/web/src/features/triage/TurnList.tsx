import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { TriageFailure, type AgentConversationAdapter } from '../../platform/adapters/agentConversationAdapter';
import { toTurnState, type Turn, type TurnPage } from '../../platform/models/agents';
import { AdviceCard } from './AdviceCard';
import { triageKeys } from './triageKeys';
import { agentName, triageMessage, turnPresentation } from './turnPresentation';
import { useInvocationPolling, type PollTarget } from './useInvocationPolling';

/** A question the platform accepted, shown until a history page contains its invocation (FR-008). */
export interface PendingTurn {
  invocationId: string;
  agentId: string;
  question: string;
  submittedAt: string;
  retryAfterSeconds?: number;
}

interface TurnListProps {
  adapter: AgentConversationAdapter;
  conversationId: string;
  pendingTurns: readonly PendingTurn[];
  /** Called with the invocation identifiers that a history page now contains, so the owner can drop them. */
  onPendingResolved?: (invocationIds: string[]) => void;
}

function formatTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('en', { dateStyle: 'medium', timeStyle: 'short' });
}

function liveKey(turn: Turn): string | undefined {
  switch (turn.state.recognized) {
    case 'succeeded':
      return 'live.adviceReady';
    case 'failed':
      return 'live.turnFailed';
    case 'pending':
      return turn.state.value === 'claimed' || turn.state.value === 'running' ? 'live.turnRunning' : 'live.turnQueued';
    default:
      return undefined;
  }
}

/** The first history page in submission order followed by accepted turns the history does not contain yet. */
export function TurnList({ adapter, conversationId, pendingTurns, onPendingResolved }: TurnListProps) {
  const { t } = useTranslation('triage');
  const queryClient = useQueryClient();
  const [announcement, setAnnouncement] = useState('');
  const lastStates = useRef(new Map<string, string>());

  const history = useQuery({
    queryKey: triageKeys.turns(conversationId),
    queryFn: async (): Promise<TurnPage> => {
      const answer = await adapter.listTurns(conversationId);
      if (answer.kind !== 'changed') throw new Error('Unexpected unconditional answer');
      return answer.value;
    },
  });

  const historyTurns = history.data?.turns;
  const knownIds = useMemo(() => new Set((historyTurns ?? []).map((turn) => turn.invocationId)), [historyTurns]);
  const stillPending = useMemo(
    () => (historyTurns === undefined ? pendingTurns : pendingTurns.filter((turn) => !knownIds.has(turn.invocationId))),
    [historyTurns, knownIds, pendingTurns],
  );

  useEffect(() => {
    if (historyTurns === undefined) return;
    const resolved = pendingTurns.filter((turn) => knownIds.has(turn.invocationId)).map((turn) => turn.invocationId);
    if (resolved.length > 0) onPendingResolved?.(resolved);
  }, [historyTurns, knownIds, pendingTurns, onPendingResolved]);

  const turns: Turn[] = useMemo(
    () => [
      ...(historyTurns ?? []),
      ...stillPending.map(
        (turn): Turn => ({
          invocationId: turn.invocationId,
          agentId: turn.agentId,
          question: turn.question,
          state: toTurnState('accepted'),
          submittedAt: turn.submittedAt,
        }),
      ),
    ],
    [historyTurns, stillPending],
  );

  const targets: PollTarget[] = useMemo(
    () =>
      turns
        .filter((turn) => turn.state.recognized !== 'succeeded' && turn.state.recognized !== 'failed')
        .map((turn) => ({
          invocationId: turn.invocationId,
          retryAfterSeconds: pendingTurns.find((pending) => pending.invocationId === turn.invocationId)?.retryAfterSeconds,
        })),
    [turns, pendingTurns],
  );

  const { changes } = useInvocationPolling(adapter, conversationId, targets);
  const changeCount = Object.values(changes).reduce((sum, count) => sum + count, 0);

  // A changed status refreshes the history so the turn shows its current state.
  useEffect(() => {
    if (changeCount > 0) void queryClient.invalidateQueries({ queryKey: triageKeys.turns(conversationId) });
  }, [changeCount, conversationId, queryClient]);

  useEffect(() => {
    lastStates.current.clear();
  }, [conversationId]);

  useEffect(() => {
    for (const turn of turns) {
      const previous = lastStates.current.get(turn.invocationId);
      lastStates.current.set(turn.invocationId, turn.state.value);
      if (previous === undefined || previous === turn.state.value) continue;
      const key = liveKey(turn);
      if (key) setAnnouncement(t(key, { name: agentName(turn.agentId, t) }));
    }
  }, [turns, t]);

  const failure = history.error;
  const failureMessage = failure
    ? triageMessage(
        failure instanceof TriageFailure ? failure.outcome : { kind: 'platform', failureKind: 'unreachable' },
      )
    : undefined;

  return (
    <Box component="section" aria-labelledby="history-title">
      <Typography id="history-title" component="h2" variant="h6">
        {t('conversation.history')}
      </Typography>
      {history.isPending && <Typography>{t('conversation.loading')}</Typography>}
      {failureMessage && (
        <Alert
          severity="error"
          role="alert"
          action={
            <Button color="inherit" size="small" onClick={() => void history.refetch()}>
              {t('conversation.retry')}
            </Button>
          }
        >
          {t(failureMessage.key, failureMessage.params)}
        </Alert>
      )}
      {!history.isPending && turns.length === 0 && !failureMessage && (
        <Typography>{t('conversation.emptyHistory')}</Typography>
      )}
      {turns.length > 0 && (
        <Box component="ul" aria-label={t('conversation.historyLabel')} sx={{ listStyle: 'none', p: 0, m: 0 }}>
          {turns.map((turn) => {
            const name = agentName(turn.agentId, t);
            const presentation = turnPresentation(turn);
            return (
              <Box
                component="li"
                key={turn.invocationId}
                aria-label={t('turn.listItem', { name, time: formatTime(turn.submittedAt) })}
                sx={{ borderBottom: 1, borderColor: 'divider', py: 1.5 }}
              >
                <Typography variant="subtitle2" component="h3">
                  {t('turn.to', { name })}
                </Typography>
                <Typography variant="caption" component="p">
                  {t('turn.submittedAt', { time: formatTime(turn.submittedAt) })}
                </Typography>
                <Typography
                  component="p"
                  aria-label={t('turn.question')}
                  sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', my: 0.5 }}
                >
                  {turn.question}
                </Typography>
                {presentation.kind === 'succeeded' ? (
                  turn.advice && <AdviceCard advice={turn.advice} agentId={turn.agentId} />
                ) : (
                  <Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>
                    {t(presentation.message.key, presentation.message.params)}
                    {presentation.attempt && ` (${t(presentation.attempt.key, presentation.attempt.params)})`}
                  </Typography>
                )}
              </Box>
            );
          })}
        </Box>
      )}
      <div role="status" aria-live="polite" style={{ position: 'absolute', left: -10000 }}>
        {announcement}
      </div>
    </Box>
  );
}
