import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { PlatformFailure } from '../../platform/failure';
import { appendSpan, validateSpan, type SpanDraft, type SpanField } from './mappingDraft';
import { formatTime } from './mappingTime';

interface TimelineMappingEditorProps {
  spans: SpanDraft[];
  onChange: (spans: SpanDraft[]) => void;
  /** The recording's duration from the local file; absent when it could not be read. */
  durationSeconds?: number;
  /** Shows client-side problems once the member tried to submit. */
  showErrors?: boolean;
  /** The platform's refusal; span-specific errors are shown next to their span. */
  failure?: PlatformFailure;
  disabled?: boolean;
}

const FIELDS: readonly SpanField[] = ['mediaStart', 'mediaEnd', 'matchStart'];
const SPAN_INDEX = /spans\[(\d+)\]/;

/** The index of the span a platform field error refers to, or undefined for the mapping as a whole. */
function spanIndexOf(field: string): number | undefined {
  const match = SPAN_INDEX.exec(field);
  return match ? Number(match[1]) : undefined;
}

/** Edits the spans of a timeline mapping: `mm:ss` or `hh:mm:ss` inputs, with add and remove (FR-018). */
export function TimelineMappingEditor({ spans, onChange, durationSeconds, showErrors, failure, disabled }: TimelineMappingEditorProps) {
  const { t } = useTranslation('uploads');
  const platformErrors = failure?.kind === 'validation' ? failure.fieldErrors.filter((e) => e.field.startsWith('timelineMapping')) : [];
  const generalRefused = platformErrors.some((e) => spanIndexOf(e.field) === undefined);

  const update = (index: number, field: SpanField, value: string) =>
    onChange(spans.map((s, i) => (i === index ? { ...s, [field]: value } : s)));

  return (
    <Stack component="section" aria-label={t('mapping.title')} spacing={2}>
      <Typography variant="h3" component="h3">
        {t('mapping.title')}
      </Typography>
      <Typography variant="body2">{t('mapping.intro')}</Typography>
      <Typography variant="body2">
        {durationSeconds === undefined ? t('mapping.noDuration') : t('mapping.durationHint', { duration: formatTime(durationSeconds) })}
      </Typography>
      {spans.map((span, index) => {
        const errors = showErrors ? validateSpan(span) : {};
        const refused = platformErrors.some((e) => spanIndexOf(e.field) === index);
        const number = index + 1;
        return (
          <Stack key={index} component="fieldset" direction="row" spacing={2} alignItems="flex-start" flexWrap="wrap" sx={{ border: 0, p: 0, m: 0 }}>
            <legend>{t('mapping.spanLabel', { number })}</legend>
            {FIELDS.map((field) => {
              const error = errors[field];
              return (
                <TextField
                  key={field}
                  size="small"
                  label={`${t(`mapping.${field}`)} (${t('mapping.spanLabel', { number })})`}
                  value={span[field]}
                  disabled={disabled}
                  onChange={(e) => update(index, field, e.target.value)}
                  error={Boolean(error)}
                  helperText={error ? t(`mapping.${error}`) : undefined}
                  slotProps={{ htmlInput: { inputMode: 'numeric', autoComplete: 'off' } }}
                />
              );
            })}
            {refused && (
              <Typography role="alert" color="error" variant="body2" sx={{ width: '100%' }}>
                {t('mapping.spanRefused')}
              </Typography>
            )}
            {spans.length > 1 && (
              <Button disabled={disabled} onClick={() => onChange(spans.filter((_, i) => i !== index))}>
                {t('mapping.removeSpan', { number })}
              </Button>
            )}
          </Stack>
        );
      })}
      {generalRefused && (
        <Typography role="alert" color="error" variant="body2">
          {t('mapping.mappingRefused')}
        </Typography>
      )}
      <div>
        <Button disabled={disabled} onClick={() => onChange(appendSpan(spans, durationSeconds))}>
          {t('mapping.addSpan')}
        </Button>
      </div>
    </Stack>
  );
}
