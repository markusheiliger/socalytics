import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import type { UseMutationResult } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toPlatformFailure } from '../../platform/failure';
import { RegistryChangedError, RegistryRefusalError, type ProfileVersion } from '../../platform/models/registry';
import { FailureAlert } from '../shared/FailureAlert';
import { RefusalAlert } from './RefusalAlert';
import { useRegistryAdapter } from './registryKeys';

export interface ProfileDialogProps {
  profile: ProfileVersion;
  open: boolean;
  onClose: () => void;
  /** Called with the changed version after a successful transition. */
  onDone?: (profile: ProfileVersion) => void;
}

interface Transition {
  profileId: string;
  version: number;
  rowVersion: number;
}

interface Props extends ProfileDialogProps {
  kind: 'approve' | 'retire';
  mutation: UseMutationResult<ProfileVersion, Error, Transition>;
  note?: string;
}

/** Shared confirmation of a profile state change: confirming sends one request with the version's `rowVersion`, cancelling sends nothing. */
export function ProfileTransitionDialog({ kind, mutation, note, profile, open, onClose, onDone }: Props) {
  const { t } = useTranslation('registry');
  const adapter = useRegistryAdapter();
  const [sent, setSent] = useState(false);
  const [done, setDone] = useState(false);
  const [reloaded, setReloaded] = useState<ProfileVersion | undefined>();
  const error = mutation.error;
  const refusal = error instanceof RegistryRefusalError ? error.refusal : undefined;
  const changed = error instanceof RegistryChangedError;
  const failure = error && !refusal && !changed ? toPlatformFailure(error) : undefined;
  const rowVersion = profile.rowVersion;

  const confirm = () => {
    if (sent || rowVersion === undefined) return;
    setSent(true);
    mutation.mutate(
      { profileId: profile.profileId, version: profile.version, rowVersion },
      {
        onSuccess: (result) => {
          setDone(true);
          onDone?.(result);
        },
        onError: (cause) => {
          if (!(cause instanceof RegistryChangedError)) return;
          adapter.getProfile(profile.profileId, profile.version).then(setReloaded, () => undefined);
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
    <Dialog open={open} onClose={close} aria-labelledby={`${kind}-profile-title`}>
      <DialogTitle id={`${kind}-profile-title`}>{t(`confirm.${kind}.title`)}</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ overflowWrap: 'anywhere' }}>
          {t(`confirm.${kind}.body`, {
            profileId: profile.profileId,
            version: profile.version,
            imageDigest: profile.imageDigest,
            modelDigest: profile.content.modelDigest,
          })}
        </DialogContentText>
        {note && <Alert severity="warning" sx={{ mt: 2 }}>{note}</Alert>}
        {done && <Alert severity="success" role="status" sx={{ mt: 2 }}>{t(kind === 'approve' ? 'profileVersion.approved' : 'profileVersion.retired')}</Alert>}
        {refusal && <RefusalAlert refusal={refusal} />}
        {changed && (
          <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
            <strong>{t('changed.title')}</strong>
            <div>{t('changed.message')}</div>
            {reloaded && (
              <div>{t('changed.currentState', { state: t(`profileState.${reloaded.state}`, { defaultValue: reloaded.state }) })}</div>
            )}
          </Alert>
        )}
        {failure && <FailureAlert failure={failure} />}
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{finished ? t('confirm.close') : t('confirm.cancel')}</Button>
        {!finished && (
          <Button variant="contained" color={kind === 'retire' ? 'error' : 'primary'} disabled={sent || rowVersion === undefined} onClick={confirm}>
            {mutation.isPending ? t('confirm.working') : t(`confirm.${kind}.action`)}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
