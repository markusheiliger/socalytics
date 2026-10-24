import type { FieldError, PlatformFailureKind } from '../failure';

/** A platform value kept raw; `known` is false when the Web UI has no wording for it. */
export interface RawValue<T extends string = string> {
  value: string;
  known: boolean;
  /** The recognized value when `known` is true. */
  recognized?: T;
}

export type KnownTurnState = 'pending' | 'succeeded' | 'failed';
export type TurnState = RawValue<KnownTurnState>;

export type KnownFailureReason = 'attempts-exhausted' | 'execution-failed' | 'scope-lost';
export type FailureReason = RawValue<KnownFailureReason>;

export const knownLineageKinds = [
  'submitted',
  'claimed',
  'checkpoint',
  'lease-expired',
  'deadline-reached',
  'failed',
  'scope-lost',
  'exhausted',
  'completed',
] as const;

export type KnownLineageKind = (typeof knownLineageKinds)[number];
export type LineageKind = RawValue<KnownLineageKind>;

export type KnownAgentGroup = 'specialist';
export type AgentGroup = RawValue<KnownAgentGroup>;

const knownStates: Record<string, KnownTurnState> = {
  accepted: 'pending',
  queued: 'pending',
  claimed: 'pending',
  running: 'pending',
  succeeded: 'succeeded',
  failed: 'failed',
};

const knownFailureReasons: readonly KnownFailureReason[] = [
  'attempts-exhausted',
  'execution-failed',
  'scope-lost',
];

function raw<T extends string>(value: string, recognized: T | undefined): RawValue<T> {
  return recognized === undefined ? { value, known: false } : { value, known: true, recognized };
}

export function toTurnState(value: string): TurnState {
  return raw(value, Object.hasOwn(knownStates, value) ? knownStates[value] : undefined);
}

export function toFailureReason(value: string): FailureReason {
  return raw(value, knownFailureReasons.find((reason) => reason === value));
}

export function toLineageKind(value: string): LineageKind {
  return raw(value, knownLineageKinds.find((kind) => kind === value));
}

export function toAgentGroup(value: string): AgentGroup {
  return raw(value, value === 'specialist' ? 'specialist' : undefined);
}

/**
 * A turn is pending until it is terminal: the API's `accepted`, `queued`, `claimed`, and `running` states and any
 * unknown value keep polling.
 */
export function isTurnPending(state: TurnState): boolean {
  return state.recognized !== 'succeeded' && state.recognized !== 'failed';
}

export interface EnabledAgent {
  agentId: string;
  group: AgentGroup;
  domain: string;
}

export type ConversationStateFilter = 'active' | 'archived';

export interface ConversationParticipant {
  agentId: string;
  addedAt: string;
}

export interface ConversationSummary {
  conversationId: string;
  matchId: string;
  analysisRunId: string;
  teamId: string;
  analysisRunIsCurrent: boolean;
  state: string;
  participants: ConversationParticipant[];
  createdAt: string;
  version: number;
}

export interface TriageConversation extends ConversationSummary {
  etag: string;
}

export interface EvidenceReference {
  kind?: string;
  reference: string;
}

export interface ExecutionReferences {
  runtime?: string;
  modelProvider?: string;
  model?: string;
  deployment?: string;
  promptRef?: string;
  toolPolicyRef?: string;
  safetyPolicyRef?: string;
}

export interface Advice {
  text: string;
  evidenceReferences: EvidenceReference[];
  executionReferences?: ExecutionReferences;
}

export interface Turn {
  invocationId: string;
  agentId: string;
  question: string;
  state: TurnState;
  failureReason?: FailureReason;
  submittedAt: string;
  endedAt?: string;
  advice?: Advice;
}

export interface TurnPage {
  turns: Turn[];
  nextCursor?: string;
  etag: string;
}

export interface InvocationStatus {
  invocationId: string;
  conversationId: string;
  agentId: string;
  route: string;
  state: TurnState;
  failureReason?: FailureReason;
  attemptCount: number;
  retryAfterSeconds?: number;
  etag: string;
}

export interface LineageEntry {
  sequence: number;
  kind: LineageKind;
  attemptId?: string;
  attemptNumber?: number;
  recordedAt: string;
  details: Record<string, string>;
}

export interface InvocationLineage {
  invocationId: string;
  policyVersion: string;
  entries: LineageEntry[];
  advice?: Advice;
  etag: string;
}

export interface SubmissionAccepted {
  conversationId: string;
  invocationId: string;
  statusLocation: string;
  retryAfterSeconds: number;
}

export type Conditional<T> =
  | { kind: 'unchanged' }
  | { kind: 'changed'; value: T; etag: string; retryAfterSeconds?: number };

export type TriageOutcome =
  | { kind: 'tooManyUnfinished' }
  | { kind: 'agentNotAvailable' }
  | { kind: 'agentNotParticipant' }
  | { kind: 'participantExists' }
  | { kind: 'runNotEligible' }
  | { kind: 'conversationArchived' }
  | { kind: 'invalidQuestion'; fieldErrors: FieldError[] }
  | { kind: 'notPermitted' }
  | { kind: 'staleVersion' }
  | { kind: 'notFound' }
  | { kind: 'unknownOutcome' }
  | { kind: 'platform'; failureKind: PlatformFailureKind };
