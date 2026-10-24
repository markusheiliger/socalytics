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
import type { AgentGroup, Enablement } from '../../platform/models/agentCatalog';
import { FailureAlert } from '../shared/FailureAlert';
import { useAgentCatalogAdapter, useSetEnablement } from './agentCatalogKeys';
import { enablementReasonText, refusalText } from './outcomeText';

export interface ChangeEnablementDialogProps {
  agentId: string;
  group: AgentGroup;
  /** The enablement as last loaded by the caller; its `etag` is sent as `If-Match`. */
  current: Enablement;
  /** The version to enable, or null to disable the agent. */
  nextVersion: string | null;
  open: boolean;
  onClose: () => void;
  onDone?: (result: Enablement) => void;
}

/** Confirmation of an enablement change: confirming sends exactly one `setEnablement`, cancelling sends nothing, and nothing is ever repeated (FR-016, FR-017). */
export function ChangeEnablementDialog({ agentId, group, current, nextVersion, open, onClose, onDone }: ChangeEnablementDialogProps) {
  const { t } = useTranslation('agentCatalog');
  const adapter = useAgentCatalogAdapter();
  const mutation = useSetEnablement();
  const [sent, setSent] = useState(false);
  const [done, setDone] = useState(false);
  const [reloaded, setReloaded] = useState<Enablement | undefined>();
  const outcome = mutation.data && 'kind' in mutation.data ? mutation.data : undefined;
  const refused = outcome?.kind === 'enablementRefused' ? outcome : undefined;
  const changed = outcome?.kind === 'changed';
  const failure = mutation.error ? toPlatformFailure(mutation.error) : undefined;
  const disabling = nextVersion === null;

  const confirm = () => {
    if (sent) return;
    setSent(true);
    mutation.mutate(
      { agentId, version: nextVersion, etag: current.etag },
      {
        onSuccess: (result) => {
          if ('kind' in result) {
            if (result.kind === 'changed') {
              adapter.listEnablements().then((all) => setReloaded(all.find((e) => e.agentId === agentId)), () => undefined);
            }
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

  const currentLine = (value: string | undefined) => (value ? t('confirm.enablement.current', { version: value }) : t('confirm.enablement.currentNone'));
  const refusal = refused ? refusalText(refused.code) : undefined;
  const reason = refused?.reason ? enablementReasonText(refused.reason) : undefined;
  const finished = done || changed || Boolean(refused) || Boolean(failure);

  return (
    <Dialog open={open} onClose={close} aria-labelledby="change-enablement-title">
      <DialogTitle id="change-enablement-title">
        {disabling ? t('confirm.enablement.titleDisable', { agentId }) : t('confirm.enablement.title', { agentId })}
      </DialogTitle>
      <DialogContent>
        <DialogContentText component="div" sx={{ overflowWrap: 'anywhere' }}>
          <div>{t('confirm.enablement.agent', { agentId })}</div>
          <div>{currentLine(current.enabledVersion)}</div>
          <div>{disabling ? t('confirm.enablement.nextNone') : t('confirm.enablement.next', { version: nextVersion })}</div>
        </DialogContentText>
        <Alert severity="info" sx={{ mt: 2 }}>{t('confirm.enablement.effect')}</Alert>
        {disabling && <Alert severity="warning" sx={{ mt: 2 }}>{t('confirm.enablement.disableEffect')}</Alert>}
        {group === 'coach' && !disabling && <Alert severity="info" sx={{ mt: 2 }}>{t('confirm.enablement.coachNote')}</Alert>}
        {done && <Alert severity="success" role="status" sx={{ mt: 2 }}>{t('confirm.enablement.done')}</Alert>}
        {refusal && (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>
            <strong>{t('refusals.title')}</strong>
            <div>{t(refusal.key, refusal.values)}</div>
            {reason && <div>{t(reason.key, reason.values)}</div>}
          </Alert>
        )}
        {changed && (
          <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
            <div>{t('outcomes.changed')}</div>
            {reloaded && <div>{currentLine(reloaded.enabledVersion)}</div>}
          </Alert>
        )}
        {failure && <FailureAlert failure={failure} />}
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{finished ? t('common.close') : t('common.cancel')}</Button>
        {!finished && (
          <Button variant="contained" disabled={sent} onClick={confirm}>
            {mutation.isPending ? t('common.working') : disabling ? t('confirm.enablement.submitDisable') : t('confirm.enablement.submitEnable')}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
