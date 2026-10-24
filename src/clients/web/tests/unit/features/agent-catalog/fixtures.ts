import { vi } from 'vitest';
import type { AgentCatalogAdapter } from '../../../../src/platform/adapters/agentCatalogAdapter';
import type {
  AgentVersion,
  AgentVersionSummary,
  ArtifactKind,
  ArtifactRecord,
  ArtifactRef,
  ArtifactSummary,
  BaselineAgent,
  CatalogBaseline,
  Distance,
  Enablement,
  MemberRef,
} from '../../../../src/platform/models/agentCatalog';

export const SEED: MemberRef = { id: 'system:seed', kind: 'seed' };
export const ADMIN: MemberRef = { id: '00000000-0000-4000-8000-000000000021', displayName: 'ada', kind: 'member' };
export const COACH: MemberRef = { id: '00000000-0000-4000-8000-000000000022', displayName: 'grace', kind: 'member' };

const at = (day: number) => new Date(Date.UTC(2026, 9, day, 12));
const digest = (char: string) => `sha256:${char.repeat(64)}`;

export const TOOLS = ['evidence.read', 'run.summary', 'match.lookup'];

export const coachAgent: BaselineAgent = {
  agentId: 'coach-lead',
  group: 'coach',
  domain: 'coaching',
  displayName: 'Lead Coach Agent',
  toolBaseline: ['evidence.read', 'run.summary'],
};
export const tacticsAgent: BaselineAgent = {
  agentId: 'specialist-tactics',
  group: 'specialist',
  domain: 'tactics',
  displayName: 'Tactics Specialist',
  toolBaseline: ['evidence.read', 'run.summary'],
};
export const videoAgent: BaselineAgent = {
  agentId: 'specialist-video-analysis',
  group: 'specialist',
  domain: 'video-analysis',
  displayName: 'Video Analysis Specialist',
  toolBaseline: ['evidence.read'],
};
export const setPiecesAgent: BaselineAgent = {
  agentId: 'specialist-set-pieces',
  group: 'specialist',
  domain: 'set-pieces',
  displayName: 'Set Pieces Specialist',
  toolBaseline: ['evidence.read', 'match.lookup'],
};

export const baseline: CatalogBaseline = {
  version: '1.0.0',
  tools: TOOLS,
  agents: [coachAgent, tacticsAgent, videoAgent, setPiecesAgent],
};

export const promptRecord: ArtifactRecord<'prompt-template'> = {
  kind: 'prompt-template',
  id: 'specialist-prompt',
  version: '0.1.0',
  digest: digest('1'),
  registeredBy: SEED,
  registeredAt: at(10),
  document: {
    group: 'specialist',
    responseContract: 'agent-response/1',
    text: 'You are {{display_name}}.\n<b>Never</b> interpret markup: {{evidence_context}}',
    slots: ['agent_id', 'display_name', 'evidence_context'],
  },
};

export const modelRecord: ArtifactRecord<'model-selection'> = {
  kind: 'model-selection',
  id: 'simulated-model',
  version: '0.1.0',
  digest: digest('2'),
  registeredBy: SEED,
  registeredAt: at(10),
  document: {
    provider: 'simulated',
    model: 'simulated-1',
    deployment: 'development',
    parameters: { temperature: 0.2, topP: 1, maxOutputTokens: 1024, reasoningEffort: 'low' },
  },
};

export const toolRecord: ArtifactRecord<'tool-policy'> = {
  kind: 'tool-policy',
  id: 'specialist-tools',
  version: '0.1.0',
  digest: digest('3'),
  registeredBy: SEED,
  registeredAt: at(10),
  document: { tools: ['evidence.read', 'run.summary'] },
};

export const safetyRecord: ArtifactRecord<'safety-policy'> = {
  kind: 'safety-policy',
  id: 'specialist-safety',
  version: '0.1.0',
  digest: digest('4'),
  registeredBy: ADMIN,
  registeredAt: at(11),
  document: {
    group: 'specialist',
    routes: ['user-to-specialist', 'coach-to-specialist'],
    hardBoundaries: ['Never reveal other teams\u2019 data.'],
    escalationCategories: ['safety', 'privacy'],
    refusalCategories: ['out-of-scope'],
    rules: ['advice-only'],
  },
};

export const dimensionSetRecord: ArtifactRecord<'dimension-set'> = {
  kind: 'dimension-set',
  id: 'football-dimensions',
  version: '1.0.0',
  digest: digest('5'),
  registeredBy: ADMIN,
  registeredAt: at(11),
  document: {
    dimensions: [
      { id: 'pressing', meaning: 'Intensity of pressing', acceptedEvidence: ['pressing-events'] },
      { id: 'possession', meaning: 'Preference for keeping the ball', acceptedEvidence: ['possession-share'] },
    ],
  },
};

