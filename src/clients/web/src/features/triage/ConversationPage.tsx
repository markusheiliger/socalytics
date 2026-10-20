import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useBlocker, useParams } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createAgentConversationAdapter, TriageFailure } from '../../platform/adapters/agentConversationAdapter';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { SubmissionAccepted, TriageConversation, TriageOutcome } from '../../platform/models/agents';
import { useSession } from '../../session/sessionContext';
import { useLeaveGuard } from '../../uploads/useLeaveGuard';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { loadAllRuns, RUNS_KEY } from '../lineage/AnalysisRunStates';
import { formatKickoff } from '../matches/MatchForm';
import { useMatchContext } from '../matches/useMatchContext';
import { FailureAlert } from '../shared/FailureAlert';
import { ParticipantsPanel } from './ParticipantsPanel';
import { QuestionComposer } from './QuestionComposer';
import { TurnList, type PendingTurn } from './TurnList';
import { triageKeys } from './triageKeys';
import { triageMessage } from './turnPresentation';

/** One triage conversation of a match: bound run, participants, history with pending turns, and the composer (FR-003, FR-004, FR-008). */
export function ConversationPage() {
  const { matchId = '', conversationId = '' } = useParams();
  const { t } = useTranslation('triage');
  const { t: tc } = useTranslation('common');
  const { t: ts } = useTranslation('shell');
  const { platform } = useSession();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createAgentConversationAdapter(platform), [platform]);
  const runAdapter = useMemo(() => createAnalysisRunAdapter(platform), [platform]);
  const { match, permitted, editor } = useMatchContext(matchId);
  const [pending, setPending] = useState<PendingTurn[]>([]);
  const [lostSubmitRight, setLostSubmitRight] = useState(false);
  const [gone, setGone] = useState(false);
  const [unsent, setUnsent] = useState(false);
  const { isUnreachable } = useConnectivity();
  const guardLeave = unsent && !gone;
  useLeaveGuard(guardLeave);
  const blocker = useBlocker(({ currentLocation, nextLocation }) => guardLeave && currentLocation.pathname !== nextLocation.pathname);

  const conversation = useQuery({
    queryKey: triageKeys.conversation(conversationId),
    queryFn: async (): Promise<TriageConversation> => {
      const answer = await adapter.getConversation(conversationId);
      if (answer.kind !== 'changed') throw new Error('Unexpected unconditional answer');
      return { ...answer.value, etag: answer.etag };
    },
    retry: false,
    refetchOnWindowFocus: false,
  });
  const data = conversation.data;
  const runs = useQuery({
    queryKey: RUNS_KEY(matchId),
    queryFn: () => loadAllRuns(runAdapter, matchId),
    enabled: Boolean(data) && permitted,
    retry: false,
    refetchOnWindowFocus: false,
  });

  const onAccepted = useCallback((accepted: SubmissionAccepted, agentId: string, question: string) => {
    setPending((current) => [
      ...current,
      {
        invocationId: accepted.invocationId,
        agentId,
        question,
        submittedAt: new Date().toISOString(),
        retryAfterSeconds: accepted.retryAfterSeconds,
      },
    ]);
  }, []);
  const onPendingResolved = useCallback((ids: string[]) => {
    setPending((current) => current.filter((turn) => !ids.includes(turn.invocationId)));
  }, []);
  const reload = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: triageKeys.conversation(conversationId) });
  }, [queryClient, conversationId]);
  const onNotFound = useCallback(() => setGone(true), []);
  const onOutcome = useCallback((outcome: TriageOutcome) => {
    if (outcome.kind === 'notPermitted') setLostSubmitRight(true);
  }, []);

  if (match.data && !permitted) return <NotPermittedPage />;
  if (match.error && toPlatformFailure(match.error).kind === 'notPermitted') return <NotPermittedPage />;

  const conversationFailure = conversation.error;
  const notFound =
    gone ||
    (conversationFailure instanceof TriageFailure && conversationFailure.outcome.kind === 'notFound') ||
    (data !== undefined && data.matchId !== matchId);

  const heading = (
    <>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('conversation.title')}</Typography>
    </>
  );

  if (notFound) {
    return (
      <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
        <Typography variant="h1">{tc('app.name')}</Typography>
        <Typography variant="h2">{ts('notFound')}</Typography>
        <Button component={Link} to={`/matches/${matchId}`} variant="outlined" sx={{ mt: 2 }}>
          {t('conversation.backToMatch')}
        </Button>
      </Container>
    );
  }

  const canAsk = editor && !lostSubmitRight;
  const runTime = data ? runs.data?.find((run) => run.runId === data.analysisRunId)?.createdAt : undefined;
  const failureMessage = conversationFailure instanceof TriageFailure ? triageMessage(conversationFailure.outcome) : undefined;

  return (
    <Container component="main" maxWidth="sm" sx={{ py: 4 }}>
      {heading}
      <Button component={Link} to={`/matches/${matchId}`} variant="outlined" sx={{ mb: 2 }}>
        {t('conversation.backToMatch')}
      </Button>
      {isUnreachable && (
        <Alert severity="warning" role="status" sx={{ mb: 2 }}>
          {ts('connectivity.connectionRequired')}
        </Alert>
      )}
      {match.error && <FailureAlert failure={toPlatformFailure(match.error)} onReload={() => void match.refetch()} />}
      {conversation.isPending && <Typography>{t('conversation.loading')}</Typography>}
      {conversationFailure &&
        (failureMessage ? (
          <Alert severity="error" role="alert">
            {t(failureMessage.key, failureMessage.params)}
          </Alert>
        ) : (
          <FailureAlert failure={toPlatformFailure(conversationFailure)} onReload={reload} />
        ))}
      {data && permitted && (
        <Stack spacing={3}>
          <section aria-labelledby="bound-run-title">
            <Typography id="bound-run-title" component="h2" variant="h6">
              {t('conversation.boundRun')}
            </Typography>
            <Typography>{runTime ? t('conversation.boundRunValue', { time: formatKickoff(runTime) }) : data.analysisRunId}</Typography>
            <Link to={`/matches/${matchId}/analysis-runs/${data.analysisRunId}`}>{t('conversation.reviewLink')}</Link>
            {!data.analysisRunIsCurrent && <Alert severity="info" sx={{ mt: 1 }}>{t('conversation.supersededNotice')}</Alert>}
          </section>
          {!canAsk && <Typography>{t('conversation.readOnlyNote')}</Typography>}
          <ParticipantsPanel
            adapter={adapter}
            conversation={data}
            canEdit={canAsk}
            onAdded={(updated) => queryClient.setQueryData(triageKeys.conversation(conversationId), updated)}
            onConflict={reload}
          />
          <TurnList adapter={adapter} conversationId={conversationId} pendingTurns={pending} onPendingResolved={onPendingResolved} onNotFound={onNotFound} />
          {canAsk && <QuestionComposer adapter={adapter} conversation={data} onAccepted={onAccepted} onOutcome={onOutcome} onUnsentChange={setUnsent} />}
        </Stack>
      )}
      <Dialog open={blocker.state === 'blocked'} onClose={() => blocker.reset?.()} aria-labelledby="leave-title">
        <DialogTitle id="leave-title">{t('leave.title')}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('leave.message')}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => blocker.reset?.()}>{t('leave.stay')}</Button>
          <Button onClick={() => blocker.proceed?.()}>{t('leave.leave')}</Button>
        </DialogActions>
      </Dialog>
    </Container>
  );
}
