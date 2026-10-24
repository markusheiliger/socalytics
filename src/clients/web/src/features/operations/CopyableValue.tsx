import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

type CopyState = 'idle' | 'copied' | 'failed';

/** A full identifier as selectable plain text with a copy control. */
export function CopyableValue({ value }: { value: string }) {
  const { t } = useTranslation('operations');
  const [copy, setCopy] = useState<CopyState>('idle');

  const copyValue = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopy('copied');
    } catch {
      setCopy('failed');
    }
  };

  return (
    <Box component="span" sx={{ display: 'inline-flex', flexWrap: 'wrap', alignItems: 'center', columnGap: 1 }}>
      <Box component="code" sx={{ fontFamily: 'monospace', userSelect: 'all', overflowWrap: 'anywhere' }}>
        {value}
      </Box>
      <Button size="small" aria-label={t('common.copyLabel', { value })} onClick={() => void copyValue()}>
        {t('common.copy')}
      </Button>
      <Box component="span" role="status">
        {copy === 'copied' ? t('common.copied') : ''}
      </Box>
      {copy === 'failed' && (
        <Box component="span" role="alert">
          {t('common.copyFailed')}
        </Box>
      )}
    </Box>
  );
}
