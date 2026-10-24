import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import FormControl from '@mui/material/FormControl';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import FormHelperText from '@mui/material/FormHelperText';
import FormLabel from '@mui/material/FormLabel';
import TextField from '@mui/material/TextField';
import { useTranslation } from 'react-i18next';
import type { CatalogViolation, DimensionDefinition, DimensionSetDocument } from '../../../platform/models/agentCatalog';
import { LIMITS } from '../catalogLimits';
import { violationText } from '../outcomeText';

const MEANING_MAX = 500;
const KEBAB_ID = /^[a-z0-9]+(-[a-z0-9]+)*$/;

interface DimensionSetFormProps {
  value: DimensionSetDocument;
  onChange: (value: DimensionSetDocument) => void;
  /** The tool allowlist of the catalog baseline: the only evidence tools the form offers. */
  allowlist: string[];
  violations?: CatalogViolation[];
  disabled?: boolean;
}

function Violations({ violations }: { violations: CatalogViolation[] }) {
  const { t } = useTranslation('agentCatalog');
  return (
    <>
      {violations.map((violation, index) => {
        const { key, values } = violationText(violation.code, violation.detail);
        return (
          <FormHelperText key={index} error role="alert">
            {t(key, values)}
          </FormHelperText>
        );
      })}
    </>
  );
}

/** Entry of a dimension set; hints never block submission, the platform's validation stays the authority (FR-010). */
export function DimensionSetForm({ value, onChange, allowlist, violations = [], disabled = false }: DimensionSetFormProps) {
  const { t } = useTranslation('agentCatalog');
  const at = (location: string) => violations.filter((violation) => violation.location === location);
  const under = (prefix: string) => violations.filter((violation) => violation.location.startsWith(prefix));
  const dimensions = value.dimensions;

  const update = (index: number, patch: Partial<DimensionDefinition>) =>
    onChange({ ...value, dimensions: dimensions.map((item, i) => (i === index ? { ...item, ...patch } : item)) });

  const toggleTool = (index: number, tool: string, checked: boolean) => {
    const rest = dimensions[index].acceptedEvidence.filter((x) => x !== tool);
    update(index, { acceptedEvidence: checked ? [...rest, tool] : rest });
  };

  return (
    <Box component="fieldset" sx={{ border: 0, p: 0, m: 0 }}>
      <FormLabel component="legend">{t('fields.dimensions')}</FormLabel>
      <FormHelperText>
        {t('hints.itemsBetween', LIMITS.dimensions)} {t('hints.uniqueKebabIds')}
      </FormHelperText>
      {dimensions.length === 0 && <FormHelperText>{t('empty.dimensions')}</FormHelperText>}
      {dimensions.map((dimension, index) => {
        const duplicate = dimension.id !== '' && dimensions.some((other, i) => i !== index && other.id === dimension.id);
        const unlisted = dimension.acceptedEvidence.filter((tool) => !allowlist.includes(tool));
        const idHint = dimension.id !== '' && !KEBAB_ID.test(dimension.id);
        const own = at(`entry:dimensions:${index}`);
        const evidence = under(`entry:dimensions.${index}.acceptedEvidence:`);
        return (
          <Box
            key={index}
            component="fieldset"
            sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2, mt: 2 }}
            aria-label={t('dimensionForm.dimension', { index: index + 1 })}
          >
            <TextField
              fullWidth
              size="small"
              required
              disabled={disabled}
              label={t('dimensionForm.id', { index: index + 1 })}
              value={dimension.id}
              error={duplicate || idHint || own.length > 0}
              helperText={
                <>
                  {t('hints.idFormat')}
                  {duplicate && <span> {t('dimensionForm.duplicate', { id: dimension.id })}</span>}
                </>
              }
              onChange={(event) => update(index, { id: event.target.value })}
              sx={{ mb: 2 }}
            />
            <Violations violations={own} />
            <TextField
              fullWidth
              multiline
              size="small"
              required
              disabled={disabled}
              label={t('dimensionForm.meaning', { index: index + 1 })}
              value={dimension.meaning}
              helperText={t('hints.remaining', { count: MEANING_MAX - dimension.meaning.length })}
              onChange={(event) => update(index, { meaning: event.target.value })}
              sx={{ mb: 2 }}
            />
            <FormControl component="fieldset" error={evidence.length > 0}>
              <FormLabel component="legend">{t('dimensionForm.evidence', { index: index + 1 })}</FormLabel>
              <FormHelperText>
                {t('dimensionForm.evidenceRule', { count: dimension.acceptedEvidence.length })}
              </FormHelperText>
              {allowlist.length === 0 && <Alert severity="info">{t('empty.tools')}</Alert>}
              <FormGroup>
                {[...allowlist, ...unlisted].map((tool) => (
                  <Box key={tool}>
                    <FormControlLabel
                      label={tool}
                      control={
                        <Checkbox
                          size="small"
                          disabled={disabled}
                          checked={dimension.acceptedEvidence.includes(tool)}
                          onChange={(event) => toggleTool(index, tool, event.target.checked)}
                        />
                      }
                    />
                    {unlisted.includes(tool) && (
                      <FormHelperText component="span" error>
                        {t('violations.unknown-tool', { detail: tool })}
                      </FormHelperText>
                    )}
                  </Box>
                ))}
              </FormGroup>
              <Violations violations={evidence} />
            </FormControl>
            <Box>
              <Button
                size="small"
                disabled={disabled}
                aria-label={t('dimensionForm.remove', { index: index + 1 })}
                onClick={() => onChange({ ...value, dimensions: dimensions.filter((_, i) => i !== index) })}
              >
                {t('dimensionForm.remove', { index: index + 1 })}
              </Button>
            </Box>
          </Box>
        );
      })}
      <Button
        size="small"
        sx={{ mt: 1 }}
        disabled={disabled}
        onClick={() => onChange({ ...value, dimensions: [...dimensions, { id: '', meaning: '', acceptedEvidence: [] }] })}
      >
        {t('dimensionForm.add')}
      </Button>
      <Violations violations={at('field:dimensions')} />
      <FormHelperText>{t('hints.hintsDoNotBlock')}</FormHelperText>
    </Box>
  );
}
