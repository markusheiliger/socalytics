import Box from '@mui/material/Box';
import { useTranslation } from 'react-i18next';
import type { ModifierPolicyDocument } from '../../../platform/models/agentCatalog';
import { Field, useEnumText, ValueList } from './ViewParts';

export function ModifierPolicyView({ document }: { document: ModifierPolicyDocument }) {
  const { t } = useTranslation('agentCatalog');
  const context = useEnumText('contexts');

  return (
    <Box component="dl" sx={{ m: 0 }}>
      <Field label={t('fields.dimensionSetMajor')}>{document.dimensionSetMajor}</Field>
      <Field label={t('fields.perDimensionBound')}>{document.perDimensionBound}</Field>
      <Field label={t('fields.maxPositiveMovement')}>{document.maxPositiveMovement}</Field>
      <Field label={t('fields.budgetNeutral')}>
        {document.budgetNeutral ? t('fields.budgetNeutralYes') : t('fields.budgetNeutralNo')}
      </Field>
      <Field label={t('fields.contextCategories')}>
        <ValueList values={document.contextCategories} text={context} empty={t('empty.contexts')} />
      </Field>
    </Box>
  );
}
