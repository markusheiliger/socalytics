import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page, Route } from '@playwright/test';
import type {
  AnalysisRunSummaryDto,
  AnalystManagerRegistrationDto,
  AnalystManagerRegistrationPageDto,
  ClubDto,
  MatchDto,
  RecordingSetVersionDto,
  RecordingVersionDto,
  TimelineMappingDto,
  UploadSessionDto,
  CurrentMemberDto,
  MemberDto,
  MemberPageDto,
  SeasonDto,
  SeasonPageDto,
  SessionInfoDto,
  TeamDto,
  TeamPageDto,
} from '../../../src/generated/platform-api/models';

// Mirrors the platform's composite content digest over the declared part digests.
function compositeDigest(partSizeBytes: number, partDigests: string[]): string {
  const raw = Buffer.concat(partDigests.map((d) => Buffer.from(d.replace('sha-256:', ''), 'hex')));
  return `sha-256-parts:${partSizeBytes}:${partDigests.length}:${createHash('sha256').update(raw).digest('hex')}`;
}

/** Registry fixtures mirror the JSON of the 13 Registry operations (dates serialize through JSON). */
export interface RegistryImage {
  imageDigest: string;
  repository: string;
  manifestDigest: string;
  capability: { id: string; version: string; tier: string; executionScope: string };
  modelDigest: string;
  manifest: Record<string, unknown>;
  source: string;
  registeredAt: Date;
  registeredBy: string | null;
}

export interface RegistryProfile {
  profileId: string;
  version: number;
  profileDigest: string;
  imageDigest: string;
  state: 'draft' | 'approved' | 'retired';
  content: {
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
  };
  createdAt: Date;
  createdBy: string;
  approvedAt?: Date;
  approvedBy?: string;
  retiredAt?: Date;
  retiredBy?: string;
  /** Sent as `ETag`; never part of the body. */
  rowVersion: number;
}

export interface RegistryWorkflow {
  workflowId: string;
  workflowVersion: string;
  definitionDigest: string;
  definition: {
    requestedCapabilities: string[];
    completionPolicy: string;
    segmentationPolicy: { version: number; durationSeconds: number; digest: string };
    nodes: { nodeKey: string; profileId: string; profileVersion: number; profileDigest: string }[];
    edges: {
      upstreamNodeKey: string;
      dependentNodeKey: string;
      kind: string;
      predicate?: { kind: string; capabilityId?: string; nodeKey?: string };
    }[];
  };
  createdAt: Date;
  createdBy: string | null;
}

export interface RegistryDefault {
  workflowId: string;
  workflowVersion: string;
  changedAt: Date;
  changedBy: string | null;
}

function imageRefusalStatus(code: string): number {
  if (code === 'reference-not-digest' || code === 'reference-not-allowed') return 400;
  if (code === 'image-not-found') return 404;
  if (code === 'registry-unavailable') return 503;
  if (code === 'digest-mismatch') return 502;
  return 422;
}

function profileDto({ rowVersion: _rowVersion, ...dto }: RegistryProfile) {
  return dto;
}

function digestOf(value: unknown): string {
  return `sha-256:${createHash('sha256').update(JSON.stringify(value)).digest('hex')}`;
}

export type SimulatedRole = 'ClubAdmin' | 'Registrar' | 'Coach' | 'Viewer';

export const ANTIFORGERY_TOKEN = 'simulated-antiforgery-token-7f3a9c';
/** Origin of the simulated identity provider's authorization endpoint; no request leaves the browser. */
export const SIMULATED_PROVIDER_ORIGIN = 'https://login.simulated.test';
export const SIMULATED_PASSWORD = 'Simulated-Password-1!';

const ids = {
  club: '00000000-0000-4000-8000-000000000001',
  season: '00000000-0000-4000-8000-000000000002',
  team: '00000000-0000-4000-8000-000000000003',
  session: '00000000-0000-4000-8000-000000000004',
  admin: '00000000-0000-4000-8000-000000000011',
  coach: '00000000-0000-4000-8000-000000000012',
  registrar: '00000000-0000-4000-8000-000000000014',
  viewer: '00000000-0000-4000-8000-000000000013',
};

/** Origin of the fake object store (tests/e2e/support/fakeObjectStorage.mjs); playwright.config.ts starts it on this port. */
export const FAKE_STORAGE_ORIGIN = 'http://127.0.0.1:4176';

const now = new Date('2026-10-12T12:00:00Z');
const later = (hours: number) => new Date(now.getTime() + hours * 3_600_000);

export const club: ClubDto = { id: ids.club, displayName: 'Simulated Club', version: 1 };

export const season: SeasonDto = {
  id: ids.season,
  name: '2026/27',
  state: 'Active',
  createdAt: now,
  activatedAt: now,
  archivedAt: null,
  version: 1,
};

export const team: TeamDto = {
  id: ids.team,
  seasonId: ids.season,
  seasonState: 'Active',
  name: 'First Team',
  createdAt: now,
  version: 1,
};

const members: Record<SimulatedRole, CurrentMemberDto> = {
  ClubAdmin: {
    id: ids.admin,
    accountName: 'admin',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: ['ClubAdmin'],
    teamRoles: [],
  },
  Registrar: {
    id: ids.registrar,
    accountName: 'registrar',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: ['Registrar'],
    teamRoles: [],
  },
  Coach: {
    id: ids.coach,
    accountName: 'coach',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: [],
    teamRoles: [{ teamId: ids.team, teamName: team.name, seasonId: ids.season, role: 'Coach' }],
  },
  Viewer: {
    id: ids.viewer,
    accountName: 'viewer',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: [],
    teamRoles: [{ teamId: ids.team, teamName: team.name, seasonId: ids.season, role: 'Viewer' }],
  },
};

export const match: MatchDto = {
  id: '00000000-0000-4000-8000-000000000021',
  teamId: ids.team,
  opponent: { name: 'Rivals FC' },
  kickoffAt: later(48),
  homeAway: 'home',
  competition: 'League',
  createdAt: now,
  version: 1,
};

export const analysisRun: AnalysisRunSummaryDto = {
  runId: '00000000-0000-4000-8000-000000000031',
  teamId: ids.team,
  matchId: match.id,
  recordingSetVersionId: null,
  workflowId: 'default',
  workflowVersion: '1',
  state: 'Queued',
  createdAt: now,
  runningAt: null,
  terminatedAt: null,
  failureReason: null,
  cancelledBy: null,
  cancellationReason: null,
  cancellationCause: null,
  isCurrent: false,
  supersededBy: null,
  supersededAt: null,
  version: 1,
};

let nextId = 100;
const newId = () => `00000000-0000-4000-8000-${String(++nextId).padStart(12, '0')}`;

export interface SeededFact {
  acceptedResultId: string;
  ordinal: number;
  kind: string;
  startMs: number;
  endMs: number;
  confidence: number;
  payload: unknown;
}

export interface SeededCoverageWindow {
  startMs: number;
  endMs: number;
  state: 'accepted' | 'missing' | 'failed' | 'blocked' | 'skipped';
}

export interface SeededProvenance {
  workflow: { id: string; version: string };
  analystProfile: { id: string; digest: string };
  imageDigest: string;
  modelDigest: string | null;
}

export interface SeededFactStream {
  nodeKey: string;
  capabilityId: string;
  capabilityVersion: string;
  schemaVersion: string;
  outputType: string;
  executionScope: string;
  required: boolean;
  provenance: SeededProvenance;
  facts: SeededFact[];
  /** Coverage windows of the whole match; every fact page carries them clipped to its range. */
  coverage: SeededCoverageWindow[];
  /** End of the recording coverage, the effective `toMs` when a read sets none. */
  coverageEndMs: number;
}

export interface SeededNode {
  nodeKey: string;
  capabilityId: string;
  capabilityVersion: string;
  tier: string;
  executionScope: string;
  state: string;
  blockedByNodeKey: string | null;
  jobs: { waiting: number; ready: number; claimed: number; completed: number; failed: number; blocked: number };
}

export interface SeededAnalysisRun {
  runId: string;
  teamId: string;
  matchId: string;
  recordingSetVersionId: string;
  workflowId: string;
  workflowVersion: string;
  state: string;
  failureReason: string | null;
  createdAt: Date;
  runningAt: Date | null;
  terminatedAt: Date | null;
  requestedCapabilities: string[];
  nodes: SeededNode[];
  streams: SeededFactStream[];
  /** Applied one per status read after the first, so a running run changes between polls; see `advanceAnalysisRun`. */
  steps: ((run: SeededAnalysisRun) => void)[];
}

export const seededProvenance: SeededProvenance = {
  workflow: { id: 'default', version: '1' },
  analystProfile: { id: 'profile-detector', digest: `sha256:${'a'.repeat(64)}` },
  imageDigest: `sha256:${'b'.repeat(64)}`,
  modelDigest: `sha256:${'c'.repeat(64)}`,
};

/** A run with one completed node and its stream; `overrides` replace any field. */
export function analysisRunFixture(overrides: Partial<SeededAnalysisRun> = {}): SeededAnalysisRun {
  const nodeKey = 'detections';
  return {
    runId: newId(),
    teamId: ids.team,
    matchId: String(match.id),
    recordingSetVersionId: newId(),
    workflowId: 'default',
    workflowVersion: '1',
    state: 'Completed',
    failureReason: null,
    createdAt: now,
    runningAt: later(0.1),
    terminatedAt: later(0.5),
    requestedCapabilities: ['player-detection'],
    nodes: [
      {
        nodeKey,
        capabilityId: 'player-detection',
        capabilityVersion: '1.0.0',
        tier: 'LowLevel',
        executionScope: 'Segment',
        state: 'Completed',
        blockedByNodeKey: null,
        jobs: { waiting: 0, ready: 0, claimed: 0, completed: 3, failed: 0, blocked: 0 },
      },
    ],
    streams: [
      {
        nodeKey,
        capabilityId: 'player-detection',
        capabilityVersion: '1.0.0',
        schemaVersion: '1.0.0',
        outputType: 'detections',
        executionScope: 'Segment',
        required: true,
        provenance: seededProvenance,
        facts: [],
        coverage: [{ startMs: 0, endMs: 90_000, state: 'accepted' }],
        coverageEndMs: 90_000,
      },
    ],
    steps: [],
    ...overrides,
  };
}

const base64Url = (text: string) => Buffer.from(text, 'utf8').toString('base64url');
const selectionHash = (runId: string, stream: SeededFactStream, fromMs: number | null, toMs: number | null) =>
  createHash('sha256').update(`${runId}|${stream.nodeKey}|${stream.schemaVersion}|${fromMs}|${toMs}`).digest('hex').slice(0, 32);
const compareFacts = (a: SeededFact, b: SeededFact) =>
  a.startMs - b.startMs ||
  a.endMs - b.endMs ||
  (a.acceptedResultId < b.acceptedResultId ? -1 : a.acceptedResultId > b.acceptedResultId ? 1 : 0) ||
  a.ordinal - b.ordinal;

/** The base64 SHA-256 a grant requires: the declared part digest may be hex or prefixed hex. */
function checksumHeader(digest: string | undefined): string {
  const hex = (digest ?? '').replace(/^sha-?256[:=]/i, '');
  return /^[0-9a-f]{64}$/i.test(hex) ? Buffer.from(hex, 'hex').toString('base64') : (digest ?? '');
}

export function sessionFor(role: SimulatedRole, passwordChangeRequired = false): SessionInfoDto {
  return {
    sessionId: ids.session,
    idleExpiresAt: later(1),
    absoluteExpiresAt: later(8),
    passwordChangeRequired,
    antiforgeryToken: ANTIFORGERY_TOKEN,
    member: { ...members[role], passwordChangeRequired },
  };
}

function memberListEntry(member: CurrentMemberDto): MemberDto {
  return {
    id: member.id,
    accountName: member.accountName,
    membershipStatus: member.membershipStatus,
    passwordSet: true,
    lockedOut: false,
    lockoutEndsAt: null,
    twoFactorEnabled: false,
    clubRoles: member.clubRoles,
    teamRoles: (member.teamRoles ?? []).map((r) => ({ teamId: r.teamId, role: r.role })),
    createdAt: now,
    version: 1,
  };
}

export function problem(status: number, title: string, extra: Record<string, unknown> = {}) {
  return { type: 'about:blank', title, status, ...extra };
}

type Handler = (route: Route, body: unknown) => Promise<void> | void;

/** Agent catalog state mirrors the JSON of the Agent Catalog API (dates serialize through JSON). */
export type CatalogKind =
  | 'prompt-templates'
  | 'model-selections'
  | 'tool-policies'
  | 'safety-policies'
  | 'dimension-sets'
  | 'philosophy-profiles'
  | 'modifier-policies';

export interface CatalogDistance {
  agentId: string;
  agentVersion: string;
  profileId: string;
  profileVersion: string;
  distance: number;
  reviewRequired: boolean;
}

export interface CatalogArtifact {
  kind: string;
  id: string;
  version: string;
  digest: string;
  registeredBy: string;
  registeredAt: Date | string;
  document: Record<string, any>;
  /** Profiles only; when set it replaces the computed distances. */
  distances?: CatalogDistance[];
}

export interface CatalogAgentVersion {
  agentId: string;
  version: string;
  digest: string;
  group: string;
  state: string;
  document: Record<string, any>;
  registeredBy: string;
  registeredAt: Date | string;
  approvedBy: string | null;
  approvedAt: Date | string | null;
  selfApproval: boolean;
  differentiationAcknowledged: boolean;
  retiredBy: string | null;
  retiredAt: Date | string | null;
  /** Coach Agent versions; when set it replaces the computed distances. */
  distances?: CatalogDistance[];
  rowVersion: number;
}

export interface CatalogEnablement {
  agentId: string;
  enabledVersion: string | null;
  changedBy: string | null;
  changedAt: Date | string | null;
  rowVersion: number;
}

export type CatalogOperation =
  | 'getAgentCatalogBaseline'
  | 'registerAgentCatalogArtifact'
  | 'listAgentCatalogArtifacts'
  | 'getAgentCatalogArtifact'
  | 'registerAgentVersion'
  | 'listAgentVersions'
  | 'getAgentVersion'
  | 'approveAgentVersion'
  | 'retireAgentVersion'
  | 'listAgentEnablements'
  | 'setAgentEnablement';

/** A scripted answer: `extra` members are added to the problem (`violations`, `reason`, …). */
export interface CatalogRefusal {
  status: number;
  code?: string;
  extra?: Record<string, unknown>;
}

export const CATALOG_SEED_ACTOR = 'system:seed';

const CATALOG_KINDS: Record<CatalogKind, string> = {
  'prompt-templates': 'prompt-template',
  'model-selections': 'model-selection',
  'tool-policies': 'tool-policy',
  'safety-policies': 'safety-policy',
  'dimension-sets': 'dimension-set',
  'philosophy-profiles': 'philosophy-profile',
  'modifier-policies': 'modifier-policy',
};
const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$/;
const CATALOG_NAME = /^[A-Za-z0-9._/:-]{1,128}$/;
const CATALOG_REVIEW_THRESHOLD = 0.1;

const catalogBaselinePath = join(
  import.meta.dirname,
  '..', '..', '..', '..', '..', '..',
  'contracts', 'agent-catalog', 'catalog-baseline', 'v1', 'examples', 'valid', 'baseline-1.0.0.json',
);

interface CatalogBaselineDocument {
  toolAllowlist: string[];
  agents: { agentId: string; group: string; domain: string; displayName: string; permittedTools: string[] }[];
}

