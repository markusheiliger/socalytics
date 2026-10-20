import registry from '../../../../src/i18n/en/registry.json';
import { describeEdge, describePredicate } from '../../../../src/features/registry/edgeText';
import type { WorkflowEdge } from '../../../../src/platform/models/registry';

const lookup = (key: string): unknown => key.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], registry);
const nodes = [{ nodeKey: 'detect' }, { nodeKey: 'track' }];

describe('describePredicate', () => {
  it('names the requested capability', () => {
    const text = describePredicate({ kind: 'capabilityRequested', capabilityId: 'pose' });
    expect(text).toEqual({ key: 'edge.capabilityRequested', values: { capability: 'pose' } });
    expect(lookup(text.key)).toEqual(expect.stringContaining('{{capability}}'));
  });

  it('names the accepted node', () => {
    const text = describePredicate({ kind: 'nodeAccepted', nodeKey: 'detect' });
    expect(text).toEqual({ key: 'edge.nodeAccepted', values: { node: 'detect' } });
    expect(lookup(text.key)).toEqual(expect.stringContaining('{{node}}'));
  });

  it('keeps an unknown kind raw', () => {
    const text = describePredicate({ kind: 'weather' });
    expect(text).toEqual({ key: 'edge.unknownPredicate', values: { kind: 'weather' } });
    expect(lookup(text.key)).toEqual(expect.stringContaining('{{kind}}'));
  });
});

describe('describeEdge', () => {
  const edge: WorkflowEdge = { upstreamNodeKey: 'detect', dependentNodeKey: 'track', kind: 'required' };

  it('describes upstream to dependent with the kind', () => {
    const result = describeEdge(edge, nodes);
    expect(result.description).toEqual({ key: 'edge.description', values: { upstream: 'detect', dependent: 'track' } });
    expect(result.kind.key).toBe('edgeKind.required');
    expect(lookup(result.kind.key)).toBe('Required');
    expect(result.predicate).toBeUndefined();
    expect(result.nodesKnown).toBe(true);
  });

  it('describes a conditional edge with its predicate', () => {
    const result = describeEdge({ ...edge, kind: 'conditional', predicate: { kind: 'nodeAccepted', nodeKey: 'detect' } }, nodes);
    expect(result.kind.key).toBe('edgeKind.conditional');
    expect(result.predicate).toEqual({ key: 'edge.nodeAccepted', values: { node: 'detect' } });
  });

  it('falls back to the raw kind', () => {
    const result = describeEdge({ ...edge, kind: 'sometimes' }, nodes);
    expect(lookup(result.kind.key)).toBeUndefined();
    expect(result.kind.defaultValue).toBe('sometimes');
  });

  it('flags an edge that refers to a missing node', () => {
    expect(describeEdge({ ...edge, dependentNodeKey: 'ghost' }, nodes).nodesKnown).toBe(false);
  });
});
