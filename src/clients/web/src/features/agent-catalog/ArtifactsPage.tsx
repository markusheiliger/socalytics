import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams, useSearchParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { DigestText } from '../registry/DigestText';
import { FailureAlert } from '../shared/FailureAlert';
import { useArtifacts } from './agentCatalogKeys';

export const KIND_OF_PATH: Record<string, string> = {
  'prompt-templates': 'prompt-template',
  'model-selections': 'model-selection',
  'tool-policies': 'tool-policy',
  'safety-policies': 'safety-policy',
  'dimension-sets': 'dimension-set',
  'philosophy-profiles': 'philosophy-profile',
  'modifier-policies': 'modifier-policy',
};
export const ARTIFACT_PATH_KINDS = Object.keys(KIND_OF_PATH);

/** The records of one artifact kind, optionally filtered by the identifier kept in the address (FR-006). */
export function ArtifactsPage() {
  const { t } = useTranslation('agentCatalog');
  const { t: tc } = useTranslation('common');
  const { kind = '' } = useParams();
  if (!Object.hasOwn(KIND_OF_PATH, kind)) {
    return (
      <Container component="main" maxWidth="md" sx={{ py: 4 }}>
        <Typography variant="h1">{tc('app.name')}</Typography>
        <Typography role="alert">{t('common.notFound')}</Typography>
        <Link to="/agent-catalog">{t('overview.title')}</Link>
      </Container>
    );
  }
  return <ArtifactRecords kind={kind} />;
}

// Rendered only for a known kind, so an unknown kind sends no request.
function ArtifactRecords({ kind }: { kind: string }) {
  const { t } = useTranslation('agentCatalog');
  const { t: tc } = useTranslation('common');
  const [params, setParams] = useSearchParams();
  const id = params.get('id')?.trim() || undefined;
  const [text, setText] = useState(id ?? '');
  useEffect(() => setText(id ?? ''), [id]);
  const query = useArtifacts(KIND_OF_PATH[kind], id ? { id } : undefined);
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const apply = (event: FormEvent) => {
    event.preventDefault();
    const value = text.trim();
    setParams(value ? { id: value } : {});
  };

  const kindLabel = t(`artifactKinds.${kind}`);

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('artifacts.title', { kind: kindLabel })}</Typography>
      <Stack direction="row" spacing={2} sx={{ flexWrap: 'wrap' }}>
        <Link to="/agent-catalog">{t('overview.title')}</Link>
        <Link to={`/agent-catalog/artifacts/${encodeURIComponent(kind)}/new`}>{t('artifacts.register')}</Link>
      </Stack>
      <Stack component="nav" aria-label={t('artifacts.switcher')} direction="row" spacing={2} sx={{ mt: 2, flexWrap: 'wrap' }}>
        {ARTIFACT_PATH_KINDS.map((other) =>
          other === kind ? (
            <Typography key={other} component="span" aria-current="page" sx={{ fontWeight: 'bold' }}>
              {t(`artifactKinds.${other}`)}
            </Typography>
          ) : (
            <Link key={other} to={`/agent-catalog/artifacts/${other}`}>
              {t(`artifactKinds.${other}`)}
            </Link>
          ),
        )}
      </Stack>
      <Stack component="form" direction="row" spacing={1} onSubmit={apply} sx={{ mt: 2, alignItems: 'center' }}>
        <TextField size="small" label={t('common.filterId')} value={text} onChange={(e) => setText(e.target.value)} />
        <Button type="submit" variant="outlined">{t('common.applyFilter')}</Button>
        {id && <Button type="button" onClick={() => setParams({})}>{t('common.clearFilter')}</Button>}
      </Stack>
      {failure && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {query.data &&
        (query.data.length === 0 ? (
          <Typography sx={{ mt: 2 }}>{id ? t('artifacts.emptyFiltered') : t('artifacts.empty')}</Typography>
        ) : (
          <Stack component="ul" aria-label={t('artifacts.listLabel', { kind: kindLabel })} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
            {query.data.map((record) => (
              <li key={`${record.id}@${record.version}`}>
                <Link to={`/agent-catalog/artifacts/${encodeURIComponent(kind)}/${encodeURIComponent(record.id)}/versions/${encodeURIComponent(record.version)}`}>
                  {t('artifacts.entry', { id: record.id, version: record.version })}
                </Link>
                <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                  <li><DigestText value={record.digest} /></li>
                  <li>{t('common.registeredAt', { value: record.registeredAt.toLocaleString() })}</li>
                </Stack>
              </li>
            ))}
          </Stack>
        ))}
    </Container>
  );
}
