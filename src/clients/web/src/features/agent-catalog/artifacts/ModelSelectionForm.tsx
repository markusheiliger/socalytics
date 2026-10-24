import Box from '@mui/material/Box';
import FormHelperText from '@mui/material/FormHelperText';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import { useTranslation } from 'react-i18next';
import type { CatalogViolation, ModelParameters, ModelSelectionDocument } from '../../../platform/models/agentCatalog';
import { NAME_PATTERN, PARAMETER_RANGES, PROVIDERS, REASONING_EFFORTS } from '../catalogLimits';
import { violationText } from '../outcomeText';
import { useEnumText } from './ViewParts';

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
            <FormHelperText key={index} error role="alert" component="span" sx={{ display: 'block' }}>
              {t(key, values)}
            </FormHelperText>
          );
        })}
    </>
  );
}

export interface ModelSelectionFormProps {
  value: ModelSelectionDocument;
  onChange: (value: ModelSelectionDocument) => void;
  violations?: CatalogViolation[];
  disabled?: boolean;
}

type NumberParameter = 'temperature' | 'topP' | 'maxOutputTokens';

/** Entry of a model selection: only names and generation parameters, never credentials or endpoints. Hints never block submission (FR-010). */
export function ModelSelectionForm({ value, onChange, violations = [], disabled = false }: ModelSelectionFormProps) {
  const { t } = useTranslation('agentCatalog');
  const provider = useEnumText('providers');
  const effort = useEnumText('reasoningEfforts');
  const parameters = value.parameters ?? {};

  const set = (change: Partial<ModelSelectionDocument>) => onChange({ ...value, ...change });
  const setParameters = (change: Partial<ModelParameters>) => {
    const next: ModelParameters = { ...parameters, ...change };
    for (const key of Object.keys(next) as (keyof ModelParameters)[]) {
      if (next[key] === undefined) delete next[key];
    }
    set({ parameters: next });
  };
  const failing = (location: string) => violations.some((violation) => violation.location === location);

  const nameHint = (name: string) => (name !== '' && !NAME_PATTERN.test(name) ? t('hints.namePattern') : undefined);

  const numberField = (key: NumberParameter, whole: boolean) => {
    const range = PARAMETER_RANGES[key];
    const location = `field:parameters.${key}`;
    return (
      <TextField
        key={key}
        type="number"
        label={t(`fields.${key}`)}
        value={parameters[key] ?? ''}
        disabled={disabled}
        error={failing(location)}
        onChange={(event) => {
          const entered = event.target.value;
          setParameters({ [key]: entered === '' ? undefined : Number(entered) });
        }}
        helperText={
          <>
            {t('hints.optional')}
            {' · '}
            {t(whole ? 'hints.wholeRange' : 'hints.range', { min: range.min, max: range.max.toLocaleString('en') })}
            <Violations violations={violations} location={location} />
          </>
        }
        slotProps={{ htmlInput: { min: range.min, max: range.max, step: whole ? 1 : 'any' } }}
      />
    );
  };

  const nameField = (key: 'model' | 'deployment') => (
    <TextField
      required
      label={t(`fields.${key}`)}
      value={value[key]}
      disabled={disabled}
      error={failing(`field:${key}`)}
      onChange={(event) => set({ [key]: event.target.value })}
      helperText={
        <>
          {nameHint(value[key]) ?? t('hints.namePattern')}
          <Violations violations={violations} location={`field:${key}`} />
        </>
      }
    />
  );

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      <TextField
        select
        required
        label={t('fields.provider')}
        value={value.provider}
        disabled={disabled}
        error={failing('field:provider')}
        onChange={(event) => set({ provider: event.target.value as ModelSelectionDocument['provider'] })}
        helperText={<Violations violations={violations} location="field:provider" />}
      >
        {withCurrent(PROVIDERS, value.provider).map((choice) => (
          <MenuItem key={choice} value={choice}>
            {provider(choice)}
          </MenuItem>
        ))}
      </TextField>
      {nameField('model')}
      {nameField('deployment')}
      {numberField('temperature', false)}
      {numberField('topP', false)}
      {numberField('maxOutputTokens', true)}
      <TextField
        select
        label={t('fields.reasoningEffort')}
        value={parameters.reasoningEffort ?? ''}
        disabled={disabled}
        error={failing('field:parameters.reasoningEffort')}
        onChange={(event) =>
          setParameters({ reasoningEffort: event.target.value === '' ? undefined : (event.target.value as ModelParameters['reasoningEffort']) })
        }
        helperText={
          <>
            {t('hints.optional')}
            <Violations violations={violations} location="field:parameters.reasoningEffort" />
          </>
        }
      >
        <MenuItem value="">{t('fields.noneChoice')}</MenuItem>
        {withCurrent(REASONING_EFFORTS, parameters.reasoningEffort ?? '').map((choice) => (
          <MenuItem key={choice} value={choice}>
            {effort(choice)}
          </MenuItem>
        ))}
      </TextField>
      <FormHelperText>{t('hints.hintsDoNotBlock')}</FormHelperText>
    </Box>
  );
}
