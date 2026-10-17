import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { IdentityProvider } from '../../platform/adapters/sessionAdapter';
import type { Member } from '../../platform/models/member';

interface Props {
  member: Member;
  providers: IdentityProvider[];
  busy: boolean;
  onIssueLink: (provider: string) => void;
  onUnlink: (provider: string) => void;
}

/** Linked provider accounts of a member, with link-code issuing and unlinking for Club Admins. */
export function ExternalIdentitySection({ member, providers, busy, onIssueLink, onUnlink }: Props) {
  const { t } = useTranslation('members');
  const [confirming, setConfirming] = useState<{ provider: string; name: string }>();
  const linked = new Set(member.externalIdentities.map((i) => i.provider));
  const linkable = providers.filter((p) => !linked.has(p.id));
  if (providers.length === 0 && member.externalIdentities.length === 0) return null;

  return (
    <>
      <Typography variant="h3" sx={{ mt: 3 }}>
        {t('members.externalIdentitiesTitle')}
      </Typography>
      {member.externalIdentities.length === 0 && <Typography>{t('members.noExternalIdentities')}</Typography>}
      <Stack component="ul" spacing={1} sx={{ listStyle: 'none', m: 0, p: 0, mt: 1 }}>
        {member.externalIdentities.map((i) => (
          <li key={i.provider}>
            <Stack direction="row" spacing={2} alignItems="center">
              <Typography>
                {t('members.externalIdentityEntry', { name: i.displayName, date: i.linkedAt.toLocaleDateString() })}
                {!i.providerConfigured && ` (${t('members.providerNotConfigured')})`}
              </Typography>
              <Button size="small" variant="outlined" disabled={busy} onClick={() => setConfirming({ provider: i.provider, name: i.displayName })}>
                {t('members.unlink', { name: i.displayName })}
              </Button>
            </Stack>
          </li>
        ))}
      </Stack>
      <Stack direction="row" spacing={1} useFlexGap flexWrap="wrap" sx={{ mt: 1 }}>
        {linkable.map((p) => (
          <Button key={p.id} variant="outlined" disabled={busy} onClick={() => onIssueLink(p.id)}>
            {t('members.issueLinkCredential', { name: p.displayName })}
          </Button>
        ))}
      </Stack>
      <Dialog open={Boolean(confirming)} onClose={() => setConfirming(undefined)} aria-labelledby="unlink-title">
        <DialogTitle id="unlink-title">{t('members.unlinkTitle', { name: confirming?.name })}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('members.unlinkBody', { member: member.accountName, name: confirming?.name })}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirming(undefined)}>{t('members.unlinkCancel')}</Button>
          <Button
            variant="contained"
            color="error"
            onClick={() => {
              if (confirming) onUnlink(confirming.provider);
              setConfirming(undefined);
            }}
          >
            {t('members.unlinkConfirm')}
          </Button>
        </DialogActions>
      </Dialog>
    </>
  );
}