function catalogBaseline(): CatalogBaselineDocument {
  return JSON.parse(readFileSync(catalogBaselinePath, 'utf8')) as CatalogBaselineDocument;
}

function seedCatalogRecord(document: Record<string, any>): CatalogArtifact {
  return {
    kind: document.kind,
    id: document.id,
    version: document.version,
    digest: digestOf(document),
    registeredBy: CATALOG_SEED_ACTOR,
    registeredAt: now,
    document,
  };
}

/** The Development and Test seed: two specialists with version 0.1.0 approved and enabled, four artifacts each. */
function seedCatalog(): {
  artifacts: CatalogArtifact[];
  versions: CatalogAgentVersion[];
  enablements: CatalogEnablement[];
} {
  const baseline = catalogBaseline();
  const artifacts: CatalogArtifact[] = [
    seedCatalogRecord({
      schemaVersion: '1.0.0',
      kind: 'model-selection',
      id: 'simulated-model',
      version: '0.1.0',
      provider: 'simulated',
      model: 'simulated',
      deployment: 'simulated',
    }),
  ];
  const versions: CatalogAgentVersion[] = [];
  const domains: Record<string, string> = {
    'specialist-tactics': 'team phases, possession, pressing, transitions, formations, spacing, and set plays',
    'specialist-video-analysis': 'evidence retrieval, sequence comparison, and clip curation',
  };
  for (const agentId of Object.keys(domains)) {
    const agent = baseline.agents.find((a) => a.agentId === agentId)!;
    const records = [
      seedCatalogRecord({
        schemaVersion: '1.0.0',
        kind: 'prompt-template',
        id: `${agentId}-prompt`,
        version: '0.1.0',
        group: 'specialist',
        responseContract: 'agent-response/1',
        text: `You are ${agentId}, a SocAlytics Specialist Agent for ${domains[agentId]}.\nProvide decision support; the human coach decides.`,
        slots: [],
      }),
      seedCatalogRecord({
        schemaVersion: '1.0.0',
        kind: 'tool-policy',
        id: `${agentId}-tools`,
        version: '0.1.0',
        tools: agent.permittedTools,
      }),
      seedCatalogRecord({
        schemaVersion: '1.0.0',
        kind: 'safety-policy',
        id: `${agentId}-safety`,
        version: '0.1.0',
        group: 'specialist',
        routes: ['user-to-specialist', 'coach-to-specialist'],
        hardBoundaries: ['Never reveal raw recordings or unauthorized team data.'],
        escalationCategories: ['medical', 'safeguarding', 'welfare'],
        refusalCategories: ['privacy', 'raw-data-access', 'unauthorized-data'],
        rules: ['evidence-citation', 'tool-allowlist-only', 'no-raw-data-access'],
      }),
    ];
    artifacts.push(...records);
    const ref = (kind: string, id: string) => {
      const found = [...records, artifacts[0]].find((r) => r.kind === kind && r.id === id)!;
      return { id: found.id, version: found.version, digest: found.digest };
    };
    const document = {
      schemaVersion: '1.0.0',
      kind: 'agent-version',
      agentId,
      version: '0.1.0',
      group: 'specialist',
      displayName: agent.displayName,
      artifacts: {
        promptTemplate: ref('prompt-template', `${agentId}-prompt`),
        modelSelection: ref('model-selection', 'simulated-model'),
        toolPolicy: ref('tool-policy', `${agentId}-tools`),
        safetyPolicy: ref('safety-policy', `${agentId}-safety`),
      },
    };
    versions.push({
      agentId,
      version: '0.1.0',
      digest: digestOf(document),
      group: 'specialist',
      state: 'approved',
      document,
      registeredBy: CATALOG_SEED_ACTOR,
      registeredAt: now,
      approvedBy: CATALOG_SEED_ACTOR,
      approvedAt: now,
      selfApproval: false,
      differentiationAcknowledged: false,
      retiredBy: null,
      retiredAt: null,
      rowVersion: 2,
    });
  }
  const enablements = baseline.agents.map<CatalogEnablement>((a) => {
    const enabled = a.agentId in domains;
    return {
      agentId: a.agentId,
      enabledVersion: enabled ? '0.1.0' : null,
      changedBy: enabled ? CATALOG_SEED_ACTOR : null,
      changedAt: enabled ? now : null,
      rowVersion: enabled ? 2 : 1,
    };
  });
  return { artifacts, versions, enablements };
}

function catalogViolation(code: string, pointer: string, detail: string) {
  return { code, pointer, detail };
}

function profileDistance(first: Record<string, any>, second: Record<string, any>): number {
  const firstWeights = Object.fromEntries(Object.entries(first).map(([k, v]) => [k, Number(v?.weight ?? 0)]));
  const secondWeights = Object.fromEntries(Object.entries(second).map(([k, v]) => [k, Number(v?.weight ?? 0)]));
  let sum = 0;
  for (const id of new Set([...Object.keys(firstWeights), ...Object.keys(secondWeights)])) {
    sum += Math.abs((firstWeights[id] ?? 0) - (secondWeights[id] ?? 0));
  }
  return sum / 200;
}

export type AgentOperation =
  | 'listAgents'
  | 'listConversations'
  | 'createConversation'
  | 'getConversation'
  | 'addParticipant'
  | 'submit'
  | 'listTurns'
  | 'getInvocation'
  | 'getLineage';

/** A refusal the simulated platform answers with; `errors` become the problem's field errors. */
export interface AgentRefusal {
  status: number;
  code?: string;
  errors?: { field: string; code: string }[];
  retryAfterSeconds?: number;
}

/** Test controls for `refuseNext`: one entry per refusal of the sibling's API contract. */
export const agentRefusals = {
  tooManyUnfinished: { status: 429, code: 'too-many-unfinished-invocations', retryAfterSeconds: 5 },
  agentNotAvailable: { status: 409, code: 'agent-not-available' },
  agentNotParticipant: { status: 409, code: 'agent-not-participant' },
  participantExists: { status: 409, code: 'participant-exists' },
  runNotEligible: { status: 409, code: 'analysis-run-not-eligible' },
  keyReused: { status: 409, code: 'idempotency-key-reused' },
  keyMissing: { status: 400, code: 'idempotency-key-missing' },
  invalidQuestion: { status: 400, errors: [{ field: 'question', code: 'required' }] },
  questionTooLong: { status: 400, errors: [{ field: 'question', code: 'too-long' }] },
  forbidden: { status: 403 },
  notFound: { status: 404 },
  staleVersion: { status: 412 },
  preconditionRequired: { status: 428 },
} satisfies Record<string, AgentRefusal>;

export interface SimulatedAgent {
  agentId: string;
  group: string;
  domain: string;
  /** Reported as `enabledVersion`; defaults to the seeded `0.1.0`. */
  enabledVersion?: string;
}

/**
 * One state an invocation shows. A status read sees the state `holdReads` times (default 1) before the next step;
 * the last step stays.
 */
export interface AgentStep {
  state: string;
  failureReason?: string;
  attemptCount?: number;
  holdReads?: number;
}

export interface AgentLineageEntry {
  sequence: number;
  kind: string;
  attemptId: string | null;
  attemptNumber: number | null;
  recordedAt: string;
  details: Record<string, unknown>;
}

export interface SimulatedConversation {
  conversationId: string;
  matchId: string;
  analysisRunId: string;
  analysisRunIsCurrent: boolean;
  teamId: string;
  state: string;
  participants: { agentId: string; addedAt: string }[];
  createdAt: string;
  version: number;
  ownerId: string;
}

export interface SimulatedInvocation {
  invocationId: string;
  conversationId: string;
  agentId: string;
  question: string;
  submittedAt: string;
  attemptId: string;
  script: AgentStep[];
  stepIndex: number;
  readsInStep: number;
  advice: string;
  lineage?: AgentLineageEntry[];
}

const MAX_QUESTION_LENGTH = 4000;
const TERMINAL_AGENT_STATES = new Set(['succeeded', 'failed']);

const AGENT_ROUTES: [AgentOperation, string, RegExp][] = [
  ['listAgents', 'GET', /^\/agent-catalog\/agents$/],
  ['listConversations', 'GET', /^\/matches\/([^/]+)\/agent-conversations$/],
  ['createConversation', 'POST', /^\/matches\/([^/]+)\/agent-conversations$/],
  ['getConversation', 'GET', /^\/agent-conversations\/([^/]+)$/],
  ['addParticipant', 'POST', /^\/agent-conversations\/([^/]+)\/participants$/],
  ['submit', 'POST', /^\/agent-conversations\/([^/]+)\/invocations$/],
  ['listTurns', 'GET', /^\/agent-conversations\/([^/]+)\/turns$/],
  ['getInvocation', 'GET', /^\/agent-invocations\/([^/]+)$/],
  ['getLineage', 'GET', /^\/agent-invocations\/([^/]+)\/lineage$/],
];

export const OUTBOX_STATES = ['pending', 'published', 'failed', 'discarded'] as const;
export const OUTBOX_MESSAGE_TYPES = [
  'matches.recordings-finalized',
  'analysis.ready-work',
  'analysis.run-state-changed',
] as const;
export const RECEIPT_OUTCOMES = ['run-created', 'run-exists', 'rejected'] as const;

/** JSON shape of `OutboxMessageDto`; dates serialize through JSON. */
export interface SimulatedOutboxMessage {
  messageId: string;
  messageType: string;
  contractVersion: string;
  subject: string;
  state: string;
  attemptCount: number;
  publicationRound: number;
  lastAttemptAt: Date | null;
  nextAttemptAt: Date | null;
  failureCategory: string | null;
  createdAt: Date;
  publishedAt: Date | null;
  ageSeconds: number;
  publicationLagSeconds: number | null;
  runId: string | null;
  logicalJobId: string | null;
  capabilityId: string | null;
}

/** JSON shape of `NotificationReceiptDto`. */
export interface SimulatedNotificationReceipt {
  messageId: string;
  messageType: string;
  recordingSetVersionId: string | null;
  outcome: string;
  rejectionCategory: string | null;
  runId: string | null;
  receivedAt: Date;
}

/** JSON shape of `AnalysisOperationalSummaryDto`. */
export interface SimulatedOperationalSummary {
  workflowDefinition: { state: string; category: string | null; since: Date | null };
  runsByState: Record<string, number>;
  runsWithBlockedNodes: number;
  staleAttempts: number;
  rejectedNotifications: number;
  outboxPending: number;
  outboxFailed: number;
  oldestPendingAgeSeconds: number | null;
  observedAt: Date;
}

export interface SimulatedReconciliationResult {
  readyJobsRepublished: number;
  notificationsRepublished: number;
  startedAt: Date;
  completedAt: Date;
}

export type OperationsOperation =
  | 'getAnalysisOperationalSummary'
  | 'listOutboxMessages'
  | 'getOutboxMessage'
  | 'listNotificationReceipts'
  | 'reconcileAnalysisPublication';

/** `forbidden` answers 403, `unavailable` 503, and `invalid-token` 400 for `continuationToken` (lists only, when a token is sent). */
export type OperationsSwitch = 'forbidden' | 'unavailable' | 'invalid-token';

const opsId = (kind: number, n: number) => `00000000-0000-4000-8000-${String(kind * 1000 + n).padStart(12, '0')}`;

/** Outgoing messages in every state and type, newest first; one has a markup subject, one an unknown failure category. */
export function outboxMessageFixtures(): SimulatedOutboxMessage[] {
  const result: SimulatedOutboxMessage[] = [];
  let n = 0;
  for (const state of OUTBOX_STATES) {
    for (const messageType of OUTBOX_MESSAGE_TYPES) {
      n += 1;
      const createdAt = new Date(now.getTime() - n * 600_000);
      const published = state === 'published';
      const failed = state === 'failed';
      result.push({
        messageId: opsId(7, n),
        messageType,
        contractVersion: '1',
        subject: `analysis.simulated.${n}`,
        state,
        attemptCount: state === 'pending' ? 0 : failed ? 4 : 1,
        publicationRound: 1,
        lastAttemptAt: state === 'pending' ? null : new Date(createdAt.getTime() + 30_000),
        nextAttemptAt: state === 'pending' || failed ? new Date(now.getTime() + 60_000) : null,
        failureCategory: failed ? 'transport-unavailable' : null,
        createdAt,
        publishedAt: published ? new Date(createdAt.getTime() + 45_000) : null,
        ageSeconds: Math.round((now.getTime() - createdAt.getTime()) / 1000),
        publicationLagSeconds: published ? 45 : null,
        runId: messageType === 'matches.recordings-finalized' ? null : opsId(3, 1),
        logicalJobId: messageType === 'analysis.ready-work' ? opsId(5, n) : null,
        capabilityId: messageType === 'analysis.ready-work' ? 'player-detection' : null,
      });
    }
  }
  const failedOnes = result.filter((m) => m.state === 'failed');
  failedOnes[0].subject = '<img src=x onerror="alert(1)">analysis.markup';
  failedOnes[1].failureCategory = 'quantum-flux';
  return result;
}

/** Receipts with each outcome, newest first. */
export function notificationReceiptFixtures(): SimulatedNotificationReceipt[] {
  const result: SimulatedNotificationReceipt[] = [];
  for (let n = 1; n <= 12; n += 1) {
    const outcome = RECEIPT_OUTCOMES[(n - 1) % RECEIPT_OUTCOMES.length];
    result.push({
      messageId: opsId(8, n),
      messageType: 'matches.recordings-finalized',
      recordingSetVersionId: opsId(9, n),
      outcome,
      rejectionCategory: outcome === 'rejected' ? (n === 3 ? 'unrecognized-reason' : 'recording-set-not-found') : null,
      runId: outcome === 'rejected' ? null : opsId(3, 1),
      receivedAt: new Date(now.getTime() - n * 900_000),
    });
  }
  return result;
}

/** A summary with an unknown run state and an unresolvable workflow definition. */
export function operationalSummaryFixture(): SimulatedOperationalSummary {
  return {
    workflowDefinition: { state: 'unresolvable', category: 'profile-not-approved', since: later(-2) },
    runsByState: { Running: 2, Queued: 1, Completed: 7, Quarantined: 1 },
    runsWithBlockedNodes: 1,
    staleAttempts: 2,
    rejectedNotifications: 4,
    outboxPending: 3,
    outboxFailed: 3,
    oldestPendingAgeSeconds: 7_500,
    observedAt: now,
  };
}

/** Answers `/api/v1/**` with typed fixtures. Tests may override any route with `on`. */
export class SimulatedApi {
  private current: { role: SimulatedRole; passwordChangeRequired: boolean } | null = null;
  private readonly overrides = new Map<string, Handler>();
  readonly requests: { method: string; path: string; headers: Record<string, string> }[] = [];
  /** Content type of every request body sent to `/api/v1`; SC-003 asserts that all of them are JSON. */
  readonly bodyContentTypes: string[] = [];
  readonly matches: MatchDto[] = [match];
  readonly uploadSessions: (UploadSessionDto & { partDigests: string[] })[] = [];
  readonly recordingVersions: RecordingVersionDto[] = [];
  readonly timelineMappings: TimelineMappingDto[] = [];
  readonly recordingSetVersions: RecordingSetVersionDto[] = [];
  readonly analysisRuns: AnalysisRunSummaryDto[] = [];
  /** Analysis state behind `/analysis/runs/{runId}`, by run identifier; see `seedAnalysisRun`. */
  readonly analysisStates = new Map<string, SeededAnalysisRun>();
  /** Fact page size of the simulated platform (its default page size); tests may lower it to page. */
  factPageSize = 50;
  private readonly statusReads = new Map<string, number>();

