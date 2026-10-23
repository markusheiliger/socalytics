import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams, useSearchParams } from 'react-router';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import { toPlatformFailure, type PlatformFailure } from '../../platform/failure';
import { isRunTerminal } from '../../platform/models/analysis';
import type { AnalysisRunSummary } from '../../platform/models/recordings';
import { useSession } from '../../session/sessionContext';
import { NotFoundPage } from '../../shell/NotFoundPage';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { useMatchContext } from '../matches/useMatchContext';
import { FailureAlert } from '../shared/FailureAlert';
import { ComparisonStreams } from './ComparisonStreams';
import { parseComparisonSelection, type ComparisonSide } from './comparisonPresentation';
import { useAnalysisComparison, type AnalysisComparison } from './useAnalysisComparison';

interface CandidateState {
  runs: AnalysisRunSummary[];
  continuationToken?: string;
  status: 'loading' | 'idle' | 'error';
  /** True only once a page answered without a continuation token. */
  exhausted: boolean;
  failure?: PlatformFailure;
}

const INITIAL: CandidateState = { runs: [], status: 'loading', exhausted: false };

/**
 * Pages the runs of one match into component memory. Nothing is cached, persisted, or shared; the pages are
 * discarded when reads stop being allowed, the member changes, or the page unmounts.
 */
function useCandidateRuns(matchId: string, enabled: boolean): { state: CandidateState; loadMore: () => void } {
  const { platform, session } = useSession();
  const memberId = session?.member.id;
  const adapter = useMemo(() => createAnalysisRunAdapter(platform), [platform]);
  const [state, setState] = useState<CandidateState>(INITIAL);
  const generation = useRef(0);
  const tokenRef = useRef<string | undefined>(undefined);
  tokenRef.current = state.continuationToken;

  const load = useCallback(
    async (token?: string) => {
      const current = generation.current;
      setState((previous) => ({ ...previous, status: 'loading', failure: undefined }));
      try {
        const page = await adapter.listByMatch(matchId, token ? { continuationToken: token } : undefined);
        if (current !== generation.current) return;
        setState((previous) => ({
          runs: [...previous.runs, ...page.items],
          continuationToken: page.continuationToken,
          status: 'idle',
          exhausted: !page.continuationToken,
        }));
      } catch (error) {
        if (current !== generation.current) return;
        setState((previous) => ({ ...previous, status: 'error', failure: toPlatformFailure(error) }));
      }
    },
    [adapter, matchId],
  );

  useEffect(() => {
    generation.current += 1;
    setState(INITIAL);
    if (enabled && memberId) void load();
    return () => {
      generation.current += 1;
    };
  }, [enabled, memberId, load]);

  const loadMore = useCallback(() => void load(tokenRef.current), [load]);
  return { state, loadMore };
}

function SelectionForm({
  initialLeft,
  initialRight,
  candidates,
  onCompare,
}: {
  initialLeft: string;
  initialRight: string;
  candidates: AnalysisRunSummary[];
  onCompare: (left: string, right: string) => void;
}) {
  const { t } = useTranslation('analysis');
  const [left, setLeft] = useState(initialLeft);
  const [right, setRight] = useState(initialRight);

  const eligible = useMemo(() => {
    const byId = new Map<string, AnalysisRunSummary>();
    for (const run of candidates) if (isRunTerminal(run.state) && !byId.has(run.runId)) byId.set(run.runId, run);
    return [...byId.values()];
  }, [candidates]);

  const label = (run: AnalysisRunSummary) =>
    t('comparison.option', {
      state: t(`run.stateName.${run.state}`, { defaultValue: run.state }),
      runId: run.runId,
      current: run.isCurrent ? t('comparison.currentMarker') : '',
      setId: run.recordingSetVersionId,
      workflow: t('run.workflowValue', { id: run.workflowId, version: run.workflowVersion }),
      time: run.createdAt.toLocaleString(),
    });

  const options = (selected: string) => (
    <>
      <option value="">{t('comparison.choose')}</option>
      {selected !== '' && !eligible.some((run) => run.runId === selected) && (
        <option value={selected}>{t('comparison.optionNotLoaded', { runId: selected })}</option>
      )}
      {eligible.map((run) => (
        <option key={run.runId} value={run.runId}>
          {label(run)}
        </option>
      ))}
    </>
  );

  const identical = left !== '' && left.toLowerCase() === right.toLowerCase();
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (left !== '' && right !== '' && !identical) onCompare(left, right);
  };

  return (
    <form onSubmit={submit} aria-label={t('comparison.formLabel')}>
      <TextField
        select
        fullWidth
        margin="normal"
        label={t('comparison.left')}
        value={left}
        onChange={(event) => setLeft(event.target.value)}
        slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
      >
        {options(left)}
      </TextField>
      <TextField
        select
        fullWidth
        margin="normal"
        label={t('comparison.right')}
        value={right}
        onChange={(event) => setRight(event.target.value)}
        slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
      >
        {options(right)}
      </TextField>
      {identical && <Typography role="alert">{t('comparison.identical')}</Typography>}
      <Button type="submit" variant="contained" disabled={left === '' || right === '' || identical} sx={{ mt: 1 }}>
        {t('comparison.compare')}
      </Button>
    </form>
  );
}

