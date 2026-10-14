import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import LinearProgress from '@mui/material/LinearProgress';
import Link from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useId, useState, useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { Link as RouterLink } from 'react-router';
import { formatBytes } from '../features/uploads/formatBytes';
import type { UploadJob } from '../uploads/uploadCoordinator';
import { useUploads } from '../uploads/uploadContext';
import { shouldGuardLeave, useLeaveGuard } from '../uploads/useLeaveGuard';
import type { UploadJobState } from '../uploads/uploadJob';

const ACTIVE: readonly UploadJobState[] = ['preparing', 'starting', 'transferring', 'completing'];
const QUEUED: readonly UploadJobState[] = ['selected', 'ready', 'queued', 'awaitingMapping', 'paused'];
const NO_JOBS: readonly UploadJob[] = [];
const noop = () => () => undefined;

function progressBytes(job: UploadJob): number {
  if (job.state === 'preparing') return job.bytesHashed;
  if (job.transferred) return job.fileSizeBytes;
  return job.bytesConfirmed + job.bytesInFlight;
}

/** Number of active and queued uploads with overall progress on every route; opens a panel listing each upload. */
export function UploadIndicator() {
  const { t } = useTranslation('shell');
  const repository = useUploads();
  const jobs = useSyncExternalStore(
    repository ? (listener) => repository.subscribe(listener) : noop,
    () => repository?.getSnapshot() ?? NO_JOBS,
  );
  const [open, setOpen] = useState(false);
  const panelId = useId();
  useLeaveGuard(shouldGuardLeave(jobs));

  const pending = jobs.filter((job) => ACTIVE.includes(job.state) || QUEUED.includes(job.state));
  if (pending.length === 0) return null;
  const activeCount = pending.filter((job) => ACTIVE.includes(job.state)).length;
  const queuedCount = pending.length - activeCount;
  const total = pending.reduce((sum, job) => sum + Math.max(job.fileSizeBytes, 1), 0);
  const done = pending.reduce((sum, job) => sum + Math.min(progressBytes(job), Math.max(job.fileSizeBytes, 1)), 0);
  const percent = Math.round(Math.min(100, Math.max(0, (done / total) * 100)));

  return (
    <Box component="section" aria-label={t('uploads.label')} sx={{ mt: 1 }}>
      <Stack direction="row" spacing={2} alignItems="center">
        <Button
          size="small"
          variant="outlined"
          aria-expanded={open}
          aria-controls={panelId}
          onClick={() => setOpen((value) => !value)}
        >
          {t('uploads.summary', { active: activeCount, queued: queuedCount, percent })}
        </Button>
        <LinearProgress
          variant="determinate"
          value={percent}
          aria-label={t('uploads.overallProgress')}
          sx={{ flexGrow: 1, maxWidth: 240 }}
        />
      </Stack>
      {open && (
        <Box id={panelId} sx={{ mt: 1 }}>
          <Stack component="ul" aria-label={t('uploads.listLabel')} spacing={1} sx={{ listStyle: 'none', p: 0, m: 0 }}>
            {pending.map((job) => {
              const name = job.displayName || job.fileName;
              const size = Math.max(job.fileSizeBytes, 1);
              return (
                <Stack component="li" key={job.localId} spacing={0.5}>
                  <Typography variant="body2">
                    {t('uploads.item', { name, size: formatBytes(job.fileSizeBytes) })}
                    {' – '}
                    {t(`uploads.state.${job.state}`)}
                  </Typography>
                  <LinearProgress
                    variant="determinate"
                    value={Math.min(100, (progressBytes(job) / size) * 100)}
                    aria-label={t('uploads.itemProgress', { name })}
                  />
                  <Link component={RouterLink} to={`/matches/${job.matchId}`} variant="body2">
                    {t('uploads.openMatch', { name })}
                  </Link>
                </Stack>
              );
            })}
          </Stack>
        </Box>
      )}
    </Box>
  );
}
