import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

/** Label and value of one document field in a read-only view. */
export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ mb: 2 }}>
      <Typography component="dt" variant="subtitle2">
        {label}
      </Typography>
      <Box component="dd" sx={{ m: 0 }}>
        {children}
      </Box>
    </Box>
  );
}

/** Translates `<group>.<value>` of the agentCatalog namespace; a value the Web UI does not know is shown raw (FR-022). */
export function useEnumText(group: string) {
  const { t, i18n } = useTranslation('agentCatalog');
  return (value: string) =>
    Object.prototype.hasOwnProperty.call(i18n.getResourceBundle(i18n.language, 'agentCatalog')?.[group] ?? {}, value)
      ? t(`${group}.${value}`)
      : t('common.unknown', { value });
}

/** A list of values, each in words or raw, or the given empty text. */
export function ValueList({ values, text, empty }: { values: string[]; text: (value: string) => string; empty: string }) {
  if (values.length === 0) return <Typography color="text.secondary">{empty}</Typography>;
  return (
    <Box component="ul" sx={{ m: 0, pl: 2 }}>
      {values.map((value, index) => (
        <li key={`${value}-${index}`}>{text(value)}</li>
      ))}
    </Box>
  );
}
