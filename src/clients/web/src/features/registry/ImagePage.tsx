import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Typography from '@mui/material/Typography';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation, useParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { MemberRef } from '../../platform/models/registry';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { DigestText } from './DigestText';
import { useImage, useMemberNames } from './registryKeys';

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <Box component="dt" sx={{ fontWeight: 'bold', mt: 1 }}>{label}</Box>
      <Box component="dd" sx={{ m: 0, overflowWrap: 'anywhere' }}>{children}</Box>
    </>
  );
}

/** A known enumerated value is translated, an unknown one is shown raw (FR-022). */
function useKnown(namespace: string) {
  const { t } = useTranslation('registry');
  return (value: string) => t(`${namespace}.${value}`, { defaultValue: value });
}

/** One Analyst image with every field of FR-005 and the stored manifest as text on request. */
export function ImagePage() {
  const { digest = '' } = useParams();
  const alreadyRegistered = (useLocation().state as { alreadyRegistered?: boolean } | null)?.alreadyRegistered === true;
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  const tier = useKnown('tier');
  const scope = useKnown('scope');
  const video = useKnown('videoAccess');
  const edgeKind = useKnown('edgeKind');
  const [showManifest, setShowManifest] = useState(false);
  const query = useImage(digest);
  const names = useMemberNames();
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const d = query.data;
  const notDeclared = t('image.notDeclared');
  const none = t('image.none');
  const who = (ref: MemberRef) => {
    const resolved = names.data ? names.data(ref.id) : ref;
    if (resolved.kind === 'seed') return t('member.seed');
    if (resolved.kind === 'member') return resolved.displayName ?? resolved.id;
    return resolved.id ? t('member.unknownWithId', { id: resolved.id }) : t('member.unknown');
  };
  const list = (values: string[] | undefined) => (values === undefined ? notDeclared : values.length === 0 ? none : values.join(', '));
  const pairs = (record: Record<string, string> | undefined) =>
    record === undefined ? notDeclared : Object.keys(record).length === 0 ? none : (
      <Box component="ul" sx={{ m: 0, pl: 2 }}>
        {Object.entries(record).map(([key, value]) => <li key={key}>{t('field.keyValue', { key, value })}</li>)}
      </Box>
    );
  const flag = (value: boolean | undefined) => (value === undefined ? notDeclared : value ? t('field.yes') : t('field.no'));
  const number = (value: number | undefined) => (value === undefined ? notDeclared : String(value));

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('image.title')}</Typography>
      <Link to="/registry/images">{t('image.back')}</Link>
      {alreadyRegistered && <Alert severity="info" role="status" sx={{ mt: 2 }}>{t('outcome.alreadyRegistered')}</Alert>}
      {failure &&
        (failure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('image.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => void query.refetch()} />
        ))}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {d && (
        <>
          <Typography variant="h3" sx={{ mt: 2 }}>{t('image.declarationTitle')}</Typography>
          <Box component="dl" sx={{ m: 0 }}>
            <Field label={t('field.imageDigest')}><DigestText value={d.imageDigest} /></Field>
            <Field label={t('field.repository')}>{d.repository}</Field>
            <Field label={t('field.manifestDigest')}><DigestText value={d.manifestDigest} /></Field>
            <Field label={t('field.capabilityId')}>{d.capability.id}</Field>
            <Field label={t('field.capabilityVersion')}>{d.capability.version}</Field>
            <Field label={t('field.tier')}>{tier(d.capability.tier)}</Field>
            <Field label={t('field.executionScope')}>{scope(d.capability.executionScope)}</Field>
            <Field label={t('field.upstream')}>
              {d.upstream === undefined ? notDeclared : d.upstream.length === 0 ? none : (
                <Box component="ul" sx={{ m: 0, pl: 2 }}>
                  {d.upstream.map((u) => (
                    <li key={`${u.capabilityId}@${u.capabilityVersion}`}>
                      {t('field.upstreamEntry', { capability: u.capabilityId, version: u.capabilityVersion, schema: u.schemaVersion, kind: edgeKind(u.kind) })}
                    </li>
                  ))}
                </Box>
              )}
            </Field>
            <Field label={t('field.videoAccess')}>{d.videoAccess === undefined ? notDeclared : video(d.videoAccess)}</Field>
            <Field label={t('field.outputSchemaVersion')}>{d.outputSchemaVersion ?? notDeclared}</Field>
            <Field label={t('field.compatibleProfiles')}>{list(d.compatibleProfiles)}</Field>
            <Field label={t('field.modelDigest')}><DigestText value={d.modelDigest} /></Field>
            <Field label={t('field.runtimeConstraints')}>{list(d.runtime?.constraints)}</Field>
            <Field label={t('field.accelerators')}>{list(d.runtime?.accelerators)}</Field>
            <Field label={t('field.resources')}>{pairs(d.resources)}</Field>
            <Field label={t('field.minMaxAttempts')}>{number(d.requirements?.minMaxAttempts)}</Field>
            <Field label={t('field.minLeaseSeconds')}>{number(d.requirements?.minLeaseSeconds)}</Field>
            <Field label={t('field.minStaleTimeoutSeconds')}>{number(d.requirements?.minStaleTimeoutSeconds)}</Field>
            <Field label={t('field.minExecutionTimeoutSeconds')}>{number(d.requirements?.minExecutionTimeoutSeconds)}</Field>
            <Field label={t('field.deterministic')}>{flag(d.determinism?.deterministic)}</Field>
            <Field label={t('field.idempotent')}>{flag(d.determinism?.idempotent)}</Field>
            <Field label={t('field.source')}>{d.source}</Field>
            <Field label={t('field.registeredAt')}>{d.registeredAt.toLocaleString()}</Field>
            <Field label={t('field.registeredBy')}>{who(d.registeredBy)}</Field>
          </Box>
          <Typography>{t('image.manifestIntro')}</Typography>
          <Button aria-expanded={showManifest} onClick={() => setShowManifest((open) => !open)}>
            {t('image.manifestTitle')}
          </Button>
          {showManifest &&
            (d.manifestText === undefined ? (
              <Typography>{notDeclared}</Typography>
            ) : (
              <Box component="pre" tabIndex={0} aria-label={t('image.manifestTitle')} sx={{ overflow: 'auto', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
                {d.manifestText}
              </Box>
            ))}
        </>
      )}
    </Container>
  );
}
