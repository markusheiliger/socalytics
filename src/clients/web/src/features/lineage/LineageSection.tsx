import Typography from '@mui/material/Typography';
import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { createRecordingLineageAdapter } from '../../platform/adapters/recordingLineageAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { formatKickoff } from '../matches/MatchForm';
import { FailureAlert } from '../shared/FailureAlert';
import { formatBytes } from '../uploads/formatBytes';
import { formatTime } from '../uploads/mappingTime';
import { LINEAGE_KEY } from './AnalysisRunStates';

/** Read-only lineage of a match: recording versions, timeline mappings, and finalized set versions (FR-021, FR-022). */
export function LineageSection({ matchId }: { matchId: string }) {
  const { t } = useTranslation('lineage');
  const { platform } = useSession();
  const adapter = useMemo(() => createRecordingLineageAdapter(platform), [platform]);
  const lineage = useQuery({ queryKey: LINEAGE_KEY(matchId), queryFn: () => adapter.getLineage(matchId), retry: false, refetchOnWindowFocus: false });
  const data = lineage.data;
  const nameOf = (versionId: string) => data?.recordingVersions.find((v) => v.id === versionId)?.recording.displayName ?? versionId;
  const sets = useMemo(() => [...(data?.recordingSetVersions ?? [])].sort((a, b) => b.finalizedAt.getTime() - a.finalizedAt.getTime()), [data]);

  return (
    <section aria-label={t('review.title')}>
      <Typography variant="h3" component="h3" sx={{ mt: 3 }}>
        {t('review.title')}
      </Typography>
      <Typography variant="body2">{t('review.intro')}</Typography>
      {lineage.error && <FailureAlert failure={toPlatformFailure(lineage.error)} onReload={() => void lineage.refetch()} />}
      {data && (
        <>
          <Typography variant="h4" component="h4" sx={{ mt: 2 }}>
            {t('review.versionsTitle')}
          </Typography>
          {data.recordingVersions.length === 0 && <Typography>{t('review.versionsEmpty')}</Typography>}
          {data.recordingVersions.length > 0 && (
            <ul aria-label={t('review.versionsLabel')}>
              {data.recordingVersions.map((v) => (
                <li key={v.id}>
                  <Link to={`/matches/${matchId}/recordings/${v.id}`} aria-label={t('review.details', { name: v.recording.displayName })}>
                    {v.recording.displayName}
                  </Link>{' '}
                  {t('review.version', {
                    name: v.recording.displayName,
                    size: formatBytes(v.totalSizeBytes),
                    digest: v.contentDigest,
                    time: formatKickoff(v.createdAt),
                  })}
                </li>
              ))}
            </ul>
          )}
          <Typography variant="h4" component="h4" sx={{ mt: 2 }}>
            {t('review.mappingsTitle')}
          </Typography>
          {data.timelineMappings.length === 0 && <Typography>{t('review.mappingsEmpty')}</Typography>}
          {data.timelineMappings.length > 0 && (
            <ul aria-label={t('review.mappingsLabel')}>
              {data.timelineMappings.map((m) => (
                <li key={m.id}>
                  {t('review.mapping', { name: nameOf(m.recordingVersionId), digest: m.digest, time: formatKickoff(m.createdAt) })}
                  <ul aria-label={t('review.spansLabel')}>
                    {m.spans.map((s, i) => (
                      <li key={i}>
                        {t('review.span', {
                          start: formatTime(s.mediaStartSeconds),
                          end: formatTime(s.mediaEndSeconds),
                          match: formatTime(s.matchStartSeconds),
                        })}
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>
          )}
          <Typography variant="h4" component="h4" sx={{ mt: 2 }}>
            {t('review.setsTitle')}
          </Typography>
          {sets.length === 0 && <Typography>{t('review.setsEmpty')}</Typography>}
          {sets.length > 0 && (
            <ul aria-label={t('review.setsLabel')}>
              {sets.map((set) => (
                <li key={set.id}>
                  {t('review.set', { time: formatKickoff(set.finalizedAt), count: set.members.length })}
                  <ol aria-label={t('review.membersLabel')}>
                    {[...set.members]
                      .sort((a, b) => a.position - b.position)
                      .map((m) => (
                        <li key={m.position}>
                          {t('review.member', {
                            position: m.position + 1,
                            name: nameOf(m.recordingVersionId),
                            content: m.recordingContentDigest,
                            mapping: m.timelineMappingDigest,
                          })}
                        </li>
                      ))}
                  </ol>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}
