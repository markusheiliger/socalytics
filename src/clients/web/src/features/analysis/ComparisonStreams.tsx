import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import type { FactStreamSummary, NodeInputCoverage, NodeStatus } from '../../platform/models/analysis';
import {
  describeStreamAvailability,
  type DependencyComparison,
  type StreamAvailability,
  type StreamComparisonEntry,
} from './comparisonPresentation';
import { ProvenanceDetails } from './ProvenanceDetails';

const COVERAGE_STATES = ['accepted', 'missing', 'failed', 'blocked', 'skipped'] as const;

type Translate = TFunction<'analysis'>;

function availabilityText(t: Translate, availability: StreamAvailability, stream: FactStreamSummary): string {
  const { coverage } = stream;
  const counts = { missing: coverage.missing, failed: coverage.failed, blocked: coverage.blocked, skipped: coverage.skipped };
  switch (availability.kind) {
    case 'noExpectedUnits':
      return t('comparison.availability.noExpectedUnits');
    case 'noAcceptedResults':
      return [
        t('comparison.availability.noAcceptedResults', counts),
        availability.allSkipped ? t('comparison.availability.allSkipped') : '',
      ]
        .filter(Boolean)
        .join(' ');
    case 'incompleteCoverage':
      return [
        t('comparison.availability.incompleteCoverage', counts),
        availability.zeroFacts ? t('comparison.availability.incompleteZero') : '',
      ]
        .filter(Boolean)
        .join(' ');
    case 'completeZeroFacts':
      return t('comparison.availability.completeZeroFacts');
    case 'completeFacts':
      return t('comparison.availability.completeFacts');
    default:
      return t('comparison.availability.incompatible');
  }
}

function policyText(t: Translate, entry: NodeInputCoverage): string {
  if (entry.coverageMode === 'complete') return t('comparison.input.policyComplete');
  if (entry.coverageMode === 'partial') return t('comparison.input.policyPartial');
  return t('comparison.input.policyUnsupported', { policy: entry.coverageMode, interpolation: { escapeValue: false } });
}

function dependencyDetail(t: Translate, entry: NodeInputCoverage): string {
  return t('comparison.align.dependencyDetail', {
    policy: policyText(t, entry),
    fraction:
      entry.minimumAcceptedFraction === undefined
        ? ''
        : t('comparison.input.fraction', { fraction: entry.minimumAcceptedFraction }),
    accepted: entry.accepted,
    missing: entry.missing,
    failed: entry.failed,
    blocked: entry.blocked,
    skipped: entry.skipped,
    interpolation: { escapeValue: false },
  });
}

function NodeInputCoverageView({ node }: { node: NodeStatus | undefined }) {
  const { t } = useTranslation('analysis');
  const coverage = node?.inputCoverage;
  return (
    <div>
      <dt>{t('comparison.streams.inputCoverage')}</dt>
      <dd>
        {coverage === undefined || coverage === null ? (
          t('comparison.input.notReported')
        ) : coverage.length === 0 ? (
          t('comparison.input.emptyList')
        ) : (
          <ul>
            {coverage.map((entry) => (
              <li key={entry.upstreamNodeKey}>
                {t('comparison.input.item', {
                  key: entry.upstreamNodeKey,
                  policy: policyText(t, entry),
                  fraction:
                    entry.minimumAcceptedFraction === undefined
                      ? ''
                      : t('comparison.input.fraction', { fraction: entry.minimumAcceptedFraction }),
                  accepted: entry.accepted,
                  missing: entry.missing,
                  failed: entry.failed,
                  blocked: entry.blocked,
                  skipped: entry.skipped,
                  interpolation: { escapeValue: false },
                })}
              </li>
            ))}
          </ul>
        )}
      </dd>
    </div>
  );
}

function fieldValue(t: Translate, value: string | undefined): string {
  return value === undefined ? t('comparison.align.absent') : value;
}

function DependencyView({ dependency }: { dependency: DependencyComparison }) {
  const { t } = useTranslation('analysis');
  const key = dependency.upstreamNodeKey;
  const options = { interpolation: { escapeValue: false } };
  if (dependency.kind === 'leftOnly') {
    return <li>{t('comparison.align.dependencyLeft', { key, left: dependencyDetail(t, dependency.left), ...options })}</li>;
  }
  if (dependency.kind === 'rightOnly') {
    return <li>{t('comparison.align.dependencyRight', { key, right: dependencyDetail(t, dependency.right), ...options })}</li>;
  }
  if (dependency.kind === 'same') return <li>{t('comparison.align.dependencySame', { key, ...options })}</li>;
  return (
    <li>
      {t('comparison.align.dependencyChanged', {
        key,
        fields: dependency.changedFields.map((field) => t(`comparison.field.${field}`)).join(', '),
        left: dependencyDetail(t, dependency.left),
        right: dependencyDetail(t, dependency.right),
        ...options,
      })}
    </li>
  );
}

