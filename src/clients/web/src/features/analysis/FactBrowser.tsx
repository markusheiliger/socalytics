import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { AnalysisFactAdapter } from '../../platform/adapters/analysisFactAdapter';
import type { Fact, FactSelection } from '../../platform/models/analysis';
import { CoverageList } from './CoverageList';
import { FactRangeForm } from './FactRangeForm';
import type { FactRange } from './FactRangeForm';
import { formatMatchRange } from './matchClock';
import { fullPayloadText, summarizePayload } from './payloadSummary';
import { ProvenanceDetails } from './ProvenanceDetails';
import { useFactPages } from './useFactPages';

const percent = new Intl.NumberFormat('en', { style: 'percent', maximumFractionDigits: 0 });

function FactItem({ fact }: { fact: Fact }) {
  const { t } = useTranslation('analysis');
  const [open, setOpen] = useState(false);
  const id = useId();
  const range = formatMatchRange(fact.startMs, fact.endMs);

  return (
    <li aria-label={`${range.label}: ${fact.kind}`}>
      <dl>
        <div>
          <dt>{t('facts.range')}</dt>
          <dd>{range.text}</dd>
        </div>
        <div>
          <dt>{t('facts.kind')}</dt>
          <dd>{fact.kind}</dd>
        </div>
        <div>
          <dt>{t('facts.confidence')}</dt>
          <dd>{percent.format(fact.confidence)}</dd>
        </div>
        <div>
          <dt>{t('facts.payloadSummary')}</dt>
          <dd>{summarizePayload(fact.payload)}</dd>
        </div>
      </dl>
      <Button size="small" aria-expanded={open} aria-controls={`${id}-payload`} onClick={() => setOpen((value) => !value)}>
        {open ? t('facts.hidePayload') : t('facts.showPayload')}
      </Button>
      {open && (
        <pre id={`${id}-payload`} aria-label={t('facts.payloadLabel')} style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
          {fullPayloadText(fact.payload)}
        </pre>
      )}
    </li>
  );
}

interface FactBrowserProps {
  adapter: AnalysisFactAdapter;
  selection: FactSelection;
  onRangeChange: (range: FactRange) => void;
}

/** Facts of the selected stream with range, paging, provenance, and coverage; all platform text renders as text (FR-008, FR-009, FR-011, FR-012, FR-020). */
export function FactBrowser({ adapter, selection, onRangeChange }: FactBrowserProps) {
  const { t } = useTranslation('analysis');
  const paging = useFactPages(adapter, selection);
  const { page } = paging;

  return (
    <section aria-label={t('facts.title')}>
      <Typography variant="h3" component="h3" sx={{ mt: 2 }}>
        {t('facts.title')}
      </Typography>
      <FactRangeForm range={selection.range} onChange={onRangeChange} />
      {paging.restarted && (
        <div role="status" aria-live="polite">
          {t('facts.continuationRestart')}
        </div>
      )}
      {paging.isLoading && <Typography>{t('states.loading')}</Typography>}
      {paging.error && <Typography role="alert">{t('states.error')}</Typography>}
      {page && (
        <>
          {page.items.length === 0 ? (
            <Typography>{t('facts.none')}</Typography>
          ) : (
            <ul aria-label={t('facts.listLabel')}>
              {page.items.map((fact) => (
                <FactItem key={`${fact.acceptedResultId}-${fact.ordinal}`} fact={fact} />
              ))}
            </ul>
          )}
          <nav aria-label={t('facts.pageLabel')}>
            <Button size="small" variant="outlined" disabled={!paging.hasPrevious} onClick={paging.previous}>
              {t('facts.previous')}
            </Button>
            <Button size="small" variant="outlined" sx={{ ml: 1 }} disabled={!paging.hasNext} onClick={paging.next}>
              {t('facts.next')}
            </Button>
            <Typography component="span" sx={{ ml: 1 }}>
              {t('facts.page', { page: paging.pageNumber })}
            </Typography>
            <Typography component="span" sx={{ ml: 1 }}>
              {[!paging.hasPrevious && t('facts.firstPage'), paging.hasNext ? t('facts.moreFacts') : t('facts.lastPage')].filter(Boolean).join(' ')}
            </Typography>
          </nav>
          <ProvenanceDetails provenance={page.stream.provenance} />
          <CoverageList windows={page.coverage} />
        </>
      )}
    </section>
  );
}
