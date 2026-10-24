// Enumerated values are unions of the known values and `string`: unknown values are kept and shown raw.
export type AgentGroup = 'coach' | 'specialist' | (string & {});
export type AgentVersionState = 'draft' | 'approved' | 'retired' | (string & {});
export type ArtifactKind =
  | 'prompt-template'
  | 'model-selection'
  | 'tool-policy'
  | 'safety-policy'
  | 'dimension-set'
  | 'philosophy-profile'
  | 'modifier-policy'
  | (string & {});
export type ModelProvider = 'openai' | 'github-models' | 'simulated' | (string & {});
export type ReasoningEffort = 'low' | 'medium' | 'high' | (string & {});
export type SafetyRoute = 'user-to-coach' | 'user-to-specialist' | 'coach-to-specialist' | (string & {});
export type ContextCategory =
  | 'score-state'
  | 'match-period'
  | 'player-number-state'
  | 'opponent-phase-behavior'
  | 'evidence-availability'
  | (string & {});
export type MemberRefKind = 'member' | 'seed' | 'unknown';

export type NotOfferedReason = 'noEnabledVersion' | 'coachNotAvailable';

export interface MemberRef {
  id: string;
  displayName?: string;
  kind: MemberRefKind;
}

export interface BaselineAgent {
  agentId: string;
  group: AgentGroup;
  domain: string;
  displayName: string;
  toolBaseline: string[];
}

export interface CatalogBaseline {
  version: string;
  tools: string[];
  agents: BaselineAgent[];
}

export interface Enablement {
  agentId: string;
  enabledVersion?: string;
  changedBy?: MemberRef;
  changedAt?: Date;
  etag: string;
}

export type Offer =
  | { kind: 'offered'; version: string }
  | { kind: 'notOffered'; reason: NotOfferedReason };

export interface AgentStateCounts {
  draft: number;
  approved: number;
  retired: number;
}

export interface CatalogAgentSummary extends BaselineAgent {
  enablement: Enablement;
  stateCounts: AgentStateCounts;
  offer: Offer;
}

export interface ArtifactRef {
  kind: ArtifactKind;
  id: string;
  version: string;
  digest: string;
}

export interface AgentVersionSummary {
  agentId: string;
  version: string;
  digest: string;
  group: AgentGroup;
  state: AgentVersionState;
  enabled: boolean;
  registeredAt: Date;
}

export interface Distance {
  agentId: string;
  agentVersion: string;
  profileId: string;
  profileVersion: string;
  distance: number;
  reviewRequired: boolean;
}

export interface AgentVersion extends AgentVersionSummary {
  displayName: string;
  disclosure?: string;
  artifacts: ArtifactRef[];
  registeredBy: MemberRef;
  approvedBy?: MemberRef;
  approvedAt?: Date;
  selfApproval: boolean;
  differentiationAcknowledged: boolean;
  retiredBy?: MemberRef;
  retiredAt?: Date;
  distances?: Distance[];
  etag: string;
}

export interface ArtifactSummary {
  kind: ArtifactKind;
  id: string;
  version: string;
  digest: string;
  registeredBy: MemberRef;
  registeredAt: Date;
}

export interface ModelParameters {
  temperature?: number;
  topP?: number;
  maxOutputTokens?: number;
  reasoningEffort?: ReasoningEffort;
}

export interface PromptTemplateDocument {
  group: AgentGroup;
  responseContract: string;
  text: string;
  slots: string[];
  dimensionSetMajor?: number;
}

export interface ModelSelectionDocument {
  provider: ModelProvider;
  model: string;
  deployment: string;
  parameters: ModelParameters;
}

export interface ToolPolicyDocument {
  tools: string[];
}

export interface SafetyPolicyDocument {
  group: AgentGroup;
  routes: SafetyRoute[];
  hardBoundaries: string[];
  escalationCategories: string[];
  refusalCategories: string[];
  rules: string[];
}

export interface DimensionDefinition {
  id: string;
  meaning: string;
  acceptedEvidence: string[];
}

export interface DimensionSetDocument {
  dimensions: DimensionDefinition[];
}

export interface ProfileWeight {
  weight: number;
  rationale: string;
  provenance: string;
}

export interface PhilosophyProfileDocument {
  dimensionSet: { id: string; version: string };
  weights: Record<string, ProfileWeight>;
  author: string;
  reviewers: { architecture: string[]; footballDomain: string[] };
  supersedes?: { version: string };
}

export interface ModifierPolicyDocument {
  dimensionSetMajor: number;
  perDimensionBound: number;
  maxPositiveMovement: number;
  contextCategories: ContextCategory[];
  budgetNeutral: boolean;
}

export interface AgentVersionDocumentArtifacts {
  promptTemplate: ArtifactReference;
  modelSelection: ArtifactReference;
  toolPolicy: ArtifactReference;
  safetyPolicy: ArtifactReference;
  philosophyProfile?: ArtifactReference;
  modifierPolicy?: ArtifactReference;
}

export interface ArtifactReference {
  id: string;
  version: string;
  digest: string;
}

export interface AgentVersionDocument {
  agentId: string;
  group: AgentGroup;
  displayName: string;
  artifacts: AgentVersionDocumentArtifacts;
  disclosure?: string;
}

export interface DocumentByKind {
  'prompt-template': PromptTemplateDocument;
  'model-selection': ModelSelectionDocument;
  'tool-policy': ToolPolicyDocument;
  'safety-policy': SafetyPolicyDocument;
  'dimension-set': DimensionSetDocument;
  'philosophy-profile': PhilosophyProfileDocument;
  'modifier-policy': ModifierPolicyDocument;
  'agent-version': AgentVersionDocument;
}

export type KnownDocumentKind = keyof DocumentByKind;

export type DocumentOf<K extends string> = K extends KnownDocumentKind ? DocumentByKind[K] : Record<string, unknown>;

export interface ArtifactRecord<K extends string = ArtifactKind> extends ArtifactSummary {
  document: DocumentOf<K>;
  distances?: Distance[];
}

// Form drafts: every field is editable text or a list, set up by the forms.
export interface ArtifactDraft<K extends string = ArtifactKind> {
  kind: K;
  id: string;
  version: string;
  fromId?: string;
  fromVersion?: string;
  document: Partial<DocumentOf<K>>;
}

export interface ProfileWeightDraft {
  weight: string;
  rationale: string;
  provenance: string;
}

export interface ProfileDraft {
  id: string;
  version: string;
  fromId?: string;
  fromVersion?: string;
  dimensionSet: { id: string; version: string } | undefined;
  rows: Array<{ dimensionId: string } & ProfileWeightDraft>;
  author: string;
  reviewers: { architecture: string[]; footballDomain: string[] };
  supersedesVersion?: string;
}

export interface AgentVersionDraft {
  agentId: string;
  group: AgentGroup;
  version: string;
  displayName: string;
  disclosure: string;
  artifacts: Partial<Record<keyof AgentVersionDocumentArtifacts, ArtifactReference>>;
}

export interface CatalogViolation {
  code: string;
  pointer: string;
  detail: string;
  location: string;
}

export type CatalogOutcome<T> =
  | { kind: 'created'; value: T }
  | { kind: 'replayed'; value: T }
  | { kind: 'validationFailed'; violations: CatalogViolation[] }
  | { kind: 'versionExists' }
  | { kind: 'transitionRefused'; code: string }
  | { kind: 'enablementRefused'; code: string; reason?: string }
  | { kind: 'changed' };
