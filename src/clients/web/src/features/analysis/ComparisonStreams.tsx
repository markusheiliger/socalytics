import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { FactStreamSummary } from '../../platform/models/analysis';
import { ProvenanceDetails } from './ProvenanceDetails';

const COVERAGE_STATES = ['accepted', 'missing', 'failed', 'blocked', 'skipped'] as const;

/**
 * Renders the fact streams of one comparison side on their own; nothing is aligned with the other side and a count of
 * zero facts is shown as reported, never as a complete analysis.
 */
export function ComparisonStreams({ streams, sideLabel }: { streams: FactStreamSummary[]; sideLabel: string }) {
  const { t } = useTranslation('analysis');
  if (streams.length === 0) return <Typography>{t('comparison.streams.empty')}</Typography>;

  return (
    <ul aria-label={t('comparison.streams.listLabel', { side: sideLabel })}>
      {streams.map((stream) => (
        <li key={stream.nodeKey} aria-label={t('comparison.streams.itemLabel', { side: sideLabel, key: stream.nodeKey })}>
          <Typography variant="h5" component="h5">
            {stream.nodeKey}
          </Typography>
          <dl>
            <div>
              <dt>{t('streams.capability')}</dt>
              <dd>{t('streams.capabilityValue', { id: stream.capabilityId, version: stream.capabilityVersion })}</dd>
            </div>
            <div>
              <dt>{t('streams.schemaVersion')}</dt>
              <dd>{stream.schemaVersion}</dd>
            </div>
            <div>
              <dt>{t('streams.outputType')}</dt>
              <dd>{t(`streams.outputTypeName.${stream.outputType}`, { defaultValue: stream.outputType })}</dd>
            </div>
            <div>
              <dt>{t('streams.factCount')}</dt>
              <dd>{stream.factCount}</dd>
            </div>
            <div>
              <dt>{t('streams.coverage')}</dt>
              <dd>
                {COVERAGE_STATES.map((state) => (
                  <span key={state} style={{ display: 'block' }}>
                    {t('streams.coverageCount', { state: t(`coverage.stateName.${state}`), count: stream.coverage[state] })}
                  </span>
                ))}
              </dd>
            </div>
          </dl>
          <ProvenanceDetails provenance={stream.provenance} />
        </li>
      ))}
    </ul>
  );
}
