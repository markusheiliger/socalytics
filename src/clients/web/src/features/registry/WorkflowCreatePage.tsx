import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import Container from '@mui/material/Container';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState, type FormEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import {
  RegistryRefusalError,
  type DefaultWorkflow,
  type EdgeKind,
  type PredicateKind,
  type ProfileVersion,
  type WorkflowDraft,
  type WorkflowVersion,
} from '../../platform/models/registry';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { RefusalAlert } from './RefusalAlert';
import { useDefaultWorkflow, useDefineWorkflow, useProfiles, useWorkflow } from './registryKeys';
import { checkDraft, draftFrom, emptyDraft, nodeCapabilities, requestedCapabilities, type DraftViolation } from './workflowDraft';

interface NodeRow {
  nodeKey: string;
  profileId: string;
  profileVersion: number;
  flagged: boolean;
}

interface EdgeRow {
  upstream: string;
  dependent: string;
  kind: EdgeKind;
  predicateKind: PredicateKind;
  predicateValue: string;
}

const EDGE_KINDS = ['required', 'optional', 'conditional'];
const PREDICATE_KINDS = ['capabilityRequested', 'nodeAccepted'];
const profileKey = (id: string, version: number) => `${id}|${version}`;

function Frame({ children }: { children?: ReactNode }) {
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('workflowCreate.title')}</Typography>
      <Link to="/registry/workflows">{t('workflowCreate.back')}</Link>
      {children}
    </Container>
  );
}

/** Defines a workflow version from approved profile versions, optionally starting from an existing version (FR-015). */
export function WorkflowCreatePage() {
  const [params] = useSearchParams();
  const fromId = params.get('fromId')?.trim() ?? '';
  const fromVersion = params.get('fromVersion')?.trim() ?? '';
  const profiles = useProfiles();
  const current = useDefaultWorkflow();
  const failure = profiles.error ? toPlatformFailure(profiles.error) : current.error ? toPlatformFailure(current.error) : undefined;
  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const reload = () => {
    void profiles.refetch();
    void current.refetch();
  };
  return (
    <Frame>
      {failure && <FailureAlert failure={failure} onReload={reload} />}
      {!failure && (profiles.isPending || current.isPending) && <LoadingText />}
      {profiles.data &&
        current.data !== undefined &&
        (fromId !== '' && fromVersion !== '' ? (
          <FromLoader fromId={fromId} fromVersion={fromVersion} profiles={profiles.data} />
        ) : (
          <DefaultLoader profiles={profiles.data} current={current.data} />
        ))}
    </Frame>
  );
}

function LoadingText() {
  const { t } = useTranslation('registry');
  return <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>;
}

// A new definition takes its segmentation policy from the definition of the default workflow, which is loaded first.
function DefaultLoader({ profiles, current }: { profiles: ProfileVersion[]; current: DefaultWorkflow | null }) {
  if (!current) return <WorkflowForm profiles={profiles} initial={emptyDraft(null)} />;
  return <DefaultDefinitionLoader profiles={profiles} current={current} />;
}

function DefaultDefinitionLoader({ profiles, current }: { profiles: ProfileVersion[]; current: DefaultWorkflow }) {
  const definition = useWorkflow(current.workflowId, current.workflowVersion);
  const failure = definition.error ? toPlatformFailure(definition.error) : undefined;
  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;
  if (failure) return <FailureAlert failure={failure} onReload={() => void definition.refetch()} />;
  if (!definition.data) return <LoadingText />;
  return <WorkflowForm profiles={profiles} initial={emptyDraft(definition.data)} />;
}

function FromLoader({ fromId, fromVersion, profiles }: { fromId: string; fromVersion: string; profiles: ProfileVersion[] }) {
  const { t } = useTranslation('registry');
  const source = useWorkflow(fromId, fromVersion);
  const failure = source.error ? toPlatformFailure(source.error) : undefined;
  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;
  if (failure) {
    return failure.status === 404 ? (
      <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('workflowVersion.notFound')}</Alert>
    ) : (
      <FailureAlert failure={failure} onReload={() => void source.refetch()} />
    );
  }
  if (!source.data) return <LoadingText />;
  return <WorkflowForm profiles={profiles} initial={draftFrom(source.data, profiles)} source={source.data} />;
}

