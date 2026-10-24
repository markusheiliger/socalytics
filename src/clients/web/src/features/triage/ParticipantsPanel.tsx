import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemText from '@mui/material/ListItemText';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { TriageFailure, type AgentConversationAdapter } from '../../platform/adapters/agentConversationAdapter';
import { useConnectivity } from '../../connectivity/useConnectivity';
import type { ConversationSummary, TriageConversation, TriageOutcome } from '../../platform/models/agents';
import { triageKeys } from './triageKeys';
import { agentName, triageMessage } from './turnPresentation';

interface ParticipantsPanelProps {
  adapter: AgentConversationAdapter;
  conversation: ConversationSummary;
  /** Editors can add agents; others only read the list. */
  canEdit: boolean;
  /** Receives the conversation returned by a successful add, carrying the new `version`. */
  onAdded?: (conversation: TriageConversation, agentId: string) => void;
  /** Called when the add was refused because the conversation or the agent list changed; the owner reloads the conversation. */
  onConflict?: (outcome: TriageOutcome) => void;
}

/** Participants with readable name, identifier, and domain, and for Editors the add control (FR-005). */
export function ParticipantsPanel({ adapter, conversation, canEdit, onAdded, onConflict }: ParticipantsPanelProps) {
  const { t } = useTranslation('triage');
  const { isUnreachable } = useConnectivity();
  const [choice, setChoice] = useState('');
  const [announcement, setAnnouncement] = useState('');
  const queryClient = useQueryClient();
  const [refusal, setRefusal] = useState<TriageOutcome | undefined>(undefined);

  const agents = useQuery({ queryKey: triageKeys.agents(), queryFn: () => adapter.listAgents() });
  const enabled = agents.data ?? [];
  const participantIds = new Set(conversation.participants.map((participant) => participant.agentId));
  const addable = enabled.filter((agent) => !participantIds.has(agent.agentId));
  const domainOf = (agentId: string) => enabled.find((agent) => agent.agentId === agentId)?.domain;

  const add = useMutation({
    mutationFn: (agentId: string) => adapter.addParticipant(conversation.conversationId, agentId, conversation.version),
    onSuccess: (updated, agentId) => {
      setChoice('');
      setRefusal(undefined);
      setAnnouncement(t('live.participantAdded', { name: agentName(agentId, t) }));
      onAdded?.(updated, agentId);
    },
    onError: (error) => {
      setChoice('');
      const outcome: TriageOutcome =
        error instanceof TriageFailure ? error.outcome : { kind: 'platform', failureKind: 'unreachable' };
      setRefusal(outcome);
      if (outcome.kind === 'agentNotAvailable') {
        void queryClient.invalidateQueries({ queryKey: triageKeys.agents() });
      }
      if (outcome.kind === 'staleVersion' || outcome.kind === 'participantExists' || outcome.kind === 'agentNotAvailable') {
        onConflict?.(outcome);
      }
    },
  });

  const submit = () => {
    if (choice === '' || add.isPending) return;
    setAnnouncement('');
    setRefusal(undefined);
    add.mutate(choice);
  };

  const refusalMessage = refusal ? triageMessage(refusal) : undefined;
  const showAllAdded = canEdit && agents.isSuccess && addable.length === 0;

  return (
    <section aria-labelledby="participants-title">
      <Typography id="participants-title" component="h2" variant="h6">
        {t('participants.title')}
      </Typography>
      {conversation.participants.length === 0 ? (
        <Typography>{t('participants.none')}</Typography>
      ) : (
        <List aria-label={t('participants.listLabel')} dense>
          {conversation.participants.map((participant) => {
            const domain = domainOf(participant.agentId);
            return (
              <ListItem key={participant.agentId} disableGutters>
                <ListItemText
                  primary={t('participants.item', {
                    name: agentName(participant.agentId, t),
                    agentId: participant.agentId,
                  })}
                  secondary={domain === undefined ? undefined : t('participants.domain', { domain })}
                />
              </ListItem>
            );
          })}
        </List>
      )}
      {canEdit && addable.length > 0 && (
        <Stack
          component="form"
          direction="row"
          spacing={1}
          sx={{ alignItems: 'flex-start' }}
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <TextField
            select
            size="small"
            label={t('participants.addLabel')}
            value={choice}
            onChange={(event) => setChoice(event.target.value)}
            slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
            sx={{ minWidth: 260 }}
          >
            <option value="">{t('participants.choose')}</option>
            {addable.map((agent) => (
              <option key={agent.agentId} value={agent.agentId}>
                {`${t('participants.item', { name: agentName(agent.agentId, t), agentId: agent.agentId })} – ${t('participants.domain', { domain: agent.domain })}`}
              </option>
            ))}
          </TextField>
          <Button type="submit" variant="contained" disabled={choice === '' || add.isPending || isUnreachable}>
            {add.isPending ? t('participants.adding') : t('participants.add')}
          </Button>
        </Stack>
      )}
      {showAllAdded && <Typography>{t('participants.allAdded')}</Typography>}
      {refusalMessage && (
        <Alert severity="error" role="alert" sx={{ mt: 1 }}>
          {t(refusalMessage.key, refusalMessage.params)}
        </Alert>
      )}
      <div role="status" aria-live="polite" style={{ position: 'absolute', left: -10000 }}>
        {announcement}
      </div>
    </section>
  );
}
