import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Typography from '@mui/material/Typography';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { CatalogAgentSummary, Offer } from '../../platform/models/agentCatalog';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { useAgentVersions, useAvailableAgents, useCatalogBaseline, useEnablements } from './agentCatalogKeys';
import { ARTIFACT_PATH_KINDS } from './ArtifactsPage';
import { composeCatalogAgents } from './offer';

/** The entry view of the agent catalog: every baseline agent with its enablement and what conversations offer (FR-003). */
export function AgentCatalogOverviewPage() {
  const { t } = useTranslation('agentCatalog');
  const { t: tc } = useTranslation('common');
  const baselineQuery = useCatalogBaseline();
  const versionsQuery = useAgentVersions();
  const enablementsQuery = useEnablements();
  const availableQuery = useAvailableAgents();
  const queries = [baselineQuery, versionsQuery, enablementsQuery, availableQuery];

  const error = queries.find((query) => query.error)?.error;
  const failure = error ? toPlatformFailure(error) : undefined;

  const baseline = baselineQuery.data;
  const versions = versionsQuery.data;
  const enablements = enablementsQuery.data;
  const available = availableQuery.data;
  const agents = useMemo(
    () => (baseline && versions && enablements && available ? composeCatalogAgents(baseline, versions, enablements, available) : undefined),
    [baseline, versions, enablements, available],
  );

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const offerText = (offer: Offer) => {
    if (offer.kind === 'offered') return t('overview.offered', { version: offer.version });
    return offer.reason === 'coachNotAvailable' ? t('overview.notAvailable') : t('overview.notOffered');
  };
  const kindText = (agent: CatalogAgentSummary) => (agent.group === 'coach' || agent.group === 'specialist' ? t(`kinds.${agent.group}`) : t('common.unknown', { value: agent.group }));

  return (
    <Container component="main" maxWidth="lg" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('overview.title')}</Typography>
      <Typography>{t('overview.intro')}</Typography>
      {failure && (
        <FailureAlert
          failure={failure}
          onReload={() => {
            for (const query of queries) void query.refetch();
          }}
        />
      )}
      {!failure && !agents && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {agents &&
        (agents.length === 0 ? (
          <Typography sx={{ mt: 2 }}>{t('overview.empty')}</Typography>
        ) : (
          <Table aria-label={t('overview.listLabel')} size="small" sx={{ mt: 2 }}>
            <TableHead>
              <TableRow>
                <TableCell>{t('overview.columns.agent')}</TableCell>
                <TableCell>{t('overview.columns.kind')}</TableCell>
                <TableCell>{t('overview.columns.domain')}</TableCell>
                <TableCell>{t('overview.columns.displayName')}</TableCell>
                <TableCell>{t('overview.columns.enabledVersion')}</TableCell>
                <TableCell>{t('overview.columns.versions')}</TableCell>
                <TableCell>{t('overview.columns.offer')}</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {agents.map((agent) => (
                <TableRow key={agent.agentId}>
                  <TableCell component="th" scope="row">
                    <Link to={`/agent-catalog/agents/${encodeURIComponent(agent.agentId)}`} aria-label={t('overview.openAgent', { agentId: agent.agentId })}>
                      {agent.agentId}
                    </Link>
                  </TableCell>
                  <TableCell>{kindText(agent)}</TableCell>
                  <TableCell>{agent.domain}</TableCell>
                  <TableCell>{agent.displayName}</TableCell>
                  <TableCell>{agent.enablement.enabledVersion ?? t('overview.enabledNone')}</TableCell>
                  <TableCell>{t('overview.stateCounts', { ...agent.stateCounts })}</TableCell>
                  <TableCell>{offerText(agent.offer)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ))}
      <Typography variant="h3" sx={{ mt: 3 }}>{t('overview.linksTitle')}</Typography>
      <Stack component="nav" aria-label={t('overview.linksTitle')} direction="row" spacing={2} sx={{ flexWrap: 'wrap' }}>
        <Link to="/agent-catalog/baseline">{t('overview.baselineLink')}</Link>
        {ARTIFACT_PATH_KINDS.map((kind) => (
          <Link key={kind} to={`/agent-catalog/artifacts/${kind}`}>
            {t('overview.artifactsLink', { kind: t(`artifactKinds.${kind}`) })}
          </Link>
        ))}
      </Stack>
    </Container>
  );
}
