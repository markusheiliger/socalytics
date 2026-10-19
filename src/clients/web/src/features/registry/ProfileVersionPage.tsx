import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { MemberRef } from '../../platform/models/registry';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { ApproveProfileDialog } from './ApproveProfileDialog';
import { DigestText } from './DigestText';
import { RetireProfileDialog } from './RetireProfileDialog';
import { useMemberNames, useProfile } from './registryKeys';

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <Box component="dt" sx={{ fontWeight: 'bold', mt: 1 }}>{label}</Box>
      <Box component="dd" sx={{ m: 0, overflowWrap: 'anywhere' }}>{children}</Box>
    </>
  );
}

function Frame({ children }: { children?: ReactNode }) {
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('profileVersion.title')}</Typography>
      <Link to="/registry/profiles">{t('profileVersion.back')}</Link>
      {children}
    </Container>
  );
}

/** One profile version with every field of FR-007; read through `getProfile` so `rowVersion` is known. */
export function ProfileVersionPage() {
  const { profileId = '', version: versionText = '' } = useParams();
  const { t } = useTranslation('registry');
  if (!/^\d+$/.test(versionText) || !Number.isSafeInteger(Number(versionText))) {
    return (
      <Frame>
        <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('profileVersion.notFound')}</Alert>
      </Frame>
    );
  }
  return <ProfileVersionView profileId={profileId} version={Number(versionText)} />;
}

function ProfileVersionView({ profileId, version }: { profileId: string; version: number }) {
  const { t } = useTranslation('registry');
  const query = useProfile(profileId, version);
  const names = useMemberNames();
  const [dialog, setDialog] = useState<'approve' | 'retire' | undefined>();
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const p = query.data;
  const canTransition = p !== undefined && p.rowVersion !== undefined && (p.state === 'draft' || p.state === 'approved');
  const known = (namespace: string, value: string) => t(`${namespace}.${value}`, { defaultValue: value });
  const none = t('image.none');
  const who = (ref: MemberRef) => {
    const resolved = names.data ? names.data(ref.id) : ref;
    if (resolved.kind === 'seed') return t('member.seed');
    if (resolved.kind === 'member') return resolved.displayName ?? resolved.id;
    return resolved.id ? t('member.unknownWithId', { id: resolved.id }) : t('member.unknown');
  };
  const list = (values: string[]) => (values.length === 0 ? none : values.join(', '));
  const pairs = (record: Record<string, string>) =>
    Object.keys(record).length === 0 ? none : (
      <Box component="ul" sx={{ m: 0, pl: 2 }}>
        {Object.entries(record).map(([key, value]) => <li key={key}>{t('field.keyValue', { key, value })}</li>)}
      </Box>
    );

  return (
    <Frame>
      {failure &&
        (failure.status === 404 ? (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>{t('profileVersion.notFound')}</Alert>
        ) : (
          <FailureAlert failure={failure} onReload={() => void query.refetch()} />
        ))}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {p && (
        <>
          <Typography variant="h3" sx={{ mt: 2 }}>{t('profileVersion.heading', { profileId: p.profileId, version: p.version })}</Typography>
          <Typography variant="h4">{t('profileVersion.contentTitle')}</Typography>
          <Box component="dl" sx={{ m: 0 }}>
            <Field label={t('field.state')}>{known('profileState', p.state)}</Field>
            <Field label={t('field.profileDigest')}><DigestText value={p.profileDigest} /></Field>
            <Field label={t('field.imageDigest')}><DigestText value={p.imageDigest} /></Field>
            <Field label={t('field.modelDigest')}><DigestText value={p.content.modelDigest} /></Field>
            <Field label={t('field.capabilityId')}>{p.content.capabilityId}</Field>
            <Field label={t('field.capabilityVersion')}>{p.content.capabilityVersion}</Field>
            <Field label={t('field.runtimeConstraints')}>{list(p.content.runtimeConstraints)}</Field>
            <Field label={t('field.accelerators')}>{list(p.content.accelerators)}</Field>
            <Field label={t('field.resources')}>{pairs(p.content.resources)}</Field>
            <Field label={t('field.maxAttempts')}>{p.content.maxAttempts}</Field>
            <Field label={t('field.leaseSeconds')}>{p.content.leaseSeconds}</Field>
            <Field label={t('field.staleTimeoutSeconds')}>{p.content.staleTimeoutSeconds}</Field>
            <Field label={t('field.executionTimeoutSeconds')}>{p.content.executionTimeoutSeconds}</Field>
            <Field label={t('field.videoPolicy')}>{known('videoAccess', p.content.videoPolicy)}</Field>
            <Field label={t('field.inputSchemaVersions')}>{pairs(p.content.inputSchemaVersions)}</Field>
            <Field label={t('field.outputSchemaVersion')}>{p.content.outputSchemaVersion}</Field>
          </Box>
          {canTransition && (
            <Stack direction="row" spacing={1} sx={{ mt: 2 }}>
              {p.state === 'draft' && <Button variant="contained" onClick={() => setDialog('approve')}>{t('profileVersion.approve')}</Button>}
              <Button variant="outlined" color="error" onClick={() => setDialog('retire')}>{t('profileVersion.retire')}</Button>
            </Stack>
          )}
          {p.rowVersion !== undefined && (
            <>
              <ApproveProfileDialog profile={p} open={dialog === 'approve'} onClose={() => setDialog(undefined)} />
              <RetireProfileDialog profile={p} open={dialog === 'retire'} onClose={() => setDialog(undefined)} />
            </>
          )}
          <Typography variant="h4" sx={{ mt: 2 }}>{t('profileVersion.historyTitle')}</Typography>
          <Box component="dl" sx={{ m: 0 }}>
            <Field label={t('field.createdAt')}>{p.createdAt.toLocaleString()}</Field>
            <Field label={t('field.createdBy')}>{who(p.createdBy)}</Field>
            {p.approvedAt && <Field label={t('field.approvedAt')}>{p.approvedAt.toLocaleString()}</Field>}
            {p.approvedBy && <Field label={t('field.approvedBy')}>{who(p.approvedBy)}</Field>}
            {p.retiredAt && <Field label={t('field.retiredAt')}>{p.retiredAt.toLocaleString()}</Field>}
            {p.retiredBy && <Field label={t('field.retiredBy')}>{who(p.retiredBy)}</Field>}
          </Box>
        </>
      )}
    </Frame>
  );
}
