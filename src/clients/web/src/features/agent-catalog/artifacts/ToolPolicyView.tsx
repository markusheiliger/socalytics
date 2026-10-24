import Box from '@mui/material/Box';
import { useTranslation } from 'react-i18next';
import type { BaselineAgent, ToolPolicyDocument } from '../../../platform/models/agentCatalog';
import { Field } from './ViewParts';

/** `agent` is the agent in context; without it the tools carry no baseline marker. */
export function ToolPolicyView({ document, agent }: { document: ToolPolicyDocument; agent?: BaselineAgent }) {
  const { t } = useTranslation('agentCatalog');

  return (
    <Box component="dl" sx={{ m: 0 }}>
      <Field label={t('fields.tools')}>
        {document.tools.length === 0 ? (
          t('empty.tools')
        ) : (
          <Box component="ul" sx={{ m: 0, pl: 2 }}>
            {document.tools.map((tool) => (
              <li key={tool}>
                {tool}
                {agent && ` (${agent.toolBaseline.includes(tool) ? t('baseline.withinBaseline') : t('baseline.outsideBaseline')})`}
              </li>
            ))}
          </Box>
        )}
      </Field>
    </Box>
  );
}
