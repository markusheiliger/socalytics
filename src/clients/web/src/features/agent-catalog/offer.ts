import type {
  AgentStateCounts,
  AgentVersionSummary,
  BaselineAgent,
  CatalogAgentSummary,
  CatalogBaseline,
  Enablement,
  Offer,
} from '../../platform/models/agentCatalog';

export interface AvailableAgent {
  agentId: string;
  version: string;
}

export function emptyStateCounts(): AgentStateCounts {
  return { draft: 0, approved: 0, retired: 0 };
}

export function countStates(versions: readonly AgentVersionSummary[], agentId: string): AgentStateCounts {
  const counts = emptyStateCounts();
  for (const version of versions) {
    if (version.agentId !== agentId) continue;
    if (version.state === 'draft') counts.draft += 1;
    else if (version.state === 'approved') counts.approved += 1;
    else if (version.state === 'retired') counts.retired += 1;
  }
  return counts;
}

export function offerFor(agent: BaselineAgent, available: readonly AvailableAgent[]): Offer {
  const listed = available.find((entry) => entry.agentId === agent.agentId);
  if (listed) return { kind: 'offered', version: listed.version };
  return { kind: 'notOffered', reason: agent.group === 'coach' ? 'coachNotAvailable' : 'noEnabledVersion' };
}

export function composeCatalogAgents(
  baseline: CatalogBaseline,
  versions: readonly AgentVersionSummary[],
  enablements: readonly Enablement[],
  available: readonly AvailableAgent[],
): CatalogAgentSummary[] {
  return baseline.agents.map((agent) => ({
    ...agent,
    enablement: enablements.find((entry) => entry.agentId === agent.agentId) ?? {
      agentId: agent.agentId,
      etag: '',
    },
    stateCounts: countStates(versions, agent.agentId),
    offer: offerFor(agent, available),
  }));
}
