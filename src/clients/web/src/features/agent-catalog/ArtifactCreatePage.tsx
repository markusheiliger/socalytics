import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useQueries, useQuery } from '@tanstack/react-query';
import { useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type {
  ArtifactKind,
  CatalogViolation,
  DimensionSetDocument,
  ModelSelectionDocument,
  ModifierPolicyDocument,
  PhilosophyProfileDocument,
  PromptTemplateDocument,
  SafetyPolicyDocument,
  ToolPolicyDocument,
} from '../../platform/models/agentCatalog';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { agentCatalogKeys, useAgentCatalogAdapter, useCatalogBaseline, useRegisterArtifact } from './agentCatalogKeys';
import { KIND_OF_PATH } from './ArtifactsPage';
import { DimensionSetForm } from './artifacts/DimensionSetForm';
import { ModelSelectionForm } from './artifacts/ModelSelectionForm';
import { ModifierPolicyForm } from './artifacts/ModifierPolicyForm';
import { PhilosophyProfileForm, type DimensionSetChoice } from './artifacts/PhilosophyProfileForm';
import { PromptTemplateForm } from './artifacts/PromptTemplateForm';
import { SafetyPolicyForm } from './artifacts/SafetyPolicyForm';
import { ToolPolicyForm } from './artifacts/ToolPolicyForm';
import { RESPONSE_CONTRACT } from './catalogLimits';
import { refusalText, violationText } from './outcomeText';
import { isSemVer, proposeNextVersion } from './versionProposal';
import { ViolationSummary } from './ViolationSummary';

const KEBAB_CASE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const SCHEMA_VERSION = '1.0.0';
const IDENTITY_KEYS = ['schemaVersion', 'kind', 'id', 'version'];

type FormKind =
  | 'prompt-template'
  | 'model-selection'
  | 'tool-policy'
  | 'safety-policy'
  | 'dimension-set'
  | 'philosophy-profile'
  | 'modifier-policy';
type Body = Record<string, unknown>;

const FORM_KINDS: readonly string[] = [
  'prompt-template',
  'model-selection',
  'tool-policy',
  'safety-policy',
  'dimension-set',
  'philosophy-profile',
  'modifier-policy',
];

function emptyBody(kind: FormKind): Body {
  switch (kind) {
    case 'prompt-template':
      return { group: 'specialist', responseContract: RESPONSE_CONTRACT, text: '', slots: [] } satisfies PromptTemplateDocument;
    case 'model-selection':
      return { provider: 'openai', model: '', deployment: '', parameters: {} } satisfies ModelSelectionDocument;
    case 'tool-policy':
      return { tools: [] } satisfies ToolPolicyDocument;
    case 'safety-policy':
      return {
        group: 'specialist',
        routes: [],
        hardBoundaries: [''],
        escalationCategories: [],
        refusalCategories: [],
        rules: [],
      } satisfies SafetyPolicyDocument;
    case 'dimension-set':
      return { dimensions: [] } satisfies DimensionSetDocument;
    case 'philosophy-profile':
      return {
        dimensionSet: { id: '', version: '' },
        weights: {},
        author: '',
        reviewers: { architecture: [''], footballDomain: [''] },
      } satisfies PhilosophyProfileDocument;
    case 'modifier-policy':
      return {
        dimensionSetMajor: 1,
        perDimensionBound: 1,
        maxPositiveMovement: 1,
        contextCategories: [],
        budgetNeutral: true,
      } satisfies ModifierPolicyDocument;
  }
}

// Loads the registered dimension sets a profile can be based on.
function ProfileFormHost(props: Omit<React.ComponentProps<typeof PhilosophyProfileForm>, 'dimensionSets'>) {
  const adapter = useAgentCatalogAdapter();
  const list = useQuery({
    queryKey: agentCatalogKeys.artifacts('dimension-set'),
    queryFn: () => adapter.listArtifacts('dimension-set'),
    retry: false,
  });
  const summaries = list.data ?? [];
  const records = useQueries({
    queries: summaries.map((summary) => ({
      queryKey: agentCatalogKeys.artifact('dimension-set', summary.id, summary.version),
      queryFn: () => adapter.getArtifact('dimension-set', summary.id, summary.version),
      retry: false,
    })),
  });
  const dimensionSets: DimensionSetChoice[] = records.flatMap((query) =>
    query.data ? [{ id: query.data.id, version: query.data.version, document: query.data.document as DimensionSetDocument }] : [],
  );
  return <PhilosophyProfileForm {...props} dimensionSets={dimensionSets} />;
}

function withoutIdentity(document: Body): Body {
  return Object.fromEntries(Object.entries(document).filter(([key]) => !IDENTITY_KEYS.includes(key)));
}

function Frame({ kindSegment, children }: { kindSegment?: string; children?: React.ReactNode }) {
  const { t } = useTranslation('agentCatalog');
  const { t: tc } = useTranslation('common');
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Link to={kindSegment ? `/agent-catalog/artifacts/${kindSegment}` : '/agent-catalog'}>
        {kindSegment ? t('artifacts.title', { kind: t(`artifactKinds.${kindSegment}`) }) : t('overview.title')}
      </Link>
      {children}
    </Container>
  );
}

