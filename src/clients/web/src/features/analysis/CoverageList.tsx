import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { CoverageWindow } from '../../platform/models/analysis';
import { formatMatchRange } from './matchClock';

interface CoverageListProps {
  windows: CoverageWindow[];
}

/** Coverage windows with translated states; a state the platform adds later shows its raw value (FR-011, FR-016). */
export function CoverageList({ windows }: CoverageListProps) {
  const { t } = useTranslation('analysis');

  return (
    <section aria-label={t('coverage.title')}>
      <Typography variant="h4" component="h4" sx={{ mt: 2 }}>
        {t('coverage.title')}
      </Typography>
      {windows.length === 0 ? (
        <Typography>{t('coverage.empty')}</Typography>
      ) : (
        <ul aria-label={t('coverage.listLabel')}>
          {windows.map((window) => {
            const range = formatMatchRange(window.startMs, window.endMs);
            const state = t(`coverage.stateName.${window.state.toLowerCase()}`, { defaultValue: window.state });
            return (
              <li key={`${window.startMs}-${window.endMs}-${window.state}`} aria-label={`${range.label}: ${state}`}>
                {t('coverage.windowValue', { range: range.text, state })}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
