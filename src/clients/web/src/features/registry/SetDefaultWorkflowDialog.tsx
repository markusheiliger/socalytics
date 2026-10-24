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
import { RegistryChangedError, RegistryRefusalError, type DefaultWorkflow } from '../../platform/models/registry';
import { FailureAlert } from '../shared/FailureAlert';
import { RefusalAlert } from './RefusalAlert';
import { useRegistryAdapter, useSetDefault } from './registryKeys';

export interface SetDefaultWorkflowDialogProps {
  workflowId: string;
  workflowVersion: string;
  /** The default as loaded by the caller; null when none existed. Its `rowVersion` is the concurrency token. */
  current: DefaultWorkflow | null | undefined;
  open: boolean;
  onClose: () => void;
  onDone?: (result: DefaultWorkflow) => void;
}

/** Confirmation of a new default workflow: confirming sends one `setDefault` with the loaded default's `rowVersion`, cancelling sends nothing. */
export function SetDefaultWorkflowDialog({ workflowId, workflowVersion, current, open, onClose, onDone }: SetDefaultWorkflowDialogProps) {
  const { t } = useTranslation('registry');
  const adapter = useRegistryAdapter();
  const mutation = useSetDefault();
  const [sent, setSent] = useState(false);
  const [done, setDone] = useState(false);
  const [reloaded, setReloaded] = useState<DefaultWorkflow | null | undefined>();
  const error = mutation.error;
  const refusal = error instanceof RegistryRefusalError ? error.refusal : undefined;
  const changed = error instanceof RegistryChangedError;
  const failure = error && !refusal && !changed ? toPlatformFailure(error) : undefined;
  const describe = (value: DefaultWorkflow | null | undefined) =>
    value ? t('overview.defaultValue', { workflowId: value.workflowId, version: value.workflowVersion }) : t('confirm.default.none');

  const confirm = () => {
    if (sent) return;
    setSent(true);
    mutation.mutate(
      { workflowId, workflowVersion, rowVersion: current?.rowVersion },
      {
        onSuccess: (result) => {
          setDone(true);
          onDone?.(result);
        },
        onError: (cause) => {
          if (!(cause instanceof RegistryChangedError)) return;
          adapter.getDefault().then(setReloaded, () => undefined);
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

  const finished = done || changed || Boolean(refusal) || Boolean(failure);
  return (
    <Dialog open={open} onClose={close} aria-labelledby="set-default-title">
      <DialogTitle id="set-default-title">{t('confirm.default.title')}</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ overflowWrap: 'anywhere' }}>
          {t('confirm.default.body', { workflowId, version: workflowVersion, current: describe(current) })}
        </DialogContentText>
        {!current && <Alert severity="info" sx={{ mt: 2 }}>{t('confirm.default.noDefaultNote')}</Alert>}
        {done && <Alert severity="success" role="status" sx={{ mt: 2 }}>{t('workflowVersion.defaultSet')}</Alert>}
        {refusal && <RefusalAlert refusal={refusal} />}
        {changed && (
          <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
            <strong>{t('changed.title')}</strong>
            <div>{t('changed.message')}</div>
            {reloaded !== undefined && <div>{t('changed.currentDefault', { current: describe(reloaded) })}</div>}
          </Alert>
        )}
        {failure && <FailureAlert failure={failure} />}
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{finished ? t('confirm.close') : t('confirm.cancel')}</Button>
        {!finished && (
          <Button variant="contained" disabled={sent} onClick={confirm}>
            {mutation.isPending ? t('confirm.working') : t('confirm.default.action')}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
