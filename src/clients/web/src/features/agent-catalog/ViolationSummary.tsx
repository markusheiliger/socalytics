import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Box from '@mui/material/Box';
import { useTranslation } from 'react-i18next';
import type { CatalogViolation } from '../../platform/models/agentCatalog';
import { violationText } from './outcomeText';
import { SUMMARY_LOCATION } from './violationLocation';

/** Violations without a known location, in words above a form and announced to assistive technology (FR-012). */
export function ViolationSummary({ violations }: { violations: CatalogViolation[] }) {
  const { t } = useTranslation('agentCatalog');
  const unplaced = violations.filter((violation) => violation.location === SUMMARY_LOCATION);
  if (unplaced.length === 0) return null;

  return (
    <Alert severity="error" role="alert" sx={{ mb: 2 }}>
      <AlertTitle>{t('violations.summaryTitle')}</AlertTitle>
      <Box component="ul" sx={{ m: 0, pl: 2 }}>
        {unplaced.map((violation, index) => {
          const { key, values } = violationText(violation.code, violation.detail);
          return (
            <li key={index}>
              {t(key, values)}
              {violation.pointer ? ` ${t('violations.location', { pointer: violation.pointer })}` : ''}
            </li>
          );
        })}
      </Box>
    </Alert>
  );
}
