import Box from '@mui/material/Box';
import { useTranslation } from 'react-i18next';
import type { PromptTemplateDocument } from '../../../platform/models/agentCatalog';
import { CatalogText } from '../CatalogText';
import { Field, useEnumText, ValueList } from './ViewParts';

export function PromptTemplateView({ document }: { document: PromptTemplateDocument }) {
  const { t } = useTranslation('agentCatalog');
  const group = useEnumText('kinds');
  const contract = useEnumText('responseContracts');
  const slot = useEnumText('slots');

  return (
    <Box component="dl" sx={{ m: 0 }}>
      <Field label={t('fields.group')}>{group(document.group)}</Field>
      <Field label={t('fields.responseContract')}>{contract(document.responseContract)}</Field>
      <Field label={t('fields.slots')}>
        <ValueList values={document.slots} text={slot} empty={t('empty.slots')} />
      </Field>
      {document.dimensionSetMajor !== undefined && <Field label={t('fields.dimensionSetMajor')}>{document.dimensionSetMajor}</Field>}
      <Field label={t('fields.text')}>
        <CatalogText text={document.text} label={t('fields.text')} />
      </Field>
    </Box>
  );
}