function ComparisonSideView({ side, matchId, label }: { side: ComparisonSide; matchId: string; label: string }) {
  const { t } = useTranslation('analysis');
  const { run, recordingSet } = side;
  const yes = t('comparison.side.yes');
  const no = t('comparison.side.no');
  const plain = !run.isCurrent && !run.supersededBy && !run.supersededAt;
  return (
    <section aria-label={label}>
      <Typography variant="h3" component="h3" sx={{ mt: 3 }}>
        {label}
      </Typography>
      <dl>
        <div>
          <dt>{t('comparison.side.runId')}</dt>
          <dd>{run.runId}</dd>
        </div>
        <div>
          <dt>{t('run.state')}</dt>
          <dd>{t(`run.stateName.${run.state}`, { defaultValue: run.state })}</dd>
        </div>
        <div>
          <dt>{t('comparison.side.current')}</dt>
          <dd>{run.isCurrent ? yes : no}</dd>
        </div>
        {run.supersededBy && (
          <div>
            <dt>{t('comparison.side.supersededBy')}</dt>
            <dd>
              {t('comparison.side.successor', {
                runId: run.supersededBy.runId,
                setId: run.supersededBy.recordingSetVersionId,
                finalizedAt: run.supersededBy.recordingSetFinalizedAt.toLocaleString(),
              })}
            </dd>
          </div>
        )}
        {run.supersededAt && (
          <div>
            <dt>{t('comparison.side.supersededAt')}</dt>
            <dd>{run.supersededAt.toLocaleString()}</dd>
          </div>
        )}
        {plain && (
          <div>
            <dt>{t('comparison.side.history')}</dt>
            <dd>{t('comparison.side.plain')}</dd>
          </div>
        )}
        <div>
          <dt>{t('run.workflow')}</dt>
          <dd>{t('run.workflowValue', { id: run.workflowId, version: run.workflowVersion })}</dd>
        </div>
        <div>
          <dt>{t('comparison.side.recordingSet')}</dt>
          <dd>{recordingSet.id}</dd>
        </div>
        <div>
          <dt>{t('comparison.side.finalizedAt')}</dt>
          <dd>{recordingSet.finalizedAt.toLocaleString()}</dd>
        </div>
      </dl>
      <Typography variant="h4" component="h4">
        {t('comparison.side.members', { count: recordingSet.members.length })}
      </Typography>
      <ol aria-label={t('comparison.side.membersLabel', { side: label })}>
        {recordingSet.members.map((member) => (
          <li key={member.position}>
            {t('comparison.side.member', {
              position: member.position,
              recordingVersionId: member.recordingVersionId,
              contentDigest: member.recordingContentDigest,
              mappingId: member.timelineMappingId,
              mappingDigest: member.timelineMappingDigest,
              interpolation: { escapeValue: false },
            })}
          </li>
        ))}
      </ol>
      <Button component={Link} to={`/matches/${matchId}/analysis-runs/${run.runId}`} variant="outlined" size="small">
        {t('comparison.side.review', { side: label })}
      </Button>
      <Typography variant="h4" component="h4" sx={{ mt: 2 }}>
        {t('comparison.streams.title', { side: label })}
      </Typography>
      <ComparisonStreams streams={side.streams} sideLabel={label} />
    </section>
  );
}

function ComparisonResult({ comparison, matchId }: { comparison: AnalysisComparison; matchId: string }) {
  const { t } = useTranslation('analysis');
  const { state, model, invalid, failure, refresh } = comparison;
  const retry = (
    <Button variant="outlined" sx={{ mt: 1 }} onClick={refresh}>
      {t('comparison.result.retry')}
    </Button>
  );
  if (state === 'loading') return <Typography role="status">{t('comparison.result.loading')}</Typography>;
  if (state === 'invalidSelection' && invalid && 'side' in invalid) {
    return <Alert severity="warning">{t(`comparison.result.${invalid.reason}`, { side: t(`comparison.${invalid.side}`) })}</Alert>;
  }
  if (state === 'notFound') return <Alert severity="warning">{t('comparison.result.notFound')}</Alert>;
  if (state === 'incompatible') {
    return (
      <>
        <Alert severity="error">{t('comparison.result.incompatible')}</Alert>
        {retry}
      </>
    );
  }
  if (state === 'error') {
    return (
      <>
        {failure && <FailureAlert failure={failure} />}
        {retry}
      </>
    );
  }
  if (state === 'ready' && model) {
    return (
      <section aria-label={t('comparison.result.title')}>
        <Typography variant="h2" component="h2" sx={{ mt: 3 }}>
          {t('comparison.result.title')}
        </Typography>
        <Typography>{t('comparison.result.caveat')}</Typography>
        <ComparisonSideView side={model.left} matchId={matchId} label={t('comparison.left')} />
        <ComparisonSideView side={model.right} matchId={matchId} label={t('comparison.right')} />
        <Button variant="outlined" sx={{ mt: 2 }} onClick={refresh}>
          {t('comparison.result.refresh')}
        </Button>
      </section>
    );
  }
  return null;
}

