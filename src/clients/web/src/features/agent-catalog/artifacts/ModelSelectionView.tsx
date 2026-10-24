import Box from '@mui/material/Box';
import { useTranslation } from 'react-i18next';
import type { ModelSelectionDocument } from '../../../platform/models/agentCatalog';
import { Field, useEnumText } from './ViewParts';

export function ModelSelectionView({ document }: { document: ModelSelectionDocument }) {
  const { t } = useTranslation('agentCatalog');
  const provider = useEnumText('providers');
  const effort = useEnumText('reasoningEfforts');
  const parameters = document.parameters ?? {};

  return (
    <Box component="dl" sx={{ m: 0 }}>
      <Field label={t('fields.provider')}>{provider(document.provider)}</Field>
      <Field label={t('fields.model')}>{document.model}</Field>
      <Field label={t('fields.deployment')}>{document.deployment}</Field>
      <Field label={t('fields.parameters')}>
        <Box component="dl" sx={{ m: 0, pl: 2 }}>
          {parameters.temperature !== undefined && <Field label={t('fields.temperature')}>{parameters.temperature}</Field>}
          {parameters.topP !== undefined && <Field label={t('fields.topP')}>{parameters.topP}</Field>}
          {parameters.maxOutputTokens !== undefined && <Field label={t('fields.maxOutputTokens')}>{parameters.maxOutputTokens}</Field>}
          {parameters.reasoningEffort !== undefined && <Field label={t('fields.reasoningEffort')}>{effort(parameters.reasoningEffort)}</Field>}
        </Box>
      </Field>
    </Box>
  );
}
