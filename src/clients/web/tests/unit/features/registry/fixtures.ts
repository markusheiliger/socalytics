import { vi } from 'vitest';
import type { RegistryAdapter } from '../../../../src/platform/adapters/registryAdapter';
import type {
  CapabilityDeclaration,
  DefaultWorkflow,
  ImageSummary,
  MemberRef,
  ProfileContent,
  ProfileVersion,
  WorkflowVersion,
} from '../../../../src/platform/models/registry';

export const SEED: MemberRef = { id: '', kind: 'seed' };
export const ADMIN: MemberRef = { id: '00000000-0000-4000-8000-000000000021', displayName: 'ada', kind: 'member' };

export const SEGMENT_DIGEST = `sha256:${'a'.repeat(64)}`;
export const MATCH_DIGEST = `sha256:${'b'.repeat(64)}`;
export const MODEL_DIGEST = `sha256:${'c'.repeat(64)}`;
export const PROFILE_DIGEST = `sha256:${'d'.repeat(64)}`;

const at = (day: number) => new Date(Date.UTC(2026, 9, day, 12));

export const segmentDeclaration: CapabilityDeclaration = {
  imageDigest: SEGMENT_DIGEST,
  repository: 'registry.example/analysts/ball-tracking',
  manifestDigest: `sha256:${'e'.repeat(64)}`,
  capability: { id: 'ball-tracking', version: '1.0.0', tier: 'low-level', executionScope: 'segment' },
  modelDigest: MODEL_DIGEST,
  source: 'registry.example/analysts/ball-tracking@' + SEGMENT_DIGEST,
  registeredAt: at(10),
  registeredBy: SEED,
  upstream: [],
  videoAccess: 'required',
  outputSchemaVersion: '1.0.0',
  compatibleProfiles: ['standard'],
  runtime: { constraints: ['linux/amd64'], accelerators: [] },
  resources: { cpu: '2', memory: '4Gi' },
  requirements: { minMaxAttempts: 2, minLeaseSeconds: 60, minStaleTimeoutSeconds: 120, minExecutionTimeoutSeconds: 600 },
  determinism: { deterministic: true, idempotent: true },
  manifestText: '{\n  "manifestVersion": "1.0.0"\n}',
};

export const matchDeclaration: CapabilityDeclaration = {
  ...segmentDeclaration,
  imageDigest: MATCH_DIGEST,
  repository: 'registry.example/analysts/possession',
  source: 'registry.example/analysts/possession@' + MATCH_DIGEST,
  registeredBy: ADMIN,
  registeredAt: at(11),
  capability: { id: 'possession', version: '2.1.0', tier: 'high-level', executionScope: 'match' },
  upstream: [{ capabilityId: 'ball-tracking', capabilityVersion: '1.0.0', schemaVersion: '1.0.0', kind: 'required' }],
  videoAccess: 'forbidden',
};

export const images: ImageSummary[] = [segmentDeclaration, matchDeclaration].map((d) => ({
  imageDigest: d.imageDigest,
  capabilityId: d.capability.id,
  capabilityVersion: d.capability.version,
  registeredAt: d.registeredAt,
}));

export const segmentContent: ProfileContent = {
  capabilityId: 'ball-tracking',
  capabilityVersion: '1.0.0',
  modelDigest: MODEL_DIGEST,
  runtimeConstraints: ['linux/amd64'],
  accelerators: [],
  resources: { cpu: '2', memory: '4Gi' },
  maxAttempts: 3,
  leaseSeconds: 120,
  staleTimeoutSeconds: 240,
  executionTimeoutSeconds: 900,
  videoPolicy: 'required',
  inputSchemaVersions: {},
  outputSchemaVersion: '1.0.0',
};

export const matchContent: ProfileContent = {
  ...segmentContent,
  capabilityId: 'possession',
  capabilityVersion: '2.1.0',
  videoPolicy: 'forbidden',
  inputSchemaVersions: { 'ball-tracking': '1.0.0' },
  outputSchemaVersion: '2.0.0',
};

export const draftProfile: ProfileVersion = {
  profileId: 'ball-tracking-standard',
  version: 1,
  rowVersion: 1,
  profileDigest: PROFILE_DIGEST,
  imageDigest: SEGMENT_DIGEST,
  state: 'draft',
  content: segmentContent,
  createdAt: at(12),
  createdBy: ADMIN,
};

export const approvedProfile: ProfileVersion = {
  ...draftProfile,
  profileId: 'possession-standard',
  version: 2,
  rowVersion: 2,
  profileDigest: `sha256:${'f'.repeat(64)}`,
  imageDigest: MATCH_DIGEST,
  state: 'approved',
  content: matchContent,
  approvedAt: at(13),
  approvedBy: ADMIN,
};

export const retiredProfile: ProfileVersion = {
  ...draftProfile,
  profileId: 'ball-tracking-legacy',
  version: 3,
  rowVersion: 3,
  profileDigest: `sha256:${'9'.repeat(64)}`,
  state: 'retired',
  approvedAt: at(13),
  approvedBy: SEED,
  retiredAt: at(14),
  retiredBy: ADMIN,
};

