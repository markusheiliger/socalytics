import Box from '@mui/material/Box';

/** Platform text shown verbatim: pre-formatted, bounded, scrollable, and rendered only as a React text node (FR-021). */
export function CatalogText({ text, label, maxHeight = 320 }: { text: string; label: string; maxHeight?: number | string }) {
  return (
    <Box
      component="pre"
      tabIndex={0}
      aria-label={label}
      sx={{ m: 0, p: 1, maxHeight, overflow: 'auto', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', fontFamily: 'monospace', border: 1, borderColor: 'divider', borderRadius: 1 }}
    >
      {text}
    </Box>
  );
}
