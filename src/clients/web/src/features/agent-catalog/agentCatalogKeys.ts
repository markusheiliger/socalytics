import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import { createAgentCatalogAdapter, type AgentCatalogAdapter } from '../../platform/adapters/agentCatalogAdapter';
import type { AgentVersionDocument, ArtifactKind, DocumentOf } from '../../platform/models/agentCatalog';
import { useSession } from '../../session/sessionContext';
import { loadMemberResolver } from '../registry/memberNames';

// In-memory caching only: nothing is persisted and nothing polls (FR-020).
export const AGENT_CATALOG_KEY = ['agent-catalog'] as const;
export const agentCatalogKeys = {
  baseline: () => [...AGENT_CATALOG_KEY, 'baseline'] as const,
  available: () => [...AGENT_CATALOG_KEY, 'available'] as const,
  enablements: () => [...AGENT_CATALOG_KEY, 'enablements'] as const,
  versions: (filter?: { agentId?: string; state?: string }) => [...AGENT_CATALOG_KEY, 'versions', { ...filter }] as const,
  version: (agentId: string, version: string) => [...AGENT_CATALOG_KEY, 'version', agentId, version] as const,
  artifacts: (kind: ArtifactKind, filter?: { id?: string }) => [...AGENT_CATALOG_KEY, 'artifacts', kind, { ...filter }] as const,
  artifact: (kind: ArtifactKind, id: string, version: string) => [...AGENT_CATALOG_KEY, 'artifact', kind, id, version] as const,
  members: () => [...AGENT_CATALOG_KEY, 'members'] as const,
};

export function useAgentCatalogAdapter(): AgentCatalogAdapter {
  const { platform } = useSession();
  return useMemo(() => createAgentCatalogAdapter(platform), [platform]);
}

export function useCatalogBaseline() {
  const adapter = useAgentCatalogAdapter();
  return useQuery({ queryKey: agentCatalogKeys.baseline(), queryFn: () => adapter.getBaseline(), retry: false });
}

export function useAvailableAgents() {
  const adapter = useAgentCatalogAdapter();
  return useQuery({ queryKey: agentCatalogKeys.available(), queryFn: () => adapter.listAvailableAgents(), retry: false });
}

export function useEnablements() {
  const adapter = useAgentCatalogAdapter();
  return useQuery({ queryKey: agentCatalogKeys.enablements(), queryFn: () => adapter.listEnablements(), retry: false });
}

export function useAgentVersions(filter?: { agentId?: string; state?: string }) {
  const adapter = useAgentCatalogAdapter();
  return useQuery({ queryKey: agentCatalogKeys.versions(filter), queryFn: () => adapter.listAgentVersions(filter), retry: false });
}

export function useAgentVersion(agentId: string, version: string) {
  const adapter = useAgentCatalogAdapter();
  return useQuery({ queryKey: agentCatalogKeys.version(agentId, version), queryFn: () => adapter.getAgentVersion(agentId, version), retry: false });
}

export function useArtifacts(kind: ArtifactKind, filter?: { id?: string }) {
  const adapter = useAgentCatalogAdapter();
  return useQuery({ queryKey: agentCatalogKeys.artifacts(kind, filter), queryFn: () => adapter.listArtifacts(kind, filter), retry: false });
}

export function useArtifact<K extends ArtifactKind>(kind: K, id: string, version: string) {
  const adapter = useAgentCatalogAdapter();
  return useQuery({ queryKey: agentCatalogKeys.artifact(kind, id, version), queryFn: () => adapter.getArtifact(kind, id, version), retry: false });
}

/** Resolves member ids to names through the member list (Club Admins may list members). */
export function useCatalogMemberNames() {
  const { platform } = useSession();
  return useQuery({ queryKey: agentCatalogKeys.members(), queryFn: () => loadMemberResolver(platform), retry: false });
}

function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: AGENT_CATALOG_KEY });
}

export function useSetEnablement() {
  const adapter = useAgentCatalogAdapter();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: { agentId: string; version: string | null; etag: string }) => adapter.setEnablement(input.agentId, input.version, input.etag),
    onSuccess: invalidate,
  });
}

export function useRegisterAgentVersion() {
  const adapter = useAgentCatalogAdapter();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: { version: string; document: AgentVersionDocument & Record<string, unknown> }) =>
      adapter.registerAgentVersion(input.version, input.document),
    onSuccess: invalidate,
  });
}

interface VersionTransition {
  agentId: string;
  version: string;
  etag: string;
}

export function useApproveAgentVersion() {
  const adapter = useAgentCatalogAdapter();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: VersionTransition & { differentiationReviewAcknowledged?: boolean }) =>
      adapter.approveAgentVersion(input.agentId, input.version, input.etag, { differentiationReviewAcknowledged: input.differentiationReviewAcknowledged }),
    onSuccess: invalidate,
  });
}

export function useRetireAgentVersion() {
  const adapter = useAgentCatalogAdapter();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: VersionTransition) => adapter.retireAgentVersion(input.agentId, input.version, input.etag),
    onSuccess: invalidate,
  });
}

export function useRegisterArtifact<K extends ArtifactKind>() {
  const adapter = useAgentCatalogAdapter();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: { kind: K; document: DocumentOf<K> & Record<string, unknown> }) => adapter.registerArtifact(input.kind, input.document),
    onSuccess: invalidate,
  });
}

// A "changed" outcome also needs fresh data, so views call this after it.
export function useReloadAgentCatalog() {
  return useInvalidate();
}
