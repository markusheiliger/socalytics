import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams, useSearchParams } from 'react-router';
import { createAnalysisFactAdapter } from '../../platform/adapters/analysisFactAdapter';
import { createAnalysisRunAdapter } from '../../platform/adapters/analysisRunAdapter';
import type { FactStreamSummary } from '../../platform/models/analysis';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { NotFoundPage } from '../../shell/NotFoundPage';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { useMatchContext } from '../matches/useMatchContext';
import { FailureAlert } from '../shared/FailureAlert';
import { analysisRunKey, analysisStreamsKey, runRefreshInterval } from './analysisKeys';
import { FactBrowser } from './FactBrowser';
import { FactRangeForm } from './FactRangeForm';
import type { FactRange } from './FactRangeForm';
import { FactStreamList } from './FactStreamList';
import { validateRange } from './matchClock';
import { RunStatusSection } from './RunStatusSection';

const WHOLE_NUMBER = /^\d+$/;

const COVERAGE_STATES = ['accepted', 'missing', 'failed', 'blocked', 'skipped'] as const;

/** True when the refreshed summary reports another fact count or other coverage counts than the loaded one (research R6). */
function hasNewResults(loaded: FactStreamSummary, current: FactStreamSummary): boolean {
  return loaded.factCount !== current.factCount || COVERAGE_STATES.some((state) => loaded.coverage[state] !== current.coverage[state]);
}

/** Reads `fromMs` and `toMs` from the address; a malformed or inconsistent range is invalid (FR-010). */
function readRange(params: URLSearchParams): { range: FactRange; valid: boolean } {
  const read = (name: string) => {
    const text = params.get(name);
    if (text === null) return { ms: undefined, valid: true };
    return WHOLE_NUMBER.test(text) && Number.isSafeInteger(Number(text)) ? { ms: Number(text), valid: true } : { ms: undefined, valid: false };
  };
  const from = read('fromMs');
  const to = read('toMs');
  if (!from.valid || !to.valid) return { range: {}, valid: false };
  const result = validateRange(from.ms, to.ms);
  return result.ok ? { range: result.range, valid: true } : { range: {}, valid: false };
}

/** Analysis review of one run of a match: run status and nodes, fact streams, and the selected stream's facts (FR-001, FR-002, FR-003, FR-006, FR-007, FR-010, FR-017). */
export function AnalysisRunPage() {
  const { matchId = '', runId = '' } = useParams();
  const { t } = useTranslation('analysis');
  const { t: tc } = useTranslation('common');
  const { platform } = useSession();
  const adapter = useMemo(() => createAnalysisRunAdapter(platform), [platform]);
  const factAdapter = useMemo(() => createAnalysisFactAdapter(platform), [platform]);
  const [searchParams, setSearchParams] = useSearchParams();
  const queryClient = useQueryClient();
  const [reloads, setReloads] = useState(0);
  const [loaded, setLoaded] = useState<{ id: string; stream: FactStreamSummary }>();
  const { match, team, permitted } = useMatchContext(matchId);
  const run = useQuery({
    queryKey: analysisRunKey(runId),
    queryFn: () => adapter.getRun(runId),
    enabled: permitted,
    retry: false,
    refetchOnWindowFocus: false,
    // TanStack Query pauses interval refetching while the page is hidden.
    refetchInterval: (query) => runRefreshInterval(query.state.data?.kind === 'found' ? query.state.data.run : undefined),
    refetchIntervalInBackground: false,
  });
  const runOutcome = run.data;
  const streams = useQuery({
    queryKey: analysisStreamsKey(runId),
    queryFn: () => factAdapter.listStreams(runId),
    enabled: permitted && runOutcome?.kind === 'found' && runOutcome.run.matchId === matchId,
    retry: false,
    refetchOnWindowFocus: false,
    refetchInterval: runOutcome?.kind === 'found' ? runRefreshInterval(runOutcome.run) : false,
    refetchIntervalInBackground: false,
  });

  const isNotPermitted = (error: unknown) => Boolean(error) && toPlatformFailure(error).kind === 'notPermitted';
  if ((match.data && !permitted) || isNotPermitted(match.error) || isNotPermitted(run.error) || isNotPermitted(streams.error)) return <NotPermittedPage />;

  const outcome = run.data;
  if (outcome?.kind === 'runNotFound' || (outcome?.kind === 'found' && outcome.run.matchId !== matchId) || streams.data?.kind === 'runNotFound') return <NotFoundPage framed />;

  const error = match.error ?? team.error ?? run.error ?? streams.error;
  const reload = () => {
    void match.refetch();
    void team.refetch();
    void run.refetch();
    void streams.refetch();
  };

  const streamKey = searchParams.get('stream') ?? undefined;
  const { range, valid: rangeValid } = readRange(searchParams);
  const streamList = streams.data?.kind === 'found' ? streams.data.streams : undefined;
  const selectedStream = streamList?.find((stream) => stream.nodeKey === streamKey);

  const selectionId = `${streamKey}|${searchParams.get('fromMs')}|${searchParams.get('toMs')}|${reloads}`;
  if (selectedStream && loaded?.id !== selectionId) setLoaded({ id: selectionId, stream: selectedStream });
  const newResults = Boolean(selectedStream && loaded?.id === selectionId && hasNewResults(loaded.stream, selectedStream));
  const reloadFacts = () => {
    queryClient.removeQueries({ queryKey: ['analysis', 'facts', runId, selectedStream?.nodeKey] });
    setReloads((count) => count + 1);
  };

  const selectStream = (nodeKey: string) => setSearchParams({ stream: nodeKey });
  const changeRange = (next: FactRange) => {
    const params: Record<string, string> = { stream: streamKey ?? '' };
    if (next.fromMs !== undefined) params.fromMs = String(next.fromMs);
    if (next.toMs !== undefined) params.toMs = String(next.toMs);
    setSearchParams(params);
  };

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('page.title')}</Typography>
      {error && <FailureAlert failure={toPlatformFailure(error)} onReload={reload} />}
      {error && (
        <Button variant="outlined" sx={{ mt: 1 }} onClick={reload}>
          {t('page.reload')}
        </Button>
      )}
      {!error && !outcome && <Typography>{t('page.loading')}</Typography>}
      {outcome?.kind === 'found' && <RunStatusSection run={outcome.run} />}
      {streamList && <FactStreamList streams={streamList} selectedNodeKey={selectedStream?.nodeKey} onSelect={selectStream} />}
      {streamList && streamKey !== undefined && !selectedStream && <Typography role="alert">{t('states.capabilityNotInRun')}</Typography>}
      {selectedStream && !rangeValid && (
        <>
          <Typography role="alert">{t('states.invalidRange')}</Typography>
          <FactRangeForm range={{}} onChange={changeRange} />
        </>
      )}
      {selectedStream && (
        <div role="status" aria-live="polite">
          {newResults && <Typography>{t('facts.newResults')}</Typography>}
        </div>
      )}
      {newResults && (
        <Button variant="outlined" size="small" onClick={reloadFacts}>
          {t('facts.reloadResults')}
        </Button>
      )}
      {selectedStream && rangeValid && (
        <FactBrowser key={reloads} adapter={factAdapter} selection={{ runId, stream: selectedStream, range }} onRangeChange={changeRange} />
      )}
      <Button component={Link} to={`/matches/${matchId}`} variant="outlined" sx={{ mt: 2 }}>
        {t('page.back')}
      </Button>
    </Container>
  );
}
