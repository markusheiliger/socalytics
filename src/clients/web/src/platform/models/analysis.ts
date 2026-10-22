export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

/** Known values are `Requested`, `Running`, `PartiallyCompleted`, `Completed`, `Failed`, and `Cancelled`; unknown values stay raw. */
export type RunState = string;

export interface JobCounts {
  waiting: number;
  ready: number;
  claimed: number;
  completed: number;
  failed: number;
  blocked: number;
}

/** One reported dependency of a node; `coverageMode` stays raw so unfamiliar policies are not interpreted. */
export interface NodeInputCoverage {
  upstreamNodeKey: string;
  coverageMode: string;
  minimumAcceptedFraction?: number;
  accepted: number;
  missing: number;
  failed: number;
  blocked: number;
  skipped: number;
}

export interface RunSupersession {
  runId: string;
  recordingSetVersionId: string;
  recordingSetFinalizedAt: Date;
}

export interface NodeStatus {
  nodeKey: string;
  capabilityId: string;
  capabilityVersion: string;
  tier: string;
  executionScope: string;
  state: string;
  blockedByNodeKey?: string;
  jobs: JobCounts;
  /** `undefined` means not reported, which differs from a reported empty list. */
  inputCoverage?: NodeInputCoverage[];
}

export interface AnalysisRunStatus {
  runId: string;
  teamId: string;
  matchId: string;
  recordingSetVersionId: string;
  workflowId: string;
  workflowVersion: string;
  state: RunState;
  failureReason?: string;
  createdAt: Date;
  runningAt?: Date;
  terminatedAt?: Date;
  /** Explicit from the status response; false does not by itself mean superseded. */
  isCurrent: boolean;
  supersededBy?: RunSupersession;
  supersededAt?: Date;
  requestedCapabilities: string[];
  nodes: NodeStatus[];
}

export interface Provenance {
  workflow: { id: string; version: string };
  analystProfile: { id: string; digest: string };
  imageDigest: string;
  modelDigest?: string;
}

export interface CoverageCounts {
  accepted: number;
  missing: number;
  failed: number;
  blocked: number;
  skipped: number;
}

export interface FactStreamSummary {
  nodeKey: string;
  capabilityId: string;
  capabilityVersion: string;
  schemaVersion: string;
  outputType: string;
  executionScope: string;
  required: boolean;
  factCount: number;
  provenance: Provenance;
  coverage: CoverageCounts;
}

export interface Fact {
  acceptedResultId: string;
  ordinal: number;
  kind: string;
  startMs: number;
  endMs: number;
  confidence: number;
  payload: JsonValue;
}

/** State is `accepted`, `missing`, `failed`, `blocked`, or `skipped`; other values stay raw. */
export interface CoverageWindow {
  startMs: number;
  endMs: number;
  state: string;
}

export interface FactPage {
  run: { runId: string; matchId: string; state: RunState };
  stream: {
    nodeKey: string;
    capability: string;
    schemaVersion: string;
    outputType: string;
    executionScope: string;
    provenance: Provenance;
  };
  range: { fromMs?: number; toMs?: number };
  items: Fact[];
  coverage: CoverageWindow[];
  continuationToken?: string;
}

/** Integers, `fromMs >= 0`, and `toMs > fromMs` when both are set; otherwise the range is invalid. */
export interface FactSelection {
  runId: string;
  stream: FactStreamSummary;
  range: { fromMs?: number; toMs?: number };
}

export type RunOutcome = { kind: 'found'; run: AnalysisRunStatus } | { kind: 'runNotFound' };

export type StreamsOutcome = { kind: 'found'; streams: FactStreamSummary[] } | { kind: 'runNotFound' };

export type FactsOutcome =
  | { kind: 'found'; page: FactPage }
  | { kind: 'runNotFound' }
  | { kind: 'capabilityNotInRun' }
  | { kind: 'continuationRejected' };

export function isRunInProgress(state: string | undefined): boolean {
  const normalized = state?.toLowerCase();
  return normalized === 'requested' || normalized === 'running';
}

const TERMINAL_STATES = ['completed', 'partiallycompleted', 'failed', 'cancelled'];

/** True only for the known terminal states; unknown states are not assumed terminal. */
export function isRunTerminal(state: string | undefined): boolean {
  return state !== undefined && TERMINAL_STATES.includes(state.toLowerCase());
}
