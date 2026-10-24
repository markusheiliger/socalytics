import { useTranslation } from 'react-i18next';
import { useSession } from '../../session/sessionContext';
import { ProfileTransitionDialog, type ProfileDialogProps } from './ProfileTransitionDialog';
import { useApproveProfile } from './registryKeys';

/** Approval names the version, its digests, and that approved versions never change; adds a note when the signed-in member created it (FR-014). */
export function ApproveProfileDialog(props: ProfileDialogProps) {
  const { t } = useTranslation('registry');
  const { session } = useSession();
  const mutation = useApproveProfile();
  const selfApproval = session?.member.id !== undefined && props.profile.createdBy.id === session.member.id;
  return <ProfileTransitionDialog {...props} kind="approve" mutation={mutation} note={selfApproval ? t('selfApproval') : undefined} />;
}
