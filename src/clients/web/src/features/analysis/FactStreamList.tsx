import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { CoverageCounts, FactStreamSummary } from '../../platform/models/analysis';

const COVERAGE_STATES: (keyof CoverageCounts)[] = ['accepted', 'missing', 'failed', 'blocked', 'skipped'];

interface FactStreamListProps {
  streams: FactStreamSummary[];
  selectedNodeKey?: string;
  onSelect: (nodeKey: string) => void;
}

/** One keyboard-selectable entry per fact stream; unknown output types show their raw value (FR-007, FR-016). */
export function FactStreamList({ streams, selectedNodeKey, onSelect }: FactStreamListProps) {
  const { t } = useTranslation('analysis');

  return (
    <section aria-label={t('streams.title')}>
      <Typography variant="h3" component="h3" sx={{ mt: 2 }}>
        {t('streams.title')}
      </Typography>
      {streams.length === 0 ? (
        <Typography>{t('streams.empty')}</Typography>
      ) : (
        <ul aria-label={t('streams.listLabel')}>
          {streams.map((stream) => {
            const selected = stream.nodeKey === selectedNodeKey;
            return (
              <li key={stream.nodeKey}>
                <Button
                  variant={selected ? 'contained' : 'outlined'}
                  aria-pressed={selected}
                  aria-current={selected ? 'true' : undefined}
                  aria-label={t('streams.select', { key: stream.nodeKey })}
                  onClick={() => onSelect(stream.nodeKey)}
                  sx={{ textAlign: 'left', textTransform: 'none', display: 'block' }}
                >
                  <dl style={{ margin: 0 }}>
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
                        <ul aria-label={t('streams.coverage')}>
                          {COVERAGE_STATES.map((state) => (
                            <li key={state}>
                              {t('streams.coverageCount', { state: t(`coverage.stateName.${state}`), count: stream.coverage[state] })}
                            </li>
                          ))}
                        </ul>
                      </dd>
                    </div>
                  </dl>
                  {selected && <Typography component="span">{t('streams.selected')}</Typography>}
                </Button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
