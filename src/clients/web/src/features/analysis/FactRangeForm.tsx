import Button from '@mui/material/Button';
import TextField from '@mui/material/TextField';
import { useEffect, useId, useState } from 'react';
import type { FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { formatMatchClock, validateRange } from './matchClock';
import type { RangeError } from './matchClock';

export type FactRange = { fromMs?: number; toMs?: number };

const clockText = (ms?: number) => (ms === undefined ? '' : formatMatchClock(ms));

/** Match-clock range input; validates before reporting the new range, and Clear reports an empty range (FR-010, FR-019). */
export function FactRangeForm({ range, onChange }: { range: FactRange; onChange: (range: FactRange) => void }) {
  const { t } = useTranslation('analysis');
  const id = useId();
  const [from, setFrom] = useState(clockText(range.fromMs));
  const [to, setTo] = useState(clockText(range.toMs));
  const [error, setError] = useState<{ error: RangeError; side: 'from' | 'to' | 'range' } | undefined>();

  useEffect(() => {
    setFrom(clockText(range.fromMs));
    setTo(clockText(range.toMs));
    setError(undefined);
  }, [range.fromMs, range.toMs]);

  const apply = (event: FormEvent) => {
    event.preventDefault();
    const result = validateRange(from, to);
    if (!result.ok) {
      setError({ error: result.error, side: result.side });
      return;
    }
    setError(undefined);
    onChange(result.range);
  };

  const clear = () => {
    setFrom('');
    setTo('');
    setError(undefined);
    onChange({});
  };

  const message = error ? t(`range.${error.error}`) : undefined;
  const fromError = error?.side === 'from' ? message : undefined;
  // An end that is not after the start is reported at the end field.
  const toError = error?.side === 'to' || error?.side === 'range' ? message : undefined;

  return (
    <form onSubmit={apply} noValidate aria-label={t('range.title')}>
      <TextField
        id={`${id}-from`}
        label={t('range.from')}
        value={from}
        onChange={(event) => setFrom(event.target.value)}
        error={Boolean(fromError)}
        helperText={fromError ?? t('range.hint')}
        size="small"
        slotProps={{ htmlInput: { inputMode: 'text', autoComplete: 'off' } }}
      />
      <TextField
        id={`${id}-to`}
        label={t('range.to')}
        value={to}
        onChange={(event) => setTo(event.target.value)}
        error={Boolean(toError)}
        helperText={toError ?? t('range.hint')}
        size="small"
        sx={{ ml: 1 }}
        slotProps={{ htmlInput: { inputMode: 'text', autoComplete: 'off' } }}
      />
      <Button type="submit" variant="contained" size="small" sx={{ ml: 1 }}>
        {t('range.apply')}
      </Button>
      <Button type="button" variant="outlined" size="small" sx={{ ml: 1 }} onClick={clear}>
        {t('range.clear')}
      </Button>
    </form>
  );
}
