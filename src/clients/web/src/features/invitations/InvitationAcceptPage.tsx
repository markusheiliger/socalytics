import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link as RouterLink, useLocation, useNavigate } from 'react-router';
import { createInvitationAdapter } from '../../platform/adapters/invitationAdapter';
import { toPlatformFailure } from '../../platform/failure';
import { useSession } from '../../session/sessionContext';

type Stage =
  | { kind: 'checking' }
  | { kind: 'invalid' }
  | { kind: 'ready'; clubName: string; accountName: string }
  | { kind: 'done' }
  | { kind: 'blocked'; reason: 'rateLimited' | 'unreachable' | 'error'; seconds?: number };

type Problem = 'mismatch' | 'policy' | 'refused' | 'unreachable' | 'error' | { rateLimited: number | undefined };

/** Accepts an invitation: the link only opens this page, the token is used when the password is submitted. */
export function InvitationAcceptPage() {
  const { t } = useTranslation('invitations');
  const { platform } = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  const adapter = useMemo(() => createInvitationAdapter(platform), [platform]);
  // The token lives only in this ref: never in state, storage, or the address.
  const token = useRef('');
  const fromFragment = new URLSearchParams(location.hash.replace(/^#/, '')).get('token');
  if (fromFragment) token.current = fromFragment;
  const checked = useRef(false);
  const [stage, setStage] = useState<Stage>({ kind: 'checking' });
  const [password, setPassword] = useState('');
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
    adapter.checkInvitation(token.current).then(
      (result) => {
        if (result.ok) setStage({ kind: 'ready', clubName: result.value.clubName, accountName: result.value.accountName });
        else if (result.kind === 'rateLimited') setStage({ kind: 'blocked', reason: 'rateLimited', seconds: result.retryAfterSeconds });
        else setStage({ kind: 'invalid' });
      },
      (error: unknown) => setStage({ kind: 'blocked', reason: toPlatformFailure(error).kind === 'unreachable' ? 'unreachable' : 'error' }),
    );
  }, [adapter]);

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (password !== confirmation) {
      setProblem('mismatch');
      return;
    }
    setProblem(undefined);
    setBusy(true);
    try {
      const result = await adapter.acceptInvitation(token.current, password);
      if (result.ok) {
        token.current = '';
        setPassword('');
        setConfirmation('');
        setStage({ kind: 'done' });
      } else if (result.kind === 'invalid') {
        token.current = '';
        setStage({ kind: 'invalid' });
      } else if (result.kind === 'rateLimited') {
        setProblem({ rateLimited: result.retryAfterSeconds });
      } else if (result.kind === 'fieldErrors') {
        setProblem(result.fieldErrors.some((e) => e.field === 'password' && e.code === 'password-policy') ? 'policy' : 'refused');
      } else {
        setProblem('refused');
      }
    } catch (error) {
      setProblem(toPlatformFailure(error).kind === 'unreachable' ? 'unreachable' : 'error');
    } finally {
      setBusy(false);
    }
  };

  const rateLimitedText = (seconds: number | undefined) =>
    seconds === undefined ? t('accept.rateLimitedUnknown') : t('accept.rateLimited', { seconds });

  const problemText = (() => {
    if (problem === undefined) return undefined;
    if (typeof problem === 'string') return t(`accept.problem.${problem}`);
    return rateLimitedText(problem.rateLimited);
  })();

  return (
    <Container component="main" maxWidth="xs" sx={{ py: 4 }}>
      <Typography variant="h1">{t('accept.title')}</Typography>
      {stage.kind === 'checking' && (
        <Typography role="status" sx={{ mt: 2 }}>
          {t('accept.checking')}
        </Typography>
      )}
      {stage.kind === 'invalid' && (
        <Alert severity="error" role="alert" sx={{ mt: 2 }}>
          {t('accept.invalid')}
        </Alert>
      )}
      {stage.kind === 'blocked' && (
        <Alert severity={stage.reason === 'rateLimited' ? 'warning' : 'error'} role="alert" sx={{ mt: 2 }}>
          {stage.reason === 'rateLimited' ? rateLimitedText(stage.seconds) : t(`accept.problem.${stage.reason}`)}
        </Alert>
      )}
      {stage.kind === 'done' && (
        <Stack spacing={2} sx={{ mt: 2 }}>
          <Alert severity="success" role="status">
            {t('accept.done')}
          </Alert>
          <Button component={RouterLink} to="/sign-in" variant="contained">
            {t('accept.toSignIn')}
          </Button>
        </Stack>
      )}
      {stage.kind === 'ready' && (
        <Stack component="form" spacing={2} sx={{ mt: 2 }} onSubmit={(e: FormEvent) => void onSubmit(e)} noValidate>
          <Typography>{t('accept.intro', { clubName: stage.clubName })}</Typography>
          {problemText && (
            <Alert severity="error" role="alert">
              {problemText}
            </Alert>
          )}
          <TextField
            label={t('accept.accountName')}
            name="accountName"
            autoComplete="username"
            value={stage.accountName}
            slotProps={{ htmlInput: { readOnly: true } }}
          />
          <TextField
            label={t('accept.password')}
            name="password"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            autoFocus
          />
          <TextField
            label={t('accept.confirmPassword')}
            name="confirmation"
            type="password"
            autoComplete="new-password"
            value={confirmation}
            onChange={(e) => setConfirmation(e.target.value)}
            required
          />
          <Button type="submit" variant="contained" disabled={busy || !password || !confirmation}>
            {busy ? t('accept.submitting') : t('accept.submit')}
          </Button>
        </Stack>
      )}
    </Container>
  );
}
