import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { DimensionSetDocument } from '../../../platform/models/agentCatalog';
import { Field } from './ViewParts';

export function DimensionSetView({ document }: { document: DimensionSetDocument }) {
  const { t } = useTranslation('agentCatalog');

  return (
    <Box component="dl" sx={{ m: 0 }}>
      <Field label={t('fields.dimensions')}>
        {document.dimensions.length === 0 ? (
          t('empty.dimensions')
        ) : (
          <Box component="ul" sx={{ m: 0, pl: 2 }}>
            {document.dimensions.map((dimension) => (
              <li key={dimension.id}>
                <Typography component="span" sx={{ fontWeight: 'bold' }}>
                  {dimension.id}
                </Typography>
                <Box component="dl" sx={{ m: 0, pl: 2 }}>
                  <Field label={t('fields.meaning')}>{dimension.meaning}</Field>
                  <Field label={t('fields.acceptedEvidence')}>
                    <Box component="ul" sx={{ m: 0, pl: 2 }}>
                      {dimension.acceptedEvidence.map((tool) => (
                        <li key={tool}>{tool}</li>
                      ))}
                    </Box>
                  </Field>
                </Box>
              </li>
            ))}
          </Box>
        )}
      </Field>
    </Box>
  );
}