export const distanceWithoutReview: Distance = {
  agentId: 'coach-lead',
  agentVersion: '0.1.0',
  profileId: 'possession-first',
  profileVersion: '1.0.0',
  distance: 42,
  reviewRequired: false,
};
export const distanceWithReview: Distance = {
  agentId: 'coach-lead',
  agentVersion: '0.2.0',
  profileId: 'possession-first',
  profileVersion: '1.0.0',
  distance: 8,
  reviewRequired: true,
};
export const distances: Distance[] = [distanceWithoutReview, distanceWithReview];

export const profileRecord: ArtifactRecord<'philosophy-profile'> = {
  kind: 'philosophy-profile',
  id: 'possession-first',
  version: '1.0.0',
  digest: digest('6'),
  registeredBy: COACH,
  registeredAt: at(12),
  document: {
    dimensionSet: { id: dimensionSetRecord.id, version: dimensionSetRecord.version },
    weights: {
      pressing: { weight: 40, rationale: 'High press wins the ball early.', provenance: 'Club playbook' },
      possession: { weight: 60, rationale: 'Control the tempo.', provenance: 'Club playbook' },
    },
    author: 'grace',
    reviewers: { architecture: ['ada'], footballDomain: ['grace'] },
  },
  distances,
};

export const modifierRecord: ArtifactRecord<'modifier-policy'> = {
  kind: 'modifier-policy',
  id: 'coach-modifiers',
  version: '0.1.0',
  digest: digest('7'),
  registeredBy: COACH,
  registeredAt: at(12),
  document: {
    dimensionSetMajor: 1,
    perDimensionBound: 2,
    maxPositiveMovement: 5,
    contextCategories: ['score-state', 'match-period'],
    budgetNeutral: true,
  },
};

export const records = [
  promptRecord,
  modelRecord,
  toolRecord,
  safetyRecord,
  dimensionSetRecord,
  profileRecord,
  modifierRecord,
] as ArtifactRecord[];

export const artifactSummaries: ArtifactSummary[] = records.map(
  ({ kind, id, version, digest: d, registeredBy, registeredAt }) => ({ kind, id, version, digest: d, registeredBy, registeredAt }),
);

const ref = (record: ArtifactRecord): ArtifactRef => ({ kind: record.kind, id: record.id, version: record.version, digest: record.digest });

export const specialistArtifacts: ArtifactRef[] = [promptRecord, modelRecord, toolRecord, safetyRecord].map(ref);
export const coachArtifacts: ArtifactRef[] = [...specialistArtifacts, ref(profileRecord), ref(modifierRecord)];

const summaryOf = (version: AgentVersion): AgentVersionSummary => ({
  agentId: version.agentId,
  version: version.version,
  digest: version.digest,
  group: version.group,
  state: version.state,
  enabled: version.enabled,
  registeredAt: version.registeredAt,
});

export const approvedTactics: AgentVersion = {
  agentId: tacticsAgent.agentId,
  version: '0.1.0',
  digest: digest('a'),
  group: 'specialist',
  state: 'approved',
  enabled: true,
  registeredAt: at(13),
  displayName: tacticsAgent.displayName,
  disclosure: 'This advice is generated by an AI model and may be wrong; verify it before acting.',
  artifacts: specialistArtifacts,
  registeredBy: SEED,
  approvedBy: SEED,
  approvedAt: at(13),
  selfApproval: false,
  differentiationAcknowledged: false,
  etag: '"tactics-0.1.0-2"',
};

export const draftTactics: AgentVersion = {
  ...approvedTactics,
  version: '0.1.1',
  digest: digest('b'),
  state: 'draft',
  enabled: false,
  registeredBy: ADMIN,
  registeredAt: at(14),
  approvedBy: undefined,
  approvedAt: undefined,
  etag: '"tactics-0.1.1-1"',
};

export const retiredTactics: AgentVersion = {
  ...approvedTactics,
  version: '0.0.9',
  digest: digest('c'),
  state: 'retired',
  enabled: false,
  registeredAt: at(12),
  approvedBy: ADMIN,
  selfApproval: true,
  retiredBy: ADMIN,
  retiredAt: at(15),
  etag: '"tactics-0.0.9-3"',
};

export const approvedVideo: AgentVersion = {
  ...approvedTactics,
  agentId: videoAgent.agentId,
  digest: digest('d'),
  displayName: videoAgent.displayName,
  etag: '"video-0.1.0-2"',
};

