import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Checkbox from '@mui/material/Checkbox';
import FormControl from '@mui/material/FormControl';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import FormHelperText from '@mui/material/FormHelperText';
import FormLabel from '@mui/material/FormLabel';
import { useTranslation } from 'react-i18next';
import type { BaselineAgent, CatalogViolation, ToolPolicyDocument } from '../../../platform/models/agentCatalog';
import { violationText } from '../outcomeText';

const MIN_TOOLS = 1;
const MAX_TOOLS = 11;

interface ToolPolicyFormProps {
  value: ToolPolicyDocument;
  onChange: (value: ToolPolicyDocument) => void;
  /** The tool allowlist of the catalog baseline: the only tools the form offers. */
  allowlist: string[];
  /** The agent named by the `agentId` search parameter; tools outside its baseline get a hint. */
  agent?: BaselineAgent;
  violations?: CatalogViolation[];
}

export function ToolPolicyForm({ value, onChange, allowlist, agent, violations = [] }: ToolPolicyFormProps) {
  const { t } = useTranslation('agentCatalog');
  const fieldViolations = violations.filter((v) => v.location === 'field:tools');
  const entryViolations = (tool: string) => {
    const index = value.tools.indexOf(tool);
    return index < 0 ? [] : violations.filter((v) => v.location === `entry:tools:${index}`);
  };
  // Tools already in the record but not on the allowlist stay visible so they can be removed.
  const unlisted = value.tools.filter((tool) => !allowlist.includes(tool));

  const toggle = (tool: string, checked: boolean) => {
    const rest = value.tools.filter((x) => x !== tool);
    onChange({ ...value, tools: checked ? [...rest, tool] : rest });
  };

  const renderTool = (tool: string, listed: boolean) => {
    const outside = !!agent && !agent.toolBaseline.includes(tool);
    const problems = entryViolations(tool);
    return (
      <Box key={tool}>
        <FormControlLabel
          control={<Checkbox checked={value.tools.includes(tool)} onChange={(e) => toggle(tool, e.target.checked)} />}
          label={tool}
        />
        {outside && agent && (
          <FormHelperText component="span">{t('hints.outsideBaseline', { agentId: agent.agentId })}</FormHelperText>
        )}
        {!listed && (
          <FormHelperText component="span" error>
            {t('violations.unknown-tool', { detail: tool })}
          </FormHelperText>
        )}
        {problems.map((problem, index) => {
          const { key, values } = violationText(problem.code, problem.detail);
          return (
            <FormHelperText key={index} error role="alert">
              {t(key, values)}
            </FormHelperText>
          );
        })}
      </Box>
    );
  };

  return (
    <FormControl component="fieldset" error={fieldViolations.length > 0} sx={{ width: '100%' }}>
      <FormLabel component="legend">{t('fields.tools')}</FormLabel>
      <FormHelperText>
        {t('hints.toolsRule', { min: MIN_TOOLS, max: MAX_TOOLS, count: value.tools.length })}
      </FormHelperText>
      {allowlist.length === 0 && <Alert severity="info">{t('empty.tools')}</Alert>}
      <FormGroup>
        {allowlist.map((tool) => renderTool(tool, true))}
        {unlisted.map((tool) => renderTool(tool, false))}
      </FormGroup>
      {fieldViolations.map((violation, index) => {
        const { key, values } = violationText(violation.code, violation.detail);
        return (
          <FormHelperText key={index} role="alert">
            {t(key, values)}
          </FormHelperText>
        );
      })}
    </FormControl>
  );
}
