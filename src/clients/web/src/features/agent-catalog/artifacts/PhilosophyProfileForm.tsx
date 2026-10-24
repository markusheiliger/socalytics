import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import FormHelperText from '@mui/material/FormHelperText';
import FormLabel from '@mui/material/FormLabel';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { CatalogViolation, DimensionSetDocument, PhilosophyProfileDocument, ProfileWeight } from '../../../platform/models/agentCatalog';
import { LIMITS } from '../catalogLimits';
import { violationText } from '../outcomeText';

/** A registered dimension set version the profile can be based on. */
export interface DimensionSetChoice {
  id: string;
  version: string;
  document: DimensionSetDocument;
}

interface Props {
  value: PhilosophyProfileDocument;
  onChange: (value: PhilosophyProfileDocument) => void;
  dimensionSets: readonly DimensionSetChoice[];
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

const outsideRange = (weight: number) =>
  !Number.isNaN(weight) && (!Number.isInteger(weight) || weight < LIMITS.weight.min || weight > LIMITS.weight.max);

const blankWeight = (): ProfileWeight => ({ weight: Number.NaN, rationale: '', provenance: '' });

type ReviewerKey = 'architecture' | 'footballDomain';

/** Entry of a philosophy profile; hints never block submission, the platform's validation stays the authority (FR-010, FR-012). */
export function PhilosophyProfileForm({ value, onChange, dimensionSets, violations = [], disabled = false }: Props) {
  const { t } = useTranslation('agentCatalog');
  const [dropped, setDropped] = useState<string[]>([]);
  const at = (location: string) => violations.filter((violation) => violation.location === location);
  const under = (prefix: string) => violations.filter((violation) => violation.location.startsWith(prefix));

  const choiceIndex = dimensionSets.findIndex((s) => s.id === value.dimensionSet.id && s.version === value.dimensionSet.version);
  const chosen = choiceIndex >= 0 ? dimensionSets[choiceIndex] : undefined;
  const rows = chosen?.document.dimensions ?? [];
  const total = rows.reduce((sum, row) => {
    const weight = value.weights[row.id]?.weight;
    return sum + (weight === undefined || Number.isNaN(weight) ? 0 : weight);
  }, 0);

  const chooseSet = (index: number) => {
    const next = dimensionSets[index];
    if (!next) return;
    const weights: Record<string, ProfileWeight> = {};
    for (const row of next.document.dimensions) {
      weights[row.id] = value.weights[row.id] ?? blankWeight();
    }
    setDropped(Object.keys(value.weights).filter((id) => !(id in weights)));
    onChange({ ...value, dimensionSet: { id: next.id, version: next.version }, weights });
  };

  const updateWeight = (id: string, patch: Partial<ProfileWeight>) =>
    onChange({
      ...value,
      weights: { ...value.weights, [id]: { ...blankWeight(), ...value.weights[id], ...patch } },
    });

  const setReviewers = (key: ReviewerKey, list: string[]) => onChange({ ...value, reviewers: { ...value.reviewers, [key]: list } });

  const reviewerList = (key: ReviewerKey, label: string, add: string, remove: string, itemLabel: string) => {
    const list = value.reviewers[key];
    const own = [...at(`field:reviewers`), ...under(`entry:reviewers.${key}:`)];
    return (
      <Box component="fieldset" sx={{ border: 0, p: 0, m: 0, mb: 2 }}>
        <FormLabel component="legend">{label}</FormLabel>
        <FormHelperText>{t('hints.reviewersEach')}</FormHelperText>
        {list.length === 0 && <FormHelperText>{t('empty.reviewers')}</FormHelperText>}
        {list.map((reviewer, index) => (
          <Box key={index} sx={{ display: 'flex', gap: 1, mt: 1 }}>
            <TextField
              fullWidth
              size="small"
              required
              disabled={disabled}
              label={t(itemLabel, { index: index + 1 })}
              value={reviewer}
              onChange={(event) => setReviewers(key, list.map((r, i) => (i === index ? event.target.value : r)))}
            />
            <Button
              size="small"
              disabled={disabled}
              aria-label={t(remove, { index: index + 1 })}
              onClick={() => setReviewers(key, list.filter((_, i) => i !== index))}
            >
              {t(remove, { index: index + 1 })}
            </Button>
          </Box>
        ))}
        <Button size="small" sx={{ mt: 1 }} disabled={disabled} onClick={() => setReviewers(key, [...list, ''])}>
          {t(add)}
        </Button>
        <Violations violations={own} />
      </Box>
    );
  };

  const setViolations = at('field:dimensionSet');

  return (
    <Box>
      <TextField
        select
        fullWidth
        size="small"
        required
        disabled={disabled}
        label={t('profileForm.dimensionSet')}
        value={choiceIndex >= 0 ? String(choiceIndex) : ''}
        error={setViolations.length > 0}
        onChange={(event) => chooseSet(Number(event.target.value))}
        sx={{ mb: 1 }}
      >
        {dimensionSets.length === 0 && (
          <MenuItem value="" disabled>
            {t('empty.artifactReferences')}
          </MenuItem>
        )}
        {dimensionSets.map((s, index) => (
          <MenuItem key={`${s.id}@${s.version}`} value={String(index)}>
            {s.id} {s.version}
          </MenuItem>
        ))}
      </TextField>
      <Violations violations={setViolations} />
      {dropped.length > 0 && (
        <Alert severity="info" sx={{ mb: 2 }}>
          {t('hints.dimensionNotice', { ids: dropped.join(', ') })}
        </Alert>
      )}

      <Box component="fieldset" sx={{ border: 0, p: 0, m: 0, mb: 2 }}>
        <FormLabel component="legend">{t('fields.weights')}</FormLabel>
        {rows.length === 0 && <FormHelperText>{t('empty.weights')}</FormHelperText>}
        {rows.map((row) => {
          const weight = value.weights[row.id] ?? blankWeight();
          const rowViolations = at(`row:${row.id}`);
          const weightViolations = at(`cell:${row.id}:weight`);
          const rationaleViolations = at(`cell:${row.id}:rationale`);
          const provenanceViolations = at(`cell:${row.id}:provenance`);
          return (
            <Box
              key={row.id}
              component="fieldset"
              aria-label={t('profileForm.row', { id: row.id })}
              sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2, mt: 2 }}
            >
              <Typography sx={{ fontWeight: 'bold' }}>{row.id}</Typography>
              <Typography color="text.secondary" sx={{ mb: 1 }}>
                {row.meaning}
              </Typography>
              <Violations violations={rowViolations} />
              <TextField
                fullWidth
                size="small"
                required
                type="number"
                disabled={disabled}
                label={t('profileForm.weight', { id: row.id })}
                value={Number.isNaN(weight.weight) ? '' : weight.weight}
                error={outsideRange(weight.weight) || weightViolations.length > 0}
                helperText={outsideRange(weight.weight) ? t('hints.weightWhole') : t('hints.wholeRange', LIMITS.weight)}
                onChange={(event) => updateWeight(row.id, { weight: event.target.value === '' ? Number.NaN : Number(event.target.value) })}
                slotProps={{ htmlInput: { step: 1 } }}
                sx={{ mb: 1 }}
              />
              <Violations violations={weightViolations} />
              <TextField
                fullWidth
                multiline
                size="small"
                required
                disabled={disabled}
                label={t('profileForm.rationale', { id: row.id })}
                value={weight.rationale}
                error={rationaleViolations.length > 0}
                helperText={t('hints.remaining', { count: LIMITS.rationale.max - weight.rationale.length })}
                onChange={(event) => updateWeight(row.id, { rationale: event.target.value })}
                sx={{ mb: 1 }}
              />
              <Violations violations={rationaleViolations} />
              <TextField
                fullWidth
                multiline
                size="small"
                required
                disabled={disabled}
                label={t('profileForm.provenance', { id: row.id })}
                value={weight.provenance}
                error={provenanceViolations.length > 0}
                helperText={t('hints.remaining', { count: LIMITS.provenance.max - weight.provenance.length })}
                onChange={(event) => updateWeight(row.id, { provenance: event.target.value })}
              />
              <Violations violations={provenanceViolations} />
            </Box>
          );
        })}
        {rows.length > 0 && (
          <Box sx={{ mt: 2 }} role="status">
            <Typography>{t('profileForm.total', { total })}</Typography>
            {total !== LIMITS.weightTotal && <FormHelperText error>{t('hints.weightsTotalExact')}</FormHelperText>}
          </Box>
        )}
        <Violations violations={at('weights')} />
      </Box>

