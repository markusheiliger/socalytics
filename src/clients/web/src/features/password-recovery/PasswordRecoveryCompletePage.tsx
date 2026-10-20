import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link as RouterLink, useLocation, useNavigate } from 'react-router';
import { createPasswordRecoveryAdapter } from '../../platform/adapters/passwordRecoveryAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';

type Stage =
  | { kind: 'checking' }
  | { kind: 'invalid' }
  | { kind: 'ready'; accountName: string }
  | { kind: 'done' }
  | { kind: 'blocked'; reason: 'rateLimited' | 'unreachable' | 'error'; seconds?: number };

type Problem = 'mismatch' | 'policy' | 'refused' | 'unreachable' | 'error' | { rateLimited: number | undefined };

/** Completes a recovery: the link only opens this page, the token is used when the new password is submitted. */
export function PasswordRecoveryCompletePage() {
  const { t } = useTranslation('passwordRecovery');
  const { platform } = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  const adapter = useMemo(() => createPasswordRecoveryAdapter(platform), [platform]);
  // The token lives only in this ref: never in state, storage, or the address.
  const token = useRef('');
  const fromFragment = new URLSearchParams(location.hash.replace(/^#/, '')).get('token');
  if (fromFragment) token.current = fromFragment;
  const checked = useRef(false);
  const [stage, setStage] = useState<Stage>({ kind: 'checking' });
  const [newPassword, setNewPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem>();

  useLayoutEffect(() => {
    if (!window.location.hash && !location.hash) return;
    window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search);
    void navigate({ pathname: location.pathname, search: location.search }, { replace: true, state: location.state });
  }, [location.hash, location.pathname, location.search, location.state, navigate]);

  useEffect(() => {
    if (checked.current) return;
    checked.current = true;
    if (!token.current) {
      setStage({ kind: 'invalid' });
      return;
    }
    adapter.checkRecovery(token.current).then(
      (result) => {
        if (result.ok) setStage({ kind: 'ready', accountName: result.value });
        else if (result.kind === 'rateLimited') setStage({ kind: 'blocked', reason: 'rateLimited', seconds: result.retryAfterSeconds });
        else setStage({ kind: 'invalid' });
      },
      (error: unknown) => setStage({ kind: 'blocked', reason: toPlatformFailure(error).kind === 'unreachable' ? 'unreachable' : 'error' }),
    );
  }, [adapter]);

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (newPassword !== confirmation) {
      setProblem('mismatch');
      return;
    }
    setProblem(undefined);
    setBusy(true);
    try {
      const result = await adapter.completeRecovery(token.current, newPassword);
      if (result.ok) {
        token.current = '';
        setNewPassword('');
        setConfirmation('');
        setStage({ kind: 'done' });
      } else if (result.kind === 'invalid') {
        token.current = '';
        setStage({ kind: 'invalid' });
      } else if (result.kind === 'rateLimited') {
        setProblem({ rateLimited: result.retryAfterSeconds });
      } else {
        setProblem(result.fieldErrors.some((e) => e.field === 'newPassword' && e.code === 'password-policy') ? 'policy' : 'refused');
      }
    } catch (error) {
      setProblem(toPlatformFailure(error).kind === 'unreachable' ? 'unreachable' : 'error');
    } finally {
      setBusy(false);
    }
  };

  const problemText = (() => {
    if (problem === undefined) return undefined;
    if (typeof problem === 'string') return t(`complete.problem.${problem}`);
    return problem.rateLimited === undefined
      ? t('complete.rateLimitedUnknown')
      : t('complete.rateLimited', { seconds: problem.rateLimited });
  })();

  return (
    <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
      <Typography variant="h1">{t('complete.title')}</Typography>
      {stage.kind === 'checking' && (
        <Typography role="status" sx={{ mt: 2 }}>
          {t('complete.checking')}
        </Typography>
      )}
      {stage.kind === 'invalid' && (
        <Stack spacing={2} sx={{ mt: 2 }}>
          <Alert severity="error" role="alert">
            {t('complete.invalid')}
          </Alert>
          <Button component={RouterLink} to="/password-recovery" variant="contained">
            {t('complete.requestNew')}
          </Button>
        </Stack>
      )}
      {stage.kind === 'blocked' && (
        <Alert severity={stage.reason === 'rateLimited' ? 'warning' : 'error'} role="alert" sx={{ mt: 2 }}>
          {stage.reason === 'rateLimited'
            ? stage.seconds === undefined
              ? t('complete.rateLimitedUnknown')
              : t('complete.rateLimited', { seconds: stage.seconds })
            : t(`complete.problem.${stage.reason}`)}
        </Alert>
      )}
      {stage.kind === 'done' && (
        <Stack spacing={2} sx={{ mt: 2 }}>
          <Alert severity="success" role="status">
            {t('complete.done')}
          </Alert>
          <Button component={RouterLink} to="/sign-in" variant="contained">
            {t('complete.toSignIn')}
          </Button>
        </Stack>
      )}
      {stage.kind === 'ready' && (
        <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={(e: FormEvent) => void onSubmit(e)} noValidate>
          <Typography>{t('complete.intro', { accountName: stage.accountName })}</Typography>
          {problemText && (
            <Alert severity="error" role="alert">
              {problemText}
            </Alert>
          )}
          <TextField
            label={t('complete.accountName')}
            name="accountName"
            autoComplete="username"
            value={stage.accountName}
            slotProps={{ htmlInput: { readOnly: true } }}
          />
          <TextField
            label={t('complete.newPassword')}
            name="newPassword"
            type="password"
            autoComplete="new-password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            required
            autoFocus
          />
          <TextField
            label={t('complete.confirmPassword')}
            name="confirmation"
            type="password"
            autoComplete="new-password"
            value={confirmation}
            onChange={(e) => setConfirmation(e.target.value)}
            required
          />
          <Button type="submit" variant="contained" disabled={busy || !newPassword || !confirmation}>
            {busy ? t('complete.submitting') : t('complete.submit')}
          </Button>
        </Stack>
      )}
    </Container>
  );
}