  /** Operational fixtures behind `/operations/**`; tests may replace or mutate them. */
  operationalSummary: SimulatedOperationalSummary = operationalSummaryFixture();
  outboxMessages: SimulatedOutboxMessage[] = outboxMessageFixtures();
  notificationReceipts: SimulatedNotificationReceipt[] = notificationReceiptFixtures();
  reconciliationResult: SimulatedReconciliationResult = {
    readyJobsRepublished: 2,
    notificationsRepublished: 1,
    startedAt: now,
    completedAt: new Date(now.getTime() + 1500),
  };
  /** Page size of the operational lists; tests may change it. */
  operationsPageSize = 5;
  /** Per-operation answers that replace the data: 403, 503, or 400 for `continuationToken`. */
  readonly operationsSwitches = new Map<OperationsOperation, OperationsSwitch>();
  /** Reconciliation requests received, including refused ones. */
  reconciliationRequests = 0;

  constructor(private readonly page: Page) {
    this.resetCatalog();
  }

  async install(): Promise<void> {
    await this.page.route('**/api/v1/**', (route) => this.handle(route));
  }

  /** Seeds a run for the review routes and keeps the match's run list consistent with it. */
  seedAnalysisRun(overrides: Partial<SeededAnalysisRun> = {}): SeededAnalysisRun {
    const run = analysisRunFixture(overrides);
    this.analysisStates.set(run.runId, run);
    this.syncRunSummary(run);
    return run;
  }

  /** Applies the next step of the run, as a status poll does; returns false when no step is left. */
  advanceAnalysisRun(runId: string): boolean {
    const run = this.analysisStates.get(runId);
    const step = run?.steps.shift();
    if (!run || !step) return false;
    step(run);
    this.syncRunSummary(run);
    return true;
  }

  private syncRunSummary(run: SeededAnalysisRun): void {
    const summary: AnalysisRunSummaryDto = {
      runId: run.runId,
      teamId: run.teamId,
      matchId: run.matchId,
      recordingSetVersionId: run.recordingSetVersionId,
      workflowId: run.workflowId,
      workflowVersion: run.workflowVersion,
      state: run.state,
      createdAt: run.createdAt,
      runningAt: run.runningAt,
      terminatedAt: run.terminatedAt,
      failureReason: run.failureReason,
      version: 1,
    };
    const index = this.analysisRuns.findIndex((r) => r.runId === run.runId);
    if (index >= 0) this.analysisRuns[index] = summary;
    else this.analysisRuns.push(summary);
  }

  private async handleAnalysisRead(
    route: Route,
    path: string,
    role: SimulatedRole,
    json: (status: number, body: unknown) => Promise<void>,
  ): Promise<void> {
    const parsed = /^\/analysis\/runs\/([^/]+)(?:\/(fact-streams|facts))?$/.exec(path);
    const run = parsed && this.analysisStates.get(parsed[1]);
    const permitted = run && (members[role].teamRoles ?? []).some((r) => r.teamId === run.teamId);
    if (!parsed || !run || !permitted) return json(404, problem(404, 'Not found'));

    if (!parsed[2]) {
      const reads = this.statusReads.get(run.runId) ?? 0;
      this.statusReads.set(run.runId, reads + 1);
      if (reads > 0) this.advanceAnalysisRun(run.runId);
      return json(200, {
        runId: run.runId,
        teamId: run.teamId,
        matchId: run.matchId,
        recordingSetVersionId: run.recordingSetVersionId,
        workflowId: run.workflowId,
        workflowVersion: run.workflowVersion,
        state: run.state,
        failureReason: run.failureReason,
        createdAt: run.createdAt,
        runningAt: run.runningAt,
        terminatedAt: run.terminatedAt,
        version: 1,
        requestedCapabilities: run.requestedCapabilities,
        nodes: run.nodes.map((n, i) => ({ nodeId: `00000000-0000-4000-8000-${String(900 + i).padStart(12, '0')}`, ...n })),
      });
    }

    const runDto = { runId: run.runId, teamId: run.teamId, matchId: run.matchId, state: run.state };
    if (parsed[2] === 'fact-streams') {
      return json(200, {
        run: runDto,
        items: run.streams.map((s) => {
          const count = (state: string) => s.coverage.filter((w) => w.state === state).length;
          return {
            nodeKey: s.nodeKey,
            capability: { id: s.capabilityId, version: s.capabilityVersion },
            schemaVersion: s.schemaVersion,
            outputType: s.outputType,
            executionScope: s.executionScope,
            required: s.required,
            factCount: s.facts.length,
            provenance: s.provenance,
            coverage: {
              accepted: count('accepted'),
              missing: count('missing'),
              failed: count('failed'),
              blocked: count('blocked'),
              skipped: count('skipped'),
            },
          };
        }),
      });
    }

    const query = new URL(route.request().url()).searchParams;
    const errors: { field: string; code: string }[] = [];
    const required = (field: string, pattern: RegExp) => {
      const value = query.get(field);
      if (!value) errors.push({ field, code: 'required' });
      else if (!pattern.test(value)) errors.push({ field, code: 'invalid' });
      return value ?? '';
    };
    const capabilityId = required('capabilityId', /^[a-z0-9][a-z0-9._-]{0,99}$/);
    const capabilityVersion = required('capabilityVersion', /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/);
    const schemaVersion = required('schemaVersion', /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/);
    const nodeKey = query.get('nodeKey');
    if (nodeKey !== null && !/^[a-z0-9][a-z0-9._-]{0,99}$/.test(nodeKey)) errors.push({ field: 'nodeKey', code: 'invalid' });
    const milliseconds = (field: string, minimum: number) => {
      const value = query.get(field);
      if (value === null) return null;
      const parsedValue = /^\d+$/.test(value) ? Number(value) : Number.NaN;
      if (!Number.isSafeInteger(parsedValue) || parsedValue < minimum) {
        errors.push({ field, code: 'out-of-range' });
        return null;
      }
      return parsedValue;
    };
    const fromMs = milliseconds('fromMs', 0);
    const toMs = milliseconds('toMs', fromMs === null ? 1 : fromMs + 1);
    const token = query.get('continuationToken');
    let after: { s: number; e: number; r: string; o: number; h: string } | null = null;
    if (token !== null) {
      try {
        after = JSON.parse(Buffer.from(token, 'base64url').toString('utf8'));
        if (!after || typeof after.s !== 'number' || typeof after.e !== 'number' || typeof after.r !== 'string' || typeof after.o !== 'number' || typeof after.h !== 'string') {
          throw new Error('malformed');
        }
      } catch {
        after = null;
        errors.push({ field: 'continuationToken', code: 'invalid' });
      }
    }
    if (errors.length > 0) return json(400, problem(400, 'Validation failed', { errors }));

    const nodes = run.streams.filter(
      (s) =>
        s.capabilityId === capabilityId &&
        s.capabilityVersion === capabilityVersion &&
        s.schemaVersion === schemaVersion &&
        (nodeKey === null || s.nodeKey === nodeKey),
    );
    if (nodes.length === 0) {
      return json(404, problem(404, 'Capability not in run', { code: 'analysis-capability-not-in-run' }));
    }
    if (nodes.length > 1) {
      return json(400, problem(400, 'Capability ambiguous', { code: 'analysis-capability-ambiguous' }));
    }
    const stream = nodes[0];
    const hash = selectionHash(run.runId, stream, fromMs, toMs);
    if (after && after.h !== hash) {
      return json(400, problem(400, 'Validation failed', { errors: [{ field: 'continuationToken', code: 'invalid' }] }));
    }

    // Same ordering and overlap rule as the platform's fact index query: half-open intervals that overlap the range.
    const ordered = stream.facts
      .filter((f) => (toMs === null || f.startMs < toMs) && (fromMs === null || f.endMs > fromMs))
      .sort(compareFacts)
      .filter((f) => {
        if (!after) return true;
        const key = (x: { s: number; e: number; r: string; o: number }) => [x.s, x.e, x.r, x.o] as const;
        const [a, b] = [key({ s: f.startMs, e: f.endMs, r: f.acceptedResultId, o: f.ordinal }), key(after as never)];
        for (let i = 0; i < 4; i++) if (a[i] !== b[i]) return a[i] > b[i];
        return false;
      });
    const items = ordered.slice(0, this.factPageSize);
    const last = items[items.length - 1];
    const next =
      ordered.length > items.length && last
        ? base64Url(JSON.stringify({ s: last.startMs, e: last.endMs, r: last.acceptedResultId, o: last.ordinal, h: hash }))
        : null;
    const rangeStart = fromMs ?? 0;
    const rangeEnd = toMs ?? stream.coverageEndMs;
    return json(200, {
      run: runDto,
      stream: {
        nodeKey: stream.nodeKey,
        capability: { id: stream.capabilityId, version: stream.capabilityVersion },
        schemaVersion: stream.schemaVersion,
        outputType: stream.outputType,
        executionScope: stream.executionScope,
        provenance: stream.provenance,
      },
      range: { fromMs, toMs },
      items,
      coverage: stream.coverage
        .map((w) => ({ startMs: Math.max(w.startMs, rangeStart), endMs: Math.min(w.endMs, rangeEnd), state: w.state }))
        .filter((w) => w.endMs > w.startMs),
      continuationToken: next,
    });
  }

  /** Subsequent requests are answered by the signed-in state; sign-in succeeds for this role. */
  signInAs(role: SimulatedRole, options: { passwordChangeRequired?: boolean } = {}): void {
    this.current = { role, passwordChangeRequired: options.passwordChangeRequired ?? false };
  }

  /** Simulates a session ended on the platform, for example by a Club Admin: every request answers 401. */
  endSession(): void {
    this.current = null;
  }

  /** Overrides one route, for example `on('POST', '/session', ...)`. */
  on(method: string, path: string, handler: Handler): void {
    this.overrides.set(`${method.toUpperCase()} ${path}`, handler);
  }

  /** Usable recovery tokens mapped to their account name; a completion uses its token up. */
  readonly recoveryTokens = new Map<string, string>();
  /** Addresses of the recovery requests the simulated platform accepted, in order. */
  readonly recoveryRequests: string[] = [];
  /** Completed recoveries, in order. */
  readonly recoveryCompletions: { accountName: string; newPassword: string }[] = [];

  /** Identity providers the simulated platform lists; empty means none is configured. */
  providers: { id: string; displayName: string }[] = [];
  /** When true, starting an external sign-in answers 503 `provider-unavailable`. */
  providerUnavailable = false;
  /** Bodies of the external sign-in starts the simulated platform received, in order. */
  readonly externalStarts: Record<string, unknown>[] = [];
  /** Link codes the simulated platform accepts; a start that carries any other code answers the generic 400. */
  readonly linkCodes = new Set<string>();

  /**
   * Simulates the provider and the platform callback: the browser navigating to the authorization URL
   * is redirected as the callback would, to the Web UI home (signed in as `role`) or to `?external=failed`.
   */
  async simulateProviderRedirect(outcome: 'success' | 'failed', role: SimulatedRole = 'ClubAdmin'): Promise<void> {
    await this.page.route(`${SIMULATED_PROVIDER_ORIGIN}/**`, (route) => {
      const target = new URL(outcome === 'success' ? '/' : '/sign-in?external=failed', this.page.url());
      if (outcome === 'success') this.signInAs(role);
      return route.fulfill({ status: 302, headers: { Location: target.toString() } });
    });
  }

  /** Registrations the simulated platform knows, in list order; tests may add, replace, or clear them. */
  registrations: AnalystManagerRegistrationDto[] = [];
  /** Pairing codes (as typed, upper-case, with dashes) the platform accepts, mapped to the registration they pair. */
  readonly pairingCodes = new Map<string, string>();
  /** When set, every code submission answers 429 with this `Retry-After` in seconds. */
  rateLimitSubmissionsFor: number | null = null;

  /**
   * The minimum Manager version the simulated platform reports to Club Admins. A registration that does not set
   * `belowMinimumVersion` itself is flagged when its effective version (status report, else device metadata) is lower.
   */
  minimumManagerVersion: string | null = null;

