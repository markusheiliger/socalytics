import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { TriageFailure, type AgentConversationAdapter } from '../../platform/adapters/agentConversationAdapter';
import type { ConversationSummary, SubmissionAccepted, TriageOutcome } from '../../platform/models/agents';
import { useQuestionDraft } from './useQuestionDraft';
import { agentName, triageMessage } from './turnPresentation';

interface QuestionComposerProps {
  adapter: AgentConversationAdapter;
  conversation: ConversationSummary;
  /** Called once the platform accepted the question; the owner shows it as a pending turn. */
  onAccepted: (accepted: SubmissionAccepted, agentId: string, question: string) => void;
  /** Called with every refusal or unknown outcome so the owner can react (for example to a lost submit right). */
  onOutcome?: (outcome: TriageOutcome) => void;
  maxQuestionCharacters?: number;
}

interface Submission {
  agentId: string;
  question: string;
  key: string;
}

/** Plain-text question to exactly one participant, sent with a reusable idempotency key (FR-006, FR-007, FR-008). */
export function QuestionComposer({
  adapter,
  conversation,
  onAccepted,
  onOutcome,
  maxQuestionCharacters,
}: QuestionComposerProps) {
  const { t } = useTranslation('triage');
  const draft = useQuestionDraft();
  const { agentId, setAgentId } = draft;
  const [announcement, setAnnouncement] = useState('');
  const [refusal, setRefusal] = useState<TriageOutcome | undefined>(undefined);

  const participants = conversation.participants;
  const onlyParticipant = participants.length === 1 ? participants[0]?.agentId : undefined;

  useEffect(() => {
    if (onlyParticipant !== undefined && agentId === '') setAgentId(onlyParticipant);
  }, [onlyParticipant, agentId, setAgentId]);

  const send = useMutation({
    mutationFn: (submission: Submission) =>
      adapter.submitQuestion(conversation.conversationId, submission.agentId, submission.question, submission.key),
  });

  const trimmed = draft.text.trim();
  const canSend = trimmed !== '' && draft.agentId !== '' && !send.isPending;

  const submit = () => {
    if (!canSend) return;
    const submission: Submission = { agentId: draft.agentId, question: draft.text, key: draft.startSend() };
    setAnnouncement('');
    setRefusal(undefined);
    send.mutate(submission, {
      onSuccess: (accepted) => {
        draft.settle('accepted');
        draft.reset();
        setAnnouncement(t('live.questionSent', { name: agentName(submission.agentId, t) }));
        onAccepted(accepted, submission.agentId, submission.question);
      },
      onError: (error) => {
        const outcome: TriageOutcome =
          error instanceof TriageFailure ? error.outcome : { kind: 'platform', failureKind: 'unreachable' };
        draft.settle(outcome.kind === 'unknownOutcome' ? 'unknown' : 'refused');
        setRefusal(outcome);
        onOutcome?.(outcome);
      },
    });
  };

  if (participants.length === 0) {
    return (
      <section aria-labelledby="composer-title">
        <Typography id="composer-title" component="h2" variant="h6">
          {t('composer.title')}
        </Typography>
        <Typography>{t('composer.noParticipantAgents')}</Typography>
      </section>
    );
  }

  const unknown = refusal?.kind === 'unknownOutcome';
  const refusalMessage = refusal && !unknown ? triageMessage(refusal, { maxQuestionCharacters }) : undefined;
  const agentRefused = refusal?.kind === 'agentNotAvailable' || refusal?.kind === 'agentNotParticipant';
  const describedBy = refusalMessage || unknown ? 'composer-refusal' : undefined;

  return (
    <section aria-labelledby="composer-title">
      <Typography id="composer-title" component="h2" variant="h6">
        {t('composer.title')}
      </Typography>
      <Stack
        component="form"
        spacing={1}
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <TextField
          select
          size="small"
          label={t('composer.agent')}
          error={agentRefused}
          value={draft.agentId}
          onChange={(event) => {
            draft.setAgentId(event.target.value);
            setRefusal(undefined);
          }}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
          sx={{ maxWidth: 360 }}
        >
          <option value="">{t('composer.chooseAgent')}</option>
          {participants.map((participant) => (
            <option key={participant.agentId} value={participant.agentId}>
              {t('participants.item', { name: agentName(participant.agentId, t), agentId: participant.agentId })}
            </option>
          ))}
        </TextField>
        <TextField
          multiline
          minRows={3}
          label={t('composer.question')}
          error={refusalMessage !== undefined && !agentRefused}
          slotProps={{ htmlInput: { 'aria-describedby': describedBy } }}
          value={draft.text}
          onChange={(event) => {
            draft.setText(event.target.value);
            setRefusal(undefined);
          }}
        />
        {unknown && (
          <Alert id="composer-refusal" severity="warning" role="alert">
            {t('composer.unknownOutcome')} {t('composer.unknownOutcomeHint')}
          </Alert>
        )}
        {refusalMessage && (
          <Alert id="composer-refusal" severity="error" role="alert">
            {t(refusalMessage.key, refusalMessage.params)}
          </Alert>
        )}
        <div>
          <Button type="submit" variant="contained" disabled={!canSend}>
            {send.isPending ? t('composer.sending') : unknown ? t('composer.sendAgain') : t('composer.send')}
          </Button>
        </div>
      </Stack>
      <div role="status" aria-live="polite" style={{ position: 'absolute', left: -10000 }}>
        {announcement}
      </div>
    </section>
  );
}