export const draftCoach: AgentVersion = {
  ...approvedTactics,
  agentId: coachAgent.agentId,
  version: '0.2.0',
  digest: digest('e'),
  group: 'coach',
  state: 'draft',
  enabled: false,
  registeredBy: COACH,
  registeredAt: at(16),
  displayName: coachAgent.displayName,
  artifacts: coachArtifacts,
  approvedBy: undefined,
  approvedAt: undefined,
  distances,
  etag: '"coach-0.2.0-1"',
};

export const approvedCoach: AgentVersion = {
  ...draftCoach,
  version: '0.1.0',
  digest: digest('f'),
  state: 'approved',
  approvedBy: ADMIN,
  approvedAt: at(17),
  differentiationAcknowledged: true,
  distances: [distanceWithoutReview],
  etag: '"coach-0.1.0-2"',
};

export const agentVersions: AgentVersion[] = [approvedTactics, draftTactics, retiredTactics, approvedVideo, draftCoach, approvedCoach];
export const versionSummaries: AgentVersionSummary[] = agentVersions.map(summaryOf);

export const enablements: Enablement[] = [
  { agentId: tacticsAgent.agentId, enabledVersion: '0.1.0', changedBy: SEED, changedAt: at(13), etag: '"enablement-tactics-1"' },
  { agentId: videoAgent.agentId, enabledVersion: '0.1.0', changedBy: SEED, changedAt: at(13), etag: '"enablement-video-1"' },
  { agentId: setPiecesAgent.agentId, etag: '"enablement-set-pieces-0"' },
  { agentId: coachAgent.agentId, etag: '"enablement-coach-0"' },
];

export const availableAgents = [
  { agentId: tacticsAgent.agentId, version: '0.1.0' },
  { agentId: videoAgent.agentId, version: '0.1.0' },
];

export type FakeAgentCatalogAdapter = { [K in keyof AgentCatalogAdapter]: ReturnType<typeof vi.fn> & AgentCatalogAdapter[K] };

/** A fake adapter serving the sample data; override single methods per test. */
export function fakeAgentCatalogAdapter(overrides: Partial<AgentCatalogAdapter> = {}): FakeAgentCatalogAdapter {
  const notFound = () => new Error('not found');
  const fake: AgentCatalogAdapter = {
    getBaseline: vi.fn(async () => baseline),
    listAvailableAgents: vi.fn(async () => availableAgents),
    listEnablements: vi.fn(async () => enablements),
    setEnablement: vi.fn(async (agentId: string, version: string | null) => ({
      agentId,
      enabledVersion: version ?? undefined,
      changedBy: ADMIN,
      changedAt: at(18),
      etag: '"enablement-changed"',
    })),
    listAgentVersions: vi.fn(async (filter?: { agentId?: string; state?: string }) =>
      versionSummaries.filter((v) => (!filter?.agentId || v.agentId === filter.agentId) && (!filter?.state || v.state === filter.state)),
    ),
    getAgentVersion: vi.fn(async (agentId: string, version: string) => {
      const found = agentVersions.find((v) => v.agentId === agentId && v.version === version);
      if (!found) throw notFound();
      return found;
    }),
    registerAgentVersion: vi.fn(async () => ({ kind: 'created' as const, value: draftTactics })),
    approveAgentVersion: vi.fn(async () => ({ ...draftTactics, state: 'approved', approvedBy: ADMIN, approvedAt: at(18) })),
    retireAgentVersion: vi.fn(async () => ({ ...approvedTactics, state: 'retired', retiredBy: ADMIN, retiredAt: at(18) })),
    listArtifacts: vi.fn(async (kind: ArtifactKind, filter?: { id?: string }) =>
      artifactSummaries.filter((a) => a.kind === kind && (!filter?.id || a.id === filter.id)),
    ),
    getArtifact: vi.fn(async (kind: ArtifactKind, id: string, version: string) => {
      const found = records.find((r) => r.kind === kind && r.id === id && r.version === version);
      if (!found) throw notFound();
      return found;
    }) as AgentCatalogAdapter['getArtifact'],
    registerArtifact: vi.fn(async (kind: ArtifactKind, document: Record<string, unknown>) => {
      const found = records.find((r) => r.kind === kind) ?? records[0];
      return { kind: 'created' as const, value: { ...found, kind, document } };
    }) as AgentCatalogAdapter['registerArtifact'],
  };
  Object.assign(fake, overrides);
  return fake as FakeAgentCatalogAdapter;
}
