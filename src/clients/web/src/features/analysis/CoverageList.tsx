import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { CoverageWindow } from '../../platform/models/analysis';
import { formatMatchRange } from './matchClock';

interface CoverageListProps {
  windows: CoverageWindow[];
  /** Replaces the default heading, for example when only some windows are listed. */
  title?: string;
}

/** Coverage windows with translated states; a state the platform adds later shows its raw value (FR-011, FR-016). */
export function CoverageList({ windows, title }: CoverageListProps) {
  const { t } = useTranslation('analysis');

  return (
    <section aria-label={title ?? t('coverage.title')}>
      <Typography variant="h4" component="h4" sx={{ mt: 2 }}>
        {title ?? t('coverage.title')}
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
