import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import { useTranslation } from 'react-i18next';
import { SessionProblemAlert } from '../account/SessionProblemAlert';
import type { PlatformFailure } from '../../platform/failure';

/** Message for a failed platform request; conflicts offer to reload the current version (FR-017). */
export function FailureAlert({ failure, onReload }: { failure: PlatformFailure; onReload?: () => void }) {
  const { t } = useTranslation('common');
  if (failure.kind === 'sessionProblem') return <SessionProblemAlert />;
  const key = ['conflict', 'unreachable', 'validation', 'incompatible'].includes(failure.kind) ? failure.kind : 'error';
  return (
    <Alert
      severity="error"
      role="alert"
      action={
        failure.kind === 'conflict' && onReload ? (
          <Button color="inherit" size="small" onClick={onReload}>
            {t('failure.reload')}
          </Button>
        ) : undefined
      }
    >
      {t(`failure.${key}`)}
      {key === 'error' && failure.correlationId ? ` ${t('failure.reference', { id: failure.correlationId })}` : ''}
    </Alert>
  );
}

/** The platform's reason for one field, shown next to it. */
export function useFieldError(failure: PlatformFailure | undefined, field: string, t: (key: string) => string) {
  const error = failure?.kind === 'validation' ? failure.fieldErrors.find((e) => e.field === field) : undefined;
  if (!error) return undefined;
  return ['required', 'too-long'].includes(error.code) ? t(`fieldError.${error.code}`) : t('fieldError.other');
}
