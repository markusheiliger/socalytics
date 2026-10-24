import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import type { ComponentType, ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { ArtifactRecord, DimensionSetDocument, PhilosophyProfileDocument } from '../../platform/models/agentCatalog';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { DigestText } from '../registry/DigestText';
import { FailureAlert } from '../shared/FailureAlert';
import { useArtifact } from './agentCatalogKeys';
import { KIND_OF_PATH } from './ArtifactsPage';
import { CatalogText } from './CatalogText';
import { DimensionSetView } from './artifacts/DimensionSetView';
import { ModelSelectionView } from './artifacts/ModelSelectionView';
import { ModifierPolicyView } from './artifacts/ModifierPolicyView';
import { PhilosophyProfileView } from './artifacts/PhilosophyProfileView';
import { PromptTemplateView } from './artifacts/PromptTemplateView';
import { SafetyPolicyView } from './artifacts/SafetyPolicyView';
import { ToolPolicyView } from './artifacts/ToolPolicyView';
import { Field } from './artifacts/ViewParts';
import { isSemVer } from './versionProposal';

const KEBAB_CASE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/** A view of one artifact kind, given the whole record. */
export type ArtifactViewMap = Readonly<Record<string, ComponentType<{ record: ArtifactRecord }>>>;

function ProfileRecordView({ record }: { record: ArtifactRecord }) {
  const document = record.document as unknown as PhilosophyProfileDocument;
  const set = useArtifact('dimension-set', document.dimensionSet.id, document.dimensionSet.version);
  return (
    <PhilosophyProfileView
      document={document}
      profileId={record.id}
      dimensionSet={set.data ? (set.data.document as DimensionSetDocument) : undefined}
      distances={record.distances}
    />
  );
}

export const ARTIFACT_VIEWS: ArtifactViewMap = {
  'prompt-template': ({ record }) => <PromptTemplateView document={record.document as never} />,
  'model-selection': ({ record }) => <ModelSelectionView document={record.document as never} />,
  'tool-policy': ({ record }) => <ToolPolicyView document={record.document as never} />,
  'safety-policy': ({ record }) => <SafetyPolicyView document={record.document as never} />,
  'dimension-set': ({ record }) => <DimensionSetView document={record.document as never} />,
  'philosophy-profile': ProfileRecordView,
  'modifier-policy': ({ record }) => <ModifierPolicyView document={record.document as never} />,
};

function Frame({ kindSegment, children }: { kindSegment?: string; children?: ReactNode }) {
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

/** One artifact record of a kind with a view, or its raw document for the other kinds (FR-006). */
export function ArtifactPage({ views = ARTIFACT_VIEWS }: { views?: ArtifactViewMap } = {}) {
  const { t } = useTranslation('agentCatalog');
  const { kind = '', artifactId = '', version = '' } = useParams();
  const valid = Object.hasOwn(KIND_OF_PATH, kind) && KEBAB_CASE.test(artifactId) && isSemVer(version);
  if (!valid) {
    return (
      <Frame kindSegment={Object.hasOwn(KIND_OF_PATH, kind) ? kind : undefined}>
        <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
      </Frame>
    );
  }
  return <ArtifactRecordView kindSegment={kind} artifactId={artifactId} version={version} views={views} />;
}

// Rendered only for a valid address, so an invalid address sends no request.
function ArtifactRecordView({ kindSegment, artifactId, version, views }: { kindSegment: string; artifactId: string; version: string; views: ArtifactViewMap }) {
  const { t } = useTranslation('agentCatalog');
  const { t: tr } = useTranslation('registry');
  const kind = KIND_OF_PATH[kindSegment];
  const query = useArtifact(kind, artifactId, version);
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const record = query.data as ArtifactRecord | undefined;
  const who = (ref: NonNullable<typeof record>['registeredBy']) => {
    if (ref.kind === 'seed') return t('common.developmentSeed');
    if (ref.kind === 'member') return ref.displayName ?? ref.id;
    return ref.id ? tr('member.unknownWithId', { id: ref.id }) : tr('member.unknown');
  };

  return (
    <Frame kindSegment={kindSegment}>
      {failure &&
        (failure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => void query.refetch()} />
        ))}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {record && (
        <>
          <Typography variant="h2" sx={{ mt: 2 }}>{t('artifacts.entry', { id: record.id, version: record.version, interpolation: { escapeValue: false } })}</Typography>
          <Box component="dl" sx={{ m: 0 }}>
            <Field label={t('common.digest')}><DigestText value={record.digest} /></Field>
          </Box>
          <Typography>{t('common.registeredBy', { value: who(record.registeredBy), interpolation: { escapeValue: false } })}</Typography>
          <Typography>{t('common.registeredAt', { value: record.registeredAt.toLocaleString() })}</Typography>
          <Typography sx={{ mt: 2 }}>
            <Link to={`/agent-catalog/artifacts/${kindSegment}/new?fromId=${encodeURIComponent(record.id)}&fromVersion=${encodeURIComponent(record.version)}`}>
              {t('common.newVersionFrom')}
            </Link>
          </Typography>
          <RecordDocument kind={kind} record={record} views={views} />
        </>
      )}
    </Frame>
  );
}

function RecordDocument({ kind, record, views }: { kind: string; record: ArtifactRecord; views: ArtifactViewMap }) {
  const { t } = useTranslation('agentCatalog');
  const View = Object.hasOwn(views, kind) ? views[kind] : undefined;
  if (View) return <View record={record} />;
  return (
    <>
      <Typography sx={{ mt: 2 }}>{t('artifacts.unsupportedView')}</Typography>
      <CatalogText text={JSON.stringify(record.document, null, 2)} label={t('artifacts.document')} />
    </>
  );
}
