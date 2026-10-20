import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import {
  RegistryRefusalError,
  type CapabilityDeclaration,
  type ProfileVersion,
  type RegistryRefusalDetail,
} from '../../platform/models/registry';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { DigestText } from './DigestText';
import { draftFromDeclaration } from './profileDraft';
import { RefusalAlert } from './RefusalAlert';
import { useCreateProfile, useImage, useImages, useProfiles } from './registryKeys';

const NUMBER_FIELDS = ['maxAttempts', 'leaseSeconds', 'staleTimeoutSeconds', 'executionTimeoutSeconds'] as const;
type NumberField = (typeof NUMBER_FIELDS)[number];

const lines = (text: string) => text.split('\n').map((line) => line.trim()).filter((line) => line !== '');
const joinLines = (values: string[]) => values.join('\n');
const joinPairs = (record: Record<string, string>) => Object.entries(record).map(([key, value]) => `${key}=${value}`).join('\n');

// Parses `key=value` lines; returns undefined when a line has no key or no separator.
function parsePairs(text: string): Record<string, string> | undefined {
  const result: Record<string, string> = {};
  for (const line of lines(text)) {
    const index = line.indexOf('=');
    if (index <= 0) return undefined;
    result[line.slice(0, index).trim()] = line.slice(index + 1).trim();
  }
  return result;
}

function Frame({ children }: { children?: React.ReactNode }) {
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('profileCreate.title')}</Typography>
      <Link to="/registry/profiles">{t('profileCreate.back')}</Link>
      {children}
    </Container>
  );
}

