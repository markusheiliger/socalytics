import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import { useMutation } from '@tanstack/react-query';
import { useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { createOperationsAdapter } from '../../platform/adapters/operationsAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';
import { NotPermittedPage } from '../../shell/NotPermittedPage';
import { FailureAlert } from '../shared/FailureAlert';
import { formatTimestamp } from './formatOperations';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Called once after a successful outcome so the summary can be read again. */
  onCompleted?: () => void;
}

/** Confirms and sends exactly one reconciliation request, then shows its outcome or refusal (FR-014 to FR-017). */
export function ReconcileDialog({ open, onClose, onCompleted }: Props) {
  const { t } = useTranslation('operations');
  const { platform } = useSession();
  const adapter = useMemo(() => createOperationsAdapter(platform), [platform]);
  const sent = useRef(false);
  const mutation = useMutation({
    mutationFn: () => adapter.reconcile(),
    retry: false,
    onSuccess: () => onCompleted?.(),
  });
  const failure = mutation.error ? toPlatformFailure(mutation.error) : undefined;

  if (failure?.kind === 'notPermitted') return <NotPermittedPage />;

  const confirm = () => {
    if (sent.current) return;
    sent.current = true;
    mutation.mutate();
  };
  const close = () => {
    if (mutation.isPending) return;
    sent.current = false;
    mutation.reset();
    onClose();
  };
  const outcome = mutation.data;
  const started = formatTimestamp(outcome?.startedAt);
  const completed = formatTimestamp(outcome?.completedAt);
  const finished = mutation.isSuccess || mutation.isError;

  return (
    <Dialog open={open} onClose={close} aria-labelledby="reconcile-title">
      <DialogTitle id="reconcile-title">{t('reconcile.title')}</DialogTitle>
      <DialogContent>
        {!finished && (
          <>
            <DialogContentText>{t('reconcile.description')}</DialogContentText>
            <DialogContentText sx={{ mt: 1 }}>{t('reconcile.neverAuthorizes')}</DialogContentText>
            <DialogContentText sx={{ mt: 1 }}>
              {t('reconcile.mayTakeAWhile')} {t('reconcile.audited')}
            </DialogContentText>
          </>
        )}
        {mutation.isPending && (
          <DialogContentText role="status" sx={{ mt: 2 }}>
            {t('reconcile.progress')}
          </DialogContentText>
        )}
        {outcome && (
          <div role="status">
            <DialogContentText component="h2" sx={{ fontWeight: 'bold' }}>
              {t('reconcile.outcomeTitle')}
            </DialogContentText>
            <DialogContentText>
              {outcome.readyJobsRepublished === 0 && outcome.notificationsRepublished === 0
                ? t('reconcile.nothing')
                : t('reconcile.outcome', { jobs: outcome.readyJobsRepublished, notifications: outcome.notificationsRepublished })}
            </DialogContentText>
            <DialogContentText>
              {t('reconcile.started', { time: started?.text ?? t('common.absent') })}
            </DialogContentText>
            <DialogContentText>
              {t('reconcile.completed', { time: completed?.text ?? t('common.absent') })}
            </DialogContentText>
          </div>
        )}
        {failure &&
          (failure.kind === 'unreachable' || failure.kind === 'unexpected' ? (
            <Alert severity="error" role="alert" sx={{ mt: 2 }}>
              {t('reconcile.refused')}
            </Alert>
          ) : (
            <FailureAlert failure={failure} />
          ))}
      </DialogContent>
      <DialogActions>
        {finished ? (
          <Button onClick={close}>{t('reconcile.close')}</Button>
        ) : (
          <>
            <Button onClick={close} disabled={mutation.isPending}>
              {t('reconcile.cancel')}
            </Button>
            <Button variant="contained" onClick={confirm} disabled={mutation.isPending}>
              {t('reconcile.confirm')}
            </Button>
          </>
        )}
      </DialogActions>
    </Dialog>
  );
}
