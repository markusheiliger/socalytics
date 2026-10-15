import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import LinearProgress from '@mui/material/LinearProgress';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { finalStates, type UploadJobState } from '../../uploads/uploadJob';
import type { UploadJob } from '../../uploads/uploadCoordinator';
import type { UploadRepository } from '../../uploads/uploadRepository';
import { formatBytes } from './formatBytes';
import { createDefaultDraft, toTimelineSpans, type SpanDraft } from './mappingDraft';
import { isRecoverable } from './PendingUploads';
import { TimelineMappingEditor } from './TimelineMappingEditor';

interface UploadListProps {
  repository: UploadRepository;
  jobs: readonly UploadJob[];
  /** `contentDigest` values of the match's recording versions, for the same-file warning. */
  knownDigests: ReadonlySet<string>;
  canUpload: boolean;
}

const EDITABLE: readonly UploadJobState[] = ['preparing', 'ready', 'starting', 'queued', 'transferring', 'paused', 'awaitingMapping', 'refused'];
const REMOVABLE: readonly UploadJobState[] = ['preparing', 'ready', 'starting'];

/** The member's upload queue for one match: progress, states, retry, removal, mapping, and completion (FR-008–FR-011). */
export function UploadList({ repository, jobs, knownDigests, canUpload }: UploadListProps) {
  const { t } = useTranslation('uploads');
  const visible = jobs.filter((job) => !isRecoverable(job) && (job.state !== 'cancelled' || job.failureKind));
  if (visible.length === 0) return null;
  const isDuplicate = (job: UploadJob) => {
    const digest = job.summary?.contentDigest;
    if (!digest || finalStates.includes(job.state)) return false;
    return knownDigests.has(digest) || jobs.some((other) => other.localId !== job.localId && other.summary?.contentDigest === digest && !['cancelled', 'abandoned', 'expired', 'failed'].includes(other.state));
  };
  return (
    <Stack component="section" aria-label={t('queue.title')} spacing={2} sx={{ mt: 3 }}>
      <Typography variant="h3" component="h3">
        {t('queue.title')}
      </Typography>
      <Stack component="ul" aria-label={t('queue.listLabel')} spacing={2} sx={{ listStyle: 'none', p: 0, m: 0 }}>
        {visible.map((job) => (
          <UploadItem key={job.localId} repository={repository} job={job} duplicate={isDuplicate(job)} canUpload={canUpload} />
        ))}
      </Stack>
    </Stack>
  );
}

function UploadItem({ repository, job, duplicate, canUpload }: { repository: UploadRepository; job: UploadJob; duplicate: boolean; canUpload: boolean }) {
  const { t } = useTranslation('uploads');
  const [draft, setDraft] = useState<SpanDraft[]>(() => createDefaultDraft(job.durationSeconds));
  const [showErrors, setShowErrors] = useState(false);
  const name = job.displayName || job.fileName;
  const size = Math.max(job.fileSizeBytes, 1);
  const preparing = job.state === 'preparing';
  const value = preparing ? (job.bytesHashed / size) * 100 : ((job.bytesConfirmed + job.bytesInFlight) / size) * 100;
  const transferred = job.transferred || job.state === 'completed';
  const showProgress = preparing || ['starting', 'queued', 'transferring', 'paused', 'awaitingMapping', 'completing'].includes(job.state);
  const editable = canUpload && EDITABLE.includes(job.state) && !job.mapping;
  const canRetry = job.state === 'paused' || (job.state === 'refused' && job.reason !== 'fileMismatch');
  const needsFile = job.state === 'needsFile' || (job.state === 'refused' && job.reason === 'fileMismatch');
  const failureKey = job.failureKind === 'tooLarge' || job.failureKind === 'unsupportedType' || job.failureKind === 'validation' ? job.failureKind : 'other';

  const saveMapping = () => {
    const result = toTimelineSpans(draft);
    setShowErrors(true);
    if (result.ok) repository.setMapping(job.localId, result.spans);
  };

  return (
    <Stack component="li" spacing={1} sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2 }}>
      <Typography variant="subtitle1" component="p">
        {t('fileLabel', { name, size: formatBytes(job.fileSizeBytes) })}
      </Typography>
      <Typography role="status">
        {t(`state.${job.state}`)}
        {job.reason ? ` – ${t(`reason.${job.reason}`)}` : ''}
      </Typography>
      {showProgress && (
        <LinearProgress
          variant="determinate"
          value={Math.min(100, Math.max(0, transferred ? 100 : value))}
          aria-label={t(preparing ? 'queue.progressPreparing' : 'queue.progressTransfer', { file: name })}
        />
      )}
      {duplicate && <Alert severity="warning">{t('queue.duplicate')}</Alert>}
      {job.state === 'cancelled' && job.failureKind && <Alert severity="error" role="alert">{t(`failure.${failureKey}`)}</Alert>}
      {job.mapping && job.state !== 'completed' && <Typography variant="body2">{t('queue.mappingSaved')}</Typography>}
      {job.state === 'awaitingMapping' && <Typography variant="body2">{t('queue.mappingNeeded')}</Typography>}
      {editable && (
        <>
          <TimelineMappingEditor spans={draft} onChange={setDraft} durationSeconds={job.durationSeconds} showErrors={showErrors} />
          <Button variant="outlined" onClick={saveMapping}>
            {t('queue.saveMapping', { file: name })}
          </Button>
        </>
      )}
      <Stack direction="row" spacing={1}>
        {canRetry && (
          <Button size="small" variant="contained" onClick={() => repository.retry(job.localId)}>
            {t('queue.retry', { file: name })}
          </Button>
        )}
        {needsFile && (
          <Button size="small" component="label" variant="contained">
            {t('queue.reselect', { file: name })}
            <input
              type="file"
              accept="video/*"
              hidden
              onChange={(event) => {
                const file = event.target.files?.[0];
                if (file) void repository.reselectFile(job.localId, file);
                event.target.value = '';
              }}
            />
          </Button>
        )}
        {REMOVABLE.includes(job.state) && !job.uploadSessionId && (
          <Button size="small" onClick={() => repository.removeFromQueue(job.localId)}>
            {t('queue.remove', { file: name })}
          </Button>
        )}
        {!finalStates.includes(job.state) && !(REMOVABLE.includes(job.state) && !job.uploadSessionId) && (
          <Button size="small" onClick={() => repository.abandon(job.localId)}>
            {t('queue.abandon', { file: name })}
          </Button>
        )}
        {job.state === 'expired' && (
          <Button size="small" onClick={() => repository.acknowledgeExpiry(job.localId)}>
            {t('queue.dismiss', { file: name })}
          </Button>
        )}
      </Stack>
    </Stack>
  );
}
