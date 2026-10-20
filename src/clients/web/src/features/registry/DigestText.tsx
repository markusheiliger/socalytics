import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { shortDigest } from './digest';

type CopyState = 'idle' | 'copied' | 'failed';

/** A digest as shortened text, with the full value revealed as selectable text and a copy button; platform text is rendered as text only (FR-010, FR-021). */
export function DigestText({ value }: { value: string }) {
  const { t } = useTranslation('registry');
  const [expanded, setExpanded] = useState(false);
  const [copy, setCopy] = useState<CopyState>('idle');
  const short = shortDigest(value);

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
      <Box component="code" sx={{ fontFamily: 'monospace' }}>
        {short}
      </Box>
      <Button size="small" aria-expanded={expanded} onClick={() => setExpanded((open) => !open)}>
        {expanded ? t('digest.hideFull') : t('digest.showFull')}
      </Button>
      <Button size="small" onClick={() => void copyValue()}>
        {t('digest.copy')}
      </Button>
      {expanded && (
        <Box component="code" aria-label={t('digest.fullLabel')} sx={{ fontFamily: 'monospace', userSelect: 'all', overflowWrap: 'anywhere', flexBasis: '100%' }}>
          {value}
        </Box>
      )}
      <Box component="span" role="status">
        {copy === 'copied' ? t('digest.copied') : ''}
      </Box>
      {copy === 'failed' && (
        <Box component="span" role="alert" sx={{ flexBasis: '100%' }}>
          {t('digest.copyFailed')}
        </Box>
      )}
    </Box>
  );
}
