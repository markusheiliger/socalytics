import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { TriageFailure, type AgentConversationAdapter } from '../../platform/adapters/agentConversationAdapter';
import type { ExecutionReferences, InvocationLineage } from '../../platform/models/agents';
import { triageMessage } from './turnPresentation';

interface LineagePanelProps {
  adapter: AgentConversationAdapter;
  invocationId: string;
  /** Grows whenever the turn's status check reports a change; an unfinished turn's lineage refetches then. */
  changeCount?: number;
  /** True once the turn is terminal: the lineage no longer refreshes after it has loaded. */
  ended?: boolean;
}

const EXECUTION_FIELDS: readonly (keyof ExecutionReferences)[] = [
  'runtime',
  'modelProvider',
  'model',
  'deployment',
  'promptRef',
  'toolPolicyRef',
  'safetyPolicyRef',
];

function formatTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleString('en', { dateStyle: 'medium', timeStyle: 'medium' });
}

/** The ordered lineage of one invocation with its declared execution references, shown as text (FR-015, FR-022). */
export function LineagePanel({ adapter, invocationId, changeCount = 0, ended = false }: LineagePanelProps) {
  const { t } = useTranslation('triage');
  const [lineage, setLineage] = useState<InvocationLineage>();
  const [failed, setFailed] = useState<ReturnType<typeof triageMessage>>();
  const [loading, setLoading] = useState(true);
  const etag = useRef<string | undefined>(undefined);
  const loaded = useRef(false);
  const endedRef = useRef(ended);
  endedRef.current = ended;
  const requestId = useRef(0);

  const load = useCallback(async () => {
    const current = ++requestId.current;
    try {
      const answer = await adapter.getLineage(invocationId, etag.current);
      if (current !== requestId.current) return;
      if (answer.kind === 'changed') {
        etag.current = answer.etag;
        setLineage(answer.value);
      }
      loaded.current = true;
      setFailed(undefined);
    } catch (error) {
      if (current !== requestId.current) return;
      setFailed(
        triageMessage(error instanceof TriageFailure ? error.outcome : { kind: 'platform', failureKind: 'unreachable' }),
      );
    } finally {
      if (current === requestId.current) setLoading(false);
    }
  }, [adapter, invocationId]);

  useEffect(() => {
    etag.current = undefined;
    loaded.current = false;
    setLineage(undefined);
    setLoading(true);
    void load();
    return () => {
      requestId.current++;
    };
  }, [load]);

  const firstCount = useRef(changeCount);
  useEffect(() => {
    if (changeCount === firstCount.current) return;
    firstCount.current = changeCount;
    if (endedRef.current && loaded.current) return;
    void load();
  }, [changeCount, load]);

  const references = lineage?.advice?.executionReferences;
  const referenceFields = references ? EXECUTION_FIELDS.filter((field) => references[field] !== undefined) : [];

  return (
    <Box component="section" aria-label={t('lineage.title')} sx={{ mt: 1 }}>
      {loading && lineage === undefined && <Typography>{t('lineage.loading')}</Typography>}
      {failed && (
        <Alert
          severity="error"
          role="alert"
          action={
            <Button color="inherit" size="small" onClick={() => void load()}>
              {t('lineage.retry')}
            </Button>
          }
        >
          {t(failed.key, failed.params)}
        </Alert>
      )}
      {lineage && (
        <>
          {lineage.policyVersion !== '' && (
            <Typography variant="caption" component="p">
              {t('lineage.policyVersion', { version: lineage.policyVersion })}
            </Typography>
          )}
          {lineage.entries.length === 0 ? (
            <Typography variant="body2">{t('lineage.empty')}</Typography>
          ) : (
            <Box component="ol" aria-label={t('lineage.listLabel')} sx={{ pl: 3, m: 0 }}>
              {lineage.entries.map((entry) => {
                const details = Object.entries(entry.details);
                return (
                  <li key={entry.sequence} style={{ overflowWrap: 'anywhere' }}>
                    <Typography variant="body2" component="span" sx={{ fontWeight: 'bold' }}>
                      {entry.kind.recognized
                        ? t(`lineage.kind.${entry.kind.recognized}`)
                        : t('lineage.unknownKind', { kind: entry.kind.value, interpolation: { escapeValue: false } })}
                    </Typography>
                    {entry.attemptNumber !== undefined && (
                      <Typography variant="body2" component="span">
                        {` · ${t('lineage.attempt', { number: entry.attemptNumber })}`}
                      </Typography>
                    )}
                    <Typography variant="body2" component="span">
                      {` · ${t('lineage.recordedAt', { time: formatTime(entry.recordedAt) })}`}
                    </Typography>
                    {details.length > 0 && (
                      <ul aria-label={t('lineage.details')} style={{ margin: 0 }}>
                        {details.map(([key, value]) => (
                          <li key={key}>
                            {t('lineage.detail', { key, value, interpolation: { escapeValue: false } })}
                          </li>
                        ))}
                      </ul>
                    )}
                  </li>
                );
              })}
            </Box>
          )}
          {referenceFields.length > 0 && references && (
            <>
              <Typography variant="subtitle2" component="h5" sx={{ mt: 1 }}>
                {t('lineage.executionReferences')}
              </Typography>
              <ul aria-label={t('lineage.executionReferences')} style={{ margin: 0 }}>
                {referenceFields.map((field) => (
                  <li key={field} style={{ overflowWrap: 'anywhere' }}>
                    {t('lineage.executionItem', {
                      name: t(`lineage.execution.${field}`),
                      value: references[field],
                      interpolation: { escapeValue: false },
                    })}
                  </li>
                ))}
              </ul>
            </>
          )}
        </>
      )}
    </Box>
  );
}
