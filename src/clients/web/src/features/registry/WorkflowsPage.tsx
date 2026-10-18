import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { DigestText } from './DigestText';
import { useWorkflows } from './registryKeys';

/** The workflow versions with the default marker derived from the default workflow (FR-008). */
export function WorkflowsPage() {
  const { t } = useTranslation('registry');
  const { t: tc } = useTranslation('common');
  const query = useWorkflows();
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('workflows.title')}</Typography>
      <Link to="/registry">{t('overview.title')}</Link>
      {failure && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {query.data &&
        (query.data.length === 0 ? (
          <Typography sx={{ mt: 2 }}>{t('workflows.empty')}</Typography>
        ) : (
          <Stack component="ul" aria-label={t('workflows.listLabel')} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
            {query.data.map((w) => (
              <li key={`${w.workflowId}/${w.workflowVersion}`}>
                <Link to={`/registry/workflows/${encodeURIComponent(w.workflowId)}/versions/${encodeURIComponent(w.workflowVersion)}`}>
                  {t('workflows.listEntry', { workflowId: w.workflowId, version: w.workflowVersion })}
                </Link>
                <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                  <li><DigestText value={w.definitionDigest} /></li>
                  <li>{t('workflows.createdAt', { value: w.createdAt.toLocaleString() })}</li>
                  {w.isDefault && <li>{t('workflows.defaultMarker')}</li>}
                </Stack>
              </li>
            ))}
          </Stack>
        ))}
    </Container>
  );
}
