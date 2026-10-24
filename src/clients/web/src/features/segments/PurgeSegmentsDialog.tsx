import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import { useMutation } from '@tanstack/react-query';
import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useConnectivity } from '../../connectivity/useConnectivity';
import { createSegmentAdapter } from '../../platform/adapters/segmentAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';

interface Props {
  open: boolean;
  matchId: string;
  /** The opponent's name, which identifies the match in the confirmation. */
  opponent: string;
  onClose: () => void;
  /** Called once when a purge request ends, on success and on failure, so the list can be read again. */
  onFinished?: () => void;
  /** Reports whether a purge request is pending, so the list area can disable its own controls. */
  onPendingChange?: (pending: boolean) => void;
}

/** Confirms and sends exactly one purge request per confirmation, then shows its outcome or failure (FR-010 to FR-015). */
export function PurgeSegmentsDialog({ open, matchId, opponent, onClose, onFinished, onPendingChange }: Props) {
  const { t } = useTranslation('segments');
  const { t: ts } = useTranslation('shell');
  const { isUnreachable } = useConnectivity();
  const { platform } = useSession();
  const adapter = useMemo(() => createSegmentAdapter(platform), [platform]);
  const sent = useRef(false);
  const mutation = useMutation({
    mutationFn: () => adapter.purge(matchId),
    retry: false,
    onSettled: () => onFinished?.(),
  });
  useEffect(() => onPendingChange?.(mutation.isPending), [mutation.isPending, onPendingChange]);
  const failure = mutation.error ? toPlatformFailure(mutation.error) : undefined;

  const send = () => {
    if (sent.current || isUnreachable) return;
    sent.current = true;
    mutation.mutate();
  };
  const retry = () => {
    sent.current = false;
    mutation.reset();
    send();
  };
  const close = () => {
    if (mutation.isPending) return;
    sent.current = false;
    mutation.reset();
    onClose();
  };
  const outcome = mutation.data;
  const finished = mutation.isSuccess || mutation.isError;

  return (
    <Dialog
      open={open}
      onClose={close}
      disableEscapeKeyDown={mutation.isPending}
      aria-labelledby="purge-segments-title"
    >
      <DialogTitle id="purge-segments-title">{t('purge.confirmTitle')}</DialogTitle>
      <DialogContent>
        {!finished && <DialogContentText>{t('purge.confirmBody', { opponent })}</DialogContentText>}
        {isUnreachable && !finished && (
          <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
            {ts('connectivity.connectionRequired')}
          </Alert>
        )}
        {mutation.isPending && (
          <DialogContentText role="status" sx={{ mt: 2 }}>
            {t('purge.inProgress')}
          </DialogContentText>
        )}
        {outcome && (
          <div role="status">
            <DialogContentText>{t('purge.outcome', { purged: outcome.purged, inUse: outcome.inUse })}</DialogContentText>
            <DialogContentText>{t('purge.outcomeNote')}</DialogContentText>
          </div>
        )}
        {failure &&
          (failure.kind === 'notPermitted' ? (
            <Alert severity="error" role="alert" sx={{ mt: 2 }}>
              {t('notPermitted')}
            </Alert>
          ) : (
            <Alert severity="error" role="alert" sx={{ mt: 2 }}>
              {failure.kind === 'unreachable' ? t('purge.causeUnreachable') : t('purge.causeOther')} {t('purge.failure')}
            </Alert>
          ))}
      </DialogContent>
      <DialogActions>
        {finished ? (
          <>
            {failure && failure.kind !== 'notPermitted' && (
              <Button onClick={retry} disabled={isUnreachable}>
                {t('purge.retry')}
              </Button>
            )}
            <Button onClick={close}>{t('purge.close')}</Button>
          </>
        ) : (
          <>
            <Button onClick={close} disabled={mutation.isPending}>
              {t('purge.cancel')}
            </Button>
            <Button variant="contained" onClick={send} disabled={mutation.isPending || isUnreachable}>
              {t('purge.confirm')}
            </Button>
          </>
        )}
      </DialogActions>
    </Dialog>
  );
}
