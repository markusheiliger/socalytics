import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { useCatalogBaseline } from './agentCatalogKeys';

/** The catalog baseline, read-only: the tool allowlist and the permitted tools of each agent (FR-007). */
export function CatalogBaselinePage() {
  const { t } = useTranslation('agentCatalog');
  const { t: tc } = useTranslation('common');
  const query = useCatalogBaseline();
  const failure = query.error ? toPlatformFailure(query.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const baseline = query.data;
  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('baseline.title')}</Typography>
      <Link to="/agent-catalog">{t('overview.title')}</Link>
      <Typography sx={{ mt: 1 }}>{t('baseline.intro')}</Typography>
      {failure && <FailureAlert failure={failure} onReload={() => void query.refetch()} />}
      {query.isPending && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {baseline && (
        <>
          <Typography sx={{ mt: 2 }}>{t('baseline.version', { version: baseline.version })}</Typography>
          <Typography variant="h3" sx={{ mt: 2 }}>{t('baseline.allowlistTitle')}</Typography>
          {baseline.tools.length === 0 ? (
            <Typography>{t('common.none')}</Typography>
          ) : (
            <Stack component="ul" aria-label={t('baseline.allowlistTitle')} sx={{ m: 0 }}>
              {baseline.tools.map((tool) => (
                <li key={tool}>{tool}</li>
              ))}
            </Stack>
          )}
          <Typography variant="h3" sx={{ mt: 2 }}>{t('baseline.agentsTitle')}</Typography>
          <Stack component="ul" spacing={2} sx={{ listStyle: 'none', m: 0, p: 0 }}>
            {baseline.agents.map((agent) => (
              <li key={agent.agentId}>
                <Link to={`/agent-catalog/agents/${encodeURIComponent(agent.agentId)}`}>{agent.agentId}</Link>
                <Typography variant="h4" component="h4">{t('baseline.toolBaselineTitle', { agentId: agent.agentId })}</Typography>
                {agent.toolBaseline.length === 0 ? (
                  <Typography>{t('common.none')}</Typography>
                ) : (
                  <Stack component="ul" aria-label={t('baseline.toolBaselineTitle', { agentId: agent.agentId })} sx={{ m: 0 }}>
                    {agent.toolBaseline.map((tool) => (
                      <li key={tool}>{tool}</li>
                    ))}
                  </Stack>
                )}
              </li>
            ))}
          </Stack>
        </>
      )}
    </Container>
  );
}
