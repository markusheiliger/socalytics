// Enumerated values are unions of the known values and `string`: unknown values are kept and shown raw.
export type CapabilityTier = 'low-level' | 'high-level' | (string & {});
export type ExecutionScope = 'segment' | 'match' | (string & {});
export type EdgeKind = 'required' | 'optional' | 'conditional' | (string & {});
export type VideoAccess = 'required' | 'optional' | 'forbidden' | (string & {});
export type ProfileState = 'draft' | 'approved' | 'retired' | (string & {});
export type CompletionPolicy = 'all-requested-capabilities' | (string & {});
export type PredicateKind = 'capabilityRequested' | 'nodeAccepted' | (string & {});
export type MemberRefKind = 'member' | 'seed' | 'unknown';

export type RegistryOperation =
  | 'imageRefused'
  | 'profileRefused'
  | 'transitionRefused'
  | 'workflowRefused'
  | 'defaultRefused'
  | (string & {});

export interface MemberRef {
  id: string;
  displayName?: string;
  kind: MemberRefKind;
}

export interface ImageSummary {
  imageDigest: string;
  capabilityId: string;
  capabilityVersion: string;
  registeredAt: Date;
}

export interface DeclarationCapability {
  id: string;
  version: string;
  tier: CapabilityTier;
  executionScope: ExecutionScope;
}

export interface DeclarationUpstream {
  capabilityId: string;
  capabilityVersion: string;
  schemaVersion: string;
  kind: EdgeKind;
}

export interface DeclarationRuntime {
  constraints: string[];
  accelerators: string[];
}

export interface DeclarationRequirements {
  minMaxAttempts?: number;
  minLeaseSeconds?: number;
  minStaleTimeoutSeconds?: number;
  minExecutionTimeoutSeconds?: number;
}

export interface DeclarationDeterminism {
  deterministic?: boolean;
  idempotent?: boolean;
}

export interface CapabilityDeclaration {
  imageDigest: string;
  repository: string;
  manifestDigest: string;
  capability: DeclarationCapability;
  modelDigest: string;
  source: string;
  registeredAt: Date;
  registeredBy: MemberRef;
  upstream?: DeclarationUpstream[];
  videoAccess?: VideoAccess;
  outputSchemaVersion?: string;
  compatibleProfiles?: string[];
  runtime?: DeclarationRuntime;
  resources?: Record<string, string>;
  requirements?: DeclarationRequirements;
  determinism?: DeclarationDeterminism;
  manifestText?: string;
}

export interface ProfileContent {
  capabilityId: string;
  capabilityVersion: string;
  modelDigest: string;
  runtimeConstraints: string[];
  accelerators: string[];
  resources: Record<string, string>;
  maxAttempts: number;
  leaseSeconds: number;
  staleTimeoutSeconds: number;
  executionTimeoutSeconds: number;
  videoPolicy: string;
  inputSchemaVersions: Record<string, string>;
  outputSchemaVersion: string;
}

export interface ProfileVersion {
  profileId: string;
  version: number;
  rowVersion: number | undefined;
  profileDigest: string;
  imageDigest: string;
  state: ProfileState;
  content: ProfileContent;
  createdAt: Date;
  createdBy: MemberRef;
  approvedAt?: Date;
  approvedBy?: MemberRef;
  retiredAt?: Date;
  retiredBy?: MemberRef;
}

export interface WorkflowSummary {
  workflowId: string;
  workflowVersion: string;
  definitionDigest: string;
  createdAt: Date;
  isDefault: boolean;
}

export interface SegmentationPolicy {
  version: number;
  durationSeconds: number;
  digest: string;
}

export interface WorkflowNode {
  nodeKey: string;
  profileId: string;
  profileVersion: number;
  profileDigest: string;
}

export interface EdgePredicate {
  kind: PredicateKind;
  capabilityId?: string;
  nodeKey?: string;
}

export interface WorkflowEdge {
  upstreamNodeKey: string;
  dependentNodeKey: string;
  kind: EdgeKind;
  predicate?: EdgePredicate;
}

export interface WorkflowVersion {
  workflowId: string;
  workflowVersion: string;
  definitionDigest: string;
  requestedCapabilities: string[];
  completionPolicy: CompletionPolicy;
  segmentationPolicy: SegmentationPolicy;
  nodes: WorkflowNode[];
  edges: WorkflowEdge[];
  createdAt: Date;
  createdBy: MemberRef;
}

export interface DefaultWorkflow {
  workflowId: string;
  workflowVersion: string;
  changedAt: Date;
  changedBy: MemberRef;
  rowVersion: number | undefined;
}

export interface ImageRegistrationDraft {
  reference: string;
}

export interface ProfileDraft {
  imageDigest: string;
  profileId: string;
  content: ProfileContent;
}

export interface WorkflowDraftNode {
  nodeKey: string;
  profileId: string;
  profileVersion: number;
  // Set when prefilled from a version whose profile is no longer approved.
  flagged?: boolean;
}

export interface WorkflowDraft {
  workflowId: string;
  workflowVersion: string;
  requestedCapabilities: string[];
  completionPolicy: CompletionPolicy;
  segmentationPolicy: SegmentationPolicy;
  nodes: WorkflowDraftNode[];
  edges: WorkflowEdge[];
}

export interface RegistryRefusalDetail {
  field: string;
  code: string;
}

export interface RegistryRefusal {
  operation: RegistryOperation;
  code: string;
  details: RegistryRefusalDetail[];
  retryable: boolean;
}

export class RegistryRefusalError extends Error {
  readonly refusal: RegistryRefusal;

  constructor(refusal: RegistryRefusal) {
    super(`Registry refused ${refusal.operation}: ${refusal.code}`);
    this.name = 'RegistryRefusalError';
    this.refusal = refusal;
  }
}

// The record changed since it was loaded (412, or 428 for the default).
export class RegistryChangedError extends Error {
  constructor() {
    super('Registry record has changed');
    this.name = 'RegistryChangedError';
  }
}
