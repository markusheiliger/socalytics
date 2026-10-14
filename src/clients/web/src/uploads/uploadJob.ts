export type UploadJobState =
  | 'selected'
  | 'preparing'
  | 'ready'
  | 'starting'
  | 'queued'
  | 'transferring'
  | 'paused'
  | 'awaitingMapping'
  | 'completing'
  | 'completed'
  | 'refused'
  | 'needsFile'
  | 'notPermitted'
  | 'cancelled'
  | 'abandoned'
  | 'expired'
  | 'failed';

export type PausedReason = 'retriesExhausted' | 'unreachable' | 'storageUnavailable' | 'sessionEnded' | 'runningElsewhere' | 'memberRequested';
export type FailedReason = 'fileChanged' | 'objectMismatch' | 'integrityUnavailable';
export type RefusedReason = 'partsIncomplete' | 'partMismatch' | 'fileMismatch';

export type UploadJobReason = PausedReason | FailedReason | RefusedReason;

export const finalStates: readonly UploadJobState[] = ['completed', 'abandoned', 'expired', 'cancelled', 'failed'];

const reasonsByState: Partial<Record<UploadJobState, readonly UploadJobReason[]>> = {
  paused: ['retriesExhausted', 'unreachable', 'storageUnavailable', 'sessionEnded', 'runningElsewhere', 'memberRequested'],
  failed: ['fileChanged', 'objectMismatch', 'integrityUnavailable'],
  refused: ['partsIncomplete', 'partMismatch', 'fileMismatch'],
};

const transitions: Record<UploadJobState, readonly UploadJobState[]> = {
  selected: ['preparing'],
  preparing: ['ready', 'queued', 'cancelled', 'refused'],
  ready: ['starting', 'cancelled'],
  // A refused or unreachable start returns to ready; the retry reuses the start key.
  starting: ['queued', 'ready', 'cancelled'],
  queued: ['transferring', 'paused'],
  transferring: ['awaitingMapping', 'completing', 'paused', 'queued'],
  paused: ['queued', 'transferring', 'awaitingMapping', 'completing'],
  awaitingMapping: ['completing', 'paused'],
  completing: ['completed', 'refused', 'paused'],
  // A reloaded job whose session the platform reports as completed ends without its file.
  refused: ['queued', 'preparing', 'completed'],
  needsFile: ['preparing', 'completed'],
  notPermitted: ['abandoned'],
  completed: [],
  cancelled: [],
  abandoned: [],
  expired: [],
  failed: [],
};

// Any non-final state can end as abandoned, expired, notPermitted, or failed.
const fromAnyNonFinal: readonly UploadJobState[] = ['abandoned', 'expired', 'notPermitted', 'failed'];

export function reasonRequired(state: UploadJobState): boolean {
  return state in reasonsByState;
}

export function canTransition(from: UploadJobState, to: UploadJobState): boolean {
  if (finalStates.includes(from)) {
    return false;
  }
  if (from === 'notPermitted') {
    return to === 'abandoned';
  }
  return transitions[from].includes(to) || fromAnyNonFinal.includes(to);
}

export interface JobStatus {
  readonly state: UploadJobState;
  readonly reason?: UploadJobReason;
}

export class InvalidUploadTransitionError extends Error {
  constructor(from: JobStatus, to: UploadJobState, reason?: UploadJobReason) {
    super(`Upload job cannot change from ${from.state} to ${to}${reason ? ` (${reason})` : ''}.`);
    this.name = 'InvalidUploadTransitionError';
  }
}

export function transition(from: JobStatus, to: UploadJobState, reason?: UploadJobReason): JobStatus {
  const allowedReasons = reasonsByState[to];
  const reasonValid = allowedReasons ? reason !== undefined && allowedReasons.includes(reason) : reason === undefined;
  if (!reasonValid || !canTransition(from.state, to)) {
    throw new InvalidUploadTransitionError(from, to, reason);
  }
  return reason ? { state: to, reason } : { state: to };
}
