import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useQueries } from '@tanstack/react-query';
import { useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type {
  AgentGroup,
  AgentVersionDocumentArtifacts,
  ArtifactKind,
  ArtifactRecord,
  ArtifactReference,
  ArtifactSummary,
  CatalogViolation,
} from '../../platform/models/agentCatalog';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import {
  agentCatalogKeys,
  useAgentCatalogAdapter,
  useAgentVersion,
  useAgentVersions,
  useCatalogBaseline,
  useRegisterAgentVersion,
} from './agentCatalogKeys';
import { refusalText, violationText } from './outcomeText';
import { isSemVer, proposeNextVersion } from './versionProposal';
import { ViolationSummary } from './ViolationSummary';

const SCHEMA_VERSION = '1.0.0';
const AGENT_ID = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

type ReferenceKey = keyof AgentVersionDocumentArtifacts;

const REFERENCES: ReadonlyArray<{ key: ReferenceKey; kind: ArtifactKind; coachOnly: boolean }> = [
  { key: 'promptTemplate', kind: 'prompt-template', coachOnly: false },
  { key: 'modelSelection', kind: 'model-selection', coachOnly: false },
  { key: 'toolPolicy', kind: 'tool-policy', coachOnly: false },
  { key: 'safetyPolicy', kind: 'safety-policy', coachOnly: false },
  { key: 'philosophyProfile', kind: 'philosophy-profile', coachOnly: true },
  { key: 'modifierPolicy', kind: 'modifier-policy', coachOnly: true },
];

// Prompt templates and safety policies are group-specific; the other kinds are not.
const GROUPED_KINDS: readonly ArtifactKind[] = ['prompt-template', 'safety-policy'];

const refKey = (reference: { id: string; version: string }) => `${reference.id}@${reference.version}`;
const majorOf = (version: string) => Number.parseInt(version.split('.')[0], 10);

function Frame({ agentId, children }: { agentId: string; children?: ReactNode }) {
  const { t } = useTranslation('agentCatalog');
  const { t: tc } = useTranslation('common');
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Link to={`/agent-catalog/agents/${encodeURIComponent(agentId)}`}>{t('version.back')}</Link>
      {children}
    </Container>
  );
}

/** Registers an agent version of the agent in the address, optionally starting from one of its versions (FR-009, FR-011). */
export function AgentVersionCreatePage() {
  const { agentId = '' } = useParams();
  const { t } = useTranslation('agentCatalog');
  const [params] = useSearchParams();
  const from = params.get('from') ?? '';
  const baseline = useCatalogBaseline();
  const validFrom = from === '' || isSemVer(from);
  const source = useAgentVersion(agentId, from);
  const sourceEnabled = from !== '' && validFrom;
  const failures = [baseline.error, sourceEnabled ? source.error : undefined].map((error) => (error ? toPlatformFailure(error) : undefined));
  const failure = failures.find((candidate) => candidate !== undefined);

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  if (!AGENT_ID.test(agentId) || !validFrom) {
    return (
      <Frame agentId={agentId}>
        <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
      </Frame>
    );
  }
  if (failure) {
    return (
      <Frame agentId={agentId}>
        {failure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => void (baseline.error ? baseline.refetch() : source.refetch())} />
        )}
      </Frame>
    );
  }
  if (!baseline.data || (sourceEnabled && !source.data)) {
    return (
      <Frame agentId={agentId}>
        <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>
      </Frame>
    );
  }
  const agent = baseline.data.agents.find((candidate) => candidate.agentId === agentId);
  if (!agent) {
    return (
      <Frame agentId={agentId}>
        <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
      </Frame>
    );
  }
  const record = sourceEnabled ? source.data : undefined;
  const initialRefs: Partial<Record<ReferenceKey, ArtifactReference>> = {};
  for (const { key, kind } of REFERENCES) {
    const found = record?.artifacts.find((artifact) => artifact.kind === kind);
    if (found) initialRefs[key] = { id: found.id, version: found.version, digest: found.digest };
  }
  return (
    <AgentVersionCreateForm
      agentId={agentId}
      group={agent.group}
      initialDisplayName={record ? record.displayName : agent.displayName}
      initialDisclosure={record?.disclosure ?? ''}
      initialRefs={initialRefs}
    />
  );
}

interface FormProps {
  agentId: string;
  group: AgentGroup;
  initialDisplayName: string;
  initialDisclosure: string;
  initialRefs: Partial<Record<ReferenceKey, ArtifactReference>>;
}

