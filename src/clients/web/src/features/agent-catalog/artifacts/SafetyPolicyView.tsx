import Box from '@mui/material/Box';
import { useTranslation } from 'react-i18next';
import type { SafetyPolicyDocument } from '../../../platform/models/agentCatalog';
import { CatalogText } from '../CatalogText';
import { Field, useEnumText, ValueList } from './ViewParts';

export function SafetyPolicyView({ document }: { document: SafetyPolicyDocument }) {
  const { t } = useTranslation('agentCatalog');
  const group = useEnumText('kinds');
  const route = useEnumText('routes');
  const category = useEnumText('categories');
  const rule = useEnumText('rules');

  return (
    <Box component="dl" sx={{ m: 0 }}>
      <Field label={t('fields.group')}>{group(document.group)}</Field>
      <Field label={t('fields.routes')}>
        <ValueList values={document.routes} text={route} empty={t('empty.routes')} />
      </Field>
      <Field label={t('fields.hardBoundaries')}>
        {document.hardBoundaries.length === 0 ? (
          t('empty.hardBoundaries')
        ) : (
          <Box component="ul" sx={{ m: 0, pl: 0, listStyle: 'none' }}>
            {document.hardBoundaries.map((boundary, index) => (
              <li key={index}>
                <CatalogText text={boundary} label={t('fields.hardBoundaries')} maxHeight={160} />
              </li>
            ))}
          </Box>
        )}
      </Field>
      <Field label={t('fields.escalationCategories')}>
        <ValueList values={document.escalationCategories} text={category} empty={t('empty.categories')} />
      </Field>
      <Field label={t('fields.refusalCategories')}>
        <ValueList values={document.refusalCategories} text={category} empty={t('empty.categories')} />
      </Field>
      <Field label={t('fields.rules')}>
        <ValueList values={document.rules} text={rule} empty={t('empty.rules')} />
      </Field>
    </Box>
  );
}
