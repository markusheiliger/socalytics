import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Box from '@mui/material/Box';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { ArtifactKind, MemberRef } from '../../platform/models/agentCatalog';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { DigestText } from '../registry/DigestText';
import { FailureAlert } from '../shared/FailureAlert';
import { useAgentVersion, useEnablements } from './agentCatalogKeys';
import { ChangeEnablementDialog } from './ChangeEnablementDialog';
import { CatalogText } from './CatalogText';
import { DistanceList } from './artifacts/DistanceList';

const KIND_SEGMENTS: Record<string, string> = {
  'prompt-template': 'prompt-templates',
  'model-selection': 'model-selections',
  'tool-policy': 'tool-policies',
  'safety-policy': 'safety-policies',
  'dimension-set': 'dimension-sets',
  'philosophy-profile': 'philosophy-profiles',
  'modifier-policy': 'modifier-policies',
};

/** Route segment of an artifact kind; undefined for a kind without a view. */
function kindSegment(kind: ArtifactKind): string | undefined {
  return Object.hasOwn(KIND_SEGMENTS, kind) ? KIND_SEGMENTS[kind] : undefined;
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <Box component="dt" sx={{ fontWeight: 'bold', mt: 1 }}>{label}</Box>
      <Box component="dd" sx={{ m: 0, overflowWrap: 'anywhere' }}>{children}</Box>
    </>
  );
}

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

/** One agent version with every field of FR-005, read-only; digests are shortened through DigestText (FR-008). */
export function AgentVersionPage() {
  const { agentId = '', version = '' } = useParams();
  const { t } = useTranslation('agentCatalog');
  const { t: tr } = useTranslation('registry');
  const query = useAgentVersion(agentId, version);
  const enablementsQuery = useEnablements();
  const [enabling, setEnabling] = useState(false);
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const v = query.data;
  const enablement = enablementsQuery.data?.find((e) => e.agentId === agentId);
  const who = (ref: MemberRef) => {
    if (ref.kind === 'seed') return t('common.developmentSeed');
    if (ref.kind === 'member') return ref.displayName ?? ref.id;
    return ref.id ? tr('member.unknownWithId', { id: ref.id }) : tr('member.unknown');
  };
  const when = (date: Date) => date.toLocaleString();
  const known = (namespace: string, value: string) => t(`${namespace}.${value}`, { defaultValue: value });

  return (
    <Frame agentId={agentId}>
      {failure &&
        (failure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => void query.refetch()} />
        ))}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {v && (
        <>
          <Typography variant="h2" sx={{ mt: 2 }}>{t('version.title', { agentId: v.agentId, version: v.version })}</Typography>
          <Box component="dl" sx={{ m: 0 }}>
            <Field label={t('version.state')}>{known('states', v.state)}</Field>
            <Field label={t('version.displayName')}>{v.displayName}</Field>
            <Field label={t('common.digest')}><DigestText value={v.digest} /></Field>
            {v.group === 'coach' && (
              <Field label={t('version.disclosure')}>
                {v.disclosure ? <CatalogText text={v.disclosure} label={t('version.disclosure')} /> : t('version.noDisclosure')}
              </Field>
            )}
          </Box>
          <Typography variant="h3" sx={{ mt: 2 }}>{t('version.artifactsTitle')}</Typography>
          <Box component="ul" sx={{ m: 0, pl: 2 }}>
            {v.artifacts.map((a) => {
              const segment = kindSegment(a.kind);
              const text = t('version.artifactEntry', {
                kind: known('artifactKind', a.kind),
                id: a.id,
                version: a.version,
                interpolation: { escapeValue: false },
              });
              return (
                <li key={`${a.kind}/${a.id}/${a.version}`}>
                  {segment ? (
                    <Link to={`/agent-catalog/artifacts/${segment}/${encodeURIComponent(a.id)}/versions/${encodeURIComponent(a.version)}`}>{text}</Link>
                  ) : (
                    text
                  )}{' '}
                  <DigestText value={a.digest} />
                </li>
              );
            })}
          </Box>
          <Typography sx={{ mt: 2 }}>{t('version.registeredBy', { who: who(v.registeredBy), when: when(v.registeredAt), interpolation: { escapeValue: false } })}</Typography>
          {v.approvedBy && v.approvedAt && (
            <Typography>{t('version.approvedBy', { who: who(v.approvedBy), when: when(v.approvedAt), interpolation: { escapeValue: false } })}</Typography>
          )}
          {v.retiredBy && v.retiredAt && (
            <Typography>{t('version.retiredBy', { who: who(v.retiredBy), when: when(v.retiredAt), interpolation: { escapeValue: false } })}</Typography>
          )}
          {v.selfApproval && <Typography>{t('version.selfApproval')}</Typography>}
          {v.differentiationAcknowledged && <Typography>{t('version.differentiationAcknowledged')}</Typography>}
          <Typography>{v.enabled ? t('version.enabled') : t('version.notEnabled')}</Typography>
          {v.state === 'approved' && !v.enabled && enablement && (
            <Button variant="contained" sx={{ mt: 1 }} onClick={() => setEnabling(true)}>{t('version.enable')}</Button>
          )}
          <Box sx={{ mt: 1 }}>
            <Link to={`/agent-catalog/agents/${encodeURIComponent(agentId)}/versions/new?from=${encodeURIComponent(v.version)}`}>
              {t('version.newVersion')}
            </Link>
          </Box>
          {enabling && enablement && (
            <ChangeEnablementDialog
              agentId={agentId}
              group={v.group}
              current={enablement}
              nextVersion={v.version}
              open
              onClose={() => setEnabling(false)}
            />
          )}
          {v.group === 'coach' && (
            <>
              <Typography variant="h3" sx={{ mt: 2 }}>{t('version.distancesTitle')}</Typography>
              <DistanceList distances={v.distances ?? []} />
            </>
          )}
        </>
      )}
    </Frame>
  );
}