/** Registers a record of one artifact kind, optionally starting from a registered record (FR-009, FR-013). */
export function ArtifactCreatePage() {
  const { t } = useTranslation('agentCatalog');
  const { kind: kindSegment = '' } = useParams();
  const known = Object.hasOwn(KIND_OF_PATH, kindSegment);
  if (!known) {
    return (
      <Frame>
        <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
      </Frame>
    );
  }
  if (!FORM_KINDS.includes(KIND_OF_PATH[kindSegment])) {
    return (
      <Frame kindSegment={kindSegment}>
        <Typography sx={{ mt: 2 }}>{t('create.unsupported')}</Typography>
      </Frame>
    );
  }
  return <ArtifactCreateLoader kindSegment={kindSegment} kind={KIND_OF_PATH[kindSegment] as FormKind} />;
}

// Loads the record to start from, so the form mounts with every field prefilled.
function ArtifactCreateLoader({ kindSegment, kind }: { kindSegment: string; kind: FormKind }) {
  const { t } = useTranslation('agentCatalog');
  const [params] = useSearchParams();
  const fromId = params.get('fromId') ?? '';
  const fromVersion = params.get('fromVersion') ?? '';
  const from = fromId !== '' || fromVersion !== '';
  const validFrom = KEBAB_CASE.test(fromId) && isSemVer(fromVersion);
  const source = useSource(kind, fromId, fromVersion, from && validFrom);
  const failure = source.error ? toPlatformFailure(source.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;
  if (from && !validFrom) {
    return (
      <Frame kindSegment={kindSegment}>
        <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
      </Frame>
    );
  }
  if (from && source.isLoading) {
    return (
      <Frame kindSegment={kindSegment}>
        <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>
      </Frame>
    );
  }
  if (failure) {
    return (
      <Frame kindSegment={kindSegment}>
        {failure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => void source.refetch()} />
        )}
      </Frame>
    );
  }
  const record = from ? source.data : undefined;
  return (
    <ArtifactCreateForm
      kindSegment={kindSegment}
      kind={kind}
      initialId={record ? record.id : ''}
      initialBody={record ? withoutIdentity(record.document as unknown as Body) : emptyBody(kind)}
    />
  );
}

function useSource(kind: ArtifactKind, id: string, version: string, enabled: boolean) {
  const adapter = useAgentCatalogAdapter();
  return useQuery({
    queryKey: agentCatalogKeys.artifact(kind, id, version),
    queryFn: () => adapter.getArtifact(kind, id, version),
    enabled,
    retry: false,
  });
}

interface FormProps {
  kindSegment: string;
  kind: FormKind;
  initialId: string;
  initialBody: Body;
}