export const profiles: ProfileVersion[] = [draftProfile, approvedProfile, retiredProfile];

export const workflowVersion: WorkflowVersion = {
  workflowId: 'standard-analysis',
  workflowVersion: '1.0.0',
  definitionDigest: `sha256:${'1'.repeat(64)}`,
  requestedCapabilities: ['ball-tracking', 'possession'],
  completionPolicy: 'all-requested-capabilities',
  segmentationPolicy: { version: 1, durationSeconds: 300, digest: `sha256:${'2'.repeat(64)}` },
  nodes: [
    { nodeKey: 'tracking', profileId: 'ball-tracking-standard', profileVersion: 1, profileDigest: PROFILE_DIGEST },
    { nodeKey: 'possession', profileId: 'possession-standard', profileVersion: 2, profileDigest: approvedProfile.profileDigest },
  ],
  edges: [
    { upstreamNodeKey: 'tracking', dependentNodeKey: 'possession', kind: 'required' },
    {
      upstreamNodeKey: 'tracking',
      dependentNodeKey: 'possession',
      kind: 'conditional',
      predicate: { kind: 'capabilityRequested', capabilityId: 'possession' },
    },
    {
      upstreamNodeKey: 'tracking',
      dependentNodeKey: 'possession',
      kind: 'conditional',
      predicate: { kind: 'nodeAccepted', nodeKey: 'tracking' },
    },
  ],
  createdAt: at(15),
  createdBy: SEED,
};

export const newerWorkflowVersion: WorkflowVersion = { ...workflowVersion, workflowVersion: '1.1.0', createdAt: at(16), createdBy: ADMIN };

export const defaultWorkflow: DefaultWorkflow = {
  workflowId: workflowVersion.workflowId,
  workflowVersion: workflowVersion.workflowVersion,
  changedAt: at(17),
  changedBy: ADMIN,
  rowVersion: 1,
};

export type FakeRegistryAdapter = { [K in keyof RegistryAdapter]: ReturnType<typeof vi.fn<RegistryAdapter[K]>> };

/** A fake adapter serving the sample data; override single methods per test. */
export function fakeRegistryAdapter(overrides: Partial<RegistryAdapter> = {}): FakeRegistryAdapter {
  const fake: FakeRegistryAdapter = {
    listImages: vi.fn<RegistryAdapter['listImages']>(async (filter) => images.filter((i) => !filter?.capabilityId || i.capabilityId === filter.capabilityId)),
    getImage: vi.fn<RegistryAdapter['getImage']>(async (digest) => {
      const found = [segmentDeclaration, matchDeclaration].find((d) => d.imageDigest === digest);
      if (!found) throw new Error('not found');
      return found;
    }),
    registerImage: vi.fn<RegistryAdapter['registerImage']>(async () => ({ declaration: segmentDeclaration, created: true })),
    listProfiles: vi.fn<RegistryAdapter['listProfiles']>(async (filter) =>
      profiles.filter((p) => (!filter?.state || p.state === filter.state) && (!filter?.capabilityId || p.content.capabilityId === filter.capabilityId)),
    ),
    getProfile: vi.fn<RegistryAdapter['getProfile']>(async (profileId, version) => {
      const found = profiles.find((p) => p.profileId === profileId && p.version === version);
      if (!found) throw new Error('not found');
      return found;
    }),
    createProfile: vi.fn<RegistryAdapter['createProfile']>(async () => ({ profile: draftProfile, created: true })),
    approveProfile: vi.fn<RegistryAdapter['approveProfile']>(async () => ({ ...draftProfile, state: 'approved', approvedAt: at(18), approvedBy: ADMIN })),
    retireProfile: vi.fn<RegistryAdapter['retireProfile']>(async () => ({ ...approvedProfile, state: 'retired', retiredAt: at(18), retiredBy: ADMIN })),
    listWorkflows: vi.fn<RegistryAdapter['listWorkflows']>(async () =>
      [workflowVersion, newerWorkflowVersion].map(({ workflowId, workflowVersion: version, definitionDigest, createdAt }) => ({
        workflowId, workflowVersion: version, definitionDigest, createdAt,
      })),
    ),
    getWorkflow: vi.fn<RegistryAdapter['getWorkflow']>(async (workflowId, version) => {
      const found = [workflowVersion, newerWorkflowVersion].find((w) => w.workflowId === workflowId && w.workflowVersion === version);
      if (!found) throw new Error('not found');
      return found;
    }),
    defineWorkflow: vi.fn<RegistryAdapter['defineWorkflow']>(async () => newerWorkflowVersion),
    getDefault: vi.fn<RegistryAdapter['getDefault']>(async () => defaultWorkflow),
    setDefault: vi.fn<RegistryAdapter['setDefault']>(async (target) => ({ ...defaultWorkflow, ...target, changedAt: at(19), rowVersion: 2 })),
  };
  Object.assign(fake, overrides);
  return fake;
}
