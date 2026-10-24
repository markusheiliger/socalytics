import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createAgentConversationAdapter } from '../../platform/adapters/agentConversationAdapter';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { ConversationStateFilter } from '../../platform/models/agents';
import { useSession } from '../../session/sessionContext';
import { loadAllRuns, RUNS_KEY } from '../lineage/AnalysisRunStates';
import { formatKickoff } from '../matches/MatchForm';
import { FailureAlert } from '../shared/FailureAlert';
import { StartConversationDialog, eligibleRuns } from './StartConversationDialog';
import { triageKeys } from './triageKeys';
import { agentName } from './turnPresentation';

interface TriageSectionProps {
  matchId: string;
  canStart: boolean;
}

/** The member's triage conversations for one match and, for editors, the start action (FR-001, FR-002). */
export function TriageSection({ matchId, canStart }: TriageSectionProps) {
  const { t } = useTranslation('triage');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const { t: ts } = useTranslation('shell');
  const adapter = useMemo(() => createAgentConversationAdapter(platform), [platform]);
  const runAdapter = useMemo(() => createAnalysisRunAdapter(platform), [platform]);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [stateFilter, setStateFilter] = useState<ConversationStateFilter>('active');
  const conversations = useQuery({
    queryKey: triageKeys.conversations(matchId, stateFilter),
    queryFn: () => adapter.listConversations(matchId, stateFilter),
    retry: false,
    refetchOnWindowFocus: false,
  });
  const runs = useQuery({
    queryKey: RUNS_KEY(matchId),
    queryFn: () => loadAllRuns(runAdapter, matchId),
    retry: false,
    refetchOnWindowFocus: false,
  });

  const items = useMemo(
    () => [...(conversations.data ?? [])].sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()),
    [conversations.data],
  );
  const runTimes = useMemo(() => new Map((runs.data ?? []).map((run) => [run.runId, run.createdAt])), [runs.data]);
  const eligible = useMemo(() => eligibleRuns(runs.data ?? []), [runs.data]);
  const failure = conversations.error ?? (canStart ? runs.error : null);

  return (
    <section aria-label={t('section.title')}>
      <Typography variant="h3" component="h3" sx={{ mt: 3 }}>
        {t('section.title')}
      </Typography>
      {isUnreachable && (
        <Alert severity="warning" role="status" sx={{ mb: 1 }}>
          {ts('connectivity.connectionRequired')}
        </Alert>
      )}
      {failure && (
        <FailureAlert
          failure={toPlatformFailure(failure)}
          onReload={() => {
            void conversations.refetch();
            if (canStart) void runs.refetch();
          }}
        />
      )}
      <ToggleButtonGroup
        exclusive
        size="small"
        value={stateFilter}
        aria-label={t('lifecycle.filter.label')}
        onChange={(_event, value: ConversationStateFilter | null) => {
          if (value) setStateFilter(value);
        }}
        sx={{ mb: 1 }}
      >
        <ToggleButton value="active">{t('lifecycle.filter.active')}</ToggleButton>
        <ToggleButton value="archived">{t('lifecycle.filter.archived')}</ToggleButton>
      </ToggleButtonGroup>
      {conversations.data && items.length === 0 && <Typography>{t(`lifecycle.empty.${stateFilter}`)}</Typography>}
      {items.length > 0 && (
        <ul aria-label={t('section.listLabel')}>
          {items.map((conversation) => {
            const runTime = runTimes.get(conversation.analysisRunId);
            const names = conversation.participants.map((p) => agentName(p.agentId, t)).join(', ');
            return (
              <li key={conversation.conversationId}>
                <Link to={`/matches/${matchId}/triage/${conversation.conversationId}`}>
                  {t('section.createdAt', { time: formatKickoff(new Date(conversation.createdAt)) })}
                </Link>
                {` · ${runTime ? t('section.boundRun', { time: formatKickoff(runTime) }) : t('section.boundRunId', { id: conversation.analysisRunId })}`}
                {` · `}
                <Chip
                  size="small"
                  component="span"
                  label={
                    conversation.state === 'active' || conversation.state === 'archived'
                      ? t(`lifecycle.state.${conversation.state}`)
                      : t('lifecycle.state.unknown', { state: conversation.state })
                  }
                />
                {!conversation.analysisRunIsCurrent && ` · ${t('section.notCurrentRun')}`}
                {` · ${names ? t('section.participants', { names }) : t('section.noParticipants')}`}
              </li>
            );
          })}
        </ul>
      )}
      {canStart && runs.data && eligible.length > 0 && (
        <Button variant="contained" sx={{ mt: 1 }} disabled={isUnreachable} onClick={() => setDialogOpen(true)}>
          {t('section.start')}
        </Button>
      )}
      {canStart && runs.data && eligible.length === 0 && <Typography sx={{ mt: 1 }}>{t('section.noEligibleRun')}</Typography>}
      {canStart && runs.data && <StartConversationDialog open={dialogOpen} matchId={matchId} runs={runs.data} onClose={() => setDialogOpen(false)} />}
    </section>
  );
}