/** Creates a profile version as a draft from a registered image's declaration (FR-013). */
export function ProfileCreatePage() {
  const { t } = useTranslation('registry');
  const [params, setParams] = useSearchParams();
  const digest = params.get('image')?.trim() ?? '';
  const images = useImages();
  const failure = images.error ? toPlatformFailure(images.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  if (digest === '') {
    return (
      <Frame>
        <Typography sx={{ mt: 2 }}>{t('profileCreate.intro')}</Typography>
        {failure && <FailureAlert failure={failure} onReload={() => void images.refetch()} />}
        {images.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
        {images.data &&
          (images.data.length === 0 ? (
            <Typography sx={{ mt: 2 }}>{t('images.empty')}</Typography>
          ) : (
            <TextField
              select
              fullWidth
              label={t('profileCreate.chooseImage')}
              value=""
              onChange={(e) => e.target.value && setParams({ image: e.target.value })}
              slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
              sx={{ mt: 2 }}
            >
              <option value="">{t('profileCreate.chooseImagePlaceholder')}</option>
              {images.data.map((image) => (
                <option key={image.imageDigest} value={image.imageDigest}>
                  {t('images.listEntry', { capability: image.capabilityId, version: image.capabilityVersion })}
                </option>
              ))}
            </TextField>
          ))}
      </Frame>
    );
  }
  return <ProfileCreateLoader digest={digest} />;
}

function ProfileCreateLoader({ digest }: { digest: string }) {
  const { t } = useTranslation('registry');
  const image = useImage(digest);
  const profiles = useProfiles();
  const failure = image.error ? toPlatformFailure(image.error) : profiles.error ? toPlatformFailure(profiles.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  return (
    <Frame>
      {failure &&
        (failure.status === 404 && image.error ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('image.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => { void image.refetch(); void profiles.refetch(); }} />
        ))}
      {(image.isPending || profiles.isPending) && !failure && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {image.data && profiles.data && <ProfileForm declaration={image.data} existing={profiles.data} />}
    </Frame>
  );
}

function ProfileForm({ declaration, existing }: { declaration: CapabilityDeclaration; existing: ProfileVersion[] }) {
  const { t } = useTranslation('registry');
  const draft = useMemo(() => draftFromDeclaration(declaration, existing), [declaration, existing]);
  const create = useCreateProfile();
  const [profileId, setProfileId] = useState(draft.profileId);
  const [constraints, setConstraints] = useState(joinLines(draft.content.runtimeConstraints));
  const [accelerators, setAccelerators] = useState(joinLines(draft.content.accelerators));
  const [resources, setResources] = useState(joinPairs(draft.content.resources));
  const [schemas, setSchemas] = useState(joinPairs(draft.content.inputSchemaVersions));
  const [numbers, setNumbers] = useState<Record<NumberField, string>>({
    maxAttempts: String(draft.content.maxAttempts),
    leaseSeconds: String(draft.content.leaseSeconds),
    staleTimeoutSeconds: String(draft.content.staleTimeoutSeconds),
    executionTimeoutSeconds: String(draft.content.executionTimeoutSeconds),
  });
  const [invalid, setInvalid] = useState<Set<string>>(new Set());
  const [result, setResult] = useState<{ profile: ProfileVersion; created: boolean } | undefined>();

  const error = create.error;
  const refusal = error instanceof RegistryRefusalError ? error.refusal : undefined;
  const failure = error && !refusal ? toPlatformFailure(error) : undefined;
  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const detailsFor = (field: string): RegistryRefusalDetail[] => (refusal?.details ?? []).filter((detail) => detail.field === field);
  // Contradictions of a field are shown as that field's error text.
  const fieldProps = (field: string, hint?: string, localError?: string) => {
    const details = detailsFor(field);
    const message = localError ?? (details.length > 0 ? details.map((d) => t('refusal.detail', { field: d.field, code: d.code })).join(' ') : undefined);
    return { error: message !== undefined, helperText: message ?? hint };
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (create.isPending) return;
    const problems = new Set<string>();
    if (profileId.trim() === '') problems.add('profileId');
    const parsedNumbers = {} as Record<NumberField, number>;
    for (const name of NUMBER_FIELDS) {
      const text = numbers[name].trim();
      const value = Number(text);
      if (!/^\d+$/.test(text) || !Number.isSafeInteger(value) || value <= 0) problems.add(name);
      else parsedNumbers[name] = value;
    }
    const parsedResources = parsePairs(resources);
    const parsedSchemas = parsePairs(schemas);
    if (!parsedResources) problems.add('resources');
    if (!parsedSchemas) problems.add('inputSchemaVersions');
    setInvalid(problems);
    if (problems.size > 0 || !parsedResources || !parsedSchemas) return;
    setResult(undefined);
    create.mutate(
      {
        imageDigest: draft.imageDigest,
        profileId: profileId.trim(),
        content: {
          ...draft.content,
          ...parsedNumbers,
          runtimeConstraints: lines(constraints),
          accelerators: lines(accelerators),
          resources: parsedResources,
          inputSchemaVersions: parsedSchemas,
        },
      },
      { onSuccess: setResult },
    );
  };

  const listId = 'profile-id-options';
  const numberLabel = (name: NumberField) => t(`field.${name}`);
  return (
    <>
      <Typography sx={{ mt: 2 }}>{t('profileCreate.intro')}</Typography>
      <Box component="dl" sx={{ m: 0 }}>
        <Box component="dt" sx={{ fontWeight: 'bold', mt: 1 }}>{t('field.capabilityId')}</Box>
        <Box component="dd" sx={{ m: 0 }}>{t('profiles.capability', { capability: draft.content.capabilityId, version: draft.content.capabilityVersion })}</Box>
        <Box component="dt" sx={{ fontWeight: 'bold', mt: 1 }}>{t('field.imageDigest')}</Box>
        <Box component="dd" sx={{ m: 0, overflowWrap: 'anywhere' }}><DigestText value={draft.imageDigest} /></Box>
        <Box component="dt" sx={{ fontWeight: 'bold', mt: 1 }}>{t('field.modelDigest')}</Box>
        <Box component="dd" sx={{ m: 0, overflowWrap: 'anywhere' }}><DigestText value={draft.content.modelDigest} /></Box>
      </Box>
      <Stack component="form" spacing={2} onSubmit={submit} noValidate sx={{ mt: 2, alignItems: 'flex-start' }}>
        <TextField
          fullWidth
          label={t('profileCreate.profileId')}
          value={profileId}
          onChange={(e) => setProfileId(e.target.value)}
          {...fieldProps('profileId', t('profileCreate.profileIdHint'), invalid.has('profileId') ? t('profileCreate.profileIdRequired') : undefined)}
          slotProps={{ htmlInput: { list: listId, autoComplete: 'off', spellCheck: false } }}
        />
        <datalist id={listId}>
          {draft.offeredProfileIds.map((id) => <option key={id} value={id} />)}
        </datalist>
        <Typography variant="h3">{t('profileCreate.contentTitle')}</Typography>
        <TextField
          fullWidth multiline minRows={2}
          label={t('field.runtimeConstraints')}
          value={constraints}
          onChange={(e) => setConstraints(e.target.value)}
          {...fieldProps('content.runtimeConstraints', t('profileCreate.listHint'))}
        />
        <TextField
          fullWidth multiline minRows={2}
          label={t('field.accelerators')}
          value={accelerators}
          onChange={(e) => setAccelerators(e.target.value)}
          {...fieldProps('content.accelerators', t('profileCreate.listHint'))}
        />
        <TextField
          fullWidth multiline minRows={2}
          label={t('field.resources')}
          value={resources}
          onChange={(e) => setResources(e.target.value)}
          {...fieldProps('content.resources', t('profileCreate.pairHint'), invalid.has('resources') ? t('profileCreate.pairInvalid') : undefined)}
        />
        {NUMBER_FIELDS.map((name) => (
          <TextField
            key={name}
            fullWidth
            label={numberLabel(name)}
            value={numbers[name]}
            onChange={(e) => setNumbers((current) => ({ ...current, [name]: e.target.value }))}
            {...fieldProps(`content.${name}`, undefined, invalid.has(name) ? t('profileCreate.numberInvalid') : undefined)}
            slotProps={{ htmlInput: { inputMode: 'numeric' } }}
          />
        ))}
        <TextField
          fullWidth multiline minRows={2}
          label={t('field.inputSchemaVersions')}
          value={schemas}
          onChange={(e) => setSchemas(e.target.value)}
          {...fieldProps('content.inputSchemaVersions', t('profileCreate.pairHint'), invalid.has('inputSchemaVersions') ? t('profileCreate.pairInvalid') : undefined)}
        />
        <Button type="submit" variant="contained" disabled={create.isPending}>
          {create.isPending ? t('profileCreate.submitting') : t('profileCreate.submit')}
        </Button>
      </Stack>
      {create.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('profileCreate.submitting')}</Typography>}
      {refusal && <RefusalAlert refusal={refusal} />}
      {failure && <FailureAlert failure={failure} />}
      {result && (
        <Alert severity={result.created ? 'success' : 'info'} role="status" sx={{ mt: 2 }}>
          <strong>{result.created ? t('profileCreate.resultTitle') : t('outcome.alreadyExisted')}</strong>{' '}
          <Link to={`/registry/profiles/${encodeURIComponent(result.profile.profileId)}/versions/${result.profile.version}`}>
            {t('profileCreate.viewVersion')}
          </Link>
        </Alert>
      )}
    </>
  );
}
