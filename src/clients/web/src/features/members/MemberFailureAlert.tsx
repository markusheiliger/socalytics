import Alert from '@mui/material/Alert';
import { useTranslation } from 'react-i18next';
import type { PlatformFailure } from '../../platform/failure';
import { FailureAlert } from '../shared/FailureAlert';

/** The platform's refusals of member changes are shown as clear reasons; everything else uses the shared message. */
export function MemberFailureAlert({ failure }: { failure: PlatformFailure }) {
  const { t } = useTranslation('members');
  if (failure.kind !== 'conflict') return <FailureAlert failure={failure} />;
  return (
    <Alert severity="error" role="alert">
      {failure.code === 'last-club-admin' ? t('members.refusal.lastClubAdmin') : t('members.refusal.conflict')}
    </Alert>
  );
}
