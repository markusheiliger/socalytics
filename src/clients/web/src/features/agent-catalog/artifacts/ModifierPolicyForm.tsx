import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Checkbox from '@mui/material/Checkbox';
import FormControl from '@mui/material/FormControl';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import FormHelperText from '@mui/material/FormHelperText';
import FormLabel from '@mui/material/FormLabel';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { CatalogViolation, ModifierPolicyDocument } from '../../../platform/models/agentCatalog';
import { CONTEXT_CATEGORIES, LIMITS } from '../catalogLimits';
import { violationText } from '../outcomeText';
import { useEnumText } from './ViewParts';

interface Props {
  value: ModifierPolicyDocument;
  onChange: (value: ModifierPolicyDocument) => void;
  violations?: CatalogViolation[];
  disabled?: boolean;
}

/** Violation texts of one location; hints never block submission (FR-010, FR-012). */
function Violations({ violations }: { violations: CatalogViolation[] }) {
  const { t } = useTranslation('agentCatalog');
  if (violations.length === 0) return null;
  return (
    <Alert severity="error" role="alert" sx={{ mt: 1 }}>
      {violations.map((violation, index) => {
        const { key, values } = violationText(violation.code, violation.detail);
        return <div key={index}>{t(key, values)}</div>;
      })}
    </Alert>
  );
}

type NumberKey = 'dimensionSetMajor' | 'perDimensionBound' | 'maxPositiveMovement';

export function ModifierPolicyForm({ value, onChange, violations = [], disabled = false }: Props) {
  const { t } = useTranslation('agentCatalog');
  const contextText = useEnumText('contexts');

  const at = (location: string) => violations.filter((violation) => violation.location === location);
  const under = (prefix: string) => violations.filter((violation) => violation.location.startsWith(prefix));
  const set = (patch: Partial<ModifierPolicyDocument>) => onChange({ ...value, ...patch });

  const numberField = (key: NumberKey, hint: string, range?: { min: number; max: number }) => {
    const own = at(`field:${key}`);
    const current = value[key];
    return (
      <Box sx={{ mb: 2 }}>
        <TextField
          fullWidth
          size="small"
          required
          type="number"
          disabled={disabled}
          label={t(`fields.${key}`)}
          value={Number.isNaN(current) ? '' : current}
          error={own.length > 0}
          onChange={(event) => set({ [key]: event.target.value === '' ? Number.NaN : Number(event.target.value) })}
          helperText={hint}
          slotProps={{ htmlInput: { min: range?.min ?? 1, max: range?.max, step: 1 } }}
        />
        <Violations violations={own} />
      </Box>
    );
  };

  const selected = value.contextCategories;
  const unknown = selected.filter((item) => !(CONTEXT_CATEGORIES as readonly string[]).includes(item));
  const contextViolations = [...at('field:contextCategories'), ...under('entry:contextCategories:')];

  return (
    <Box>
      {numberField('dimensionSetMajor', t('hints.dimensionSetMajorWhole'))}
      {numberField('perDimensionBound', t('hints.perDimensionBound'), LIMITS.perDimensionBound)}
      {numberField('maxPositiveMovement', t('hints.maxPositiveMovement'), LIMITS.maxPositiveMovement)}

      <FormControl component="fieldset" sx={{ mb: 2, display: 'block' }} error={contextViolations.length > 0}>
        <FormLabel component="legend">{t('fields.contextCategories')}</FormLabel>
        <FormGroup>
          {[...CONTEXT_CATEGORIES, ...unknown].map((option) => (
            <FormControlLabel
              key={option}
              label={contextText(option)}
              control={
                <Checkbox
                  size="small"
                  disabled={disabled}
                  checked={selected.includes(option)}
                  onChange={(event) =>
                    set({
                      contextCategories: event.target.checked
                        ? selected.includes(option)
                          ? selected
                          : [...selected, option]
                        : selected.filter((item) => item !== option),
                    })
                  }
                />
              }
            />
          ))}
        </FormGroup>
        {selected.length === 0 && <FormHelperText>{t('empty.contexts')}</FormHelperText>}
        <Violations violations={contextViolations} />
      </FormControl>

      <Box sx={{ mb: 2 }}>
        <Typography component="div" variant="subtitle2">
          {t('fields.budgetNeutral')}
        </Typography>
        <Typography>{value.budgetNeutral ? t('fields.budgetNeutralYes') : t('fields.budgetNeutralNo')}</Typography>
        <FormHelperText>{t('hints.budgetNeutralRule')}</FormHelperText>
        <Violations violations={at('field:budgetNeutral')} />
      </Box>
      <FormHelperText>{t('hints.hintsDoNotBlock')}</FormHelperText>
    </Box>
  );
}
