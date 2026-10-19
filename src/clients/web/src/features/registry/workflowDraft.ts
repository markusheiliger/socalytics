import type { ProfileVersion, WorkflowDraft, WorkflowDraftNode, WorkflowVersion } from '../../platform/models/registry';

const SHA256 = /^sha256:[0-9a-f]{64}$/;

export interface DraftViolation {
  code: 'no-nodes' | 'duplicate-node-key' | 'unknown-edge-node' | 'unknown-predicate-node' | 'flagged-node' | 'invalid-duration' | 'invalid-digest';
  // The node key the violation names, when there is one.
  node?: string;
}

// Starts a new definition; the segmentation policy comes from the definition of the current default when there is one.
export function emptyDraft(defaultWorkflow?: WorkflowVersion | null): WorkflowDraft {
  return {
    workflowId: defaultWorkflow?.workflowId ?? '',
    workflowVersion: '',
    requestedCapabilities: [],
    completionPolicy: 'all-requested-capabilities',
    segmentationPolicy: defaultWorkflow ? { ...defaultWorkflow.segmentationPolicy } : { version: 1, durationSeconds: 0, digest: '' },
    nodes: [],
    edges: [],
  };
}

const isApproved = (node: { profileId: string; profileVersion: number }, profiles: ProfileVersion[]) =>
  profiles.some((p) => p.profileId === node.profileId && p.version === node.profileVersion && p.state === 'approved');

// Prefills a new version from an existing one: the workflow id stays, the version identifier is cleared, and nodes whose profile version is not approved are flagged.
export function draftFrom(version: WorkflowVersion, profiles: ProfileVersion[]): WorkflowDraft {
  return {
    workflowId: version.workflowId,
    workflowVersion: '',
    requestedCapabilities: [...version.requestedCapabilities],
    completionPolicy: version.completionPolicy,
    segmentationPolicy: { ...version.segmentationPolicy },
    nodes: version.nodes.map((n): WorkflowDraftNode => {
      const node = { nodeKey: n.nodeKey, profileId: n.profileId, profileVersion: n.profileVersion };
      return isApproved(node, profiles) ? node : { ...node, flagged: true };
    }),
    edges: version.edges.map((e) => ({ ...e, ...(e.predicate ? { predicate: { ...e.predicate } } : {}) })),
  };
}

// The capabilities of the draft's nodes, sorted and without duplicates.
export function nodeCapabilities(nodes: WorkflowDraftNode[], profiles: ProfileVersion[]): string[] {
  const ids = nodes
    .map((n) => profiles.find((p) => p.profileId === n.profileId && p.version === n.profileVersion)?.content.capabilityId)
    .filter((id): id is string => id !== undefined);
  return [...new Set(ids)].sort();
}

// All node capabilities when nothing is chosen yet; a chosen subset is kept as far as the nodes still offer it.
export function requestedCapabilities(nodes: WorkflowDraftNode[], profiles: ProfileVersion[], chosen?: string[]): string[] {
  const offered = nodeCapabilities(nodes, profiles);
  return chosen === undefined ? offered : offered.filter((id) => chosen.includes(id));
}

// Checks only what avoids needless requests; the platform stays the authority.
export function checkDraft(draft: WorkflowDraft): DraftViolation[] {
  const violations: DraftViolation[] = [];
  if (draft.nodes.length === 0) violations.push({ code: 'no-nodes' });

  const keys = new Set<string>();
  const duplicates = new Set<string>();
  for (const node of draft.nodes) {
    if (keys.has(node.nodeKey)) duplicates.add(node.nodeKey);
    keys.add(node.nodeKey);
    if (node.flagged) violations.push({ code: 'flagged-node', node: node.nodeKey });
  }
  for (const key of duplicates) violations.push({ code: 'duplicate-node-key', node: key });

  for (const edge of draft.edges) {
    for (const key of [edge.upstreamNodeKey, edge.dependentNodeKey]) {
      if (!keys.has(key)) violations.push({ code: 'unknown-edge-node', node: key });
    }
    if (edge.predicate?.kind === 'nodeAccepted' && !keys.has(edge.predicate.nodeKey ?? '')) {
      violations.push({ code: 'unknown-predicate-node', node: edge.predicate.nodeKey });
    }
  }

  const policy = draft.segmentationPolicy;
  if (!(policy.durationSeconds > 0)) violations.push({ code: 'invalid-duration' });
  if (!SHA256.test(policy.digest)) violations.push({ code: 'invalid-digest' });
  return violations;
}