function ArtifactCreateForm({ kindSegment, kind, initialId, initialBody }: FormProps) {
  const { t } = useTranslation('agentCatalog');
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const adapter = useAgentCatalogAdapter();
  const register = useRegisterArtifact<ArtifactKind>();
  const baseline = useCatalogBaseline();
  const submitting = useRef(false);

  const [id, setId] = useState(initialId);
  const [enteredVersion, setEnteredVersion] = useState<string | null>(null);
  const [body, setBody] = useState<Body>(initialBody);
  const [violations, setViolations] = useState<CatalogViolation[]>([]);
  const [refusal, setRefusal] = useState<string | undefined>();
  const [replayed, setReplayed] = useState<string | undefined>();
  const [failure, setFailure] = useState<ReturnType<typeof toPlatformFailure> | undefined>();

  const idValid = KEBAB_CASE.test(id);
  const registered = useQuery({
    queryKey: agentCatalogKeys.artifacts(kind, { id }),
    queryFn: () => adapter.listArtifacts(kind, { id }),
    enabled: idValid,
    retry: false,
  });
  const proposal = proposeNextVersion(idValid ? (registered.data ?? []).map((record) => record.version) : []);
  const version = enteredVersion ?? proposal;

  const at = (location: string) => violations.filter((violation) => violation.location === location);
  const agent = baseline.data?.agents.find((candidate) => candidate.agentId === params.get('agentId'));
  const waitingForBaseline = (kind === 'tool-policy' || kind === 'dimension-set') && !baseline.data;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting.current) return;
    submitting.current = true;
    setViolations([]);
    setRefusal(undefined);
    setReplayed(undefined);
    setFailure(undefined);
    try {
      const document = { schemaVersion: SCHEMA_VERSION, kind, id, version, ...body };
      const outcome = await register.mutateAsync({ kind, document: document as never });
      const address = (value: { id: string; version: string }) =>
        `/agent-catalog/artifacts/${kindSegment}/${encodeURIComponent(value.id)}/versions/${encodeURIComponent(value.version)}`;
      if (outcome.kind === 'created') {
        navigate(address(outcome.value));
      } else if (outcome.kind === 'replayed') {
        setReplayed(address(outcome.value));
      } else if (outcome.kind === 'versionExists') {
        setRefusal('agent-catalog-version-exists');
      } else if (outcome.kind === 'validationFailed') {
        setViolations(outcome.violations);
      }
    } catch (error) {
      setFailure(toPlatformFailure(error));
    } finally {
      submitting.current = false;
    }
  };

  const placed = (location: string) =>
    at(location).map((violation, index) => {
      const { key, values } = violationText(violation.code, violation.detail);
      return (
        <Box key={index} component="span" role="alert" sx={{ display: 'block' }}>
          {t(key, values)}
        </Box>
      );
    });

  const renderForm = () => {
    switch (kind) {
      case 'prompt-template':
        return <PromptTemplateForm value={body as unknown as PromptTemplateDocument} onChange={setBody as never} violations={violations} disabled={register.isPending} />;
      case 'model-selection':
        return <ModelSelectionForm value={body as unknown as ModelSelectionDocument} onChange={setBody as never} violations={violations} disabled={register.isPending} />;
      case 'tool-policy':
        return <ToolPolicyForm value={body as unknown as ToolPolicyDocument} onChange={setBody as never} allowlist={baseline.data?.tools ?? []} agent={agent} violations={violations} />;
      case 'safety-policy':
        return <SafetyPolicyForm value={body as unknown as SafetyPolicyDocument} onChange={setBody as never} violations={violations} disabled={register.isPending} />;
      case 'dimension-set':
        return <DimensionSetForm value={body as unknown as DimensionSetDocument} onChange={setBody as never} allowlist={baseline.data?.tools ?? []} violations={violations} disabled={register.isPending} />;
      case 'philosophy-profile':
        return <ProfileFormHost value={body as unknown as PhilosophyProfileDocument} onChange={setBody as never} violations={violations} disabled={register.isPending} />;
      case 'modifier-policy':
        return <ModifierPolicyForm value={body as unknown as ModifierPolicyDocument} onChange={setBody as never} violations={violations} disabled={register.isPending} />;
    }
  };

  const baselineFailure = baseline.error ? toPlatformFailure(baseline.error) : undefined;
  const refusalMessage = refusal ? refusalText(refusal) : undefined;

  return (
    <Frame kindSegment={kindSegment}>
      <Typography variant="h2" sx={{ mt: 2 }}>{t('create.title', { kind: t(`artifactKind.${kind}`) })}</Typography>
      {baselineFailure && <FailureAlert failure={baselineFailure} onReload={() => void baseline.refetch()} />}
      {waitingForBaseline && !baselineFailure && <Typography role="status">{t('overview.loading')}</Typography>}
      {!waitingForBaseline && (
        <Box component="form" onSubmit={submit} noValidate sx={{ mt: 2 }}>
          {violations.length > 0 && <Typography role="alert" sx={{ mb: 1 }}>{t('violations.title')}</Typography>}
          <ViolationSummary violations={violations} />
          {failure && <FailureAlert failure={failure} />}
          {refusalMessage && (
            <Alert severity="error" role="alert" sx={{ mb: 2 }}>{t(refusalMessage.key, refusalMessage.values)}</Alert>
          )}
          {replayed && (
            <Alert severity="info" role="status" sx={{ mb: 2 }}>
              {t('create.replayed')} <Link to={replayed}>{t('create.openRecord')}</Link>
            </Alert>
          )}
          <Stack spacing={2}>
            <TextField
              required
              label={t('fields.id')}
              value={id}
              disabled={register.isPending}
              error={at('field:id').length > 0 || (id !== '' && !idValid)}
              onChange={(event) => setId(event.target.value)}
              helperText={<>{t('hints.idFormat')}{placed('field:id')}</>}
            />
            <TextField
              required
              label={t('fields.version')}
              value={version}
              disabled={register.isPending}
              error={at('field:version').length > 0 || !isSemVer(version)}
              onChange={(event) => setEnteredVersion(event.target.value)}
              helperText={
                <>
                  {enteredVersion === null || enteredVersion === proposal ? t('hints.versionProposed') : t('hints.versionFormat')}
                  {placed('field:version')}
                </>
              }
            />
            {renderForm()}
            <Box>
              <Button type="submit" variant="contained" disabled={register.isPending}>
                {register.isPending ? t('common.working') : t('create.submit')}
              </Button>
            </Box>
          </Stack>
        </Box>
      )}
    </Frame>
  );
}
