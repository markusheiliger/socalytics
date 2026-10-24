import Box from '@mui/material/Box';
import Checkbox from '@mui/material/Checkbox';
import FormControl from '@mui/material/FormControl';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import FormHelperText from '@mui/material/FormHelperText';
import FormLabel from '@mui/material/FormLabel';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import { useTranslation } from 'react-i18next';
import type { CatalogViolation, PromptTemplateDocument } from '../../../platform/models/agentCatalog';
import { LIMITS, PROMPT_SLOTS, RESPONSE_CONTRACT } from '../catalogLimits';
import { violationText } from '../outcomeText';
import { useEnumText } from './ViewParts';

const GROUPS = ['coach', 'specialist'] as const;
const PLACEHOLDER = /\{\{\s*([A-Za-z0-9_]+)\s*\}\}/g;

/** The slots whose `{{slot}}` placeholder occurs in the prompt text, in order of first occurrence. */
export function placeholdersIn(text: string): string[] {
  const found: string[] = [];
  for (const match of text.matchAll(PLACEHOLDER)) {
    if (!found.includes(match[1])) found.push(match[1]);
  }
  return found;
}

function withCurrent(choices: readonly string[], current: string): string[] {
  return current === '' || choices.includes(current) ? [...choices] : [...choices, current];
}

function Violations({ violations, location }: { violations: CatalogViolation[]; location: string }) {
  const { t } = useTranslation('agentCatalog');
  return (
    <>
      {violations
        .filter((violation) => violation.location === location)
        .map((violation, index) => {
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

export interface PromptTemplateFormProps {
  value: PromptTemplateDocument;
  onChange: (value: PromptTemplateDocument) => void;
  violations?: CatalogViolation[];
  disabled?: boolean;
}

/** Entry of a prompt template: hints never block submission, the platform's validation stays the authority (FR-010). */
export function PromptTemplateForm({ value, onChange, violations = [], disabled = false }: PromptTemplateFormProps) {
  const { t } = useTranslation('agentCatalog');
  const group = useEnumText('kinds');
  const contract = useEnumText('responseContracts');
  const slotText = useEnumText('slots');

  const set = (change: Partial<PromptTemplateDocument>) => onChange({ ...value, ...change });
  const failing = (location: string) => violations.some((violation) => violation.location === location);

  const used = placeholdersIn(value.text);
  const missing = value.slots.filter((slot) => !used.includes(slot));
  const undeclared = used.filter((slot) => !value.slots.includes(slot));
  const remaining = LIMITS.promptText.max - value.text.length;

  const toggleSlot = (slot: string, checked: boolean) =>
    set({
      slots: checked
        ? [...value.slots, slot]
        : value.slots.filter((declared) => declared !== slot),
    });

  const slotChoices: string[] = [...PROMPT_SLOTS, ...value.slots.filter((slot) => !(PROMPT_SLOTS as readonly string[]).includes(slot))];

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      <TextField
        select
        required
        label={t('fields.group')}
        value={value.group}
        disabled={disabled}
        error={failing('field:group')}
        onChange={(event) => set({ group: event.target.value })}
        helperText={<Violations violations={violations} location="field:group" />}
      >
        {withCurrent(GROUPS, value.group).map((choice) => (
          <MenuItem key={choice} value={choice}>
            {group(choice)}
          </MenuItem>
        ))}
      </TextField>

      <TextField
        select
        required
        label={t('fields.responseContract')}
        value={value.responseContract}
        disabled={disabled}
        error={failing('field:responseContract')}
        onChange={(event) => set({ responseContract: event.target.value })}
        helperText={<Violations violations={violations} location="field:responseContract" />}
      >
        {withCurrent([RESPONSE_CONTRACT], value.responseContract).map((choice) => (
          <MenuItem key={choice} value={choice}>
            {contract(choice)}
          </MenuItem>
        ))}
      </TextField>

      <FormControl component="fieldset" error={failing('field:slots')} disabled={disabled}>
        <FormLabel component="legend">{t('fields.slots')}</FormLabel>
        <FormGroup>
          {slotChoices.map((slot) => {
            const index = value.slots.indexOf(slot);
            return (
              <Box key={slot}>
                <FormControlLabel
                  label={slotText(slot)}
                  control={<Checkbox checked={index >= 0} onChange={(event) => toggleSlot(slot, event.target.checked)} />}
                />
                {index >= 0 && <Violations violations={violations} location={`entry:slots:${index}`} />}
              </Box>
            );
          })}
        </FormGroup>
        <Violations violations={violations} location="field:slots" />
      </FormControl>

      <TextField
        type="number"
        label={t('fields.dimensionSetMajor')}
        value={value.dimensionSetMajor ?? ''}
        disabled={disabled}
        error={failing('field:dimensionSetMajor')}
        onChange={(event) => {
          const entered = event.target.value;
          set({ dimensionSetMajor: entered === '' ? undefined : Number(entered) });
        }}
        helperText={
          <>
            {t('hints.optional')}
            <Violations violations={violations} location="field:dimensionSetMajor" />
          </>
        }
        slotProps={{ htmlInput: { min: 0, step: 1 } }}
      />

      <Box>
        <TextField
          required
          multiline
          minRows={8}
          fullWidth
          label={t('fields.text')}
          value={value.text}
          disabled={disabled}
          error={failing('field:text') || remaining < 0}
          onChange={(event) => set({ text: event.target.value })}
          helperText={
            <>
              {t('hints.lengthBetween', { min: LIMITS.promptText.min.toLocaleString('en'), max: LIMITS.promptText.max.toLocaleString('en') })}
              {' · '}
              {t('hints.remaining', { count: remaining })}
              <Violations violations={violations} location="field:text" />
            </>
          }
        />
        {missing.map((slot) => (
          <FormHelperText key={`missing-${slot}`}>{t('hints.slotMissing', { slot })}</FormHelperText>
        ))}
        {undeclared.map((slot) => (
          <FormHelperText key={`undeclared-${slot}`}>{t('hints.slotUndeclared', { slot })}</FormHelperText>
        ))}
        <FormHelperText>{t('hints.hintsDoNotBlock')}</FormHelperText>
      </Box>
    </Box>
  );
}
