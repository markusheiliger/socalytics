import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import LinearProgress from '@mui/material/LinearProgress';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { UploadJob } from '../../uploads/uploadCoordinator';
import type { UploadRepository } from '../../uploads/uploadRepository';
import { formatBytes } from './formatBytes';

interface PendingUploadsProps {
  repository: UploadRepository;
  jobs: readonly UploadJob[];
  canUpload: boolean;
}

/** Jobs the member can only recover or abandon: remembered by this browser, expired, refused, or running in another tab. */
export function isRecoverable(job: UploadJob): boolean {
  return (
    job.state === 'needsFile' ||
    job.state === 'expired' ||
    job.state === 'notPermitted' ||
    (job.state === 'refused' && job.reason === 'fileMismatch') ||
    (job.state === 'paused' && job.reason === 'runningElsewhere')
  );
}

function progressPercent(job: UploadJob): number {
  const count = job.summary?.partCount ?? 0;
  if (count > 0 && job.bytesConfirmed === 0) return Math.min(100, (job.confirmedParts.length / count) * 100);
  return Math.min(100, (job.bytesConfirmed / Math.max(job.fileSizeBytes, 1)) * 100);
}

/** This member's remembered uploads of the match with resume, abandon, and start-again actions (FR-014, FR-015, FR-017). */
export function PendingUploads({ repository, jobs, canUpload }: PendingUploadsProps) {
  const { t } = useTranslation('uploads');
  const pending = jobs.filter(isRecoverable);
  if (pending.length === 0) return null;
  return (
    <Stack component="section" aria-label={t('pending.title')} spacing={2} sx={{ mt: 3 }}>
      <Typography variant="h3" component="h3">
        {t('pending.title')}
      </Typography>
      <Stack component="ul" aria-label={t('pending.listLabel')} spacing={2} sx={{ listStyle: 'none', p: 0, m: 0 }}>
        {pending.map((job) => (
          <PendingItem key={job.localId} repository={repository} job={job} canUpload={canUpload} />
        ))}
      </Stack>
    </Stack>
  );
}

function FilePicker({ label, onFile }: { label: string; onFile: (file: File) => void }) {
  return (
    <Button size="small" component="label" variant="contained">
      {label}
      <input
        type="file"
        accept="video/*"
        hidden
        onChange={(event) => {
          const file = event.target.files?.[0];
          if (file) onFile(file);
          event.target.value = '';
        }}
      />
    </Button>
  );
}

function PendingItem({ repository, job, canUpload }: { repository: UploadRepository; job: UploadJob; canUpload: boolean }) {
  const { t } = useTranslation('uploads');
  const name = job.displayName || job.fileName;
  const expiry = job.sessionExpiresAt ? new Date(job.sessionExpiresAt).toLocaleString() : undefined;
  const resumable = canUpload && (job.state === 'needsFile' || job.state === 'refused');
  const elsewhere = job.state === 'paused';
  const expired = job.state === 'expired';
  const notPermitted = job.state === 'notPermitted';

  const startAgain = (file: File) => {
    repository.addFiles(job.matchId, job.teamId, [{ file, displayName: name, description: job.description }]);
    repository.acknowledgeExpiry(job.localId);
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
      {!expired && !notPermitted && (
        <LinearProgress variant="determinate" value={progressPercent(job)} aria-label={t('pending.progress', { file: name })} />
      )}
      {expiry && !expired && <Typography variant="body2">{t('pending.expiresAt', { date: expiry })}</Typography>}
      {expired && <Alert severity="warning">{t('pending.expired')}</Alert>}
      {notPermitted && <Alert severity="error">{t('pending.notPermitted')}</Alert>}
      {!expired && <Typography variant="body2">{t('pending.abandonNote')}</Typography>}
      <Stack direction="row" spacing={1}>
        {resumable && <FilePicker label={t('queue.reselect', { file: name })} onFile={(file) => void repository.reselectFile(job.localId, file)} />}
        {elsewhere && canUpload && (
          <Button size="small" variant="contained" onClick={() => repository.retry(job.localId)}>
            {t('queue.retry', { file: name })}
          </Button>
        )}
        {expired && canUpload && <FilePicker label={t('pending.startAgain', { file: name })} onFile={startAgain} />}
        {expired ? (
          <Button size="small" onClick={() => repository.acknowledgeExpiry(job.localId)}>
            {t('queue.dismiss', { file: name })}
          </Button>
        ) : (
          <Button size="small" onClick={() => repository.abandon(job.localId)}>
            {t('queue.abandon', { file: name })}
          </Button>
        )}
      </Stack>
    </Stack>
  );
}
