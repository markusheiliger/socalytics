import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createOperationsAdapter } from '../../platform/adapters/operationsAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { OperationalSummary } from '../../platform/models/operations';
import { useSession } from '../../session/sessionContext';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { enumerationText, formatDuration, formatTimestamp } from './formatOperations';
import { ReconcileDialog } from './ReconcileDialog';

export const OPERATIONS_SUMMARY_KEY = ['operations', 'summary'] as const;

const KNOWN_RUN_STATES = ['Requested', 'Running', 'Completed', 'PartiallyCompleted', 'Failed', 'Cancelled'];

/** The analysis workflow's operational summary with its observation time and a manual refresh (FR-004 to FR-007). */
export function OperationsSummaryPage() {
  const { t } = useTranslation('operations');
  const { t: ts } = useTranslation('shell');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const adapter = useMemo(() => createOperationsAdapter(platform), [platform]);
  const queryClient = useQueryClient();
  const [reconcileOpen, setReconcileOpen] = useState(false);
  const query = useQuery({
    queryKey: OPERATIONS_SUMMARY_KEY,
    queryFn: () => adapter.summary(),
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchOnMount: false,
    refetchInterval: false,
  });
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const summary = query.data;
  const fetching = query.isFetching;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t('summary.title')}</Typography>
      <Typography>{t('summary.intro')}</Typography>

      {isUnreachable ? (
        <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
          {ts('connectivity.connectionRequired')}
        </Alert>
      ) : (
        <>
          <Button variant="contained" sx={{ mt: 2 }} onClick={() => setReconcileOpen(true)}>
            {t('summary.reconcile')}
          </Button>
          {query.isPending && <Typography sx={{ mt: 2 }}>{t('common.loading')}</Typography>}
          {failure && (
            <Stack spacing={1} alignItems="flex-start" sx={{ mt: 2 }}>
              <FailureAlert failure={failure} />
              <Button variant="outlined" disabled={fetching} onClick={() => void query.refetch()}>
                {t('common.tryAgain')}
              </Button>
            </Stack>
          )}
          {summary && <SummaryValues summary={summary} fetching={fetching} onRefresh={() => void query.refetch()} />}
        </>
      )}
      <ReconcileDialog
        open={reconcileOpen}
        onClose={() => setReconcileOpen(false)}
        onCompleted={() => void queryClient.invalidateQueries({ queryKey: OPERATIONS_SUMMARY_KEY })}
      />
    </Container>
  );
}

function SummaryValues({ summary, fetching, onRefresh }: { summary: OperationalSummary; fetching: boolean; onRefresh: () => void }) {
  const { t } = useTranslation('operations');
  const observed = formatTimestamp(summary.observedAt);
  const since = formatTimestamp(summary.workflowDefinition.since);
  const oldest = formatDuration(summary.oldestPendingAgeSeconds);
  const definition = summary.workflowDefinition;

  return (
    <>
      <Stack direction="row" spacing={2} alignItems="center" sx={{ mt: 2 }}>
        <Button variant="outlined" disabled={fetching} onClick={onRefresh}>
          {t('common.refresh')}
        </Button>
        <Typography role="status" title={observed?.iso}>
          {t('common.observedAt', { time: observed?.text ?? t('common.absent') })}
          {fetching ? ` ${t('common.refreshing')}` : ''}
        </Typography>
      </Stack>

      <section aria-labelledby="operations-workflow-heading">
        <Typography id="operations-workflow-heading" variant="h2" sx={{ mt: 3 }}>
          {t('summary.workflowTitle')}
        </Typography>
        {definition.state === 'resolvable' && <Typography>{t('summary.workflowResolvable')}</Typography>}
        {definition.state === 'unresolvable' && <Typography role="alert">{t('summary.workflowUnresolvable')}</Typography>}
        {definition.state !== 'resolvable' && definition.state !== 'unresolvable' && (
          <Typography>{t('summary.workflowUnknown', { state: definition.state })}</Typography>
        )}
        {(definition.category !== undefined || since) && (
          <dl>
            {definition.category !== undefined && (
              <Row label={t('summary.reasonCategory')}>{definition.category}</Row>
            )}
            {since && (
              <Row label={t('summary.since')}>
                <span title={since.iso}>{since.text}</span>
              </Row>
            )}
          </dl>
        )}
      </section>

      <section aria-labelledby="operations-runs-heading">
        <Typography id="operations-runs-heading" variant="h2" sx={{ mt: 3 }}>
          {t('summary.runsTitle')}
        </Typography>
        <Typography id="operations-runs-by-state" variant="h3">
          {t('summary.runsByState')}
        </Typography>
        {summary.runsByState.length === 0 ? (
          <Typography>{t('summary.noRuns')}</Typography>
        ) : (
          <dl aria-labelledby="operations-runs-by-state">
            {summary.runsByState.map((entry) => (
              <Row
                key={entry.state}
                label={enumerationText(entry.state, KNOWN_RUN_STATES, (value) => t(`runState.${value}`)) ?? entry.state}
              >
                {entry.count}
              </Row>
            ))}
          </dl>
        )}
        <dl>
          <Row label={t('summary.runsWithBlockedNodes')}>{summary.runsWithBlockedNodes}</Row>
          <Row label={t('summary.staleAttempts')}>{summary.staleAttempts}</Row>
        </dl>
      </section>

      <section aria-labelledby="operations-publication-heading">
        <Typography id="operations-publication-heading" variant="h2" sx={{ mt: 3 }}>
          {t('summary.publicationTitle')}
        </Typography>
        <dl>
          <Row label={t('summary.rejectedNotifications')}>
            {summary.rejectedNotifications} <Link to="/operations/notification-receipts?outcome=rejected">{t('summary.viewRejected')}</Link>
          </Row>
          <Row label={t('summary.outboxPending')}>
            {summary.outboxPending} <Link to="/operations/outbox-messages?state=pending">{t('summary.viewPending')}</Link>
          </Row>
          <Row label={t('summary.outboxFailed')}>
            {summary.outboxFailed} <Link to="/operations/outbox-messages?state=failed">{t('summary.viewFailed')}</Link>
          </Row>
          <Row label={t('summary.oldestPending')}>
            {oldest ? <span title={oldest.exact}>{oldest.text}</span> : t('summary.nothingPending')}
          </Row>
        </dl>
      </section>
    </>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <Typography component="dt" sx={{ fontWeight: 'bold' }}>
        {label}
      </Typography>
      <Typography component="dd" sx={{ ml: 0, mb: 1 }}>
        {children}
      </Typography>
    </div>
  );
}
