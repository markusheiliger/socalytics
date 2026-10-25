import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createSegmentAdapter } from '../../platform/adapters/segmentAdapter';
import { toPlatformFailure } from '../../platform/failure';
import type { MaterializedSegment } from '../../platform/models/segments';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';
import { CopyableValue } from '../operations/CopyableValue';
import { formatSegmentSize, formatSegmentTime, shortenId } from './formatSegments';
import { PurgeSegmentsDialog } from './PurgeSegmentsDialog';
import { segmentsKey } from './segmentKeys';
import { summarizeSegments } from './segmentSummary';

const KNOWN_STATES = ['absent', 'materializing', 'available', 'failed'];

interface SegmentsSectionProps {
  matchId: string;
  /** False for everyone but the Club Admin of the owning club: no request is sent. */
  enabled?: boolean;
  /** The opponent's name, which identifies the match in the purge confirmation. */
  opponent?: string;
}

/** The materialized video segments of one match with a summary and an explicit refresh (FR-003 to FR-008). */
export function SegmentsSection({ matchId, enabled = true, opponent = '' }: SegmentsSectionProps) {
  const { t, i18n } = useTranslation('segments');
  const { t: ts } = useTranslation('shell');
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const adapter = useMemo(() => createSegmentAdapter(platform), [platform]);
  const query = useQuery({
    queryKey: segmentsKey(matchId),
    queryFn: () => adapter.list(matchId),
    enabled,
    retry: false,
    refetchOnWindowFocus: false,
    refetchInterval: false,
    gcTime: 0,
  });

  const [purgeOpen, setPurgeOpen] = useState(false);
  const [purging, setPurging] = useState(false);

  if (!enabled) return null;

  const failure = query.error ? toPlatformFailure(query.error) : undefined;
  // A refusal or an ended session must leave no earlier segment data on screen.
  const dataWithdrawn = failure?.kind === 'notPermitted' || failure?.kind === 'sessionEnded';
  const segments = dataWithdrawn ? undefined : query.data?.segments;
  const fetching = query.isFetching;
  const purgeable = segments ? summarizeSegments(segments).purgeable : 0;

  return (
    <section aria-labelledby="segments-heading">
      <Typography id="segments-heading" variant="h3" component="h3" sx={{ mt: 3 }}>
        {t('area.title')}
      </Typography>
      <Typography>{t('area.note')}</Typography>
      {isUnreachable && (
        <Alert severity="warning" role="status" sx={{ my: 1 }}>
          {t('offline')} {ts('connectivity.connectionRequired')}
        </Alert>
      )}
      {query.isPending && !failure && <Typography role="status">{t('loading')}</Typography>}
      {failure?.kind === 'notPermitted' && <Typography role="alert">{t('notPermitted')}</Typography>}
      {failure && failure.kind !== 'notPermitted' && failure.kind !== 'sessionEnded' && (
        <Stack spacing={1} alignItems="flex-start" sx={{ my: 1 }}>
          {failure.kind === 'sessionProblem' ? (
            <FailureAlert failure={failure} />
          ) : (
            <Alert severity="error" role="alert">
              {t('error')}
            </Alert>
          )}
          {!segments && (
            <Button variant="outlined" disabled={fetching || isUnreachable} onClick={() => void query.refetch()}>
              {t('retry')}
            </Button>
          )}
        </Stack>
      )}
      {segments && (
        <>
          <Stack direction="row" spacing={2} alignItems="center" sx={{ my: 1 }}>
            <Button variant="outlined" disabled={fetching || purging || isUnreachable} onClick={() => void query.refetch()}>
              {fetching ? t('refreshing') : t('refresh')}
            </Button>
            {purgeable > 0 && (
              <Button variant="outlined" disabled={purging || isUnreachable} onClick={() => setPurgeOpen(true)}>
                {t('purge.action')}
              </Button>
            )}
            {segments.length > 0 && <Summary segments={segments} />}
          </Stack>
          {purgeable === 0 && segments.length > 0 && <Typography>{t('purge.nothingToPurge')}</Typography>}
          {segments.length === 0 ? (
            <Typography>{t('empty')}</Typography>
          ) : (
            <Box sx={{ overflowX: 'auto' }}>
              <SegmentTable segments={segments} language={i18n.language} />
            </Box>
          )}
        </>
      )}
      <PurgeSegmentsDialog
        open={purgeOpen}
        matchId={matchId}
        opponent={opponent}
        onClose={() => setPurgeOpen(false)}
        onFinished={() => void query.refetch()}
        onPendingChange={setPurging}
      />
    </section>
  );
}

function Summary({ segments }: { segments: readonly MaterializedSegment[] }) {
  const { t } = useTranslation('segments');
  const summary = summarizeSegments(segments);
  const parts = [t('summary.total', { count: summary.total })];
  for (const state of KNOWN_STATES as Array<'absent' | 'materializing' | 'available' | 'failed'>) {
    if (summary[state] > 0) parts.push(t(`summary.${state}`, { count: summary[state] }));
  }
  if (summary.other > 0) parts.push(t('summary.other', { count: summary.other }));
  parts.push(t('summary.inUse', { count: summary.inUse }));
  return <Typography role="status">{parts.join(', ')}</Typography>;
}

function SegmentTable({ segments, language }: { segments: readonly MaterializedSegment[]; language: string }) {
  const { t } = useTranslation('segments');
  const notReported = t('notReported');
  return (
    <Box component="table" sx={{ borderCollapse: 'collapse', width: '100%', '& th, & td': { textAlign: 'left', p: 1, verticalAlign: 'top' } }}>
      <caption>{t('area.caption')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('columns.recordingVersion')}</th>
          <th scope="col">{t('columns.segmentNumber')}</th>
          <th scope="col">{t('columns.state')}</th>
          <th scope="col">{t('columns.size')}</th>
          <th scope="col">{t('columns.failureReason')}</th>
          <th scope="col">{t('columns.availableAt')}</th>
          <th scope="col">{t('columns.inUse')}</th>
        </tr>
      </thead>
      <tbody>
        {segments.map((segment) => (
          <tr key={`${segment.recordingVersionId}|${segment.timelineMappingDigest}|${segment.segmentationPolicyDigest}|${segment.segmentNumber}`}>
            <td>
              <details>
                <summary title={segment.recordingVersionId}>{shortenId(segment.recordingVersionId)}</summary>
                <dl>
                  <dt>{t('columns.recordingVersion')}</dt>
                  <dd>
                    <CopyableValue value={segment.recordingVersionId} />
                  </dd>
                  <dt>{t('digests.timelineMapping')}</dt>
                  <dd>
                    <CopyableValue value={segment.timelineMappingDigest} />
                  </dd>
                  <dt>{t('digests.segmentationPolicy')}</dt>
                  <dd>
                    <CopyableValue value={segment.segmentationPolicyDigest} />
                  </dd>
                </dl>
              </details>
            </td>
            <td>{segment.segmentNumber}</td>
            <td>{KNOWN_STATES.includes(segment.state) ? t(`state.${segment.state}`) : segment.state}</td>
            <td>{segment.mediaSizeBytes === undefined ? notReported : formatSegmentSize(segment.mediaSizeBytes, language)}</td>
            <td>{segment.failureReason === undefined ? notReported : segment.failureReason}</td>
            <td>{segment.availableAt === undefined ? notReported : formatSegmentTime(segment.availableAt, language)}</td>
            <td>{segment.inUse ? t('inUse') : t('notInUse')}</td>
          </tr>
        ))}
      </tbody>
    </Box>
  );
}
