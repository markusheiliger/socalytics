export type OutboxState = 'pending' | 'published' | 'failed' | 'discarded';

export type OutboxMessageType =
  | 'matches.recordings-finalized'
  | 'analysis.ready-work'
  | 'analysis.run-state-changed';

export type ReceiptOutcome = 'run-created' | 'run-exists' | 'rejected';

export interface WorkflowDefinitionStatus {
  /** `resolvable`, `unresolvable`, or a raw unknown value. */
  state: string;
  category?: string;
  since?: Date;
}

export interface RunStateCount {
  state: string;
  count: number;
}

export interface OperationalSummary {
  workflowDefinition: WorkflowDefinitionStatus;
  /** Known states first in lifecycle order, then unknown ones; unreported states are absent. */
  runsByState: RunStateCount[];
  runsWithBlockedNodes: number;
  staleAttempts: number;
  rejectedNotifications: number;
  outboxPending: number;
  outboxFailed: number;
  /** Absent means nothing is pending. */
  oldestPendingAgeSeconds: number | undefined;
  observedAt: Date;
}

export interface OutboxMessage {
  messageId: string;
  /** Known types are `OutboxMessageType` values; unknown values stay raw. */
  messageType: OutboxMessageType | string;
  contractVersion: string;
  subject: string;
  /** Known states are `OutboxState` values; unknown values stay raw. */
  state: OutboxState | string;
  attemptCount: number;
  publicationRound: number;
  lastAttemptAt?: Date;
  nextAttemptAt?: Date;
  failureCategory?: string;
  createdAt: Date;
  publishedAt?: Date;
  ageSeconds: number;
  publicationLagSeconds?: number;
  runId?: string;
  logicalJobId?: string;
  capabilityId?: string;
}

export interface NotificationReceipt {
  messageId: string;
  messageType: OutboxMessageType | string;
  recordingSetVersionId?: string;
  /** Known outcomes are `ReceiptOutcome` values; unknown values stay raw. */
  outcome: ReceiptOutcome | string;
  rejectionCategory?: string;
  runId?: string;
  receivedAt: Date;
}

export interface Page<T> {
  items: T[];
  /** Opaque; kept only in the in-memory page stack. */
  continuationToken: string | undefined;
}

export interface ReconciliationOutcome {
  readyJobsRepublished: number;
  notificationsRepublished: number;
  startedAt: Date;
  completedAt: Date;
}
