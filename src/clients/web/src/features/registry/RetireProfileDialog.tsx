import { ProfileTransitionDialog, type ProfileDialogProps } from './ProfileTransitionDialog';
import { useRetireProfile } from './registryKeys';

/** Retirement names the version, its digests, and what stays usable (FR-014, FR-017). */
export function RetireProfileDialog(props: ProfileDialogProps) {
  const mutation = useRetireProfile();
  return <ProfileTransitionDialog {...props} kind="retire" mutation={mutation} />;
}