function AlignedEntry({ entry }: { entry: Extract<StreamComparisonEntry, { kind: 'aligned' }> }) {
  const { t } = useTranslation('analysis');
  const { inputCoverage } = entry;
  const options = { interpolation: { escapeValue: false } };
  const countsLine = (side: string, stream: FactStreamSummary) =>
    t('comparison.align.counts', {
      side,
      facts: stream.factCount,
      ...stream.coverage,
      ...options,
    });
  return (
    <li>
      <Typography component="p">
        {t('comparison.align.aligned', {
          left: entry.left.nodeKey,
          right: entry.right.nodeKey,
          capability: entry.left.capabilityId,
          scope: entry.left.executionScope,
          ...options,
        })}
      </Typography>
      {entry.differences.length === 0 ? (
        <Typography>{t('comparison.align.noDifferences')}</Typography>
      ) : (
        <>
          <Typography component="p">{t('comparison.align.differencesTitle')}</Typography>
          <ul>
            {entry.differences.map((difference) => (
              <li key={difference.field}>
                {t('comparison.align.difference', {
                  field: t(`comparison.field.${difference.field}`),
                  left: fieldValue(t, difference.left),
                  right: fieldValue(t, difference.right),
                  ...options,
                })}
              </li>
            ))}
          </ul>
        </>
      )}
      <ul>
        <li>{countsLine(t('comparison.left'), entry.left)}</li>
        <li>{availabilityText(t, entry.leftAvailability, entry.left)}</li>
        <li>{countsLine(t('comparison.right'), entry.right)}</li>
        <li>{availabilityText(t, entry.rightAvailability, entry.right)}</li>
      </ul>
      <Typography component="p">{t('comparison.align.dependenciesTitle')}</Typography>
      {inputCoverage.left === undefined || inputCoverage.right === undefined ? (
        <Typography>{t('comparison.align.dependencyNotReported')}</Typography>
      ) : null}
      {inputCoverage.dependencies.length === 0 ? (
        <Typography>{t('comparison.align.dependenciesNone')}</Typography>
      ) : (
        <ul>
          {inputCoverage.dependencies.map((dependency) => (
            <DependencyView key={dependency.upstreamNodeKey} dependency={dependency} />
          ))}
        </ul>
      )}
      <Typography>{t('comparison.align.dependencyNote')}</Typography>
    </li>
  );
}

/** Alignment of unambiguous capability/scope pairs as a navigation aid; unmatched streams stay on their own side. */
export function ComparisonAlignment({ entries }: { entries: StreamComparisonEntry[] }) {
  const { t } = useTranslation('analysis');
  const options = { interpolation: { escapeValue: false } };
  return (
    <section aria-label={t('comparison.align.title')}>
      <Typography variant="h3" component="h3" sx={{ mt: 3 }}>
        {t('comparison.align.title')}
      </Typography>
      <Typography>{t('comparison.align.note')}</Typography>
      {entries.length === 0 ? (
        <Typography>{t('comparison.align.empty')}</Typography>
      ) : (
        <ul aria-label={t('comparison.align.listLabel')}>
          {entries.map((entry) =>
            entry.kind === 'aligned' ? (
              <AlignedEntry key={`aligned:${entry.left.nodeKey}`} entry={entry} />
            ) : (
              <li key={`${entry.side}:${entry.stream.nodeKey}`}>
                <Typography component="p">
                  {t(entry.side === 'left' ? 'comparison.align.unmatchedLeft' : 'comparison.align.unmatchedRight', {
                    key: entry.stream.nodeKey,
                    capability: entry.stream.capabilityId,
                    scope: entry.stream.executionScope,
                    ...options,
                  })}
                </Typography>
                <Typography>
                  {entry.reason === 'ambiguous' ? t('comparison.align.ambiguous') : t('comparison.align.noCounterpart')}
                </Typography>
              </li>
            ),
          )}
        </ul>
      )}
    </section>
  );
}

/**
 * Renders the fact streams of one comparison side on their own; nothing is aligned with the other side and a count of
 * zero facts is shown as reported, never as a complete analysis.
 */
export function ComparisonStreams({
  streams,
  sideLabel,
  nodes = [],
}: {
  streams: FactStreamSummary[];
  sideLabel: string;
  nodes?: NodeStatus[];
}) {
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
              <dt>{t('comparison.streams.availability')}</dt>
              <dd>{availabilityText(t, describeStreamAvailability(stream), stream)}</dd>
            </div>
            <NodeInputCoverageView node={nodes.find((node) => node.nodeKey === stream.nodeKey)} />
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
