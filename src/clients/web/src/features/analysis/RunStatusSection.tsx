import Typography from '@mui/material/Typography';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { AnalysisRunStatus, JobCounts, NodeStatus } from '../../platform/models/analysis';
import { formatKickoff } from '../matches/MatchForm';

const JOB_STATES: (keyof JobCounts)[] = ['waiting', 'ready', 'claimed', 'completed', 'failed', 'blocked'];

/** Run details and one entry per capability node; unknown states, tiers, and scopes show their raw value (FR-004, FR-005, FR-016, FR-019, FR-020). */
export function RunStatusSection({ run }: { run: AnalysisRunStatus }) {
  const { t } = useTranslation('analysis');
  const stateName = (state: string) => t(`run.stateName.${state}`, { defaultValue: state });
  const lastState = useRef(run.state);
  const [announcement, setAnnouncement] = useState('');

  useEffect(() => {
    if (lastState.current !== run.state) {
      lastState.current = run.state;
      setAnnouncement(t('run.stateChanged', { state: stateName(run.state) }));
    }
  }, [run.state, t]);

  const time = (date?: Date) => (date ? formatKickoff(date) : t('run.notYet'));

  return (
    <section aria-label={t('run.title')}>
      <Typography variant="h2" component="h2" sx={{ mt: 2 }}>
        {t('run.title')}
      </Typography>
      <div role="status" aria-live="polite" aria-atomic="true">
        {announcement}
      </div>
      <dl aria-label={t('run.title')}>
        <div>
          <dt>{t('run.state')}</dt>
          <dd>{stateName(run.state)}</dd>
        </div>
        <div>
          <dt>{t('run.requested')}</dt>
          <dd>{formatKickoff(run.createdAt)}</dd>
        </div>
        <div>
          <dt>{t('run.started')}</dt>
          <dd>{time(run.runningAt)}</dd>
        </div>
        <div>
          <dt>{t('run.ended')}</dt>
          <dd>{time(run.terminatedAt)}</dd>
        </div>
        {run.failureReason && (
          <div>
            <dt>{t('run.failureReason')}</dt>
            <dd>{run.failureReason}</dd>
          </div>
        )}
        <div>
          <dt>{t('run.workflow')}</dt>
          <dd>{t('run.workflowValue', { id: run.workflowId, version: run.workflowVersion })}</dd>
        </div>
      </dl>
      <Typography variant="h3" component="h3" sx={{ mt: 2 }}>
        {t('nodes.title')}
      </Typography>
      {run.nodes.length === 0 ? (
        <Typography>{t('nodes.empty')}</Typography>
      ) : (
        <ul aria-label={t('nodes.listLabel')}>
          {run.nodes.map((node) => (
            <NodeEntry key={node.nodeKey} node={node} />
          ))}
        </ul>
      )}
    </section>
  );
}

function NodeEntry({ node }: { node: NodeStatus }) {
  const { t } = useTranslation('analysis');
  return (
    <li>
      <dl aria-label={node.nodeKey}>
        <div>
          <dt>{t('nodes.nodeKey')}</dt>
          <dd>{node.nodeKey}</dd>
        </div>
        <div>
          <dt>{t('nodes.capability')}</dt>
          <dd>{t('nodes.capabilityValue', { id: node.capabilityId, version: node.capabilityVersion })}</dd>
        </div>
        <div>
          <dt>{t('nodes.tier')}</dt>
          <dd>{t(`nodes.tierName.${node.tier}`, { defaultValue: node.tier })}</dd>
        </div>
        <div>
          <dt>{t('nodes.scope')}</dt>
          <dd>{t(`nodes.scopeName.${node.executionScope}`, { defaultValue: node.executionScope })}</dd>
        </div>
        <div>
          <dt>{t('nodes.state')}</dt>
          <dd>{t(`nodes.stateName.${node.state}`, { defaultValue: node.state })}</dd>
        </div>
        {node.blockedByNodeKey && (
          <div>
            <dt>{t('nodes.blockedBy')}</dt>
            <dd>{node.blockedByNodeKey}</dd>
          </div>
        )}
        <div>
          <dt>{t('nodes.jobs')}</dt>
          <dd>
            <ul aria-label={t('nodes.jobs')}>
              {JOB_STATES.map((key) => (
                <li key={key}>{t('nodes.jobCount', { label: t(`nodes.jobCounts.${key}`), count: node.jobs[key] })}</li>
              ))}
            </ul>
          </dd>
        </div>
      </dl>
    </li>
  );
}