/** Two-run selection of one match (FR-001 through FR-005, FR-012 through FR-015) and the initial independent stream presentation of the chosen pair. */
export function AnalysisComparisonPage() {
  const { matchId = '' } = useParams();
  const { t } = useTranslation('analysis');
  const { t: tc } = useTranslation('common');
  const { isUnreachable } = useConnectivity();
  const [searchParams, setSearchParams] = useSearchParams();
  const selection = useMemo(() => parseComparisonSelection(searchParams), [searchParams]);
  const { match, team, permitted } = useMatchContext(matchId);
  const { state, loadMore } = useCandidateRuns(matchId, permitted && !isUnreachable);
  const comparison = useAnalysisComparison(matchId, selection);

  if (isUnreachable) {
    return (
      <Container component="main" maxWidth="md" sx={{ py: 4 }}>
        <Typography variant="h1">{tc('app.name')}</Typography>
        <Typography variant="h2">{t('comparison.title')}</Typography>
        <Typography role="alert">{t('comparison.connectionRequired')}</Typography>
      </Container>
    );
  }

  const isNotPermitted = (error: unknown) => Boolean(error) && toPlatformFailure(error).kind === 'notPermitted';
  if (comparison.state === 'notPermitted') return <NotPermittedPage />;
  if ((match.data && !permitted) || isNotPermitted(match.error) || isNotPermitted(team.error)) return <NotPermittedPage />;
  if (match.error && toPlatformFailure(match.error).status === 404) return <NotFoundPage framed />;

  const leftId = selection.kind === 'left' || selection.kind === 'pair' ? selection.leftRunId : '';
  const rightId = selection.kind === 'pair' ? selection.rightRunId : '';
  const formKey = `${leftId}|${rightId}`;
  const eligibleCount = new Set(state.runs.filter((run) => isRunTerminal(run.state)).map((run) => run.runId)).size;
  const error = match.error ?? team.error;
  const reload = () => {
    void match.refetch();
    void team.refetch();
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('comparison.title')}</Typography>
      <Typography>{t('comparison.intro')}</Typography>
      {error && <FailureAlert failure={toPlatformFailure(error)} onReload={reload} />}
      {selection.kind === 'invalid' && <Alert severity="warning">{t(`comparison.invalid.${selection.reason}`)}</Alert>}
      {permitted && (
        <>
          <Typography>{t('comparison.eligibility')}</Typography>
          <SelectionForm
            key={formKey}
            initialLeft={leftId}
            initialRight={rightId}
            candidates={state.runs}
            onCompare={(left, right) => setSearchParams({ left, right })}
          />
          <div role="status" aria-live="polite">
            {state.status === 'loading' && <Typography>{t('comparison.loadingRuns')}</Typography>}
            {state.status === 'idle' && state.continuationToken && (
              <Typography>{t('comparison.loaded', { count: state.runs.length, eligible: eligibleCount })}</Typography>
            )}
            {state.exhausted && eligibleCount < 2 && <Typography>{t('comparison.insufficient', { count: eligibleCount })}</Typography>}
            {state.exhausted && eligibleCount >= 2 && <Typography>{t('comparison.exhausted', { count: state.runs.length })}</Typography>}
          </div>
          {state.status === 'error' && state.failure && <FailureAlert failure={state.failure} />}
          {state.status === 'error' && (
            <Button variant="outlined" sx={{ mt: 1, mr: 1 }} onClick={loadMore}>
              {t('comparison.retry')}
            </Button>
          )}
          {state.status === 'idle' && state.continuationToken && (
            <Button variant="outlined" sx={{ mt: 1, mr: 1 }} onClick={loadMore}>
              {t('comparison.loadMore')}
            </Button>
          )}
          <ComparisonResult comparison={comparison} matchId={matchId} />
        </>
      )}
      <Button component={Link} to={`/matches/${matchId}`} variant="outlined" sx={{ mt: 2 }}>
        {t('page.back')}
      </Button>
    </Container>
  );
}
