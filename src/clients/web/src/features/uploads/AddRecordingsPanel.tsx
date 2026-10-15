import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useRef, useState, type DragEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { readVideoDuration } from '../../uploads/duration';
import { isVideoFile } from '../../uploads/uploadCoordinator';
import type { UploadRepository } from '../../uploads/uploadRepository';
import { formatBytes } from './formatBytes';

interface StagedFile {
  id: number;
  file: File;
  displayName: string;
  description: string;
}

interface AddRecordingsPanelProps {
  repository: UploadRepository;
  matchId: string;
  teamId: string;
}

/** Picks video files for a match: a file picker and drop zone, a name and description per file, and the refusal of other files (FR-005). */
export function AddRecordingsPanel({ repository, matchId, teamId }: AddRecordingsPanelProps) {
  const { t } = useTranslation('uploads');
  const [staged, setStaged] = useState<StagedFile[]>([]);
  const [refused, setRefused] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const nextId = useRef(0);
  const input = useRef<HTMLInputElement>(null);

  const stage = (files: File[]) => {
    const accepted = files.filter(isVideoFile);
    setRefused(files.filter((f) => !isVideoFile(f)).map((f) => f.name));
    setStaged((current) => [
      ...current,
      ...accepted.map((file) => ({ id: nextId.current++, file, displayName: file.name, description: '' })),
    ]);
  };

  const update = (id: number, patch: Partial<StagedFile>) =>
    setStaged((current) => current.map((s) => (s.id === id ? { ...s, ...patch } : s)));

  const onDrop = (event: DragEvent) => {
    event.preventDefault();
    stage([...event.dataTransfer.files]);
  };

  const submit = async () => {
    setBusy(true);
    try {
      const inputs = [];
      for (const s of staged) {
        inputs.push({
          file: s.file,
          displayName: s.displayName.trim() || s.file.name,
          description: s.description.trim() || undefined,
          durationSeconds: await readVideoDuration(s.file),
        });
      }
      const result = repository.addFiles(matchId, teamId, inputs);
      setRefused(result.refused.map((r) => r.file.name));
      setStaged([]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Stack component="section" aria-label={t('add.title')} spacing={2} sx={{ mt: 3 }}>
      <Typography variant="h3" component="h3">
        {t('add.title')}
      </Typography>
      <Box
        onDragOver={(event) => event.preventDefault()}
        onDrop={onDrop}
        sx={{ border: '2px dashed', borderColor: 'divider', borderRadius: 1, p: 2 }}
      >
        <Typography>{t('add.dropZone')}</Typography>
        <input
          ref={input}
          type="file"
          accept="video/*"
          multiple
          hidden
          aria-label={t('add.fileInput')}
          onChange={(event) => {
            stage([...(event.target.files ?? [])]);
            event.target.value = '';
          }}
        />
        <Button variant="outlined" sx={{ mt: 1 }} onClick={() => input.current?.click()}>
          {t('add.choose')}
        </Button>
      </Box>
      {!repository.journalAvailable() && <Alert severity="warning">{t('add.journalUnavailable')}</Alert>}
      {refused.map((name) => (
        <Alert key={name} severity="error" role="alert">
          {t('add.notVideo', { file: name })}
        </Alert>
      ))}
      {staged.length > 0 && (
        <Stack component="ul" aria-label={t('add.stagedList')} spacing={2} sx={{ listStyle: 'none', p: 0, m: 0 }}>
          {staged.map((s) => (
            <Stack component="li" key={s.id} spacing={1}>
              <Typography variant="body2">
                {t('fileLabel', { name: s.file.name, size: formatBytes(s.file.size) })}
              </Typography>
              <TextField
                label={t('add.displayName', { file: s.file.name })}
                value={s.displayName}
                onChange={(event) => update(s.id, { displayName: event.target.value })}
                size="small"
              />
              <TextField
                label={t('add.description', { file: s.file.name })}
                value={s.description}
                onChange={(event) => update(s.id, { description: event.target.value })}
                size="small"
              />
              <Button size="small" onClick={() => setStaged((current) => current.filter((c) => c.id !== s.id))}>
                {t('add.removeStaged', { file: s.file.name })}
              </Button>
            </Stack>
          ))}
          <Button variant="contained" disabled={busy} onClick={() => void submit()}>
            {busy ? t('add.reading') : t('add.submit')}
          </Button>
        </Stack>
      )}
    </Stack>
  );
}
