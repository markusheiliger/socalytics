import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Typography from '@mui/material/Typography';
import { useTranslation } from 'react-i18next';
import type { CredentialCode } from '../../platform/models/member';

/** Shows a credential code once; the caller drops it from state on close, so it is never shown again. */
export function CredentialDialog({ credential, accountName, onClose }: { credential: CredentialCode | undefined; accountName: string; onClose: () => void }) {
  const { t } = useTranslation('members');
  return (
    <Dialog open={Boolean(credential)} onClose={onClose} aria-labelledby="credential-title">
      <DialogTitle id="credential-title">{t('members.credential.title')}</DialogTitle>
      <DialogContent>
        <DialogContentText>{t('members.credential.intro', { name: accountName })}</DialogContentText>
        {credential && (
          <>
            <Typography component="p" sx={{ mt: 2, fontFamily: 'monospace', fontSize: '1.25rem', wordBreak: 'break-all' }}>
              <span aria-label={t('members.credential.codeLabel')}>{credential.code}</span>
            </Typography>
            <Typography sx={{ mt: 1 }}>{t('members.credential.expires', { date: credential.expiresAt.toLocaleString() })}</Typography>
          </>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} variant="contained" autoFocus>
          {t('members.credential.close')}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
