import { describe, expect, it } from 'vitest';
import { draftFromDeclaration } from '../../../../src/features/registry/profileDraft';
import { approvedProfile, draftProfile, matchDeclaration, profiles, retiredProfile, segmentDeclaration } from './fixtures';

describe('draftFromDeclaration', () => {
  it('fixes capability, image, and model digest and takes the rest from the manifest', () => {
    const draft = draftFromDeclaration(segmentDeclaration, []);
    expect(draft.imageDigest).toBe(segmentDeclaration.imageDigest);
    expect(draft.content).toEqual({
      capabilityId: 'ball-tracking',
      capabilityVersion: '1.0.0',
      modelDigest: segmentDeclaration.modelDigest,
      runtimeConstraints: ['linux/amd64'],
      accelerators: [],
      resources: { cpu: '2', memory: '4Gi' },
      maxAttempts: 2,
      leaseSeconds: 60,
      staleTimeoutSeconds: 120,
      executionTimeoutSeconds: 600,
      videoPolicy: 'required',
      inputSchemaVersions: {},
      outputSchemaVersion: '1.0.0',
    });
  });

  it('takes input schema versions from the upstream entries', () => {
    const draft = draftFromDeclaration(matchDeclaration, []);
    expect(draft.content.inputSchemaVersions).toEqual({ 'ball-tracking': '1.0.0' });
    expect(draft.content.videoPolicy).toBe('forbidden');
  });

  it('handles a declaration without upstream capabilities', () => {
    const draft = draftFromDeclaration({ ...segmentDeclaration, upstream: undefined }, []);
    expect(draft.content.inputSchemaVersions).toEqual({});
  });

  it('offers the profile ids of existing versions with the same capability id', () => {
    const draft = draftFromDeclaration(segmentDeclaration, profiles);
    expect(draft.offeredProfileIds).toEqual([retiredProfile.profileId, draftProfile.profileId].sort());
    expect(draft.offeredProfileIds).not.toContain(approvedProfile.profileId);
  });

  it('offers no id and leaves the profile id empty for a new capability', () => {
    const draft = draftFromDeclaration(segmentDeclaration, [approvedProfile]);
    expect(draft.offeredProfileIds).toEqual([]);
    expect(draft.profileId).toBe('');
  });

  it('does not share mutable state with the declaration', () => {
    const draft = draftFromDeclaration(segmentDeclaration, []);
    draft.content.runtimeConstraints.push('x');
    draft.content.resources.cpu = '9';
    expect(segmentDeclaration.runtime?.constraints).toEqual(['linux/amd64']);
    expect(segmentDeclaration.resources?.cpu).toBe('2');
  });
});
