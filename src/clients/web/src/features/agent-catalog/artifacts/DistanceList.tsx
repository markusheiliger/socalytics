import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { Distance } from '../../../platform/models/agentCatalog';

/** The distances exactly as the platform reports them; every distance that requires a differentiation review is marked. */
export function DistanceList({ distances }: { distances: readonly Distance[] }) {
  const { t } = useTranslation('agentCatalog');
  if (distances.length === 0) return <Typography>{t('distances.empty')}</Typography>;
  return (
    <Box component="ul" aria-label={t('distances.title')} sx={{ m: 0, pl: 2 }}>
      {distances.map((d) => (
        <li key={`${d.agentId}/${d.agentVersion}/${d.profileId}/${d.profileVersion}`}>
          {t('distances.entry', {
            agentId: d.agentId,
            agentVersion: d.agentVersion,
            profileId: d.profileId,
            profileVersion: d.profileVersion,
            distance: d.distance,
            interpolation: { escapeValue: false },
          })}
          {d.reviewRequired && (
            <Box component="strong" sx={{ ml: 1 }}>
              {t('distances.reviewRequired')}
            </Box>
          )}
        </li>
      ))}
    </Box>
  );
}