  private isBelowMinimum(r: AnalystManagerRegistrationDto, minimum: string): boolean {
    const effective = r.status?.managerVersion ?? r.deviceMetadata?.managerVersion;
    if (!effective) return false;
    const parts = (v: string) => v.split('.').map((p) => Number.parseInt(p, 10) || 0);
    const a = parts(String(effective));
    const b = parts(minimum);
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      const d = (a[i] ?? 0) - (b[i] ?? 0);
      if (d !== 0) return d < 0;
    }
    return false;
  }

  /** Adds a registration, and optionally a pairing code that submits it. */
  addRegistration(registration: AnalystManagerRegistrationDto, pairingCode?: string): AnalystManagerRegistrationDto {
    this.registrations.push(registration);
    if (pairingCode) this.pairingCodes.set(pairingCode, String(registration.registrationId));
    return registration;
  }

  private async handleRegistrations(
    route: Route,
    method: string,
    path: string,
    role: SimulatedRole,
    json: (status: number, body: unknown) => Promise<void>,
  ): Promise<void> {
    const request = route.request();
    const isAdmin = role === 'ClubAdmin';
    if (!isAdmin && role !== 'Registrar') return json(403, problem(403, 'Forbidden'));
    const self = String(members[role].id);
    const visible = (r: AnalystManagerRegistrationDto) => isAdmin || String(r.submittedBy) === self;
    // Status, connectivity, and the minimum version marking belong to the Club Admin fleet view only.
    const shape = (r: AnalystManagerRegistrationDto): AnalystManagerRegistrationDto => {
      if (isAdmin) {
        const minimum = r.minimumManagerVersion ?? this.minimumManagerVersion ?? undefined;
        const below = r.belowMinimumVersion ?? (minimum !== undefined && this.isBelowMinimum(r, minimum));
        if (minimum === undefined && !below) return r;
        return { ...r, minimumManagerVersion: minimum, belowMinimumVersion: below } as AnalystManagerRegistrationDto;
      }
      const {
        status: _status,
        connectivity: _connectivity,
        minimumManagerVersion: _minimum,
        belowMinimumVersion: _below,
        ...rest
      } = r;
      return rest as AnalystManagerRegistrationDto;
    };
    const entity = (r: AnalystManagerRegistrationDto, status = 200) =>
      route.fulfill({
        status,
        contentType: 'application/json',
        headers: { ETag: `"${r.version}"` },
        body: JSON.stringify(shape(r)),
      });
    let body: Record<string, unknown> = {};
    try {
      body = (request.postDataJSON() ?? {}) as Record<string, unknown>;
    } catch {
      body = {};
    }

    if (path === '/analyst-manager-registrations') {
      if (method === 'POST') {
        if (this.rateLimitSubmissionsFor !== null) {
          return route.fulfill({
            status: 429,
            contentType: 'application/problem+json',
            headers: { 'Retry-After': String(this.rateLimitSubmissionsFor) },
            body: JSON.stringify(problem(429, 'Too many requests', { code: 'analyst-manager-rate-limited' })),
          });
        }
        const normalize = (value: unknown) => String(value ?? '').replace(/[^0-9a-z]/gi, '').toUpperCase();
        const code = [...this.pairingCodes.entries()].find(([c]) => normalize(c) === normalize(body.pairingCode));
        const found = code && this.registrations.find((r) => String(r.registrationId) === code[1]);
        if (
          !found ||
          found.state !== 'pairing' ||
          normalize(found.deviceFingerprint) !== normalize(body.deviceFingerprint)
        ) {
          return json(400, problem(400, 'Pairing code invalid', { code: 'analyst-manager-pairing-code-invalid' }));
        }
        Object.assign(found, {
          state: 'pending_approval',
          submittedBy: self,
          submittedAt: now,
          version: Number(found.version) + 1,
        });
        this.pairingCodes.delete(code[0]);
        return entity(found, 201);
      }
      if (method === 'GET') {
        const state = new URL(request.url()).searchParams.get('state');
        const items = this.registrations.filter((r) => visible(r) && (!state || r.state === state)).map(shape);
        return json(200, { items, continuationToken: null } satisfies AnalystManagerRegistrationPageDto);
      }
      return json(404, problem(404, 'Not found'));
    }

    const match = /^\/analyst-manager-registrations\/([^/]+)(?:\/(approval|rejection|revocation))?$/.exec(path);
    const target = match && this.registrations.find((r) => String(r.registrationId) === match[1]);
    if (!match || !target || !visible(target)) return json(404, problem(404, 'Not found'));
    const action = match[2];
    if (!action && method === 'GET') return entity(target);
    if (!action || method !== 'POST') return json(404, problem(404, 'Not found'));
    if (!isAdmin) return json(403, problem(403, 'Forbidden'));

    const required = action === 'approval' || action === 'rejection' ? 'pending_approval' : 'active';
    if (action !== 'approval') {
      const reason = typeof body.reason === 'string' ? body.reason.trim() : '';
      if (reason.length < 1 || reason.length > 500) {
        return json(400, problem(400, 'Validation failed', { errors: [{ field: 'reason', code: 'invalid' }] }));
      }
    }
    if (target.state !== required) {
      return json(409, problem(409, 'Invalid state transition', { code: 'invalid-state-transition' }));
    }
    if (
      action !== 'revocation' &&
      target.approvalExpiresAt &&
      new Date(target.approvalExpiresAt).getTime() < Date.now()
    ) {
      return json(409, problem(409, 'Registration expired', { code: 'analyst-manager-registration-expired' }));
    }
    const decided = { decidedBy: self, decidedAt: new Date(), version: Number(target.version) + 1 };
    if (action === 'approval') Object.assign(target, decided, { state: 'approved' });
    if (action === 'rejection') {
      Object.assign(target, decided, { state: 'rejected', decisionReason: body.reason, endedAt: decided.decidedAt });
    }
    if (action === 'revocation') {
      Object.assign(target, {
        state: 'revoked',
        revokedBy: self,
        endedAt: decided.decidedAt,
        endReason: body.reason,
        connectivity: 'not-applicable',
        version: decided.version,
      });
    }
    return entity(target);
  }

  /** Registered Analyst images (stored manifests included), in list order; tests may add, replace, or clear them. */
  registryImages: RegistryImage[] = [];
  /** Images the simulated image registry can serve; registering a reference to one of their digests registers it. */
  readonly registryCatalog = new Map<string, RegistryImage>();
  /** Scripted image refusals by reference (or digest); the first registration with that reference is refused. */
  readonly imageRefusals = new Map<string, { code: string; errors?: { field: string; code: string }[] }>();
  /** Profile versions with the row version the platform sends as `ETag`. */
  registryProfiles: RegistryProfile[] = [];
  registryWorkflows: RegistryWorkflow[] = [];
  /** The default workflow; `null` answers 404 and a first change needs no `If-Match`. */
  registryDefault: (RegistryDefault & { rowVersion: number }) | null = null;

  seedImage(image: RegistryImage): RegistryImage {
    this.registryImages.push(image);
    return image;
  }

  seedProfile(profile: RegistryProfile): RegistryProfile {
    this.registryProfiles.push(profile);
    return profile;
  }

  seedWorkflow(workflow: RegistryWorkflow): RegistryWorkflow {
    this.registryWorkflows.push(workflow);
    return workflow;
  }

  seedDefault(target: { workflowId: string; workflowVersion: string }, rowVersion = 1): void {
    this.registryDefault = {
      ...target,
      changedAt: now,
      changedBy: String(ids.admin),
      rowVersion,
    };
  }

  private async handleRegistry(
    route: Route,
    method: string,
    path: string,
    role: SimulatedRole,
    json: (status: number, body: unknown) => Promise<void>,
  ): Promise<void> {
    if (role !== 'ClubAdmin') return json(403, problem(403, 'Forbidden', { code: 'forbidden' }));
    const request = route.request();
    const url = new URL(request.url());
    // A 503 carries Retry-After: 0 so the client's automatic retries do not slow the test down.
    const refuse = (status: number, code: string, errors?: { field: string; code: string }[]) =>
      status === 503
        ? route.fulfill({
            status,
            contentType: 'application/problem+json',
            headers: { 'Retry-After': '0' },
            body: JSON.stringify(problem(status, code, { code, ...(errors ? { errors } : {}) })),
          })
        : json(status, problem(status, code, { code, ...(errors ? { errors } : {}) }));
    const respond = (status: number, body: unknown, rowVersion?: number) =>
      route.fulfill({
        status,
        contentType: 'application/json',
        headers: rowVersion === undefined ? {} : { ETag: `"${rowVersion}"` },
        body: JSON.stringify(body),
      });
    let body: Record<string, any> = {};
    try {
      body = (request.postDataJSON() ?? {}) as Record<string, any>;
    } catch {
      body = {};
    }
    // Returns null when the precondition holds, else the refusal.
    const precondition = (current: number): Promise<void> | null => {
      const header = request.headers()['if-match'];
      if (header === undefined) return refuse(428, 'version-required');
      return header.replace(/"/g, '') === String(current) ? null : refuse(412, 'version-mismatch');
    };
    const seg = path.split('/').map(decodeURIComponent);

    if (path === '/registry/analyst-images') {
      if (method === 'GET') {
        const capabilityId = url.searchParams.get('capabilityId');
        const items = this.registryImages
          .filter((i) => !capabilityId || i.capability.id === capabilityId)
          .map((i) => ({
            imageDigest: i.imageDigest,
            capabilityId: i.capability.id,
            capabilityVersion: i.capability.version,
            registeredAt: i.registeredAt,
          }));
        return respond(200, { items });
      }
      if (method !== 'POST') return json(404, problem(404, 'Not found'));
      const reference = typeof body.reference === 'string' ? body.reference.trim() : '';
      if (!reference) return refuse(400, 'validation-failed', [{ field: 'reference', code: 'required' }]);
      const digest = /@(sha256:[0-9a-f]{64})$/i.exec(reference)?.[1];
      const scripted = this.imageRefusals.get(reference) ?? (digest ? this.imageRefusals.get(`sha-256:${digest.slice(7)}`) : undefined);
      if (scripted) return refuse(imageRefusalStatus(scripted.code), scripted.code, scripted.errors);
      if (!digest) return refuse(400, 'reference-not-digest');
      const imageDigest = `sha-256:${digest.slice(7).toLowerCase()}`;
      const existing = this.registryImages.find((i) => i.imageDigest === imageDigest);
      if (existing) return respond(200, existing);
      const offered = this.registryCatalog.get(imageDigest);
      if (!offered) return refuse(404, 'image-not-found');
      const created = { ...offered, registeredAt: new Date(), registeredBy: String(ids.admin) };
      this.registryImages.push(created);
      return respond(201, created);
    }
    if (seg[2] === 'analyst-images' && seg.length === 4 && method === 'GET') {
      const found = this.registryImages.find((i) => i.imageDigest === seg[3]);
      return found ? respond(200, found) : refuse(404, 'not-found');
    }

    if (path === '/registry/analyst-profiles') {
      if (method === 'GET') {
        const state = url.searchParams.get('state');
        const capabilityId = url.searchParams.get('capabilityId');
        const items = this.registryProfiles
          .filter((p) => (!state || p.state === state) && (!capabilityId || p.content.capabilityId === capabilityId))
          .map(profileDto);
        return respond(200, { items });
      }
      if (method !== 'POST') return json(404, problem(404, 'Not found'));
      const content = (body.content ?? null) as RegistryProfile['content'] | null;
      if (!body.profileId || !body.imageDigest || !content) {
        return refuse(400, 'validation-failed', [{ field: 'profileId', code: 'required' }]);
      }
      const image = this.registryImages.find((i) => i.imageDigest === body.imageDigest);
      if (!image) return refuse(404, 'image-not-registered');
      const contradictions: { field: string; code: string }[] = [];
      if (content.capabilityId !== image.capability.id) contradictions.push({ field: 'capabilityId', code: 'differs' });
      if (content.capabilityVersion !== image.capability.version) {
        contradictions.push({ field: 'capabilityVersion', code: 'differs' });
      }
      if (content.modelDigest !== image.modelDigest) contradictions.push({ field: 'modelDigest', code: 'differs' });
      if (contradictions.length > 0) return refuse(422, 'profile-contradicts-declaration', contradictions);
      const sameContent = this.registryProfiles.find(
        (p) =>
          p.profileId === body.profileId &&
          p.imageDigest === body.imageDigest &&
          JSON.stringify(p.content) === JSON.stringify(content),
      );
      if (sameContent) {
        if (sameContent.state === 'retired') return refuse(409, 'profile-content-exists');
        return respond(200, profileDto(sameContent), sameContent.rowVersion);
      }
      const version =
        Math.max(0, ...this.registryProfiles.filter((p) => p.profileId === body.profileId).map((p) => p.version)) + 1;
      const profile: RegistryProfile = {
        profileId: String(body.profileId),
        version,
        profileDigest: digestOf({ profileId: body.profileId, version, imageDigest: body.imageDigest, content }),
        imageDigest: String(body.imageDigest),
        state: 'draft',
        content,
        createdAt: new Date(),
        createdBy: String(ids.admin),
        rowVersion: 1,
      };
      this.registryProfiles.push(profile);
      return respond(201, profileDto(profile), profile.rowVersion);
    }
    const profileMatch = /^\/registry\/analyst-profiles\/([^/]+)\/versions\/(\d+)(?:\/(approval|retirement))?$/.exec(path);
    if (profileMatch) {
      const profile = this.registryProfiles.find(
        (p) => p.profileId === decodeURIComponent(profileMatch[1]) && p.version === Number(profileMatch[2]),
      );
      if (!profile) return refuse(404, 'not-found');
      const action = profileMatch[3];
      if (!action) return method === 'GET' ? respond(200, profileDto(profile), profile.rowVersion) : json(404, problem(404, 'Not found'));
      if (method !== 'POST') return json(404, problem(404, 'Not found'));
      const failed = precondition(profile.rowVersion);
      if (failed) return failed;
      if (action === 'approval') {
        if (profile.state !== 'draft') return refuse(409, 'profile-not-draft');
        Object.assign(profile, { state: 'approved', approvedAt: new Date(), approvedBy: String(ids.admin) });
      } else {
        if (profile.state === 'retired') return refuse(409, 'profile-already-retired');
        const inDefault = this.registryDefault
          ? this.registryWorkflows
              .find(
                (w) =>
                  w.workflowId === this.registryDefault?.workflowId &&
                  w.workflowVersion === this.registryDefault?.workflowVersion,
              )
              ?.definition.nodes.some((n) => n.profileId === profile.profileId && n.profileVersion === profile.version)
          : false;
        if (inDefault) return refuse(409, 'profile-in-default-workflow');
        Object.assign(profile, { state: 'retired', retiredAt: new Date(), retiredBy: String(ids.admin) });
      }
      profile.rowVersion += 1;
      return respond(200, profileDto(profile), profile.rowVersion);
    }

    if (path === '/registry/workflows') {
      if (method === 'GET') {
        return respond(200, {
          items: this.registryWorkflows.map(({ workflowId, workflowVersion, definitionDigest, createdAt }) => ({
            workflowId,
            workflowVersion,
            definitionDigest,
            createdAt,
          })),
        });
      }
      if (method !== 'POST') return json(404, problem(404, 'Not found'));
      return this.defineRegistryWorkflow(body, refuse, respond);
    }
    const workflowMatch = /^\/registry\/workflows\/([^/]+)\/versions\/([^/]+)$/.exec(path);
    if (workflowMatch && method === 'GET') {
      const found = this.registryWorkflows.find(
        (w) => w.workflowId === decodeURIComponent(workflowMatch[1]) && w.workflowVersion === decodeURIComponent(workflowMatch[2]),
      );
      return found ? respond(200, found) : refuse(404, 'not-found');
    }

    if (path === '/registry/default-workflow') {
      if (method === 'GET') {
        if (!this.registryDefault) return refuse(404, 'not-found');
        const { rowVersion, ...dto } = this.registryDefault;
        return respond(200, dto, rowVersion);
      }
      if (method !== 'PUT') return json(404, problem(404, 'Not found'));
      if (!body.workflowId || !body.workflowVersion) {
        return refuse(400, 'validation-failed', [{ field: 'workflowId', code: 'required' }]);
      }
      if (this.registryDefault) {
        const failed = precondition(this.registryDefault.rowVersion);
        if (failed) return failed;
      }
      const target = this.registryWorkflows.find(
        (w) => w.workflowId === body.workflowId && w.workflowVersion === body.workflowVersion,
      );
      if (!target) return refuse(422, 'workflow-not-resolvable');
      this.registryDefault = {
        workflowId: target.workflowId,
        workflowVersion: target.workflowVersion,
        changedAt: new Date(),
        changedBy: String(ids.admin),
        rowVersion: (this.registryDefault?.rowVersion ?? 0) + 1,
      };
      const { rowVersion, ...dto } = this.registryDefault;
      return respond(200, dto, rowVersion);
    }
    return json(404, problem(404, 'Not found'));
  }

  private defineRegistryWorkflow(
    body: Record<string, any>,
    refuse: (status: number, code: string, errors?: { field: string; code: string }[]) => Promise<void>,
    respond: (status: number, body: unknown, rowVersion?: number) => Promise<void>,
  ): Promise<void> {
    const nodes = (Array.isArray(body.nodes) ? body.nodes : []) as { nodeKey: string; profileId: string; profileVersion: number }[];
    const edges = (Array.isArray(body.edges) ? body.edges : []) as RegistryWorkflow['definition']['edges'];
    const requested = (Array.isArray(body.requestedCapabilities) ? body.requestedCapabilities : []) as string[];
    if (!body.workflowId || !body.workflowVersion || nodes.length === 0) {
      return refuse(400, 'validation-failed', [{ field: 'workflowId', code: 'required' }]);
    }
    if (this.registryWorkflows.some((w) => w.workflowId === body.workflowId && w.workflowVersion === body.workflowVersion)) {
      return refuse(409, 'workflow-version-exists');
    }
    const violations: { field: string; code: string }[] = [];
    const resolved = nodes.map((n) => {
      const profile = this.registryProfiles.find((p) => p.profileId === n.profileId && p.version === n.profileVersion);
      if (!profile) violations.push({ field: `nodes.${n.nodeKey}`, code: 'profile-not-found' });
      else if (profile.state !== 'approved') violations.push({ field: `nodes.${n.nodeKey}`, code: 'profile-not-approved' });
      return { ...n, profileDigest: profile?.profileDigest ?? '', capabilityId: profile?.content.capabilityId };
    });
    if (body.completionPolicy !== 'all-requested-capabilities') {
      violations.push({ field: 'workflow', code: 'unknown-completion-policy' });
    }
    const keys = new Set(nodes.map((n) => n.nodeKey));
    if (edges.some((e) => !keys.has(e.upstreamNodeKey) || !keys.has(e.dependentNodeKey))) {
      violations.push({ field: 'workflow', code: 'invalid-graph' });
    }
    if (requested.some((c) => !resolved.some((n) => n.capabilityId === c))) {
      violations.push({ field: 'workflow', code: 'requested-capability-missing' });
    }
    if (violations.length > 0) return refuse(422, 'workflow-invalid', violations);
    const definition = {
      requestedCapabilities: requested,
      completionPolicy: String(body.completionPolicy),
      segmentationPolicy: body.segmentationPolicy,
      nodes: resolved.map(({ nodeKey, profileId, profileVersion, profileDigest }) => ({
        nodeKey,
        profileId,
        profileVersion,
        profileDigest,
      })),
      edges,
    };
    const workflow: RegistryWorkflow = {
      workflowId: String(body.workflowId),
      workflowVersion: String(body.workflowVersion),
      definitionDigest: digestOf(definition),
      definition,
      createdAt: new Date(),
      createdBy: String(ids.admin),
    };
    this.registryWorkflows.push(workflow);
    return respond(201, workflow);
  }

  /** Agent catalog state, seeded like the Development and Test seed; tests may add, replace, or clear any of it. */
  catalogArtifacts: CatalogArtifact[] = [];
  catalogVersions: CatalogAgentVersion[] = [];
  catalogEnablements: CatalogEnablement[] = [];
  private readonly catalogRefusals = new Map<CatalogOperation, CatalogRefusal[]>();
  private catalogClock = 1;

  /** Restores the seeded catalog (nine baseline agents, two approved and enabled specialists with four artifacts each). */
  resetCatalog(): void {
    const seeded = seedCatalog();
    this.catalogArtifacts = seeded.artifacts;
    this.catalogVersions = seeded.versions;
    this.catalogEnablements = seeded.enablements;
    this.catalogRefusals.clear();
  }

  /** The next `count` requests of the operation answer the refusal before any state change (any validation problem or refusal). */
  refuseCatalogNext(operation: CatalogOperation, refusal: CatalogRefusal, count = 1): void {
    const queue = this.catalogRefusals.get(operation) ?? [];
    for (let i = 0; i < count; i++) queue.push(refusal);
    this.catalogRefusals.set(operation, queue);
  }

  /** Injects an artifact registered by any member (default: the seed actor). */
  seedCatalogArtifact(
    document: Record<string, any>,
    options: { registeredBy?: string; distances?: CatalogDistance[] } = {},
  ): CatalogArtifact {
    const record: CatalogArtifact = {
      kind: String(document.kind),
      id: String(document.id),
      version: String(document.version),
      digest: digestOf(document),
      registeredBy: options.registeredBy ?? CATALOG_SEED_ACTOR,
      registeredAt: later(this.catalogClock++),
      document,
      ...(options.distances ? { distances: options.distances } : {}),
    };
    this.catalogArtifacts.push(record);
    return record;
  }

  /** Injects an agent version in any state registered by any member; Coach Agent versions may carry fixed distances. */
  seedCatalogAgentVersion(
    document: Record<string, any>,
    options: Partial<Omit<CatalogAgentVersion, 'agentId' | 'version' | 'digest' | 'group' | 'document'>> = {},
  ): CatalogAgentVersion {
    const state = options.state ?? 'draft';
    const stamp = later(this.catalogClock++);
    const version: CatalogAgentVersion = {
      agentId: String(document.agentId),
      version: String(document.version),
      digest: digestOf(document),
      group: String(document.group),
      document,
      registeredBy: CATALOG_SEED_ACTOR,
      registeredAt: stamp,
      approvedBy: state === 'approved' || state === 'retired' ? CATALOG_SEED_ACTOR : null,
      approvedAt: state === 'approved' || state === 'retired' ? stamp : null,
      selfApproval: false,
      differentiationAcknowledged: false,
      retiredBy: state === 'retired' ? CATALOG_SEED_ACTOR : null,
      retiredAt: state === 'retired' ? stamp : null,
      rowVersion: 1,
      ...options,
      state,
    };
    this.catalogVersions.push(version);
    return version;
  }

  /** Enables (or, with `null`, disables) a version directly, as another member's change would. */
  seedCatalogEnablement(agentId: string, enabledVersion: string | null, changedBy = CATALOG_SEED_ACTOR): void {
    const found = this.catalogEnablements.find((e) => e.agentId === agentId);
    const changed = {
      enabledVersion,
      changedBy: enabledVersion ? changedBy : null,
      changedAt: enabledVersion ? later(this.catalogClock++) : null,
    };
    if (found) Object.assign(found, changed, { rowVersion: found.rowVersion + 1 });
    else this.catalogEnablements.push({ agentId, rowVersion: 1, ...changed });
    this.syncAvailableAgents();
  }

  /** Simulates a change by someone else: the stored row version moves on, so a held `If-Match` is stale. */
  changeCatalogBehindTheScenes(target: { agentId: string; version?: string }): void {
    const record = target.version
      ? this.catalogVersions.find((v) => v.agentId === target.agentId && v.version === target.version)
      : this.catalogEnablements.find((e) => e.agentId === target.agentId);
    if (record) record.rowVersion += 1;
  }

  /** After an enablement change the enablement is the source of `listAvailableAgents`; the untouched default keeps the six specialists of the triage tests. */
  private syncAvailableAgents(): void {
    const baseline = catalogBaseline().agents;
    this.agents = this.catalogEnablements
      .filter((e) => e.enabledVersion)
      .map((e) => baseline.find((a) => a.agentId === e.agentId))
      .filter((a): a is CatalogBaselineDocument['agents'][number] => !!a && a.group === 'specialist')
      .map((a) => ({ agentId: a.agentId, group: a.group, domain: a.domain, enabledVersion: this.catalogEnablements.find((e) => e.agentId === a.agentId)!.enabledVersion! }));
  }

  private catalogDistances(profileId: string, profileVersion: string): CatalogDistance[] {
    const profile = this.catalogArtifacts.find(
      (a) => a.kind === 'philosophy-profile' && a.id === profileId && a.version === profileVersion,
    );
    if (!profile) return [];
    const result: CatalogDistance[] = [];
    for (const coach of this.catalogVersions) {
      const ref = coach.document.artifacts?.philosophyProfile as { id: string; version: string } | undefined;
      if (coach.group !== 'coach' || coach.state !== 'approved' || !ref) continue;
      if (ref.id === profileId && ref.version === profileVersion) continue;
      const other = this.catalogArtifacts.find(
        (a) => a.kind === 'philosophy-profile' && a.id === ref.id && a.version === ref.version,
      );
      if (!other) continue;
      const distance = profileDistance(profile.document.weights ?? {}, other.document.weights ?? {});
      result.push({
        agentId: coach.agentId,
        agentVersion: coach.version,
        profileId: ref.id,
        profileVersion: ref.version,
        distance: Math.round(distance * 100) / 100,
        reviewRequired: distance < CATALOG_REVIEW_THRESHOLD,
      });
    }
    return result;
  }

  private versionDistances(version: CatalogAgentVersion): CatalogDistance[] | undefined {
    if (version.distances) return version.distances;
    const ref = version.document.artifacts?.philosophyProfile as { id: string; version: string } | undefined;
    return version.group === 'coach' && ref ? this.catalogDistances(ref.id, ref.version) : undefined;
  }

  private validateCatalogRecord(kind: string, document: Record<string, any>): ReturnType<typeof catalogViolation>[] {
    const violations: ReturnType<typeof catalogViolation>[] = [];
    const schema = (pointer: string, detail: string) => violations.push(catalogViolation('schema-violation', pointer, detail));
    if (document.kind !== kind) schema('/kind', `The record kind is '${kind}'.`);
    if (document.schemaVersion !== '1.0.0') schema('/schemaVersion', 'The schema version is 1.0.0.');
    if (kind !== 'agent-version') {
      if (typeof document.id !== 'string' || !CATALOG_NAME.test(document.id)) schema('/id', 'The identifier is 1 to 128 name characters.');
    }
    if (typeof document.version !== 'string' || !SEMVER.test(document.version)) schema('/version', 'The version is a semantic version.');
    const requireString = (field: string) => {
      if (typeof document[field] !== 'string' || document[field] === '') schema(`/${field}`, `${field} is required.`);
    };
    const requireList = (field: string, min = 1) => {
      if (!Array.isArray(document[field]) || document[field].length < min) schema(`/${field}`, `${field} needs at least ${min} entr${min === 1 ? 'y' : 'ies'}.`);
    };
    const allowlist = catalogBaseline().toolAllowlist;
    const checkTools = (pointer: string, tools: unknown) => {
      if (!Array.isArray(tools)) return;
      tools.forEach((tool, i) => {
        if (!allowlist.includes(String(tool))) {
          violations.push(catalogViolation('unknown-tool', `${pointer}/${i}`, `Tool '${tool}' is not on the allowlist.`));
        }
      });
    };
    switch (kind) {
      case 'prompt-template': {
        requireString('group');
        requireString('responseContract');
        requireString('text');
        if (typeof document.text === 'string' && document.text.length > 32_000) schema('/text', 'The text is at most 32,000 characters.');
        const slots: string[] = Array.isArray(document.slots) ? document.slots : [];
        slots.forEach((slot, i) => {
          const hits = typeof document.text === 'string' ? document.text.split(`{{${slot}}}`).length - 1 : 0;
          if (hits !== 1) {
            violations.push(catalogViolation('profile-slot-missing', `/slots/${i}`, `Declared slot '${slot}' must occur exactly once as {{${slot}}}.`));
          }
        });
        const used = typeof document.text === 'string' ? [...document.text.matchAll(/\{\{([^{}]+)\}\}/g)].map((m) => m[1]) : [];
        for (const slot of new Set(used)) {
          if (!slots.includes(slot)) {
            violations.push(catalogViolation('profile-slot-not-allowed', '/text', `Slot '${slot}' occurs but is not declared.`));
          }
        }
        break;
      }
      case 'model-selection':
        requireString('provider');
        requireString('model');
        requireString('deployment');
        break;
      case 'tool-policy':
        requireList('tools');
        checkTools('/tools', document.tools);
        break;
      case 'safety-policy':
        requireString('group');
        requireList('routes');
        requireList('hardBoundaries');
        break;
      case 'dimension-set':
        requireList('dimensions');
        break;
      case 'philosophy-profile': {
        const set = this.catalogArtifacts.find(
          (a) => a.kind === 'dimension-set' && a.id === document.dimensionSet?.id && a.version === document.dimensionSet?.version,
        );
        if (!set) {
          violations.push(catalogViolation('dimension-set-not-found', '/dimensionSet', 'The dimension set does not exist.'));
          break;
        }
        const weights = (document.weights ?? {}) as Record<string, any>;
        const dimensions: { id: string }[] = set.document.dimensions ?? [];
        let total = 0;
        for (const { id } of dimensions) {
          if (!(id in weights)) violations.push(catalogViolation('missing-dimension', `/weights/${id}`, `Dimension '${id}' has no weight.`));
        }
        for (const [id, entry] of Object.entries(weights)) {
          if (!dimensions.some((d) => d.id === id)) {
            violations.push(catalogViolation('unknown-dimension', `/weights/${id}`, `Dimension '${id}' is not part of the dimension set.`));
            continue;
          }
          const weight = entry?.weight;
          if (!Number.isInteger(weight) || weight < 0 || weight > 100) {
            violations.push(catalogViolation('weight-out-of-range', `/weights/${id}/weight`, 'A weight is an integer from 0 to 100.'));
          } else total += weight;
          if (!entry?.rationale) violations.push(catalogViolation('rationale-missing', `/weights/${id}/rationale`, 'A rationale is required.'));
        }
        if (total !== 100 && !violations.some((v) => v.code === 'weight-out-of-range')) {
          violations.push(catalogViolation('weights-total-not-100', '/weights', `The weights total ${total}, not 100.`));
        }
        break;
      }
      case 'modifier-policy':
        if (!Number.isInteger(document.perDimensionBound) || document.perDimensionBound < 1 || document.perDimensionBound > 3) {
          violations.push(catalogViolation('modifier-bound-exceeded', '/perDimensionBound', 'The per-dimension bound is at most 3.'));
        }
        if (!Number.isInteger(document.maxPositiveMovement) || document.maxPositiveMovement < 1 || document.maxPositiveMovement > 10) {
          violations.push(catalogViolation('modifier-movement-exceeded', '/maxPositiveMovement', 'The maximum positive movement is at most 10.'));
        }
        break;
      case 'agent-version': {
        const agent = catalogBaseline().agents.find((a) => a.agentId === document.agentId);
        if (!agent) {
          violations.push(catalogViolation('unknown-agent-id', '/agentId', `'${document.agentId}' is not a catalog agent identifier.`));
          break;
        }
        if (document.group !== agent.group) {
          violations.push(catalogViolation('group-mismatch', '/group', `Agent '${agent.agentId}' belongs to the group '${agent.group}'.`));
        }
        if (agent.group === 'coach' && !document.disclosure) {
          violations.push(catalogViolation('disclosure-missing', '/disclosure', 'Coach Agent versions carry a disclosure.'));
        }
        const slots: [string, string][] = [
          ['promptTemplate', 'prompt-template'],
          ['modelSelection', 'model-selection'],
          ['toolPolicy', 'tool-policy'],
          ['safetyPolicy', 'safety-policy'],
          ['philosophyProfile', 'philosophy-profile'],
          ['modifierPolicy', 'modifier-policy'],
        ];
        for (const [slot, slotKind] of slots) {
          const ref = document.artifacts?.[slot] as { id: string; version: string } | undefined;
          const coachOnly = slot === 'philosophyProfile' || slot === 'modifierPolicy';
          if (!ref) {
            if (coachOnly && agent.group === 'coach') {
              violations.push(catalogViolation('artifact-not-found', `/artifacts/${slot}`, `Coach Agent versions reference a ${slotKind}.`));
            }
            continue;
          }
          if (coachOnly && agent.group !== 'coach') {
            violations.push(catalogViolation('profile-slot-not-allowed', `/artifacts/${slot}`, `Specialist Agent versions reference no ${slotKind}.`));
            continue;
          }
          const found = this.catalogArtifacts.find((a) => a.id === ref.id && a.version === ref.version);
          if (!found) violations.push(catalogViolation('artifact-not-found', `/artifacts/${slot}`, `The ${slotKind} ${ref.id} ${ref.version} does not exist.`));
          else if (found.kind !== slotKind) violations.push(catalogViolation('artifact-kind-mismatch', `/artifacts/${slot}`, `${ref.id} is not a ${slotKind}.`));
          else if (slot === 'toolPolicy') {
            (found.document.tools ?? []).forEach((tool: string, i: number) => {
              if (!agent.permittedTools.includes(tool)) {
                violations.push(catalogViolation('tool-outside-baseline', `/artifacts/toolPolicy/tools/${i}`, `Tool '${tool}' is outside the baseline of '${agent.agentId}'.`));
              }
            });
          }
        }
        break;
      }
    }
    return violations;
  }

  private async handleAgentCatalog(
    route: Route,
    method: string,
    path: string,
    role: SimulatedRole,
    json: (status: number, body: unknown) => Promise<void>,
  ): Promise<void> {
    const request = route.request();
    const headers = request.headers();
    const actor = String(members[role].id);
    const reply = (status: number, body: unknown, extra: Record<string, string> = {}) =>
      route.fulfill({
        status,
        contentType: status >= 400 ? 'application/problem+json' : 'application/json',
        headers: extra,
        body: JSON.stringify(body),
      });
    const refuse = (status: number, code?: string, extra: Record<string, unknown> = {}) =>
      reply(status, problem(status, code ?? `Refused ${status}`, { ...(code ? { code } : {}), ...extra }));
    const conditional = (etag: string, payload: unknown, extra: Record<string, string> = {}) =>
      headers['if-none-match'] === etag
        ? route.fulfill({ status: 304, headers: { ETag: etag, ...extra } })
        : reply(200, payload, { ETag: etag, ...extra });
    const precondition = (current: number): Promise<void> | null => {
      const header = headers['if-match'];
      if (header === undefined) return refuse(428, 'version-required');
      return header.replace(/"/g, '') === String(current) ? null : refuse(412, 'version-mismatch');
    };
    const etagOf = (rowVersion: number) => `"${rowVersion}"`;
    let body: Record<string, any> = {};
    try {
      body = (request.postDataJSON() ?? {}) as Record<string, any>;
    } catch {
      body = {};
    }
    const url = new URL(request.url());
    const seg = path.split('/').map(decodeURIComponent);
    const baseline = catalogBaseline();
    const enabledOf = (agentId: string) => this.catalogEnablements.find((e) => e.agentId === agentId)?.enabledVersion ?? null;
    const versionView = (v: CatalogAgentVersion) => {
      const { rowVersion: _rowVersion, distances: _distances, ...rest } = v;
      const distances = this.versionDistances(v);
      return { ...rest, enabled: enabledOf(v.agentId) === v.version, ...(distances ? { distances } : {}) };
    };
    const summaryOf = (v: CatalogAgentVersion) => ({
      agentId: v.agentId,
      version: v.version,
      digest: v.digest,
      group: v.group,
      state: v.state,
      enabled: enabledOf(v.agentId) === v.version,
      registeredAt: v.registeredAt,
    });
    const enablementView = (e: CatalogEnablement) => {
      const { rowVersion, ...rest } = e;
      return { ...rest, group: baseline.agents.find((a) => a.agentId === e.agentId)?.group ?? 'specialist', etag: etagOf(rowVersion) };
    };
    const artifactView = (a: CatalogArtifact, withDistances: boolean) => {
      const { distances, ...rest } = a;
      const computed = withDistances && a.kind === 'philosophy-profile' ? (distances ?? this.catalogDistances(a.id, a.version)) : undefined;
      return { ...rest, ...(computed ? { distances: computed } : {}) };
    };

    const operation = ((): CatalogOperation | null => {
      if (path === '/agent-catalog/baseline' && method === 'GET') return 'getAgentCatalogBaseline';
      if (seg[2] === 'artifacts' && seg.length === 4) return method === 'POST' ? 'registerAgentCatalogArtifact' : method === 'GET' ? 'listAgentCatalogArtifacts' : null;
      if (seg[2] === 'artifacts' && seg.length === 7 && method === 'GET') return 'getAgentCatalogArtifact';
      if (seg[2] === 'agent-versions' && seg.length === 3) return method === 'POST' ? 'registerAgentVersion' : method === 'GET' ? 'listAgentVersions' : null;
      if (seg[2] === 'agent-versions' && seg.length === 6 && method === 'GET') return 'getAgentVersion';
      if (seg[2] === 'agent-versions' && seg.length === 7 && method === 'POST') return seg[6] === 'approval' ? 'approveAgentVersion' : seg[6] === 'retirement' ? 'retireAgentVersion' : null;
      if (path === '/agent-catalog/enablements' && method === 'GET') return 'listAgentEnablements';
      if (seg[2] === 'enablements' && seg.length === 4 && method === 'PUT') return 'setAgentEnablement';
      return null;
    })();
    if (!operation) return json(404, problem(404, 'Not found'));
    if (role !== 'ClubAdmin') return refuse(403, 'forbidden');
    const scripted = this.catalogRefusals.get(operation)?.shift();
    if (scripted) return refuse(scripted.status, scripted.code, scripted.extra);

    switch (operation) {
      case 'getAgentCatalogBaseline':
        return reply(200, { schemaVersion: '1.0.0', kind: 'catalog-baseline', version: '1.0.0', ...baseline });
      case 'listAgentCatalogArtifacts': {
        const kind = CATALOG_KINDS[seg[3] as CatalogKind];
        if (!kind) return refuse(404, 'not-found');
        const id = url.searchParams.get('id');
        const items = this.catalogArtifacts
          .filter((a) => a.kind === kind && (!id || a.id === id))
          .map(({ kind: k, id: i, version, digest, registeredBy, registeredAt }) => ({ kind: k, id: i, version, digest, registeredBy, registeredAt }));
        return reply(200, { items });
      }
      case 'registerAgentCatalogArtifact': {
        const kind = CATALOG_KINDS[seg[3] as CatalogKind];
        if (!kind) return refuse(404, 'not-found');
        const violations = this.validateCatalogRecord(kind, body);
        if (violations.length) return refuse(400, 'agent-catalog-validation-failed', { violations });
        const digest = digestOf(body);
        const existing = this.catalogArtifacts.find((a) => a.kind === kind && a.id === body.id && a.version === body.version);
        if (existing) {
          return existing.digest === digest ? reply(200, artifactView(existing, true)) : refuse(409, 'agent-catalog-version-exists');
        }
        const created = this.seedCatalogArtifact(body, { registeredBy: actor });
        return reply(201, artifactView(created, true), {
          Location: `/api/v1/agent-catalog/artifacts/${seg[3]}/${encodeURIComponent(created.id)}/versions/${created.version}`,
        });
      }
      case 'getAgentCatalogArtifact': {
        const kind = CATALOG_KINDS[seg[3] as CatalogKind];
        const found = this.catalogArtifacts.find((a) => a.kind === kind && a.id === seg[4] && a.version === seg[6]);
        if (!found) return refuse(404, 'not-found');
        return conditional(`"${found.digest}"`, artifactView(found, true));
      }
      case 'listAgentVersions': {
        const agentId = url.searchParams.get('agentId');
        const state = url.searchParams.get('state');
        const items = this.catalogVersions.filter((v) => (!agentId || v.agentId === agentId) && (!state || v.state === state)).map(summaryOf);
        return reply(200, { items });
      }
      case 'registerAgentVersion': {
        const violations = this.validateCatalogRecord('agent-version', body);
        if (violations.length) return refuse(400, 'agent-catalog-validation-failed', { violations });
        const existing = this.catalogVersions.find((v) => v.agentId === body.agentId && v.version === body.version);
        if (existing) {
          return existing.digest === digestOf(body)
            ? reply(200, { ...versionView(existing) }, { ETag: etagOf(existing.rowVersion) })
            : refuse(409, 'agent-catalog-version-exists');
        }
        const created = this.seedCatalogAgentVersion(body, { registeredBy: actor });
        return reply(201, { ...versionView(created) }, {
          ETag: etagOf(created.rowVersion),
          Location: `/api/v1/agent-catalog/agent-versions/${created.agentId}/versions/${created.version}`,
        });
      }
      case 'getAgentVersion': {
        const found = this.catalogVersions.find((v) => v.agentId === seg[3] && v.version === seg[5]);
        if (!found) return refuse(404, 'not-found');
        return conditional(`"${found.rowVersion}"`, versionView(found));
      }
      case 'approveAgentVersion':
      case 'retireAgentVersion': {
        const found = this.catalogVersions.find((v) => v.agentId === seg[3] && v.version === seg[5]);
        if (!found) return refuse(404, 'not-found');
        const failed = precondition(found.rowVersion);
        if (failed) return failed;
        const stamp = later(this.catalogClock++);
        if (operation === 'approveAgentVersion') {
          if (found.state !== 'draft') return refuse(409, 'agent-version-not-draft');
          const acknowledged = body.differentiationReviewAcknowledged === true;
          const review = (this.versionDistances(found) ?? []).some((d) => d.reviewRequired);
          if (review && !acknowledged) return refuse(409, 'differentiation-review-required');
          Object.assign(found, {
            state: 'approved',
            approvedBy: actor,
            approvedAt: stamp,
            selfApproval: found.registeredBy === actor,
            differentiationAcknowledged: review && acknowledged,
          });
        } else {
          if (found.state !== 'approved') return refuse(409, 'agent-version-not-approved');
          if (enabledOf(found.agentId) === found.version) return refuse(409, 'agent-version-enabled');
          Object.assign(found, { state: 'retired', retiredBy: actor, retiredAt: stamp });
        }
        found.rowVersion += 1;
        return reply(200, versionView(found), { ETag: etagOf(found.rowVersion) });
      }
      case 'listAgentEnablements':
        return reply(200, { items: this.catalogEnablements.map(enablementView) });
      case 'setAgentEnablement': {
        const agent = baseline.agents.find((a) => a.agentId === seg[3]);
        const enablement = this.catalogEnablements.find((e) => e.agentId === seg[3]);
        if (!agent || !enablement) return refuse(404, 'not-found');
        if (body.version !== null && body.version !== undefined && typeof body.version !== 'string') {
          return refuse(400, 'validation-failed', { errors: [{ field: 'version', code: 'invalid' }] });
        }
        const failed = precondition(enablement.rowVersion);
        if (failed) return failed;
        const target = (body.version ?? null) as string | null;
        if (target !== null) {
          if (agent.group === 'coach') return refuse(409, 'coach-agent-enablement-unsupported');
          const version = this.catalogVersions.find((v) => v.agentId === agent.agentId && v.version === target);
          if (!version) return refuse(404, 'not-found');
          if (version.state !== 'approved') {
            return refuse(409, 'agent-version-not-enableable', { reason: version.state === 'retired' ? 'retired' : 'not-approved' });
          }
        }
        this.seedCatalogEnablement(agent.agentId, target, actor);
        return reply(200, enablementView(this.catalogEnablements.find((e) => e.agentId === agent.agentId)!), {
          ETag: etagOf(this.catalogEnablements.find((e) => e.agentId === agent.agentId)!.rowVersion),
        });
      }
    }
  }

  /** Agents the catalog lists as enabled; tests may replace them. */
  agents: SimulatedAgent[] = [
    ['specialist-fitness', 'Fitness'],
    ['specialist-goalkeeping', 'Goalkeeping'],
    ['specialist-tactics', 'Tactics'],
    ['specialist-video-analysis', 'Video analysis'],
    ['specialist-scouting', 'Scouting'],
    ['specialist-player-development', 'Player development'],
  ].map(([agentId, domain]) => ({ agentId, group: 'specialist', domain }));
  readonly agentConversations: SimulatedConversation[] = [];
  readonly agentInvocations = new Map<string, SimulatedInvocation>();
  /** Idempotency keys recorded per member, with the request fingerprint and the resource the first request created. */
  readonly idempotencyRecords = new Map<string, { fingerprint: string; resourceId: string }>();
  /** The states a newly submitted invocation advances through. */
  agentScript: AgentStep[] = [{ state: 'accepted' }, { state: 'running' }, { state: 'succeeded' }];
  /** Suggested polling interval, in seconds, sent as `Retry-After` and `retryAfterSeconds` while unfinished. */
  statusRetryAfterSeconds = 1;
  /** Unfinished invocations of one member from which a submission answers 429. */
  maxUnfinishedInvocations = 3;
  /** Analysis run states that allow a conversation. */
  eligibleRunStates = new Set(['Completed']);
  private readonly agentRefusalQueue = new Map<AgentOperation, AgentRefusal[]>();
  private readonly droppedResponses = new Set<AgentOperation>();

  /** The next `count` requests of the operation are refused before any state change. */
  refuseNext(operation: AgentOperation, refusal: AgentRefusal, count = 1): void {
    const queue = this.agentRefusalQueue.get(operation) ?? [];
    for (let i = 0; i < count; i++) queue.push(refusal);
    this.agentRefusalQueue.set(operation, queue);
  }

  /** The next create or submit is accepted and recorded, but the browser sees a network failure instead of the answer. */
  dropNextResponse(operation: 'createConversation' | 'submit'): void {
    this.droppedResponses.add(operation);
  }

  /** Adds an analysis run of the match in an eligible state that conversations may bind to. */
  seedEligibleRun(matchId: string = String(match.id)): AnalysisRunSummaryDto {
    const run = { ...analysisRun, runId: newId(), matchId, recordingSetVersionId: newId(), state: [...this.eligibleRunStates][0] ?? 'Completed' };
    this.analysisRuns.push(run);
    return run;
  }

  /** Adds a conversation owned by the member of `ownerRole` (default Coach). */
  seedAgentConversation(
    options: {
      ownerRole?: SimulatedRole;
      matchId?: string;
      analysisRunId?: string;
      participants?: string[];
      analysisRunIsCurrent?: boolean;
    } = {},
  ): SimulatedConversation {
    const matchId = options.matchId ?? String(match.id);
    const analysisRunId = options.analysisRunId ?? String(this.seedEligibleRun(matchId).runId);
    const conversation: SimulatedConversation = {
      conversationId: newId(),
      matchId,
      analysisRunId,
      analysisRunIsCurrent: options.analysisRunIsCurrent ?? true,
      teamId: ids.team,
      state: 'active',
      participants: (options.participants ?? []).map((agentId) => ({ agentId, addedAt: now.toISOString() })),
      createdAt: now.toISOString(),
      version: 1,
      ownerId: String(members[options.ownerRole ?? 'Coach'].id),
    };
    this.agentConversations.push(conversation);
    return conversation;
  }

  /** The conversation's run is no longer the match's current run. */
  supersedeRun(conversationId: string): void {
    const found = this.agentConversations.find((c) => c.conversationId === conversationId);
    if (found) found.analysisRunIsCurrent = false;
  }

  /** Adds an invocation (and its turn) to a conversation; the agent becomes a participant. */
  seedAgentTurn(
    conversationId: string,
    options: { agentId?: string; question?: string; script?: AgentStep[]; advice?: string; lineage?: AgentLineageEntry[] } = {},
  ): SimulatedInvocation {
    const conversation = this.agentConversations.find((c) => c.conversationId === conversationId);
    if (!conversation) throw new Error(`Unknown conversation ${conversationId}`);
    const agentId = options.agentId ?? 'specialist-tactics';
    if (!conversation.participants.some((p) => p.agentId === agentId)) {
      conversation.participants.push({ agentId, addedAt: now.toISOString() });
      conversation.version += 1;
    }
    const question = options.question ?? 'How did we press?';
    const invocation: SimulatedInvocation = {
      invocationId: newId(),
      conversationId,
      agentId,
      question,
      submittedAt: new Date(now.getTime() + this.agentInvocations.size * 1000).toISOString(),
      attemptId: newId(),
      script: (options.script ?? this.agentScript).map((step) => ({ ...step })),
      stepIndex: 0,
      readsInStep: 0,
      advice: options.advice ?? `Simulated advice for: ${question}`,
      lineage: options.lineage,
    };
    this.agentInvocations.set(invocation.invocationId, invocation);
    return invocation;
  }

  private agentStep(invocation: SimulatedInvocation): AgentStep {
    return invocation.script[invocation.stepIndex] ?? { state: 'succeeded' };
  }

  private agentTurn(invocation: SimulatedInvocation) {
    const step = this.agentStep(invocation);
    const terminal = TERMINAL_AGENT_STATES.has(step.state);
    return {
      invocationId: invocation.invocationId,
      agentId: invocation.agentId,
      question: invocation.question,
      state: step.state,
      submittedAt: invocation.submittedAt,
      endedAt: terminal ? new Date(Date.parse(invocation.submittedAt) + 60_000).toISOString() : null,
      failureReason: step.state === 'failed' ? (step.failureReason ?? 'execution-failed') : null,
      advice: step.state === 'succeeded' ? invocation.advice : null,
    };
  }

  private agentLineage(invocation: SimulatedInvocation) {
    const step = this.agentStep(invocation);
    const kindOf = (s: AgentStep, index: number) => {
      if (s.state === 'succeeded') return 'completed';
      if (s.state === 'failed') {
        if (s.failureReason === 'scope-lost') return 'scope-lost';
        return s.failureReason === 'attempts-exhausted' ? 'exhausted' : 'failed';
      }
      return index === 0 ? 'submitted' : 'claimed';
    };
    const entries: AgentLineageEntry[] =
      invocation.lineage ??
      invocation.script.slice(0, invocation.stepIndex + 1).map((s, index) => {
        const kind = kindOf(s, index);
        return {
          sequence: index + 1,
          kind,
          attemptId: kind === 'submitted' ? null : invocation.attemptId,
          attemptNumber: kind === 'submitted' ? null : 1,
          recordedAt: new Date(Date.parse(invocation.submittedAt) + index * 1000).toISOString(),
          details: kind === 'failed' ? { reasonCode: s.failureReason ?? 'execution-failed' } : {},
        };
      });
    return {
      invocationId: invocation.invocationId,
      policyVersion: '1',
      entries,
      advice:
        step.state === 'succeeded'
          ? {
              attemptId: invocation.attemptId,
              advice: invocation.advice,
              acceptedAt: new Date(Date.parse(invocation.submittedAt) + 60_000).toISOString(),
              executionReferences: {
                runtime: 'simulated-runtime',
                modelProvider: 'simulated-provider',
                model: 'simulated-model',
                deployment: 'simulated-deployment',
                promptRef: 'prompt/1',
                toolPolicyRef: 'tool-policy/1',
                safetyPolicyRef: 'safety-policy/1',
              },
            }
          : null,
    };
  }

  private publicConversation(conversation: SimulatedConversation) {
    const { ownerId: _owner, ...rest } = conversation;
    return rest;
  }

  private async handleAgents(route: Route, method: string, path: string, role: SimulatedRole): Promise<void> {
    const request = route.request();
    const found = AGENT_ROUTES.map(([operation, verb, pattern]) => ({ operation, verb, hit: pattern.exec(path) })).find(
      (candidate) => candidate.verb === method && candidate.hit,
    );
    if (!found?.hit) return route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify(problem(404, 'Not found')) });
    const { operation } = found;
    const id = found.hit[1] ?? '';
    const memberId = String(members[role].id);
    const headers = request.headers();
    const url = new URL(request.url());

    const reply = (status: number, body: unknown, extra: Record<string, string> = {}) =>
      route.fulfill({
        status,
        contentType: status >= 400 ? 'application/problem+json' : 'application/json',
        headers: extra,
        body: JSON.stringify(body),
      });
    const refuse = (refusal: AgentRefusal) =>
      reply(
        refusal.status,
        problem(refusal.status, refusal.code ?? `Refused ${refusal.status}`, {
          ...(refusal.code ? { code: refusal.code } : {}),
          ...(refusal.errors ? { errors: refusal.errors } : {}),
        }),
        refusal.retryAfterSeconds === undefined ? {} : { 'Retry-After': String(refusal.retryAfterSeconds) },
      );
    const conditional = (etag: string, payload: unknown, extra: Record<string, string> = {}) =>
      headers['if-none-match'] === etag
        ? route.fulfill({ status: 304, headers: { ETag: etag, ...extra } })
        : reply(200, payload, { ETag: etag, ...extra });

    const scripted = this.agentRefusalQueue.get(operation)?.shift();
    if (scripted) return refuse(scripted);
    if (role === 'Registrar') return refuse(agentRefusals.forbidden);
    const canWrite = role !== 'Viewer';

    let body: Record<string, unknown> = {};
    try {
      body = (request.postDataJSON() ?? {}) as Record<string, unknown>;
    } catch {
      body = {};
    }
    const own = (conversationId: string) =>
      this.agentConversations.find((c) => c.conversationId === conversationId && c.ownerId === memberId);
    const ownInvocation = (invocationId: string) => {
      const invocation = this.agentInvocations.get(invocationId);
      return invocation && own(invocation.conversationId) ? invocation : undefined;
    };
    const conversationEtag = (c: SimulatedConversation) => `"${c.version}-${c.analysisRunIsCurrent ? 1 : 0}"`;
    const conversationHeaders = (c: SimulatedConversation) => ({
      ETag: conversationEtag(c),
      Location: `/api/v1/agent-conversations/${c.conversationId}`,
    });
    const unfinished = (invocation: SimulatedInvocation) => !TERMINAL_AGENT_STATES.has(this.agentStep(invocation).state);

    switch (operation) {
      case 'listAgents':
        return reply(200, { agents: this.agents.map((a) => ({ ...a, enabledVersion: a.enabledVersion ?? '0.1.0' })) });
      case 'listConversations': {
        if (!this.matches.some((m) => m.id === id)) return refuse(agentRefusals.notFound);
        const items = this.agentConversations.filter((c) => c.matchId === id && c.ownerId === memberId).reverse();
        return reply(200, { conversations: items.map((c) => this.publicConversation(c)) });
      }
      case 'createConversation': {
        if (!this.matches.some((m) => m.id === id)) return refuse(agentRefusals.notFound);
        if (!canWrite) return refuse(agentRefusals.forbidden);
        const key = headers['idempotency-key'];
        if (!key) return refuse(agentRefusals.keyMissing);
        const analysisRunId = String(body.analysisRunId ?? '');
        const fingerprint = JSON.stringify(['create', id, analysisRunId]);
        const prior = this.idempotencyRecords.get(`${memberId}:${key}`);
        if (prior) {
          if (prior.fingerprint !== fingerprint) return refuse(agentRefusals.keyReused);
          const replayed = own(prior.resourceId);
          if (replayed) return reply(200, this.publicConversation(replayed), conversationHeaders(replayed));
        }
        if (!analysisRunId) return refuse({ status: 400, errors: [{ field: 'analysisRunId', code: 'required' }] });
        const run = this.analysisRuns.find((r) => String(r.runId) === analysisRunId && r.matchId === id);
        if (!run || !this.eligibleRunStates.has(String(run.state))) return refuse(agentRefusals.runNotEligible);
        const created = this.seedAgentConversation({ matchId: id, analysisRunId, ownerRole: role });
        this.idempotencyRecords.set(`${memberId}:${key}`, { fingerprint, resourceId: created.conversationId });
        if (this.droppedResponses.delete('createConversation')) return route.abort('connectionfailed');
        return reply(201, this.publicConversation(created), conversationHeaders(created));
      }
      case 'getConversation': {
        const conversation = own(id);
        if (!conversation) return refuse(agentRefusals.notFound);
        return conditional(conversationEtag(conversation), this.publicConversation(conversation));
      }
      case 'addParticipant': {
        const conversation = own(id);
        if (!conversation) return refuse(agentRefusals.notFound);
        if (!canWrite) return refuse(agentRefusals.forbidden);
        const ifMatch = headers['if-match'];
        if (!ifMatch) return refuse(agentRefusals.preconditionRequired);
        if (ifMatch !== `"${conversation.version}"`) return refuse(agentRefusals.staleVersion);
        const agentId = typeof body.agentId === 'string' ? body.agentId.trim() : '';
        if (!agentId) return refuse({ status: 400, errors: [{ field: 'agentId', code: 'required' }] });
        if (!this.agents.some((a) => a.agentId === agentId)) return refuse(agentRefusals.agentNotAvailable);
        if (conversation.participants.some((p) => p.agentId === agentId)) return refuse(agentRefusals.participantExists);
        conversation.participants.push({ agentId, addedAt: new Date().toISOString() });
        conversation.version += 1;
        return reply(200, this.publicConversation(conversation), { ETag: conversationEtag(conversation) });
      }
      case 'submit': {
        const conversation = own(id);
        if (!conversation) return refuse(agentRefusals.notFound);
        if (!canWrite) return refuse(agentRefusals.forbidden);
        const key = headers['idempotency-key'];
        if (!key) return refuse(agentRefusals.keyMissing);
        const agentId = String(body.agentId ?? '');
        const question = String(body.question ?? '');
        const accepted = (invocation: SimulatedInvocation) =>
          reply(
            202,
            {
              conversationId: id,
              invocationId: invocation.invocationId,
              statusLocation: `/api/v1/agent-invocations/${invocation.invocationId}`,
              retryAfterSeconds: this.statusRetryAfterSeconds,
            },
            {
              Location: `/api/v1/agent-invocations/${invocation.invocationId}`,
              'Retry-After': String(this.statusRetryAfterSeconds),
            },
          );
        const fingerprint = JSON.stringify(['submit', id, agentId, question]);
        const prior = this.idempotencyRecords.get(`${memberId}:${key}`);
        if (prior) {
          if (prior.fingerprint !== fingerprint) return refuse(agentRefusals.keyReused);
          const replayed = this.agentInvocations.get(prior.resourceId);
          if (replayed) return accepted(replayed);
        }
        if (!question.trim()) return refuse(agentRefusals.invalidQuestion);
        if (question.length > MAX_QUESTION_LENGTH) return refuse(agentRefusals.questionTooLong);
        if (!this.agents.some((a) => a.agentId === agentId)) return refuse(agentRefusals.agentNotAvailable);
        if (!conversation.participants.some((p) => p.agentId === agentId)) return refuse(agentRefusals.agentNotParticipant);
        const open = [...this.agentInvocations.values()].filter((i) => own(i.conversationId) && unfinished(i)).length;
        if (open >= this.maxUnfinishedInvocations) return refuse(agentRefusals.tooManyUnfinished);
        const invocation = this.seedAgentTurn(id, { agentId, question });
        this.idempotencyRecords.set(`${memberId}:${key}`, { fingerprint, resourceId: invocation.invocationId });
        if (this.droppedResponses.delete('submit')) return route.abort('connectionfailed');
        return accepted(invocation);
      }
      case 'listTurns': {
        const conversation = own(id);
        if (!conversation) return refuse(agentRefusals.notFound);
        const offset = Math.max(0, Number.parseInt(url.searchParams.get('cursor') ?? '0', 10) || 0);
        const limit = Math.min(100, Math.max(1, Number.parseInt(url.searchParams.get('limit') ?? '20', 10) || 20));
        const all = [...this.agentInvocations.values()].filter((i) => i.conversationId === id);
        const page = all.slice(offset, offset + limit);
        const digest = createHash('sha256')
          .update(page.map((i) => `${i.invocationId}:${i.stepIndex}`).join('|'))
          .digest('hex')
          .slice(0, 16);
        return conditional(`"turns-${offset}-${limit}-${all.length}-${digest}"`, {
          turns: page.map((i) => this.agentTurn(i)),
          nextCursor: offset + limit < all.length ? String(offset + limit) : null,
        });
      }
      case 'getInvocation': {
        const invocation = ownInvocation(id);
        if (!invocation) return refuse(agentRefusals.notFound);
        const hold = Math.max(1, this.agentStep(invocation).holdReads ?? 1);
        if (invocation.readsInStep >= hold && invocation.stepIndex < invocation.script.length - 1) {
          invocation.stepIndex += 1;
          invocation.readsInStep = 0;
        }
        invocation.readsInStep += 1;
        const step = this.agentStep(invocation);
        const open = unfinished(invocation);
        return conditional(
          `"inv-${invocation.invocationId}-${invocation.stepIndex}"`,
          {
            ...this.agentTurn(invocation),
            conversationId: invocation.conversationId,
            route: 'specialist',
            attemptCount: step.attemptCount ?? (step.state === 'accepted' ? 0 : 1),
            retryAfterSeconds: open ? this.statusRetryAfterSeconds : null,
          },
          open ? { 'Retry-After': String(this.statusRetryAfterSeconds) } : {},
        );
      }
      case 'getLineage': {
        const invocation = ownInvocation(id);
        if (!invocation) return refuse(agentRefusals.notFound);
        return conditional(
          `"lin-${invocation.invocationId}-${invocation.stepIndex}-${invocation.lineage?.length ?? 0}"`,
          this.agentLineage(invocation),
        );
      }
    }
  }

  /** When set, every simulated API response carries `SocAlytics-Minimum-Web-Version` with this value. */
  minimumWebVersion: string | null = null;

  private async handle(route: Route): Promise<void> {
    if (this.minimumWebVersion) {
      const version = this.minimumWebVersion;
      const fulfill = route.fulfill.bind(route);
      route.fulfill = (options) => fulfill({ ...options, headers: { ...options?.headers, 'SocAlytics-Minimum-Web-Version': version } });
    }
    const request = route.request();
    const path = new URL(request.url()).pathname.replace(/^\/api\/v1/, '');
    const method = request.method();
    this.requests.push({ method, path, headers: request.headers() });
    if (request.postDataBuffer()) this.bodyContentTypes.push(request.headers()['content-type'] ?? '');

    const override = this.overrides.get(`${method} ${path}`);
    if (override) {
      let body: unknown = null;
      try {
        body = request.postDataJSON();
      } catch {
        body = null;
      }
      await override(route, body);
      return;
    }

    const json = (status: number, body: unknown) =>
      route.fulfill({
        status,
        contentType: status >= 400 ? 'application/problem+json' : 'application/json',
        body: JSON.stringify(body),
      });
    const unauthorized = () => json(401, problem(401, 'Unauthorized'));

    if (method === 'POST' && path === '/session') {
      if (!this.current) {
        return json(401, problem(401, 'Invalid credentials'));
      }
      return json(200, sessionFor(this.current.role, this.current.passwordChangeRequired));
    }
    if (method === 'GET' && path === '/identity-providers') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: { 'Cache-Control': 'no-store' },
        body: JSON.stringify({ providers: this.providers }),
      });
    }
    if (method === 'POST' && path === '/session/external') {
      if (this.providerUnavailable) return json(503, problem(503, 'Provider unavailable', { code: 'provider-unavailable' }));
      const started = (route.request().postDataJSON?.() ?? {}) as Record<string, unknown>;
      this.externalStarts.push(started);
      if (typeof started.linkCredential === 'string' && !this.linkCodes.has(started.linkCredential)) {
        return json(400, problem(400, 'Sign-in failed', { code: 'sign-in-failed' }));
      }
      return json(200, { authorizationUrl: `${SIMULATED_PROVIDER_ORIGIN}/authorize?state=simulated` });
    }
    if (method === 'POST' && path === '/credentials/redeem') {
      return route.fulfill({ status: 204 });
    }
    if (method === 'POST' && path === '/password-recovery/requests') {
      const address = String((request.postDataJSON() as { address?: unknown } | null)?.address ?? '').trim();
      if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(address)) {
        return json(400, problem(400, 'Validation failed', { errors: [{ field: 'address', code: address ? 'invalid' : 'required' }] }));
      }
      this.recoveryRequests.push(address);
      return route.fulfill({ status: 202, headers: { 'Cache-Control': 'no-store' } });
    }
    if (method === 'POST' && (path === '/password-recovery/checks' || path === '/password-recovery/completions')) {
      const sent = (request.postDataJSON() ?? {}) as { token?: string; newPassword?: string };
      const accountName = sent.token ? this.recoveryTokens.get(sent.token) : undefined;
      if (!accountName) {
        return json(400, problem(400, 'Password recovery invalid', { code: 'password-recovery-invalid' }));
      }
      if (path === '/password-recovery/checks') return json(200, { accountName });
      if (!sent.newPassword) {
        return json(400, problem(400, 'Validation failed', { errors: [{ field: 'newPassword', code: 'required' }] }));
      }
      this.recoveryTokens.delete(sent.token as string);
      this.recoveryCompletions.push({ accountName, newPassword: sent.newPassword });
      return route.fulfill({ status: 204 });
    }
    if (!this.current) {
      return unauthorized();
    }
    const { role, passwordChangeRequired } = this.current;

    if (path === '/session' && method === 'GET') {
      return json(200, sessionFor(role, passwordChangeRequired));
    }
    if (path === '/session' && method === 'DELETE') {
      this.current = null;
      return route.fulfill({ status: 204 });
    }
    if (path === '/me' && method === 'GET') {
      return json(200, { ...members[role], passwordChangeRequired });
    }
    if (path === '/me/password' && method === 'POST') {
      this.current = { role, passwordChangeRequired: false };
      return route.fulfill({ status: 204 });
    }
    if (passwordChangeRequired) {
      return json(403, problem(403, 'Password change required', { code: 'password_change_required' }));
    }
    if (AGENT_ROUTES.some(([, verb, pattern]) => verb === method && pattern.test(path))) {
      return this.handleAgents(route, method, path, role);
    }
    if (path.startsWith('/agent-catalog/')) {
      return this.handleAgentCatalog(route, method, path, role, json);
    }
    if (path.startsWith('/analyst-manager-registrations')) {
      return this.handleRegistrations(route, method, path, role, json);
    }
    if (path.startsWith('/operations/')) {
      return this.handleOperations(method, path, new URL(request.url()).searchParams, role, json);
    }
    if (method === 'GET' && path.startsWith('/analysis/runs/')) {
      return this.handleAnalysisRead(route, path, role, json);
    }
    if (path.startsWith('/registry/')) {
      return this.handleRegistry(route, method, path, role, json);
    }
    if (method === 'GET') {
      if (path === '/me/notification-preferences') {
        const items = [{ type: 'email-confirmation', enabled: true, accountMessage: true }];
        if (role === 'ClubAdmin') items.push({ type: 'registration-pending', enabled: true, accountMessage: false });
        return json(200, { items });
      }
      if (path === '/club') return json(200, club);
      if (path === '/seasons') {
        return json(200, { items: [season], continuationToken: null } satisfies SeasonPageDto);
      }
      if (path === '/teams') {
        return json(200, { items: [team], continuationToken: null } satisfies TeamPageDto);
      }
      const found = this.handleMatchReads(path, json);
      if (found) return found;
      if (path === '/members') {
        if (role !== 'ClubAdmin') return json(403, problem(403, 'Forbidden'));
        return json(200, {
          items: [members.ClubAdmin, members.Coach, members.Viewer].map(memberListEntry),
          continuationToken: null,
        } satisfies MemberPageDto);
      }
    }
    if (method === 'POST' || method === 'PUT') {
      if (role === 'Viewer') return json(403, problem(403, 'Forbidden'));
      const cancellation = /^\/analysis\/runs\/([^/]+)\/cancellation$/.exec(path);
      if (cancellation) {
        return this.handleRunCancellation(cancellation[1], request.headers()['if-match'], request.postDataJSON?.() ?? null, role, json);
      }
      const result = this.handleMatchWrites(method, path, request.postDataJSON?.() ?? null, json);
      if (result) return result;
    }
    return json(404, problem(404, 'Not found'));
  }

  /** The five workflow operations: Club Admin only, filters and opaque continuation tokens like the platform. */
  private handleOperations(
    method: string,
    path: string,
    query: URLSearchParams,
    role: SimulatedRole,
    json: (status: number, body: unknown) => Promise<void>,
  ) {
    const operation = this.operationFor(method, path);
    if (!operation) return json(404, problem(404, 'Not found'));
    if (operation === 'reconcileAnalysisPublication') this.reconciliationRequests += 1;
    const mode = this.operationsSwitches.get(operation);
    if (role !== 'ClubAdmin' || mode === 'forbidden') return json(403, problem(403, 'Forbidden'));
    if (mode === 'unavailable') return json(503, problem(503, 'Service unavailable'));
    const invalidToken = () =>
      json(400, problem(400, 'Validation failed', { errors: [{ field: 'continuationToken', code: 'invalid' }] }));
    const filterError = (field: string) =>
      json(400, problem(400, 'Validation failed', { errors: [{ field, code: 'invalid' }] }));

    if (operation === 'getAnalysisOperationalSummary') return json(200, this.operationalSummary);
    if (operation === 'reconcileAnalysisPublication') return json(200, this.reconciliationResult);
    if (operation === 'getOutboxMessage') {
      const found = this.outboxMessages.find((m) => m.messageId === path.split('/').pop());
      return found ? json(200, found) : json(404, problem(404, 'Not found'));
    }

    const token = query.get('continuationToken');
    if (mode === 'invalid-token' && token !== null) return invalidToken();
    let items: (SimulatedOutboxMessage | SimulatedNotificationReceipt)[];
    let filterKey: string;
    if (operation === 'listOutboxMessages') {
      const state = query.get('state');
      const messageType = query.get('messageType');
      if (state !== null && !(OUTBOX_STATES as readonly string[]).includes(state)) return filterError('state');
      if (messageType !== null && !(OUTBOX_MESSAGE_TYPES as readonly string[]).includes(messageType)) {
        return filterError('messageType');
      }
      items = this.outboxMessages.filter((m) => (!state || m.state === state) && (!messageType || m.messageType === messageType));
      filterKey = `${state ?? ''}|${messageType ?? ''}`;
    } else {
      const outcome = query.get('outcome');
      if (outcome !== null && !(RECEIPT_OUTCOMES as readonly string[]).includes(outcome)) return filterError('outcome');
      items = this.notificationReceipts.filter((r) => !outcome || r.outcome === outcome);
      filterKey = outcome ?? '';
    }

    let offset = 0;
    if (token !== null) {
      const match = /^simulated-page:(\d+):(.*)$/.exec(token);
      if (!match || match[2] !== `${operation}:${filterKey}` || Number(match[1]) > items.length) return invalidToken();
      offset = Number(match[1]);
    }
    const end = offset + this.operationsPageSize;
    return json(200, {
      items: items.slice(offset, end),
      continuationToken: end < items.length ? `simulated-page:${end}:${operation}:${filterKey}` : null,
    });
  }

  private operationFor(method: string, path: string): OperationsOperation | null {
    if (method === 'GET' && path === '/operations/analysis/summary') return 'getAnalysisOperationalSummary';
    if (method === 'GET' && path === '/operations/outbox-messages') return 'listOutboxMessages';
    if (method === 'GET' && /^\/operations\/outbox-messages\/[^/]+$/.test(path)) return 'getOutboxMessage';
    if (method === 'GET' && path === '/operations/analysis/notification-receipts') return 'listNotificationReceipts';
    if (method === 'POST' && path === '/operations/analysis/reconciliations') return 'reconcileAnalysisPublication';
    return null;
  }

  /** Cancels an unfinished run like the platform: `If-Match` must carry the current version, terminal runs answer 409. */
  private handleRunCancellation(
    runId: string,
    ifMatch: string | undefined,
    body: { reason?: string | null } | null,
    role: SimulatedRole,
    json: (status: number, body: unknown) => Promise<void>,
  ) {
    const run = this.analysisRuns.find((r) => r.runId === runId);
    if (!run) return json(404, problem(404, 'Not found'));
    if (!ifMatch) return json(428, problem(428, 'Precondition required'));
    if (ifMatch.replace(/"/g, '') !== String(run.version)) return json(412, problem(412, 'Precondition failed'));
    if (run.state !== 'Requested' && run.state !== 'Running') {
      return json(409, problem(409, 'Run already ended', { code: 'analysis-run-terminal' }));
    }
    const member = members[role];
    run.state = 'Cancelled';
    run.terminatedAt = new Date();
    run.cancelledBy = { memberId: member.id, accountName: member.accountName };
    run.cancellationReason = body?.reason ?? null;
    run.cancellationCause = 'member';
    run.version = (run.version ?? 0) + 1;
    return json(200, run);
  }

  /** Marks `runId` as superseded by `byRunId`, as the platform does when a run of a newer recording set starts; an unfinished run is cancelled without a member. */
  supersedeAnalysisRun(runId: string, byRunId: string, at = new Date()): void {
    const run = this.analysisRuns.find((r) => r.runId === runId);
    const by = this.analysisRuns.find((r) => r.runId === byRunId);
    const set = this.recordingSetVersions.find((s) => s.id === by?.recordingSetVersionId);
    if (!run || !by || !set) throw new Error('Unknown run or recording set for supersession');
    run.isCurrent = false;
    by.isCurrent = true;
    run.supersededBy = { runId: by.runId, recordingSetVersionId: set.id, recordingSetFinalizedAt: set.finalizedAt };
    run.supersededAt = at;
    if (run.state === 'Requested' || run.state === 'Running') {
      run.state = 'Cancelled';
      run.terminatedAt = at;
      run.cancellationCause = 'superseded';
    }
    run.version = (run.version ?? 0) + 1;
  }

  private handleMatchReads(path: string, json: (status: number, body: unknown) => Promise<void>) {
    const matchId = /^\/matches\/([^/]+)/.exec(path)?.[1];
    if (/^\/teams\/[^/]+\/matches$/.test(path)) return json(200, { items: this.matches, continuationToken: null });
    if (!matchId) return null;
    const known = this.matches.find((m) => m.id === matchId);
    if (!known) return json(404, problem(404, 'Not found'));
    if (path === `/matches/${matchId}`) return json(200, known);
    if (path === `/matches/${matchId}/recording-lineage`) {
      return json(200, {
        matchId,
        teamId: known.teamId,
        recordingVersions: this.recordingVersions.filter((v) => v.matchId === matchId),
        timelineMappings: this.timelineMappings.filter((m) => m.matchId === matchId),
        recordingSetVersions: this.recordingSetVersions.filter((v) => v.matchId === matchId),
      });
    }
    if (path === `/matches/${matchId}/analysis-runs`) {
      return json(200, { items: this.analysisRuns.filter((r) => r.matchId === matchId), continuationToken: null });
    }
    const upload = /^\/matches\/[^/]+\/upload-sessions\/([^/]+)$/.exec(path)?.[1];
    if (upload) {
      const found = this.uploadSessions.find((u) => u.id === upload);
      return found ? json(200, this.publicSession(found)) : json(404, problem(404, 'Not found'));
    }
    return null;
  }

  private publicSession(session: UploadSessionDto & { partDigests: string[] }): UploadSessionDto {
    const { partDigests: _digests, ...rest } = session;
    return rest;
  }

  private grantsFor(session: UploadSessionDto & { partDigests: string[] }, partNumbers: number[]) {
    return {
      expiresAt: new Date(Date.now() + 3_600_000),
      parts: partNumbers.map((partNumber) => ({
        partNumber,
        method: 'PUT',
        url: `${FAKE_STORAGE_ORIGIN}/bucket/${session.id}/${partNumber}?partNumber=${partNumber}&X-Amz-Signature=simulated`,
        requiredHeaders: { 'x-amz-checksum-sha256': checksumHeader(session.partDigests[partNumber - 1]) },
      })),
    };
  }

  private handleMatchWrites(
    method: string,
    path: string,
    body: any,
    json: (status: number, body: unknown) => Promise<void>,
  ) {
    const matchPath = /^\/matches\/([^/]+)\/(.+)$/.exec(path);
    if (method === 'POST' && /^\/teams\/[^/]+\/matches$/.test(path)) {
      const created: MatchDto = {
        id: newId(),
        teamId: ids.team,
        opponent: { name: body?.opponent?.name ?? '' },
        kickoffAt: new Date(body?.kickoffAt ?? now),
        homeAway: body?.homeAway ?? 'home',
        competition: body?.competition ?? null,
        createdAt: now,
        version: 1,
      };
      this.matches.push(created);
      return json(201, created);
    }
    if (!matchPath) return null;
    const [, matchId, rest] = matchPath;
    const known = this.matches.find((m) => m.id === matchId);
    if (!known) return json(404, problem(404, 'Not found'));

    if (method === 'POST' && rest === 'upload-sessions') {
      const total = Number(body?.totalSizeBytes ?? 0);
      const partSize = Number(body?.partSizeBytes ?? 1);
      const partCount = Math.ceil(total / partSize);
      const session = {
        id: newId(),
        matchId,
        teamId: known.teamId,
        state: 'pending',
        recording: body?.recording ?? { displayName: null, description: null, contentType: null },
        totalSizeBytes: total,
        partSizeBytes: partSize,
        partCount,
        contentDigest: compositeDigest(partSize, body?.partDigests ?? []),
        createdAt: now,
        expiresAt: later(24),
        completedAt: null,
        expiredAt: null,
        version: 1,
        partDigests: body?.partDigests ?? [],
      } satisfies UploadSessionDto & { partDigests: string[] };
      this.uploadSessions.push(session);
      const grants = this.grantsFor(session, Array.from({ length: Math.min(partCount, 8) }, (_, i) => i + 1));
      return json(201, { session: this.publicSession(session), grants });
    }
    const grantsMatch = /^upload-sessions\/([^/]+)\/grants$/.exec(rest ?? '');
    if (method === 'POST' && grantsMatch) {
      const session = this.uploadSessions.find((u) => u.id === grantsMatch[1]);
      if (!session) return json(404, problem(404, 'Not found'));
      return json(200, this.grantsFor(session, (body?.partNumbers as number[] | undefined) ?? []));
    }
    const completion = /^upload-sessions\/([^/]+)\/completion$/.exec(rest ?? '');
    if (method === 'POST' && completion) {
      const session = this.uploadSessions.find((u) => u.id === completion[1]);
      if (!session) return json(404, problem(404, 'Not found'));
      session.state = 'completed';
      session.completedAt = now;
      const recordingVersion: RecordingVersionDto = {
        id: newId(),
        matchId,
        teamId: known.teamId,
        uploadSessionId: session.id,
        recording: session.recording,
        totalSizeBytes: session.totalSizeBytes,
        partSizeBytes: session.partSizeBytes,
        partCount: session.partCount,
        contentDigest: session.contentDigest,
        createdAt: now,
      };
      const timelineMapping: TimelineMappingDto = {
        id: newId(),
        recordingVersionId: recordingVersion.id,
        matchId,
        spans: body?.timelineMapping?.spans ?? [],
        digest: `sha256:${'1'.repeat(64)}`,
        createdAt: now,
      };
      this.recordingVersions.push(recordingVersion);
      this.timelineMappings.push(timelineMapping);
      return json(201, { recordingVersion, timelineMapping });
    }
    if (method === 'POST' && rest === 'recording-set-versions') {
      const members = (body?.members ?? []) as { recordingVersionId: string; timelineMappingId: string }[];
      const set: RecordingSetVersionDto = {
        id: newId(),
        matchId,
        teamId: known.teamId,
        finalizedAt: now,
        members: members.map((m, position) => ({
          position,
          recordingVersionId: m.recordingVersionId,
          recordingContentDigest: this.recordingVersions.find((v) => v.id === m.recordingVersionId)?.contentDigest ?? '',
          timelineMappingId: m.timelineMappingId,
          timelineMappingDigest: this.timelineMappings.find((t) => t.id === m.timelineMappingId)?.digest ?? '',
        })),
      };
      this.recordingSetVersions.push(set);
      this.analysisRuns.push({ ...analysisRun, runId: newId(), matchId, recordingSetVersionId: set.id });
      return json(201, set);
    }
    return null;
  }
}
