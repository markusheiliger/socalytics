import type { EdgePredicate, WorkflowEdge } from '../../platform/models/registry';

// A key of the `registry` namespace with its values. `defaultValue` is the raw value shown when the key is unknown.
export interface RegistryText {
  key: string;
  values: Record<string, string>;
  defaultValue?: string;
}

export interface EdgeDescription {
  description: RegistryText;
  kind: RegistryText;
  predicate?: RegistryText;
  // False when an end of the edge is not among the given nodes.
  nodesKnown: boolean;
}

export function describePredicate(predicate: EdgePredicate): RegistryText {
  switch (predicate.kind) {
    case 'capabilityRequested':
      return { key: 'edge.capabilityRequested', values: { capability: predicate.capabilityId ?? '' } };
    case 'nodeAccepted':
      return { key: 'edge.nodeAccepted', values: { node: predicate.nodeKey ?? '' } };
    default:
      return { key: 'edge.unknownPredicate', values: { kind: predicate.kind } };
  }
}

export function describeEdge(edge: WorkflowEdge, nodes: readonly { nodeKey: string }[]): EdgeDescription {
  const keys = new Set(nodes.map((node) => node.nodeKey));
  return {
    description: { key: 'edge.description', values: { upstream: edge.upstreamNodeKey, dependent: edge.dependentNodeKey } },
    kind: { key: `edgeKind.${edge.kind}`, values: {}, defaultValue: edge.kind },
    predicate: edge.predicate ? describePredicate(edge.predicate) : undefined,
    nodesKnown: keys.has(edge.upstreamNodeKey) && keys.has(edge.dependentNodeKey),
  };
}
