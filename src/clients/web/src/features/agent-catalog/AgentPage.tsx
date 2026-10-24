import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams, useSearchParams } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { MemberRef } from '../../platform/models/agentCatalog';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { DigestText } from '../registry/DigestText';
import { FailureAlert } from '../shared/FailureAlert';
import { ChangeEnablementDialog } from './ChangeEnablementDialog';
import { useAgentVersions, useCatalogBaseline, useEnablements } from './agentCatalogKeys';

const STATES = ['draft', 'approved', 'retired'];

/** One agent: its versions (filterable by the `state` search parameter), tool baseline, and current enablement (FR-004, FR-008). */
export function AgentPage() {
  const { agentId = '' } = useParams();
  const { t } = useTranslation('agentCatalog');
  const { t: tc } = useTranslation('common');
  const { t: tr } = useTranslation('registry');
  const [params, setParams] = useSearchParams();
  const requested = params.get('state');
  const state = requested && STATES.includes(requested) ? requested : undefined;
  const baselineQuery = useCatalogBaseline();
  const enablementsQuery = useEnablements();
  const versionsQuery = useAgentVersions({ agentId, state });
  const approvedQuery = useAgentVersions({ agentId, state: 'approved' });
  const [dialog, setDialog] = useState<'change' | 'disable' | undefined>();
  const [nextVersion, setNextVersion] = useState('');
  const queries = [baselineQuery, enablementsQuery, versionsQuery];
  const failures = queries.map((q) => (q.error ? toPlatformFailure(q.error) : undefined));
  const failure = failures.find((f) => f !== undefined);

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const agent = baselineQuery.data?.agents.find((a) => a.agentId === agentId);
  const enablement = enablementsQuery.data?.find((e) => e.agentId === agentId);
  const versions = versionsQuery.data;
  const candidates = (approvedQuery.data ?? []).filter((v) => v.version !== enablement?.enabledVersion);
  const closeDialog = () => {
    setDialog(undefined);
    setNextVersion('');
  };
  const reload = () => queries.forEach((q) => void q.refetch());

  const who = (ref: MemberRef) => {
    if (ref.kind === 'seed') return t('common.developmentSeed');
    if (ref.kind === 'member') return ref.displayName ?? ref.id;
    return ref.id ? tr('member.unknownWithId', { id: ref.id }) : tr('member.unknown');
  };
  const known = (namespace: string, value: string) => t(`${namespace}.${value}`, { defaultValue: value });
  const registerTo = `/agent-catalog/agents/${encodeURIComponent(agentId)}/versions/new`;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{tc('app.name')}</Typography>
      <Typography variant="h2">{t('agent.title', { agentId })}</Typography>
      <Link to="/agent-catalog">{t('agent.back')}</Link>
      {failure && <FailureAlert failure={failure} onReload={reload} />}
      {queries.some((q) => q.isPending) && <Typography role="status" sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {baselineQuery.data && !agent && <Typography role="alert" sx={{ mt: 2 }}>{t('common.notFound')}</Typography>}
      {agent && (
        <>
          <Typography variant="h3" sx={{ mt: 3 }}>{t('agent.toolBaselineTitle')}</Typography>
          {agent.toolBaseline.length === 0 ? (
            <Typography>{t('common.none')}</Typography>
          ) : (
            <Stack component="ul" aria-label={t('agent.toolBaselineTitle')} sx={{ m: 0 }}>
              {agent.toolBaseline.map((tool) => (
                <li key={tool}>{tool}</li>
              ))}
            </Stack>
          )}
          {enablementsQuery.data && (
            <>
              <Typography variant="h3" sx={{ mt: 3 }}>{t('agent.enablementTitle')}</Typography>
              <Typography>
                {enablement?.enabledVersion ? t('agent.enabledVersion', { version: enablement.enabledVersion }) : t('agent.notEnabled')}
              </Typography>
              {enablement?.changedBy && enablement.changedAt && (
                <Typography>
                  {t('agent.changedBy', { who: who(enablement.changedBy), when: enablement.changedAt.toLocaleString(), interpolation: { escapeValue: false } })}
                </Typography>
              )}
              {enablement && (
                <Stack direction="row" spacing={2} sx={{ mt: 1, alignItems: 'center', flexWrap: 'wrap' }}>
                  {candidates.length === 0 ? (
                    <Typography>{t('agent.noOtherApproved')}</Typography>
                  ) : (
                    <>
                      <TextField
                        select
                        size="small"
                        label={t('agent.changeVersionLabel')}
                        value={nextVersion}
                        onChange={(e) => setNextVersion(e.target.value)}
                        slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
                      >
                        <option value="">{t('agent.changeVersionChoose')}</option>
                        {candidates.map((v) => (
                          <option key={v.version} value={v.version}>{v.version}</option>
                        ))}
                      </TextField>
                      <Button variant="contained" disabled={!nextVersion} onClick={() => setDialog('change')}>{t('agent.changeEnabled')}</Button>
                    </>
                  )}
                  {enablement.enabledVersion && (
                    <Button variant="outlined" color="error" onClick={() => setDialog('disable')}>{t('agent.disable')}</Button>
                  )}
                </Stack>
              )}
              {enablement && dialog && (
                <ChangeEnablementDialog
                  agentId={agentId}
                  group={agent.group}
                  current={enablement}
                  nextVersion={dialog === 'disable' ? null : nextVersion}
                  open
                  onClose={closeDialog}
                />
              )}
            </>
          )}
          <Typography variant="h3" sx={{ mt: 3 }}>{t('agent.versionsTitle')}</Typography>
          <TextField
            select
            size="small"
            label={t('common.filterState')}
            value={state ?? ''}
            onChange={(e) => setParams(e.target.value ? { state: e.target.value } : {})}
            slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
            sx={{ mt: 1 }}
          >
            <option value="">{t('common.filterAllStates')}</option>
            {STATES.map((s) => (
              <option key={s} value={s}>{known('states', s)}</option>
            ))}
          </TextField>
          {versions &&
            (versions.length === 0 ? (
              <Stack sx={{ mt: 2 }}>
                <Typography>{state ? t('agent.emptyVersionsFiltered') : t('agent.emptyVersions')}</Typography>
                {!state && <Link to={registerTo}>{t('agent.registerVersion')}</Link>}
              </Stack>
            ) : (
              <Stack component="ul" aria-label={t('agent.versionsLabel')} spacing={2} sx={{ listStyle: 'none', m: 0, p: 0, mt: 2 }}>
                {versions.map((v) => (
                  <li key={v.version}>
                    <Link to={`/agent-catalog/agents/${encodeURIComponent(agentId)}/versions/${encodeURIComponent(v.version)}`}>
                      {t('agent.versionState', { version: v.version, state: known('states', v.state) })}
                    </Link>
                    <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                      <li><DigestText value={v.digest} /></li>
                      <li>{t('common.registeredAt', { value: v.registeredAt.toLocaleString() })}</li>
                      {v.enabled && <li>{t('agent.enabledMarker')}</li>}
                    </Stack>
                  </li>
                ))}
              </Stack>
            ))}
          {versions && versions.length > 0 && <Link to={registerTo}>{t('agent.registerVersion')}</Link>}
        </>
      )}
    </Container>
  );
}
