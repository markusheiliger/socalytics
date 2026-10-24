import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import FormControl from '@mui/material/FormControl';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import FormHelperText from '@mui/material/FormHelperText';
import FormLabel from '@mui/material/FormLabel';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import { useTranslation } from 'react-i18next';
import type { CatalogViolation, SafetyPolicyDocument } from '../../../platform/models/agentCatalog';
import { CATEGORIES, LIMITS, ROUTES, RULES } from '../catalogLimits';
import { violationText } from '../outcomeText';
import { useEnumText } from './ViewParts';

const GROUPS = ['coach', 'specialist'] as const;
const COACH_RULES = ['simulation-disclosure', 'non-impersonation', 'non-fabrication'] as const;

interface Props {
  value: SafetyPolicyDocument;
  onChange: (value: SafetyPolicyDocument) => void;
  violations?: CatalogViolation[];
  disabled?: boolean;
}

/** Hints and violation texts of one location; hints never block submission (FR-010, FR-012). */
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

function toggle(values: string[], item: string, checked: boolean): string[] {
  if (checked) return values.includes(item) ? values : [...values, item];
  return values.filter((value) => value !== item);
}

export function SafetyPolicyForm({ value, onChange, violations = [], disabled = false }: Props) {
  const { t } = useTranslation('agentCatalog');
  const groupText = useEnumText('kinds');
  const routeText = useEnumText('routes');
  const categoryText = useEnumText('categories');
  const ruleText = useEnumText('rules');

  const at = (location: string) => violations.filter((violation) => violation.location === location);
  const under = (prefix: string) => violations.filter((violation) => violation.location.startsWith(prefix));
  const set = (patch: Partial<SafetyPolicyDocument>) => onChange({ ...value, ...patch });

  const choices = (
    field: 'routes' | 'escalationCategories' | 'refusalCategories' | 'rules',
    label: string,
    options: readonly string[],
    text: (option: string) => string,
    empty: string,
    extra?: React.ReactNode,
  ) => {
    const selected = value[field];
    const unknown = selected.filter((item) => !options.includes(item));
    return (
      <FormControl component="fieldset" sx={{ mb: 2, display: 'block' }} error={under(`field:${field}`).length + under(`entry:${field}:`).length > 0}>
        <FormLabel component="legend">{label}</FormLabel>
        <FormGroup>
          {[...options, ...unknown].map((option) => (
            <FormControlLabel
              key={option}
              label={text(option)}
              control={
                <Checkbox
                  size="small"
                  disabled={disabled}
                  checked={selected.includes(option)}
                  onChange={(event) => set({ [field]: toggle(selected, option, event.target.checked) })}
                />
              }
            />
          ))}
        </FormGroup>
        {selected.length === 0 && <FormHelperText>{empty}</FormHelperText>}
        {extra}
        <Violations violations={[...at(`field:${field}`), ...under(`entry:${field}:`)]} />
      </FormControl>
    );
  };

  const missingCoachRules = value.group === 'coach' ? COACH_RULES.filter((rule) => !value.rules.includes(rule)) : [];
  const boundaries = value.hardBoundaries;

  return (
    <Box>
      <TextField
        select
        fullWidth
        size="small"
        required
        disabled={disabled}
        label={t('fields.group')}
        value={value.group}
        onChange={(event) => set({ group: event.target.value })}
        error={at('field:group').length > 0}
        sx={{ mb: 2 }}
      >
        {GROUPS.map((group) => (
          <MenuItem key={group} value={group}>
            {groupText(group)}
          </MenuItem>
        ))}
        {!GROUPS.some((group) => group === value.group) && value.group !== '' && (
          <MenuItem value={value.group}>{groupText(value.group)}</MenuItem>
        )}
      </TextField>
      <Violations violations={at('field:group')} />

      {choices('routes', t('fields.routes'), ROUTES, routeText, t('empty.routes'))}

      <Box component="fieldset" sx={{ border: 0, p: 0, m: 0, mb: 2 }}>
        <FormLabel component="legend">{t('fields.hardBoundaries')}</FormLabel>
        <FormHelperText>
          {t('hints.itemsBetween', LIMITS.hardBoundaries)} {t('hints.itemLength', { max: LIMITS.hardBoundaryLength.max })}
        </FormHelperText>
        {boundaries.length === 0 && <FormHelperText>{t('empty.hardBoundaries')}</FormHelperText>}
        {boundaries.map((boundary, index) => {
          const own = at(`entry:hardBoundaries:${index}`);
          return (
            <Box key={index} sx={{ display: 'flex', gap: 1, alignItems: 'flex-start', mt: 1 }}>
              <TextField
                fullWidth
                size="small"
                multiline
                disabled={disabled}
                label={t('safetyForm.hardBoundary', { index: index + 1 })}
                value={boundary}
                error={own.length > 0}
                helperText={t('hints.remaining', { count: LIMITS.hardBoundaryLength.max - boundary.length })}
                onChange={(event) => set({ hardBoundaries: boundaries.map((item, i) => (i === index ? event.target.value : item)) })}
              />
              <Button
                size="small"
                disabled={disabled}
                aria-label={t('safetyForm.remove', { index: index + 1 })}
                onClick={() => set({ hardBoundaries: boundaries.filter((_, i) => i !== index) })}
              >
                {t('safetyForm.remove', { index: index + 1 })}
              </Button>
              <Violations violations={own} />
            </Box>
          );
        })}
        <Button size="small" sx={{ mt: 1 }} disabled={disabled} onClick={() => set({ hardBoundaries: [...boundaries, ''] })}>
          {t('safetyForm.add')}
        </Button>
        <Violations violations={at('field:hardBoundaries')} />
      </Box>

      {choices('escalationCategories', t('fields.escalationCategories'), CATEGORIES, categoryText, t('empty.categories'))}
      {choices('refusalCategories', t('fields.refusalCategories'), CATEGORIES, categoryText, t('empty.categories'))}
      {choices(
        'rules',
        t('fields.rules'),
        RULES,
        ruleText,
        t('empty.rules'),
        value.group === 'coach' && (
          <FormHelperText>
            {t('safetyForm.coachRules', { rules: COACH_RULES.map(ruleText).join(', ') })}
            {missingCoachRules.length > 0 && ` ${t('safetyForm.coachRulesMissing', { rules: missingCoachRules.map(ruleText).join(', ') })}`}
          </FormHelperText>
        ),
      )}
      <FormHelperText>{t('hints.hintsDoNotBlock')}</FormHelperText>
    </Box>
  );
}
