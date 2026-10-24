import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import FormControlLabel from '@mui/material/FormControlLabel';
import Typography from '@mui/material/Typography';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toPlatformFailure } from '../../platform/failure';
import type { AgentVersion } from '../../platform/models/agentCatalog';
import { useSession } from '../../session/sessionContext';
import { FailureAlert } from '../shared/FailureAlert';
import { useAgentCatalogAdapter, useApproveAgentVersion } from './agentCatalogKeys';
import { DistanceList } from './artifacts/DistanceList';
import { refusalText } from './outcomeText';

export interface ApproveAgentVersionDialogProps {
  /** The draft version as last loaded by the caller; its `etag` is sent as `If-Match`. */
  version: AgentVersion;
  open: boolean;
  onClose: () => void;
  onDone?: (result: AgentVersion) => void;
}

/** Confirmation of an approval: confirming sends exactly one request, cancelling sends nothing, and nothing is ever repeated (FR-014, FR-017). */
export function ApproveAgentVersionDialog({ version, open, onClose, onDone }: ApproveAgentVersionDialogProps) {
  const { t } = useTranslation('agentCatalog');
  const { session } = useSession();
  const adapter = useAgentCatalogAdapter();
  const mutation = useApproveAgentVersion();
  const [sent, setSent] = useState(false);
  const [done, setDone] = useState(false);
  const [acknowledged, setAcknowledged] = useState(false);
  const [current, setCurrent] = useState<AgentVersion | undefined>();
  const outcome = mutation.data && 'kind' in mutation.data ? mutation.data : undefined;
  const refused = outcome?.kind === 'transitionRefused' ? outcome : undefined;
  const changed = outcome?.kind === 'changed';
  const failure = mutation.error ? toPlatformFailure(mutation.error) : undefined;
  const reviewDistances = (version.distances ?? []).filter((d) => d.reviewRequired);
  const needsAcknowledgement = reviewDistances.length > 0;
  const selfApproval = session?.member.id !== undefined && version.registeredBy.id === session.member.id;
  const refusal = refused ? refusalText(refused.code) : undefined;
  const finished = done || changed || Boolean(refused) || Boolean(failure);

  const confirm = () => {
    if (sent || (needsAcknowledgement && !acknowledged)) return;
    setSent(true);
    mutation.mutate(
      {
        agentId: version.agentId,
        version: version.version,
        etag: version.etag,
        ...(needsAcknowledgement ? { differentiationReviewAcknowledged: true } : {}),
      },
      {
        onSuccess: (result) => {
          if ('kind' in result) {
            if (result.kind === 'changed') {
              adapter.getAgentVersion(version.agentId, version.version).then(setCurrent, () => undefined);
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
    setAcknowledged(false);
    setCurrent(undefined);
    mutation.reset();
    onClose();
  };

  return (
    <Dialog open={open} onClose={close} aria-labelledby="approve-version-title">
      <DialogTitle id="approve-version-title">
        {t('confirm.approve.title', { version: version.version, agentId: version.agentId })}
      </DialogTitle>
      <DialogContent>
        <DialogContentText component="div" sx={{ overflowWrap: 'anywhere' }}>
          <div>{t('confirm.approve.agent', { agentId: version.agentId })}</div>
          <div>{t('confirm.approve.version', { version: version.version })}</div>
          <div>{t('confirm.approve.digest', { digest: version.digest, interpolation: { escapeValue: false } })}</div>
          <Typography component="div" sx={{ mt: 1 }}>{t('confirm.approve.artifactDigests')}</Typography>
          <ul>
            {version.artifacts.map((a) => (
              <li key={`${a.kind}/${a.id}/${a.version}`}>
                {t('confirm.approve.artifactDigest', { kind: a.kind, id: a.id, version: a.version, digest: a.digest, interpolation: { escapeValue: false } })}
              </li>
            ))}
          </ul>
        </DialogContentText>
        <Alert severity="info" sx={{ mt: 1 }}>
          <div>{t('confirm.approve.immutable')}</div>
          <div>{t('confirm.approve.accountable')}</div>
        </Alert>
        {selfApproval && <Alert severity="warning" sx={{ mt: 2 }}>{t('confirm.approve.selfApproval')}</Alert>}
        {needsAcknowledgement && (
          <Alert severity="warning" sx={{ mt: 2 }}>
            <Typography component="div" fontWeight="bold">{t('confirm.approve.distancesTitle')}</Typography>
            <DistanceList distances={reviewDistances} />
            <FormControlLabel
              control={<Checkbox checked={acknowledged} disabled={finished} onChange={(e) => setAcknowledged(e.target.checked)} />}
              label={t('confirm.approve.acknowledgement')}
            />
          </Alert>
        )}
        {done && <Alert severity="success" role="status" sx={{ mt: 2 }}>{t('confirm.approve.immutable')}</Alert>}
        {refusal && (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>
            <strong>{t('refusals.title')}</strong>
            <div>{t(refusal.key, refusal.values)}</div>
          </Alert>
        )}
        {changed && (
          <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
            <div>{t('outcomes.changed')}</div>
            {current && <div>{t(`states.${current.state}`)}</div>}
          </Alert>
        )}
        {failure && <FailureAlert failure={failure} />}
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{finished ? t('common.close') : t('common.cancel')}</Button>
        {!finished && (
          <Button variant="contained" disabled={sent || (needsAcknowledgement && !acknowledged)} onClick={confirm}>
            {mutation.isPending ? t('common.working') : t('confirm.approve.submit')}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
