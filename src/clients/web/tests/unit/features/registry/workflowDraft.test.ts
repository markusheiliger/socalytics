import { describe, expect, it } from 'vitest';
import { checkDraft, draftFrom, emptyDraft, nodeCapabilities, requestedCapabilities } from '../../../../src/features/registry/workflowDraft';
import type { WorkflowDraft } from '../../../../src/platform/models/registry';
import { approvedProfile, draftProfile, profiles, workflowVersion } from './fixtures';

const approvedTracking = { ...draftProfile, state: 'approved' as const };
const allApproved = [approvedTracking, approvedProfile];

const valid = (): WorkflowDraft => ({ ...draftFrom(workflowVersion, allApproved), workflowVersion: '2.0.0' });

describe('emptyDraft', () => {
  it('prefills the segmentation policy from the default definition and preselects the completion policy', () => {
    const draft = emptyDraft(workflowVersion);
    expect(draft.segmentationPolicy).toEqual(workflowVersion.segmentationPolicy);
    expect(draft.segmentationPolicy).not.toBe(workflowVersion.segmentationPolicy);
    expect(draft.completionPolicy).toBe('all-requested-capabilities');
    expect(draft.nodes).toEqual([]);
    expect(draft.workflowVersion).toBe('');
  });

  it('starts with an empty segmentation policy without a default', () => {
    const draft = emptyDraft(null);
    expect(draft.workflowId).toBe('');
    expect(draft.segmentationPolicy).toEqual({ version: 1, durationSeconds: 0, digest: '' });
    expect(draft.completionPolicy).toBe('all-requested-capabilities');
  });
});

describe('draftFrom', () => {
  it('keeps the workflow id, clears the version, and copies the definition', () => {
    const draft = draftFrom(workflowVersion, allApproved);
    expect(draft.workflowId).toBe('standard-analysis');
    expect(draft.workflowVersion).toBe('');
    expect(draft.nodes.map((n) => n.nodeKey)).toEqual(['tracking', 'possession']);
    expect(draft.nodes.some((n) => n.flagged)).toBe(false);
    expect(draft.edges).toHaveLength(3);
    draft.edges[1].predicate!.capabilityId = 'x';
    expect(workflowVersion.edges[1].predicate?.capabilityId).toBe('possession');
  });

  it('flags nodes whose profile version is not approved or unknown', () => {
    const draft = draftFrom(workflowVersion, profiles);
    expect(draft.nodes.find((n) => n.nodeKey === 'tracking')?.flagged).toBe(true);
    expect(draft.nodes.find((n) => n.nodeKey === 'possession')?.flagged).toBeUndefined();
    expect(draftFrom(workflowVersion, []).nodes.every((n) => n.flagged)).toBe(true);
  });
});

describe('requested capabilities', () => {
  it('defaults to all node capabilities', () => {
    const { nodes } = valid();
    expect(nodeCapabilities(nodes, allApproved)).toEqual(['ball-tracking', 'possession']);
    expect(requestedCapabilities(nodes, allApproved)).toEqual(['ball-tracking', 'possession']);
  });

  it('allows a subset and drops capabilities no node offers', () => {
    const { nodes } = valid();
    expect(requestedCapabilities(nodes, allApproved, ['possession', 'gone'])).toEqual(['possession']);
    expect(requestedCapabilities(nodes, allApproved, [])).toEqual([]);
  });
});

describe('checkDraft', () => {
  it('accepts a complete draft', () => {
    expect(checkDraft(valid())).toEqual([]);
  });

  it('requires at least one node', () => {
    expect(checkDraft({ ...valid(), nodes: [], edges: [] })).toEqual([{ code: 'no-nodes' }]);
  });

  it('refuses duplicate node keys', () => {
    const draft = valid();
    draft.nodes[1].nodeKey = 'tracking';
    expect(checkDraft(draft)).toContainEqual({ code: 'duplicate-node-key', node: 'tracking' });
  });

  it('refuses edges and predicates that refer to nodes outside the draft', () => {
    const draft = valid();
    draft.edges = [
      { upstreamNodeKey: 'ghost', dependentNodeKey: 'possession', kind: 'required' },
      { upstreamNodeKey: 'tracking', dependentNodeKey: 'phantom', kind: 'required' },
      { upstreamNodeKey: 'tracking', dependentNodeKey: 'possession', kind: 'conditional', predicate: { kind: 'nodeAccepted', nodeKey: 'spectre' } },
    ];
    expect(checkDraft(draft)).toEqual([
      { code: 'unknown-edge-node', node: 'ghost' },
      { code: 'unknown-edge-node', node: 'phantom' },
      { code: 'unknown-predicate-node', node: 'spectre' },
    ]);
  });

  it('refuses flagged nodes', () => {
    const draft = { ...draftFrom(workflowVersion, profiles), workflowVersion: '2.0.0' };
    expect(checkDraft(draft)).toContainEqual({ code: 'flagged-node', node: 'tracking' });
  });

  it('requires a positive duration and a SHA-256 digest', () => {
    const draft = valid();
    draft.segmentationPolicy = { version: 1, durationSeconds: 0, digest: 'sha256:abc' };
    expect(checkDraft(draft)).toEqual([{ code: 'invalid-duration' }, { code: 'invalid-digest' }]);
    draft.segmentationPolicy = { version: 1, durationSeconds: -5, digest: `md5:${'1'.repeat(64)}` };
    expect(checkDraft(draft).map((v) => v.code)).toEqual(['invalid-duration', 'invalid-digest']);
  });
});
