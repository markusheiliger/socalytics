export type RegistrationState =
  | 'pairing'
  | 'pendingApproval'
  | 'approved'
  | 'active'
  | 'unregistering'
  | 'unregistered'
  | 'rejected'
  | 'expired'
  | 'revoked'
  | 'unknown';

export type KeyProtection = 'tpm' | 'secure-enclave' | 'pkcs11-token' | 'software' | 'unknown';
export type ApplicationProfile = 'tray' | 'headless-server';
export type OperatingState = 'running' | 'paused' | 'runtimeUnavailable' | 'draining' | 'unknown';
export type Connectivity = 'online' | 'offline' | 'notApplicable' | 'unknown';

export interface RegistrationDeviceMetadata {
  os?: string;
  osVersion?: string;
  architecture?: string;
  managerVersion?: string;
  claimedKeyProtection: KeyProtection;
  claimedApplicationProfile?: ApplicationProfile;
  hostLabel?: string;
}

export interface RegistrationPairingOrigin {
  sourceAddress?: string;
  requestedAt?: Date;
}

export interface RegistrationStatus {
  operatingState: OperatingState;
  pausedUntil?: Date;
  runtimeUnavailableReason?: string;
  activeAnalysts?: number;
  maxAnalysts?: number;
  managerVersion?: string;
  reportedAt?: Date;
}

export interface Registration {
  registrationId: string;
  managerId?: string;
  version: number;
  state: RegistrationState;
  deviceFingerprint: string;
  deviceMetadata: RegistrationDeviceMetadata;
  pairingOrigin?: RegistrationPairingOrigin;
  approvalNotice?: string;
  submittedBy?: string;
  submittedAt?: Date;
  decidedBy?: string;
  decidedAt?: Date;
  decisionReason?: string;
  revokedBy?: string;
  endedAt?: Date;
  endReason?: string;
  activatedAt?: Date;
  createdAt?: Date;
  pairingExpiresAt?: Date;
  approvalExpiresAt?: Date;
  activationExpiresAt?: Date;
  status?: RegistrationStatus;
  connectivity?: Connectivity;
  minimumManagerVersion?: string;
  belowMinimumVersion: boolean;
}

export interface RegistrationPage {
  items: Registration[];
  continuationToken?: string;
}

export const CURRENT_STATES: readonly RegistrationState[] = ['pairing', 'pendingApproval', 'approved', 'active', 'unregistering'];
export const ENDED_STATES: readonly RegistrationState[] = ['unregistered', 'rejected', 'expired', 'revoked'];

export const isCurrent = (r: Pick<Registration, 'state'>) => CURRENT_STATES.includes(r.state);
export const isEnded = (r: Pick<Registration, 'state'>) => ENDED_STATES.includes(r.state);
export const isOwnSubmission = (r: Pick<Registration, 'submittedBy'>, memberId: string | undefined) =>
  r.submittedBy !== undefined && memberId !== undefined && r.submittedBy === memberId;
