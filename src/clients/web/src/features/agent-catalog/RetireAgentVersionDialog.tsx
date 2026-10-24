import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toPlatformFailure } from '../../platform/failure';
import type { AgentVersion } from '../../platform/models/agentCatalog';
import { FailureAlert } from '../shared/FailureAlert';
import { useAgentCatalogAdapter, useRetireAgentVersion } from './agentCatalogKeys';
import { refusalText } from './outcomeText';

export interface RetireAgentVersionDialogProps {
  /** The version as last loaded by the caller; its `etag` is sent as `If-Match`. */
  version: AgentVersion;
  open: boolean;
  onClose: () => void;
  onDone?: (result: AgentVersion) => void;
}

/** Confirmation of a retirement: confirming sends exactly one `retireAgentVersion`, cancelling sends nothing, and nothing is ever repeated (FR-015, FR-017). */
export function RetireAgentVersionDialog({ version, open, onClose, onDone }: RetireAgentVersionDialogProps) {
  const { t } = useTranslation('agentCatalog');
  const adapter = useAgentCatalogAdapter();
  const mutation = useRetireAgentVersion();
  const [sent, setSent] = useState(false);
  const [done, setDone] = useState(false);
  const [reloaded, setReloaded] = useState<AgentVersion | undefined>();
  const outcome = mutation.data && 'kind' in mutation.data ? mutation.data : undefined;
  const refused = outcome?.kind === 'transitionRefused' ? outcome : undefined;
  const changed = outcome?.kind === 'changed';
  const failure = mutation.error ? toPlatformFailure(mutation.error) : undefined;
  const { agentId } = version;

  const stateLine = (current: AgentVersion) => t('confirm.retire.currentState', { state: t(`states.${current.state}`, { defaultValue: current.state }) });

  const confirm = () => {
    if (sent) return;
    setSent(true);
    mutation.mutate(
      { agentId, version: version.version, etag: version.etag },
      {
        onSuccess: (result) => {
          if ('kind' in result) {
            adapter.getAgentVersion(agentId, version.version).then(setReloaded, () => undefined);
            return;
          }
          setDone(true);
          onDone?.(result);
        },
      },
    );
  };

  const close = () => {
    setSent(false);
    setDone(false);
    setReloaded(undefined);
    mutation.reset();
    onClose();
  };

  const refusal = refused ? refusalText(refused.code) : undefined;
  const finished = done || changed || Boolean(refused) || Boolean(failure);

  return (
    <Dialog open={open} onClose={close} aria-labelledby="retire-agent-version-title">
      <DialogTitle id="retire-agent-version-title">{t('confirm.retire.title', { agentId, version: version.version })}</DialogTitle>
      <DialogContent>
        <DialogContentText component="div" sx={{ overflowWrap: 'anywhere' }}>
          <div>{t('confirm.retire.body', { agentId, version: version.version })}</div>
        </DialogContentText>
        <Alert severity="warning" sx={{ mt: 2 }}>{t('confirm.retire.effect')}</Alert>
        {done && <Alert severity="success" role="status" sx={{ mt: 2 }}>{t('confirm.retire.done')}</Alert>}
        {refusal && (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>
            <strong>{t('refusals.title')}</strong>
            <div>{t(refusal.key, refusal.values)}</div>
            {refused?.code === 'agent-version-enabled' && <div>{t('confirm.retire.enabledAdvice')}</div>}
            {reloaded && <div>{stateLine(reloaded)}</div>}
          </Alert>
        )}
        {changed && (
          <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
            <div>{t('outcomes.changed')}</div>
            {reloaded && <div>{stateLine(reloaded)}</div>}
          </Alert>
        )}
        {failure && <FailureAlert failure={failure} />}
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{finished ? t('common.close') : t('common.cancel')}</Button>
        {!finished && (
          <Button variant="contained" color="error" disabled={sent} onClick={confirm}>
            {mutation.isPending ? t('common.working') : t('confirm.retire.submit')}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
