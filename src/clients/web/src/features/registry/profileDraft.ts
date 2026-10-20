import type { CapabilityDeclaration, ProfileDraft, ProfileVersion } from '../../platform/models/registry';

export interface ProfileFormDraft extends ProfileDraft {
  // Ids of existing profile versions with the declaration's capability id; any other id creates a new profile.
  offeredProfileIds: string[];
}

// Prefills a profile version from a declaration; the platform stays the authority for validation.
export function draftFromDeclaration(declaration: CapabilityDeclaration, existingProfiles: ProfileVersion[]): ProfileFormDraft {
  const offeredProfileIds = [
    ...new Set(existingProfiles.filter((p) => p.content.capabilityId === declaration.capability.id).map((p) => p.profileId)),
  ].sort();
  const requirements = declaration.requirements;
  const inputSchemaVersions: Record<string, string> = {};
  for (const upstream of declaration.upstream ?? []) {
    inputSchemaVersions[upstream.capabilityId] = upstream.schemaVersion;
  }
  return {
    imageDigest: declaration.imageDigest,
    profileId: offeredProfileIds[0] ?? '',
    offeredProfileIds,
    content: {
      capabilityId: declaration.capability.id,
      capabilityVersion: declaration.capability.version,
      modelDigest: declaration.modelDigest,
      runtimeConstraints: [...(declaration.runtime?.constraints ?? [])],
      accelerators: [...(declaration.runtime?.accelerators ?? [])],
      resources: { ...(declaration.resources ?? {}) },
      maxAttempts: requirements?.minMaxAttempts ?? 0,
      leaseSeconds: requirements?.minLeaseSeconds ?? 0,
      staleTimeoutSeconds: requirements?.minStaleTimeoutSeconds ?? 0,
      executionTimeoutSeconds: requirements?.minExecutionTimeoutSeconds ?? 0,
      videoPolicy: declaration.videoAccess ?? '',
      inputSchemaVersions,
      outputSchemaVersion: declaration.outputSchemaVersion ?? '',
    },
  };
}
