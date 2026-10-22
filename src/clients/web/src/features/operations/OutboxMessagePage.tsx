import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createOperationsAdapter } from '../../platform/adapters/operationsAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { OutboxMessage } from '../../platform/models/operations';
import { useSession } from '../../session/sessionContext';
import { NotFoundPage } from '../../shell/NotFoundPage';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { CopyableValue } from './CopyableValue';
import { enumerationText, formatDuration, formatTimestamp } from './formatOperations';

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const KNOWN_TYPES = ['matches.recordings-finalized', 'analysis.ready-work', 'analysis.run-state-changed'];
const KNOWN_STATES = ['pending', 'published', 'failed', 'discarded'];

/** Every field the platform reports for one outgoing message, never its payload and never an action (FR-010, FR-011). */
export function OutboxMessagePage() {
  const { t } = useTranslation('operations');
  const { t: ts } = useTranslation('shell');
  const { messageId = '' } = useParams();
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const adapter = useMemo(() => createOperationsAdapter(platform), [platform]);
  const valid = UUID_PATTERN.test(messageId);
  const query = useQuery({
    queryKey: ['operations', 'outbox-message', messageId],
    queryFn: () => adapter.getOutboxMessage(messageId),
    enabled: valid,
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchOnMount: false,
    refetchInterval: false,
  });
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (!valid || failure?.status === 404) return <NotFoundPage framed />;
  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t('message.title')}</Typography>
      <Link to="/operations/outbox-messages">{t('message.back')}</Link>

      {isUnreachable ? (
        <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
          {ts('connectivity.connectionRequired')}
        </Alert>
      ) : (
        <>
          {query.isPending && <Typography sx={{ mt: 2 }}>{t('common.loading')}</Typography>}
          {failure && (
            <Stack spacing={1} alignItems="flex-start" sx={{ mt: 2 }}>
              <FailureAlert failure={failure} />
              <Button variant="outlined" disabled={query.isFetching} onClick={() => void query.refetch()}>
                {t('common.tryAgain')}
              </Button>
            </Stack>
          )}
          {query.data && <MessageFields message={query.data} />}
        </>
      )}
    </Container>
  );
}

function MessageFields({ message }: { message: OutboxMessage }) {
  const { t } = useTranslation('operations');
  const absent = <Box component="span">{t('common.absent')}</Box>;

  const time = (value: Date | undefined) => {
    const formatted = formatTimestamp(value);
    return formatted ? <span title={formatted.iso}>{formatted.text}</span> : absent;
  };
  const duration = (seconds: number | undefined) => {
    const formatted = formatDuration(seconds);
    return formatted ? <span title={formatted.exact}>{formatted.text}</span> : absent;
  };
  const identifier = (value: string | undefined) => (value ? <CopyableValue value={value} /> : absent);
  const text = (value: string | undefined) => (value ? value : absent);

  return (
    <dl aria-label={t('message.fields')}>
      <Field label={t('message.messageId')}>{identifier(message.messageId)}</Field>
      <Field label={t('message.messageType')}>
        {enumerationText(message.messageType, KNOWN_TYPES, (value) => t(`messageType.${value}`)) ?? absent}
      </Field>
      <Field label={t('message.contractVersion')}>{text(message.contractVersion)}</Field>
      <Field label={t('message.subject')}>{text(message.subject)}</Field>
      <Field label={t('message.state')}>
        {enumerationText(message.state, KNOWN_STATES, (value) => t(`outboxState.${value}`)) ?? absent}
      </Field>
      <Field label={t('message.attemptCount')}>{message.attemptCount}</Field>
      <Field label={t('message.publicationRound')}>{message.publicationRound}</Field>
      <Field label={t('message.lastAttemptAt')}>{time(message.lastAttemptAt)}</Field>
      <Field label={t('message.nextAttemptAt')}>{time(message.nextAttemptAt)}</Field>
      <Field label={t('message.failureCategory')}>{text(message.failureCategory)}</Field>
      <Field label={t('message.createdAt')}>{time(message.createdAt)}</Field>
      <Field label={t('message.publishedAt')}>{time(message.publishedAt)}</Field>
      <Field label={t('message.age')}>{duration(message.ageSeconds)}</Field>
      <Field label={t('message.publicationLag')}>{duration(message.publicationLagSeconds)}</Field>
      <Field label={t('message.runId')}>{identifier(message.runId)}</Field>
      <Field label={t('message.logicalJobId')}>{identifier(message.logicalJobId)}</Field>
      <Field label={t('message.capabilityId')}>{identifier(message.capabilityId)}</Field>
    </dl>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <Typography component="dt" sx={{ fontWeight: 'bold' }}>
        {label}
      </Typography>
      <Typography component="dd" sx={{ ml: 0, mb: 1, overflowWrap: 'anywhere' }}>
        {children}
      </Typography>
    </div>
  );
}
