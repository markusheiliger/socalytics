import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { createAgentConversationAdapter } from '../../platform/adapters/agentConversationAdapter';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import { toPlatformFailure } from '../../platform/failure';
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
  const adapter = useMemo(() => createAgentConversationAdapter(platform), [platform]);
  const runAdapter = useMemo(() => createAnalysisRunAdapter(platform), [platform]);
  const [dialogOpen, setDialogOpen] = useState(false);
  const conversations = useQuery({
    queryKey: triageKeys.conversations(matchId),
    queryFn: () => adapter.listConversations(matchId),
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
      {failure && (
        <FailureAlert
          failure={toPlatformFailure(failure)}
          onReload={() => {
            void conversations.refetch();
            if (canStart) void runs.refetch();
          }}
        />
      )}
      {conversations.data && items.length === 0 && <Typography>{t('section.empty')}</Typography>}
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
                {!conversation.analysisRunIsCurrent && ` · ${t('section.notCurrentRun')}`}
                {` · ${names ? t('section.participants', { names }) : t('section.noParticipants')}`}
              </li>
            );
          })}
        </ul>
      )}
      {canStart && runs.data && eligible.length > 0 && (
        <Button variant="contained" sx={{ mt: 1 }} onClick={() => setDialogOpen(true)}>
          {t('section.start')}
        </Button>
      )}
      {canStart && runs.data && eligible.length === 0 && <Typography sx={{ mt: 1 }}>{t('section.noEligibleRun')}</Typography>}
      {canStart && runs.data && <StartConversationDialog open={dialogOpen} matchId={matchId} runs={runs.data} onClose={() => setDialogOpen(false)} />}
    </section>
  );
}