function AgentVersionCreateForm({ agentId, group, initialDisplayName, initialDisclosure, initialRefs }: FormProps) {
  const { t } = useTranslation('agentCatalog');
  const navigate = useNavigate();
  const adapter = useAgentCatalogAdapter();
  const register = useRegisterAgentVersion();
  const submitting = useRef(false);
  const required = REFERENCES.filter((reference) => group === 'coach' || !reference.coachOnly);

  const [displayName, setDisplayName] = useState(initialDisplayName);
  const [disclosure, setDisclosure] = useState(initialDisclosure);
  const [refs, setRefs] = useState(initialRefs);
  const [enteredVersion, setEnteredVersion] = useState<string | null>(null);
  const [violations, setViolations] = useState<CatalogViolation[]>([]);
  const [refusal, setRefusal] = useState<string | undefined>();
  const [replayed, setReplayed] = useState<string | undefined>();
  const [failure, setFailure] = useState<ReturnType<typeof toPlatformFailure> | undefined>();

  const registered = useAgentVersions({ agentId });
  const proposal = proposeNextVersion((registered.data ?? []).map((candidate) => candidate.version));
  const version = enteredVersion ?? proposal;

  const lists = useQueries({
    queries: required.map(({ kind }) => ({
      queryKey: agentCatalogKeys.artifacts(kind),
      queryFn: () => adapter.listArtifacts(kind),
      retry: false,
    })),
  });
  const summaries: ArtifactSummary[] = lists.flatMap((list) => list.data ?? []);
  // The group of a prompt template or safety policy and the dimension-set major versions are only part of the stored document.
  const needed = summaries.filter((summary) => GROUPED_KINDS.includes(summary.kind) || group === 'coach');
  const records = useQueries({
    queries: needed.map((summary) => ({
      queryKey: agentCatalogKeys.artifact(summary.kind, summary.id, summary.version),
      queryFn: () => adapter.getArtifact(summary.kind, summary.id, summary.version),
      retry: false,
    })),
  });
  const documents = new Map<string, ArtifactRecord>();
  records.forEach((query, index) => {
    if (query.data) documents.set(`${needed[index].kind}/${refKey(needed[index])}`, query.data);
  });
  const loadingDocuments = records.some((query) => query.isPending);
  const listFailure = lists.find((list) => list.error)?.error;

  const promptRef = refs.promptTemplate;
  const promptDocument = promptRef ? documents.get(`prompt-template/${refKey(promptRef)}`)?.document : undefined;
  const promptMajor = promptDocument && 'dimensionSetMajor' in promptDocument ? promptDocument.dimensionSetMajor : undefined;

  const matchesMajor = (summary: ArtifactSummary): boolean => {
    if (promptMajor === undefined) return false;
    const document = documents.get(`${summary.kind}/${refKey(summary)}`)?.document as unknown as Record<string, unknown> | undefined;
    if (!document) return false;
    if (summary.kind === 'modifier-policy') return document.dimensionSetMajor === promptMajor;
    if (summary.kind === 'philosophy-profile') {
      const set = document.dimensionSet as { version?: string } | undefined;
      return set?.version !== undefined && majorOf(set.version) === promptMajor;
    }
    return false;
  };

  const optionsFor = (kind: ArtifactKind, key: ReferenceKey) => {
    let options = summaries.filter((summary) => summary.kind === kind);
    if (GROUPED_KINDS.includes(kind)) {
      options = options.filter((summary) => {
        const document = documents.get(`${kind}/${refKey(summary)}`)?.document as unknown as { group?: string } | undefined;
        // A record whose document is not loaded yet stays offered rather than silently disappearing.
        return !document || document.group === group;
      });
    }
    const hinted = group === 'coach' && (kind === 'philosophy-profile' || kind === 'modifier-policy');
    if (hinted) {
      options = [...options.filter(matchesMajor), ...options.filter((summary) => !matchesMajor(summary))];
    }
    const current = refs[key];
    const entries = options.map((summary) => ({ value: refKey(summary), digest: summary.digest, hinted: hinted && matchesMajor(summary) }));
    if (current && !entries.some((entry) => entry.value === refKey(current))) {
      entries.unshift({ value: refKey(current), digest: current.digest, hinted: false });
    }
    return entries;
  };

  const at = (location: string) => violations.filter((violation) => violation.location === location);
  const placed = (location: string) =>
    at(location).map((violation, index) => {
      const { key, values } = violationText(violation.code, violation.detail);
      return (
        <Box key={index} component="span" role="alert" sx={{ display: 'block' }}>
          {t(key, values)}
        </Box>
      );
    });

  const choose = (key: ReferenceKey, kind: ArtifactKind, value: string) => {
    const summary = summaries.find((candidate) => candidate.kind === kind && refKey(candidate) === value);
    setRefs((previous) => {
      const next = { ...previous };
      if (summary) next[key] = { id: summary.id, version: summary.version, digest: summary.digest };
      else delete next[key];
      return next;
    });
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting.current) return;
    submitting.current = true;
    setViolations([]);
    setRefusal(undefined);
    setReplayed(undefined);
    setFailure(undefined);
    try {
      const artifacts: Record<string, ArtifactReference> = {};
      for (const { key } of required) {
        const chosen = refs[key];
        if (chosen) artifacts[key] = chosen;
      }
      const document = {
        schemaVersion: SCHEMA_VERSION,
        kind: 'agent-version',
        agentId,
        version,
        group,
        displayName,
        artifacts,
        ...(group === 'coach' ? { disclosure } : {}),
      };
      const outcome = await register.mutateAsync(document as never);
      const address = (value: { agentId: string; version: string }) =>
        `/agent-catalog/agents/${encodeURIComponent(value.agentId)}/versions/${encodeURIComponent(value.version)}`;
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

  const listFailureValue = listFailure ? toPlatformFailure(listFailure) : undefined;
  const refusalMessage = refusal ? refusalText(refusal) : undefined;

  return (
    <Frame agentId={agentId}>
      <Typography variant="h2" sx={{ mt: 2 }}>{t('versionCreate.title', { agentId })}</Typography>
      {listFailureValue && <FailureAlert failure={listFailureValue} onReload={() => lists.forEach((list) => void list.refetch())} />}
      <Box component="form" onSubmit={submit} noValidate sx={{ mt: 2 }}>
        {violations.length > 0 && <Typography role="alert" sx={{ mb: 1 }}>{t('violations.title')}</Typography>}
        <ViolationSummary violations={violations} />
        {failure && <FailureAlert failure={failure} />}
        {refusalMessage && <Alert severity="error" role="alert" sx={{ mb: 2 }}>{t(refusalMessage.key, refusalMessage.values)}</Alert>}
        {replayed && (
          <Alert severity="info" role="status" sx={{ mb: 2 }}>
            {t('create.replayed')} <Link to={replayed}>{t('create.openRecord')}</Link>
          </Alert>
        )}
        <Stack spacing={2}>
          <TextField
            label={t('fields.agentId')}
            value={agentId}
            disabled
            helperText={t(`versionCreate.group.${group}`)}
            slotProps={{ formHelperText: { sx: { '&.Mui-disabled': { color: 'text.secondary' } } } }}
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
          <TextField
            required
            label={t('fields.displayName')}
            value={displayName}
            disabled={register.isPending}
            error={at('field:displayName').length > 0}
            onChange={(event) => setDisplayName(event.target.value)}
            helperText={<>{t('versionCreate.displayNameHint')}{placed('field:displayName')}</>}
          />
          {required.map(({ key, kind }) => {
            const entries = optionsFor(kind, key);
            return (
              <TextField
                key={key}
                select
                required
                label={t(`fields.${key}`)}
                value={refs[key] ? refKey(refs[key]) : ''}
                disabled={register.isPending}
                error={at(`reference:${key}`).length > 0}
                onChange={(event) => choose(key, kind, event.target.value)}
                slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
                helperText={
                  <>
                    {entries.length === 0 && !loadingDocuments ? t('empty.artifactReferences') : t(`versionCreate.pick.${key}`)}
                    {placed(`reference:${key}`)}
                  </>
                }
              >
                <option value="">{t('versionCreate.choose')}</option>
                {entries.map((entry) => (
                  <option key={entry.value} value={entry.value}>
                    {entry.hinted ? t('versionCreate.matchingOption', { reference: entry.value }) : entry.value}
                  </option>
                ))}
              </TextField>
            );
          })}
          {group === 'coach' && promptMajor !== undefined && (
            <Typography variant="body2">{t('hints.coachDimensionSetMajor', { major: promptMajor })}</Typography>
          )}
          {group === 'coach' && (
            <TextField
              required
              multiline
              minRows={3}
              label={t('fields.disclosure')}
              value={disclosure}
              disabled={register.isPending}
              error={at('field:disclosure').length > 0}
              onChange={(event) => setDisclosure(event.target.value)}
              helperText={
                <>
                  {t('hints.lengthBetween', { min: 20, max: 1000 })}
                  {placed('field:disclosure')}
                </>
              }
            />
          )}
          <Box>
            <Button type="submit" variant="contained" disabled={register.isPending}>
              {register.isPending ? t('common.working') : t('create.submit')}
            </Button>
          </Box>
        </Stack>
      </Box>
    </Frame>
  );
}
