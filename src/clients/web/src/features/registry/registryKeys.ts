import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import { createRegistryAdapter, type RegistryAdapter } from '../../platform/adapters/registryAdapter';
import type {
  ProfileDraft,
  WorkflowDraft,
  WorkflowSummary,
} from '../../platform/models/registry';
import { useSession } from '../../session/sessionContext';
import { loadMemberResolver } from './memberNames';

// In-memory caching only: nothing is persisted and nothing polls (FR-020).
export const REGISTRY_KEY = ['registry'] as const;
export const registryKeys = {
  images: (capabilityId?: string) => [...REGISTRY_KEY, 'images', { capabilityId }] as const,
  image: (digest: string) => [...REGISTRY_KEY, 'image', digest] as const,
  profiles: (filter?: { state?: string; capabilityId?: string }) => [...REGISTRY_KEY, 'profiles', { ...filter }] as const,
  profile: (profileId: string, version: number) => [...REGISTRY_KEY, 'profile', profileId, version] as const,
  workflows: () => [...REGISTRY_KEY, 'workflows'] as const,
  workflow: (workflowId: string, workflowVersion: string) => [...REGISTRY_KEY, 'workflow', workflowId, workflowVersion] as const,
  default: () => [...REGISTRY_KEY, 'default'] as const,
  members: () => [...REGISTRY_KEY, 'members'] as const,
};

export function useRegistryAdapter(): RegistryAdapter {
  const { platform } = useSession();
  return useMemo(() => createRegistryAdapter(platform), [platform]);
}

export function useImages(capabilityId?: string) {
  const adapter = useRegistryAdapter();
  return useQuery({ queryKey: registryKeys.images(capabilityId), queryFn: () => adapter.listImages(capabilityId ? { capabilityId } : undefined), retry: false });
}

export function useImage(digest: string) {
  const adapter = useRegistryAdapter();
  return useQuery({ queryKey: registryKeys.image(digest), queryFn: () => adapter.getImage(digest), retry: false });
}

export function useProfiles(filter?: { state?: string; capabilityId?: string }) {
  const adapter = useRegistryAdapter();
  return useQuery({ queryKey: registryKeys.profiles(filter), queryFn: () => adapter.listProfiles(filter), retry: false });
}

export function useProfile(profileId: string, version: number) {
  const adapter = useRegistryAdapter();
  return useQuery({ queryKey: registryKeys.profile(profileId, version), queryFn: () => adapter.getProfile(profileId, version), retry: false });
}

export function useDefaultWorkflow() {
  const adapter = useRegistryAdapter();
  return useQuery({ queryKey: registryKeys.default(), queryFn: () => adapter.getDefault(), retry: false });
}

/** The workflow list with `isDefault` derived from the default workflow. */
export function useWorkflows() {
  const adapter = useRegistryAdapter();
  return useQuery({
    queryKey: registryKeys.workflows(),
    queryFn: async (): Promise<WorkflowSummary[]> => {
      const [items, current] = await Promise.all([adapter.listWorkflows(), adapter.getDefault()]);
      return items.map((item) => ({
        ...item,
        isDefault: current !== null && current.workflowId === item.workflowId && current.workflowVersion === item.workflowVersion,
      }));
    },
    retry: false,
  });
}

export function useWorkflow(workflowId: string, workflowVersion: string) {
  const adapter = useRegistryAdapter();
  return useQuery({ queryKey: registryKeys.workflow(workflowId, workflowVersion), queryFn: () => adapter.getWorkflow(workflowId, workflowVersion), retry: false });
}

/** Resolves member ids to names through the member list (Club Admins may list members). */
export function useMemberNames() {
  const { platform } = useSession();
  return useQuery({ queryKey: registryKeys.members(), queryFn: () => loadMemberResolver(platform), retry: false });
}

function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: REGISTRY_KEY });
}

export function useRegisterImage() {
  const adapter = useRegistryAdapter();
  const invalidate = useInvalidate();
  return useMutation({ mutationFn: (reference: string) => adapter.registerImage(reference), onSuccess: invalidate });
}

export function useCreateProfile() {
  const adapter = useRegistryAdapter();
  const invalidate = useInvalidate();
  return useMutation({ mutationFn: (draft: ProfileDraft) => adapter.createProfile(draft), onSuccess: invalidate });
}

interface Transition {
  profileId: string;
  version: number;
  rowVersion: number;
}

export function useApproveProfile() {
  const adapter = useRegistryAdapter();
  const invalidate = useInvalidate();
  return useMutation({ mutationFn: (t: Transition) => adapter.approveProfile(t.profileId, t.version, t.rowVersion), onSuccess: invalidate });
}

export function useRetireProfile() {
  const adapter = useRegistryAdapter();
  const invalidate = useInvalidate();
  return useMutation({ mutationFn: (t: Transition) => adapter.retireProfile(t.profileId, t.version, t.rowVersion), onSuccess: invalidate });
}

export function useDefineWorkflow() {
  const adapter = useRegistryAdapter();
  const invalidate = useInvalidate();
  return useMutation({ mutationFn: (draft: WorkflowDraft) => adapter.defineWorkflow(draft), onSuccess: invalidate });
}

export function useSetDefault() {
  const adapter = useRegistryAdapter();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: { workflowId: string; workflowVersion: string; rowVersion?: number }) =>
      adapter.setDefault({ workflowId: input.workflowId, workflowVersion: input.workflowVersion }, input.rowVersion),
    onSuccess: invalidate,
  });
}

// A "has changed" outcome also needs fresh data, so views call this after RegistryChangedError.
export function useReloadRegistry() {
  return useInvalidate();
}
