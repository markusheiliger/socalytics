import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { Advice } from '../../platform/models/agents';
import { agentName } from './turnPresentation';

/** Accepted advice with attribution, the AI disclosure, and its evidence references (FR-012, FR-013, FR-022). */
export function AdviceCard({ advice, agentId }: { advice: Advice; agentId: string }) {
  const { t } = useTranslation('triage');
  const name = agentName(agentId, t);

  return (
    <Box component="section" aria-label={t('advice.title', { name })} sx={{ mt: 1 }}>
      <Typography variant="subtitle1" component="h4">
        {t('advice.title', { name })}
      </Typography>
      <Typography variant="body2">{t('advice.attribution', { name, agentId })}</Typography>
      <Alert severity="info" icon={false} sx={{ my: 1 }}>
        {t('advice.disclosure')}
      </Alert>
      <Typography
        component="p"
        aria-label={t('advice.text')}
        sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', m: 0 }}
      >
        {advice.text}
      </Typography>
      <Typography variant="subtitle2" component="h5" sx={{ mt: 1 }}>
        {t('advice.evidence')}
      </Typography>
      {advice.evidenceReferences.length === 0 ? (
        <Typography variant="body2">{t('advice.noEvidence')}</Typography>
      ) : (
        <ul aria-label={t('advice.evidence')}>
          {advice.evidenceReferences.map((evidence, index) => (
            <li key={`${index}:${evidence.reference}`} style={{ overflowWrap: 'anywhere' }}>
              {evidence.kind !== undefined && evidence.kind !== '' ? `${evidence.kind}: ` : ''}
              {t('advice.evidenceItem', { reference: evidence.reference, interpolation: { escapeValue: false } })}
            </li>
          ))}
        </ul>
      )}
    </Box>
  );
}
