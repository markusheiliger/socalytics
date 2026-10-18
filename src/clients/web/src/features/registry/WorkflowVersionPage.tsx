import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { MemberRef } from '../../platform/models/registry';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { DigestText } from './DigestText';
import { describeEdge, type RegistryText } from './edgeText';
import { useDefaultWorkflow, useMemberNames, useWorkflow } from './registryKeys';

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <Box component="dt" sx={{ fontWeight: 'bold', mt: 1 }}>{label}</Box>
      <Box component="dd" sx={{ m: 0, overflowWrap: 'anywhere' }}>{children}</Box>
    </>
  );
}

/** One workflow version with its nodes linked to their profile versions and its edges described in words (FR-009). */
export function WorkflowVersionPage() {
  const { workflowId = '', version = '' } = useParams();
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  const query = useWorkflow(workflowId, version);
  const current = useDefaultWorkflow();
  const names = useMemberNames();
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const w = query.data;
  const known = (namespace: string, value: string) => t(`${namespace}.${value}`, { defaultValue: value });
  const text = ({ key, values, defaultValue }: RegistryText) => t(key, { ...values, defaultValue });
  const who = (ref: MemberRef) => {
    const resolved = names.data ? names.data(ref.id) : ref;
    if (resolved.kind === 'seed') return t('member.seed');
    if (resolved.kind === 'member') return resolved.displayName ?? resolved.id;
    return resolved.id ? t('member.unknownWithId', { id: resolved.id }) : t('member.unknown');
  };
  const isDefault =
    w !== undefined && !!current.data && current.data.workflowId === w.workflowId && current.data.workflowVersion === w.workflowVersion;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('workflowVersion.title')}</Typography>
      <Link to="/registry/workflows">{t('workflowVersion.back')}</Link>
      {failure &&
        (failure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('workflowVersion.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => void query.refetch()} />
        ))}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {w && (
        <>
          <Typography variant="h3" sx={{ mt: 2 }}>{t('workflowVersion.heading', { workflowId: w.workflowId, version: w.workflowVersion })}</Typography>
          {isDefault && <Typography role="status">{t('workflowVersion.isDefault')}</Typography>}
          <Box component="dl" sx={{ m: 0 }}>
            <Field label={t('field.definitionRequestedCapabilities')}>{w.requestedCapabilities.length === 0 ? t('image.none') : w.requestedCapabilities.join(', ')}</Field>
            <Field label={t('field.completionPolicy')}>{known('completionPolicy', w.completionPolicy)}</Field>
            <Field label={t('field.segmentationVersion')}>{w.segmentationPolicy.version}</Field>
            <Field label={t('field.segmentationDuration')}>{w.segmentationPolicy.durationSeconds}</Field>
            <Field label={t('field.segmentationDigest')}><DigestText value={w.segmentationPolicy.digest} /></Field>
            <Field label={t('field.definitionDigest')}><DigestText value={w.definitionDigest} /></Field>
            <Field label={t('field.createdAt')}>{w.createdAt.toLocaleString()}</Field>
            <Field label={t('field.createdBy')}>{who(w.createdBy)}</Field>
          </Box>
          <Typography variant="h4" sx={{ mt: 2 }}>{t('workflowVersion.nodesTitle')}</Typography>
          <Box component="ul" aria-label={t('workflowVersion.nodesTitle')} sx={{ m: 0, pl: 2 }}>
            {w.nodes.map((node) => (
              <li key={node.nodeKey}>
                <Box component="span" sx={{ mr: 1 }}>{t('workflowVersion.nodeLabel', { key: node.nodeKey })}</Box>
                <Link to={`/registry/profiles/${encodeURIComponent(node.profileId)}/versions/${node.profileVersion}`}>
                  {t('profileVersion.heading', { profileId: node.profileId, version: node.profileVersion })}
                </Link>
              </li>
            ))}
          </Box>
          <Typography variant="h4" sx={{ mt: 2 }}>{t('workflowVersion.edgesTitle')}</Typography>
          {w.edges.length === 0 ? (
            <Typography>{t('workflowVersion.noEdges')}</Typography>
          ) : (
            <Box component="ul" aria-label={t('workflowVersion.edgesTitle')} sx={{ m: 0, pl: 2 }}>
              {w.edges.map((edge, index) => {
                const described = describeEdge(edge, w.nodes);
                return (
                  <li key={index}>
                    <div>{t('edge.withKind', { ...described.description.values, kind: text(described.kind) })}</div>
                    {described.predicate && <div>{text(described.predicate)}</div>}
                    {!described.nodesKnown && <div>{t('workflowVersion.edgeUnknownNodes')}</div>}
                  </li>
                );
              })}
            </Box>
          )}
        </>
      )}
    </Container>
  );
}