function WorkflowForm({ profiles, initial, source }: { profiles: ProfileVersion[]; initial: WorkflowDraft; source?: WorkflowVersion }) {
  const { t } = useTranslation('registry');
  const define = useDefineWorkflow();
  const approved = profiles.filter((p) => p.state === 'approved');
  const policy = initial.segmentationPolicy;

  const [workflowId, setWorkflowId] = useState(initial.workflowId);
  const [workflowVersion, setWorkflowVersion] = useState('');
  const [chosen, setChosen] = useState<string[] | undefined>(source ? initial.requestedCapabilities : undefined);
  const [completionPolicy, setCompletionPolicy] = useState(initial.completionPolicy);
  const [seg, setSegmentation] = useState({
    version: String(policy.version),
    duration: policy.durationSeconds > 0 ? String(policy.durationSeconds) : '',
    digest: policy.digest,
  });
  const [nodes, setNodes] = useState<NodeRow[]>(initial.nodes.map((n) => ({ nodeKey: n.nodeKey, profileId: n.profileId, profileVersion: n.profileVersion, flagged: n.flagged === true })));
  const [edges, setEdges] = useState<EdgeRow[]>(
    initial.edges.map((e) => ({
      upstream: e.upstreamNodeKey,
      dependent: e.dependentNodeKey,
      kind: e.kind,
      predicateKind: e.predicate?.kind ?? 'capabilityRequested',
      predicateValue: e.predicate?.kind === 'nodeAccepted' ? (e.predicate.nodeKey ?? '') : (e.predicate?.capabilityId ?? ''),
    })),
  );
  const [problems, setProblems] = useState<string[]>([]);
  const [fieldErrors, setFieldErrors] = useState<Set<string>>(new Set());
  const [result, setResult] = useState<WorkflowVersion | undefined>();

  const error = define.error;
  const refusal = error instanceof RegistryRefusalError ? error.refusal : undefined;
  const failure = error && !refusal ? toPlatformFailure(error) : undefined;
  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const draftNodes = nodes.map((n) => ({ nodeKey: n.nodeKey.trim(), profileId: n.profileId, profileVersion: n.profileVersion, ...(n.flagged ? { flagged: true } : {}) }));
  const offered = nodeCapabilities(draftNodes, profiles);
  const requested = requestedCapabilities(draftNodes, profiles, chosen);
  const nodeKeys = draftNodes.map((n) => n.nodeKey).filter((key) => key !== '');

  const toggleCapability = (id: string, on: boolean) => setChosen(on ? [...new Set([...requested, id])] : requested.filter((c) => c !== id));
  const updateNode = (index: number, patch: Partial<NodeRow>) => setNodes((rows) => rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  const updateEdge = (index: number, patch: Partial<EdgeRow>) => setEdges((rows) => rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));

  const describeViolation = (violation: DraftViolation): string => {
    const key = violation.node ?? '';
    switch (violation.code) {
      case 'no-nodes': return t('workflowCreate.nodeRequired');
      case 'duplicate-node-key': return t('workflowCreate.nodeKeyDuplicate') + (key ? ` (${key})` : '');
      case 'flagged-node': {
        const node = nodes.find((n) => n.nodeKey.trim() === key);
        return node && node.profileId === '' ? t('workflowCreate.nodeProfileRequired', { key }) : t('workflowCreate.nodeFlagged', { key });
      }
      case 'unknown-edge-node':
      case 'unknown-predicate-node': return t('workflowCreate.edgeUnknownNode');
      case 'invalid-duration': return t('workflowCreate.durationInvalid');
      case 'invalid-digest': return t('workflowCreate.digestInvalid');
    }
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (define.isPending) return;
    const messages: string[] = [];
    const errors = new Set<string>();
    if (workflowId.trim() === '') { errors.add('workflowId'); messages.push(t('workflowCreate.idRequired')); }
    if (workflowVersion.trim() === '') { errors.add('workflowVersion'); messages.push(t('workflowCreate.versionRequired')); }
    const segmentationVersion = Number(seg.version.trim());
    const duration = Number(seg.duration.trim());
    if (!/^\d+$/.test(seg.version.trim()) || segmentationVersion <= 0) { errors.add('segmentationVersion'); messages.push(t('workflowCreate.segmentationVersionInvalid')); }
    const draft: WorkflowDraft = {
      workflowId: workflowId.trim(),
      workflowVersion: workflowVersion.trim(),
      requestedCapabilities: requested,
      completionPolicy,
      segmentationPolicy: { version: segmentationVersion, durationSeconds: duration, digest: seg.digest.trim() },
      nodes: draftNodes,
      edges: edges.map((e) => ({
        upstreamNodeKey: e.upstream,
        dependentNodeKey: e.dependent,
        kind: e.kind,
        ...(e.kind === 'conditional'
          ? { predicate: e.predicateKind === 'nodeAccepted' ? { kind: e.predicateKind, nodeKey: e.predicateValue } : { kind: e.predicateKind, capabilityId: e.predicateValue } }
          : {}),
      })),
    };
    const violations = checkDraft(draft);
    for (const violation of violations) {
      messages.push(describeViolation(violation));
      if (violation.code === 'invalid-duration') errors.add('duration');
      if (violation.code === 'invalid-digest') errors.add('digest');
    }
    if (edges.some((e) => e.kind === 'conditional' && e.predicateValue === '' && e.predicateKind === 'capabilityRequested')) messages.push(t('workflowCreate.predicateRequired'));
    setFieldErrors(errors);
    setProblems([...new Set(messages)]);
    if (messages.length > 0) return;
    setResult(undefined);
    define.mutate(draft, { onSuccess: setResult });
  };

  const optionLabel = (p: ProfileVersion) => t('profileVersion.heading', { profileId: p.profileId, version: p.version });
  const edgeDescription = (e: EdgeRow) => t('edge.description', { upstream: e.upstream || '?', dependent: e.dependent || '?' });
  const nodeSelect = (label: string, value: string, onChange: (value: string) => void) => (
    <TextField select label={label} value={value} onChange={(e) => onChange(e.target.value)} slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
      <option value="" />
      {nodeKeys.map((key) => <option key={key} value={key}>{key}</option>)}
    </TextField>
  );
  const text = (key: string, label: string, value: string, onChange: (value: string) => void) => (
    <TextField
      fullWidth
      label={label}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      error={fieldErrors.has(key)}
      slotProps={{ htmlInput: { autoComplete: 'off', spellCheck: false } }}
    />
  );

  return (
    <>
      <Typography sx={{ mt: 2 }}>{t('workflowCreate.intro')}</Typography>
      {source && <Typography>{t('workflowCreate.startedFrom', { workflowId: source.workflowId, version: source.workflowVersion })}</Typography>}
      <Stack component="form" spacing={2} onSubmit={submit} noValidate sx={{ mt: 2, alignItems: 'flex-start' }}>
        {text('workflowId', t('workflowCreate.workflowId'), workflowId, setWorkflowId)}
        {text('workflowVersion', t('workflowCreate.workflowVersion'), workflowVersion, setWorkflowVersion)}

        <Typography variant="h3">{t('workflowCreate.nodesTitle')}</Typography>
        {nodes.map((node, index) => (
          <Stack key={index} component="fieldset" direction="row" spacing={2} sx={{ width: '100%', flexWrap: 'wrap', border: 0, p: 0, m: 0 }}>
            <TextField
              label={t('workflowCreate.nodeKey')}
              value={node.nodeKey}
              onChange={(e) => updateNode(index, { nodeKey: e.target.value })}
              slotProps={{ htmlInput: { 'aria-label': `${t('workflowCreate.nodeKey')} ${index + 1}`, autoComplete: 'off', spellCheck: false } }}
            />
            <TextField
              select
              label={t('workflowCreate.nodeProfile')}
              value={node.flagged ? '' : profileKey(node.profileId, node.profileVersion)}
              onChange={(e) => {
                const found = approved.find((p) => profileKey(p.profileId, p.version) === e.target.value);
                if (found) updateNode(index, { profileId: found.profileId, profileVersion: found.version, flagged: false });
                else updateNode(index, { flagged: true });
              }}
              error={node.flagged}
              helperText={node.flagged && node.profileId !== '' ? t('workflowCreate.nodeFlagged', { key: node.nodeKey }) : undefined}
              slotProps={{
                select: { native: true },
                inputLabel: { shrink: true },
                htmlInput: { 'aria-label': `${t('workflowCreate.nodeProfile')} ${index + 1}` },
              }}
            >
              <option value="">{t('workflowCreate.nodeProfilePlaceholder')}</option>
              {approved.map((p) => <option key={profileKey(p.profileId, p.version)} value={profileKey(p.profileId, p.version)}>{optionLabel(p)}</option>)}
            </TextField>
            <Button type="button" onClick={() => setNodes((rows) => rows.filter((_, i) => i !== index))}>
              {t('workflowCreate.removeNode', { key: node.nodeKey || index + 1 })}
            </Button>
          </Stack>
        ))}
        <Button type="button" variant="outlined" onClick={() => setNodes((rows) => [...rows, { nodeKey: '', profileId: '', profileVersion: 0, flagged: true }])}>
          {t('workflowCreate.addNode')}
        </Button>

        <Box component="fieldset" sx={{ border: 0, p: 0, m: 0 }}>
          <Typography component="legend" variant="h3">{t('workflowCreate.requestedCapabilities')}</Typography>
          <FormGroup>
            {offered.map((id) => (
              <FormControlLabel
                key={id}
                label={id}
                control={<Checkbox checked={requested.includes(id)} onChange={(e) => toggleCapability(id, e.target.checked)} />}
              />
            ))}
          </FormGroup>
        </Box>

        <TextField
          select
          label={t('workflowCreate.completionPolicy')}
          value={completionPolicy}
          onChange={(e) => setCompletionPolicy(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        >
          {[...new Set(['all-requested-capabilities', completionPolicy])].map((value) => (
            <option key={value} value={value}>{t(`completionPolicy.${value}`, { defaultValue: value })}</option>
          ))}
        </TextField>

        <Typography variant="h3">{t('workflowCreate.edgesTitle')}</Typography>
        {edges.map((edge, index) => (
          <Stack key={index} component="fieldset" direction="row" spacing={2} sx={{ width: '100%', flexWrap: 'wrap', border: 0, p: 0, m: 0 }}>
            {nodeSelect(t('workflowCreate.edgeUpstream'), edge.upstream, (value) => updateEdge(index, { upstream: value }))}
            {nodeSelect(t('workflowCreate.edgeDependent'), edge.dependent, (value) => updateEdge(index, { dependent: value }))}
            <TextField
              select
              label={t('workflowCreate.edgeKind')}
              value={edge.kind}
              onChange={(e) => updateEdge(index, { kind: e.target.value })}
              slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
            >
              {EDGE_KINDS.map((kind) => <option key={kind} value={kind}>{t(`edgeKind.${kind}`)}</option>)}
            </TextField>
            {edge.kind === 'conditional' && (
              <>
                <TextField
                  select
                  label={t('workflowCreate.edgePredicate')}
                  value={edge.predicateKind}
                  onChange={(e) => updateEdge(index, { predicateKind: e.target.value, predicateValue: '' })}
                  slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
                >
                  {PREDICATE_KINDS.map((kind) => <option key={kind} value={kind}>{t(`predicateKind.${kind}`)}</option>)}
                </TextField>
                {edge.predicateKind === 'nodeAccepted' ? (
                  nodeSelect(t('workflowCreate.edgePredicateNode'), edge.predicateValue, (value) => updateEdge(index, { predicateValue: value }))
                ) : (
                  <TextField
                    select
                    label={t('workflowCreate.edgePredicateCapability')}
                    value={edge.predicateValue}
                    onChange={(e) => updateEdge(index, { predicateValue: e.target.value })}
                    slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
                  >
                    <option value="" />
                    {offered.map((id) => <option key={id} value={id}>{id}</option>)}
                  </TextField>
                )}
              </>
            )}
            <Button type="button" onClick={() => setEdges((rows) => rows.filter((_, i) => i !== index))}>
              {t('workflowCreate.removeEdge', { description: edgeDescription(edge) })}
            </Button>
          </Stack>
        ))}
        <Button
          type="button"
          variant="outlined"
          onClick={() => setEdges((rows) => [...rows, { upstream: '', dependent: '', kind: 'required', predicateKind: 'capabilityRequested', predicateValue: '' }])}
        >
          {t('workflowCreate.addEdge')}
        </Button>

        <Typography variant="h3">{t('workflowCreate.segmentationTitle')}</Typography>
        {text('segmentationVersion', t('workflowCreate.segmentationVersion'), seg.version, (value) => setSegmentation({ ...seg, version: value }))}
        {text('duration', t('workflowCreate.segmentationDuration'), seg.duration, (value) => setSegmentation({ ...seg, duration: value }))}
        {text('digest', t('workflowCreate.segmentationDigest'), seg.digest, (value) => setSegmentation({ ...seg, digest: value }))}

        <Button type="submit" variant="contained" disabled={define.isPending}>
          {define.isPending ? t('workflowCreate.submitting') : t('workflowCreate.submit')}
        </Button>
      </Stack>
      {problems.length > 0 && (
        <Alert severity="error" role="alert" sx={{ mt: 2 }}>
          <Box component="ul" sx={{ m: 0, pl: 2 }}>
            {problems.map((message) => <li key={message}>{message}</li>)}
          </Box>
        </Alert>
      )}
      {define.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('workflowCreate.submitting')}</Typography>}
      {refusal && <RefusalAlert refusal={refusal} />}
      {failure && <FailureAlert failure={failure} />}
      {result && (
        <Alert severity="success" role="status" sx={{ mt: 2 }}>
          <strong>{t('workflowCreate.resultTitle')}</strong>{' '}
          <Link to={`/registry/workflows/${encodeURIComponent(result.workflowId)}/versions/${encodeURIComponent(result.workflowVersion)}`}>
            {t('workflowCreate.viewVersion')}
          </Link>
        </Alert>
      )}
    </>
  );
}