      <TextField
        fullWidth
        size="small"
        required
        disabled={disabled}
        label={t('fields.author')}
        value={value.author}
        error={at('field:author').length > 0}
        onChange={(event) => onChange({ ...value, author: event.target.value })}
        sx={{ mb: 1 }}
      />
      <Violations violations={at('field:author')} />
      {reviewerList(
        'architecture',
        t('fields.architectureReviewers'),
        'profileForm.addArchitectureReviewer',
        'profileForm.removeArchitectureReviewer',
        'profileForm.architectureReviewer',
      )}
      {reviewerList(
        'footballDomain',
        t('fields.footballDomainReviewers'),
        'profileForm.addFootballDomainReviewer',
        'profileForm.removeFootballDomainReviewer',
        'profileForm.footballDomainReviewer',
      )}
      <TextField
        fullWidth
        size="small"
        disabled={disabled}
        label={t('fields.supersedes')}
        value={value.supersedes?.version ?? ''}
        error={at('field:supersedes').length > 0}
        helperText={t('profileForm.supersedesHint')}
        onChange={(event) => onChange({ ...value, supersedes: event.target.value === '' ? undefined : { version: event.target.value } })}
      />
      <Violations violations={at('field:supersedes')} />
      <FormHelperText>{t('hints.hintsDoNotBlock')}</FormHelperText>
    </Box>
  );
}
