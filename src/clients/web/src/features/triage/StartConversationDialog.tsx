import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import { useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { createAgentConversationAdapter, TriageFailure } from '../../platform/adapters/agentConversationAdapter';
import type { TriageOutcome } from '../../platform/models/agents';
import type { AnalysisRunSummary } from '../../platform/models/recordings';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { useSession } from '../../session/sessionContext';
import { formatKickoff } from '../matches/MatchForm';
import { triageKeys } from './triageKeys';
import { triageMessage } from './turnPresentation';
import { useCreationIntent } from './useQuestionDraft';

/** Runs a conversation can be bound to (FR-002). */
export const isEligibleRun = (run: AnalysisRunSummary) => run.state === 'Completed' || run.state === 'PartiallyCompleted';

/** Eligible runs, newest first. */
export function eligibleRuns(runs: AnalysisRunSummary[]): AnalysisRunSummary[] {
  return runs.filter(isEligibleRun).sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime());
}

interface StartConversationDialogProps {
  open: boolean;
  matchId: string;
  runs: AnalysisRunSummary[];
  onClose: () => void;
}

const creationMessage = (outcome: TriageOutcome) => {
  switch (outcome.kind) {
    case 'runNotEligible':
      return { key: 'section.runNotEligible' };
    case 'notPermitted':
      return { key: 'section.notPermitted' };
    default:
      return triageMessage(outcome);
  }
};

export function StartConversationDialog({ open, matchId, runs, onClose }: StartConversationDialogProps) {
  const { t } = useTranslation('triage');
  const { t: tLineage } = useTranslation('lineage');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const adapter = useMemo(() => createAgentConversationAdapter(platform), [platform]);
  const candidates = useMemo(() => eligibleRuns(runs), [runs]);
  const [chosen, setChosen] = useState<string | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ key: string; params?: Record<string, string | number> } | undefined>(undefined);
  const [unknown, setUnknown] = useState(false);

  const analysisRunId = chosen && candidates.some((run) => run.runId === chosen) ? chosen : (candidates[0]?.runId ?? '');
  const intent = useCreationIntent(analysisRunId);

  const close = () => {
    setMessage(undefined);
    setUnknown(false);
    setChosen(undefined);
    intent.reset();
    onClose();
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy || !analysisRunId) return;
    const key = intent.startSend();
    setBusy(true);
    setMessage(undefined);
    setUnknown(false);
    try {
      const created = await adapter.createConversation(matchId, analysisRunId, key);
      intent.settle('accepted');
      void queryClient.invalidateQueries({ queryKey: triageKeys.conversations(matchId) });
      void navigate(`/matches/${matchId}/triage/${created.conversationId}`);
    } catch (error) {
      if (error instanceof TriageFailure) {
        intent.settle(error.outcome.kind === 'unknownOutcome' ? 'unknown' : 'refused');
        if (error.outcome.kind === 'unknownOutcome') {
          setUnknown(true);
        } else {
          setMessage(creationMessage(error.outcome));
        }
      } else {
        intent.settle('refused');
        setMessage({ key: 'refusal.failed' });
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onClose={close} aria-labelledby="start-conversation-title" component="form" onSubmit={(e: FormEvent) => void submit(e)}>
      <DialogTitle id="start-conversation-title">{t('section.startTitle')}</DialogTitle>
      <DialogContent>
        {unknown && (
          <Alert severity="warning" role="alert" sx={{ mb: 2 }}>
            {t('section.unknownOutcome')} {t('section.unknownOutcomeHint')}
          </Alert>
        )}
        {message && (
          <Alert severity="error" role="alert" sx={{ mb: 2 }}>
            {t(message.key, message.params)}
          </Alert>
        )}
        <TextField
          select
          fullWidth
          margin="normal"
          label={t('section.runChoice')}
          value={analysisRunId}
          onChange={(e) => {
            setChosen(e.target.value);
            setMessage(undefined);
            setUnknown(false);
          }}
          disabled={busy}
        >
          {candidates.map((run) => (
            <MenuItem key={run.runId} value={run.runId}>
              {t('section.runOption', {
                state: tLineage(`runs.state.${run.state}`, { defaultValue: run.state }),
                time: formatKickoff(run.createdAt),
              })}
            </MenuItem>
          ))}
        </TextField>
      </DialogContent>
      <DialogActions>
        <Button onClick={close} disabled={busy}>
          {t('section.cancel')}
        </Button>
        <Button type="submit" variant="contained" disabled={busy || !analysisRunId || isUnreachable}>
          {busy ? t('section.starting') : unknown ? t('section.retry') : t('section.start')}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
