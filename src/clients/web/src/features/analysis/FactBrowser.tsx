import Button from '@mui/material/Button';
import CircularProgress from '@mui/material/CircularProgress';
import Typography from '@mui/material/Typography';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import type { AnalysisEvidenceAdapter } from '../../platform/adapters/analysisEvidenceAdapter';
import type { AnalysisFactAdapter } from '../../platform/adapters/analysisFactAdapter';
import type { AnalysisRunStatus, Fact, FactSelection } from '../../platform/models/analysis';
import { EvidencePlayerDialog } from './EvidencePlayerDialog';
import { CoverageList } from './CoverageList';
import { FactRangeForm } from './FactRangeForm';
import type { FactRange } from './FactRangeForm';
import { formatMatchRange } from './matchClock';
import { fullPayloadText, summarizePayload } from './payloadSummary';
import { ProvenanceDetails } from './ProvenanceDetails';
import { deriveFactViewState } from './reviewState';
import { useFactPages } from './useFactPages';

const percent = new Intl.NumberFormat('en', { style: 'percent', maximumFractionDigits: 0 });

function FactItem({ fact, runId, capability, evidenceAdapter }: { fact: Fact; runId: string; capability: string; evidenceAdapter?: AnalysisEvidenceAdapter }) {
  const { t } = useTranslation('analysis');
  const [open, setOpen] = useState(false);
  const [playing, setPlaying] = useState(false);
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
      {evidenceAdapter && (
        <>
          <Button size="small" sx={{ ml: 1 }} aria-label={`${t('evidence.play')}: ${range.label}`} onClick={() => setPlaying(true)}>
            {t('evidence.play')}
          </Button>
          {playing && (
            <EvidencePlayerDialog
              adapter={evidenceAdapter}
              runId={runId}
              capability={capability}
              fact={{ acceptedResultId: fact.acceptedResultId, ordinal: fact.ordinal, startMs: fact.startMs, endMs: fact.endMs }}
              onClose={() => setPlaying(false)}
            />
          )}
        </>
      )}
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
  evidenceAdapter?: AnalysisEvidenceAdapter;
  selection: FactSelection;
  onRangeChange: (range: FactRange) => void;
}

/** Facts of the selected stream with range, paging, provenance, and coverage; exactly one state of `contracts/review-states.md` shows at a time and all platform text renders as text (FR-008, FR-009, FR-011, FR-012, FR-014, FR-015, FR-020). */
export function FactBrowser({ adapter, evidenceAdapter, selection, onRangeChange }: FactBrowserProps) {
  const { t } = useTranslation('analysis');
  const { isUnreachable } = useConnectivity();
  const paging = useFactPages(adapter, selection);
  const { page, outcome } = paging;

  const view = deriveFactViewState({
    online: !isUnreachable,
    routeMatchId: page?.run.matchId ?? '',
    hasTeamRole: true,
    factsOutcome: outcome,
    failure: paging.error,
    rangeValid: true,
    rangeSet: selection.range.fromMs !== undefined || selection.range.toMs !== undefined,
    pending: paging.isLoading,
    page,
    stream: selection.stream,
    runOutcome: page ? { kind: 'found', run: { state: page.run.state, matchId: page.run.matchId } as AnalysisRunStatus } : undefined,
  });
  const showsData = Boolean(page) && view.state !== 'connectionRequired';
  const nonAccepted = page?.coverage.filter((window) => window.state.toLowerCase() !== 'accepted') ?? [];

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
      {view.state === 'loading' && (
        <div role="status" aria-live="polite">
          <CircularProgress size={20} aria-hidden="true" />
          <Typography component="span" sx={{ ml: 1 }}>
            {t('states.loading')}
          </Typography>
        </div>
      )}
      {view.state === 'error' && (
        <>
          <Typography role="alert">{t('states.error')}</Typography>
          <Button size="small" variant="outlined" onClick={paging.reload}>
            {t('page.reload')}
          </Button>
        </>
      )}
      {(view.state === 'connectionRequired' ||
        view.state === 'notFound' ||
        view.state === 'notPermitted' ||
        view.state === 'capabilityNotInRun' ||
        view.state === 'invalidRange') && <Typography role="alert">{t(`states.${view.state}`)}</Typography>}
      {showsData && page && (
        <>
          {view.state === 'facts' && (
            <>
              {view.incomplete && <Typography>{t('facts.incompleteNote')}</Typography>}
              <ul aria-label={t('facts.listLabel')}>
                {page.items.map((fact) => (
                  <FactItem
                    key={`${fact.acceptedResultId}-${fact.ordinal}`}
                    fact={fact}
                    runId={selection.runId}
                    capability={selection.stream.capabilityId}
                    evidenceAdapter={evidenceAdapter}
                  />
                ))}
              </ul>
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
            </>
          )}
          {(view.state === 'noFactsInRange' || view.state === 'inProgress' || view.state === 'incomplete' || view.state === 'completeWithoutFacts') && (
            <Typography>{t(`states.${view.state}`)}</Typography>
          )}
          {view.state === 'facts' || view.state === 'noFactsInRange' || view.state === 'inProgress' || view.state === 'completeWithoutFacts' ? (
            <>
              <ProvenanceDetails provenance={page.stream.provenance} />
              <CoverageList windows={page.coverage} />
            </>
          ) : (
            view.state === 'incomplete' && (
              <>
                <ProvenanceDetails provenance={page.stream.provenance} />
                <CoverageList windows={nonAccepted} title={t('states.nonAcceptedWindows')} />
              </>
            )
          )}
        </>
      )}
    </section>
  );
}
