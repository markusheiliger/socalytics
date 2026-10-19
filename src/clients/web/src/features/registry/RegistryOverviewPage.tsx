import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import TextField from '@mui/material/TextField';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { toPlatformFailure } from '../../platform/failure';
import type { MemberRef } from '../../platform/models/registry';
import { FailureAlert } from '../shared/FailureAlert';
import { SetDefaultWorkflowDialog } from './SetDefaultWorkflowDialog';
import { useDefaultWorkflow, useMemberNames, useWorkflows } from './registryKeys';

/** The entry view of the Registry area: the default workflow and the lists (FR-003). */
export function RegistryOverviewPage() {
  const { t } = useTranslation('registry');
  const current = useDefaultWorkflow();
  const names = useMemberNames();
  const workflows = useWorkflows();
  const [choosing, setChoosing] = useState(false);
  const [target, setTarget] = useState('');
  const [dialog, setDialog] = useState(false);

  const who = (id: string | undefined, fallback: MemberRef): string => {
    const ref = names.data ? names.data(id) : fallback;
    if (ref.kind === 'seed') return t('member.seed');
    if (ref.kind === 'unknown') return ref.id ? t('member.unknownWithId', { id: ref.id }) : t('member.unknown');
    return ref.displayName ?? t('member.unknownWithId', { id: ref.id });
  };

  const reload = () => {
    void current.refetch();
    void names.refetch();
  };

  const def = current.data;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t('overview.title')}</Typography>
      <Typography>{t('overview.intro')}</Typography>

      {current.isPending && <Typography sx={{ mt: 2 }}>{t('overview.loading')}</Typography>}
      {current.error && (
        <Stack spacing={1} alignItems="flex-start" sx={{ mt: 2 }}>
          <FailureAlert failure={toPlatformFailure(current.error)} />
          <Button variant="outlined" onClick={reload}>
            {t('overview.reload')}
          </Button>
        </Stack>
      )}
      {current.isSuccess && (
        <section aria-labelledby="registry-default-heading">
          <Typography id="registry-default-heading" variant="h2" sx={{ mt: 3 }}>
            {t('overview.defaultTitle')}
          </Typography>
          {def ? (
            <>
              <Typography>
                <Link to={`/registry/workflows/${encodeURIComponent(def.workflowId)}/versions/${encodeURIComponent(def.workflowVersion)}`}>
                  {t('overview.defaultValue', { workflowId: def.workflowId, version: def.workflowVersion })}
                </Link>
              </Typography>
              <Typography>{t('overview.defaultChanged', { when: def.changedAt.toLocaleString(), who: who(def.changedBy.id, def.changedBy) })}</Typography>
            </>
          ) : (
            <Typography>{t('overview.noDefault')}</Typography>
          )}
          <Button variant="outlined" sx={{ mt: 1 }} aria-expanded={choosing} onClick={() => setChoosing((open) => !open)}>
            {t('overview.changeDefault')}
          </Button>
          {choosing && (
            <Stack direction="row" spacing={2} sx={{ mt: 1, alignItems: 'center' }}>
              <TextField
                select
                label={t('overview.chooseDefault')}
                value={target}
                onChange={(e) => setTarget(e.target.value)}
                slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
              >
                <option value="">{t('overview.chooseDefaultPlaceholder')}</option>
                {(workflows.data ?? []).map((w) => (
                  <option key={`${w.workflowId}/${w.workflowVersion}`} value={JSON.stringify([w.workflowId, w.workflowVersion])}>
                    {t('workflows.listEntry', { workflowId: w.workflowId, version: w.workflowVersion })}
                  </option>
                ))}
              </TextField>
              <Button variant="contained" disabled={target === ''} onClick={() => setDialog(true)}>{t('overview.continueDefault')}</Button>
            </Stack>
          )}
          {dialog && target !== '' && (
            <SetDefaultWorkflowDialog
              workflowId={(JSON.parse(target) as [string, string])[0]}
              workflowVersion={(JSON.parse(target) as [string, string])[1]}
              current={def}
              open
              onClose={() => { setDialog(false); setChoosing(false); setTarget(''); }}
            />
          )}
        </section>
      )}

      <nav aria-label={t('overview.listsTitle')}>
        <Typography variant="h2" sx={{ mt: 3 }}>
          {t('overview.listsTitle')}
        </Typography>
        <Stack component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
          <li>
            <Link to="/registry/images">{t('overview.imagesLink')}</Link>
          </li>
          <li>
            <Link to="/registry/profiles">{t('overview.profilesLink')}</Link>
          </li>
          <li>
            <Link to="/registry/workflows">{t('overview.workflowsLink')}</Link>
          </li>
        </Stack>
      </nav>
    </Container>
  );
}
