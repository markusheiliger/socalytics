import { composeCatalogAgents, countStates, offerFor } from '../../../../src/features/agent-catalog/offer';
import type {
  AgentVersionSummary,
  BaselineAgent,
  CatalogBaseline,
  Enablement,
} from '../../../../src/platform/models/agentCatalog';

const specialist: BaselineAgent = {
  agentId: 'sa-pressing',
  group: 'specialist',
  domain: 'pressing',
  displayName: 'Pressing Specialist',
  toolBaseline: ['read-facts'],
};
const coach: BaselineAgent = {
  agentId: 'ca-head',
  group: 'coach',
  domain: 'coaching',
  displayName: 'Head Coach',
  toolBaseline: [],
};
const baseline: CatalogBaseline = { version: '1', tools: ['read-facts'], agents: [specialist, coach] };

function version(agentId: string, v: string, state: string): AgentVersionSummary {
  return {
    agentId,
    version: v,
    digest: `sha256:${'a'.repeat(64)}`,
    group: agentId.startsWith('ca') ? 'coach' : 'specialist',
    state,
    enabled: false,
    registeredAt: new Date('2026-10-01T00:00:00Z'),
  };
}

describe('offerFor', () => {
  it('offers an agent the platform lists with the version it reports', () => {
    expect(offerFor(specialist, [{ agentId: 'sa-pressing', version: '1.2.0' }])).toEqual({
      kind: 'offered',
      version: '1.2.0',
    });
  });

  it('reports no enabled version for an unlisted Specialist Agent', () => {
    expect(offerFor(specialist, [])).toEqual({ kind: 'notOffered', reason: 'noEnabledVersion' });
  });

  it('reports a Coach Agent as not available in conversations yet when unlisted', () => {
    expect(offerFor(coach, [{ agentId: 'sa-pressing', version: '1.0.0' }])).toEqual({
      kind: 'notOffered',
      reason: 'coachNotAvailable',
    });
  });

  it('offers a Coach Agent the platform lists', () => {
    expect(offerFor(coach, [{ agentId: 'ca-head', version: '0.3.0' }])).toEqual({
      kind: 'offered',
      version: '0.3.0',
    });
  });
});

describe('countStates', () => {
  it('counts draft, approved, and retired versions of one agent and ignores unknown states', () => {
    const versions = [
      version('sa-pressing', '1.0.0', 'draft'),
      version('sa-pressing', '1.1.0', 'approved'),
      version('sa-pressing', '1.2.0', 'approved'),
      version('sa-pressing', '0.9.0', 'retired'),
      version('sa-pressing', '2.0.0', 'quarantined'),
      version('ca-head', '0.1.0', 'draft'),
    ];
    expect(countStates(versions, 'sa-pressing')).toEqual({ draft: 1, approved: 2, retired: 1 });
    expect(countStates(versions, 'ca-head')).toEqual({ draft: 1, approved: 0, retired: 0 });
    expect(countStates(versions, 'other')).toEqual({ draft: 0, approved: 0, retired: 0 });
  });
});

describe('composeCatalogAgents', () => {
  it('combines baseline, versions, enablements, and available agents in baseline order', () => {
    const enablements: Enablement[] = [
      { agentId: 'sa-pressing', enabledVersion: '1.1.0', etag: '"3"' },
    ];
    const result = composeCatalogAgents(
      baseline,
      [version('sa-pressing', '1.1.0', 'approved'), version('ca-head', '0.1.0', 'draft')],
      enablements,
      [{ agentId: 'sa-pressing', version: '1.1.0' }],
    );

    expect(result.map((entry) => entry.agentId)).toEqual(['sa-pressing', 'ca-head']);
    expect(result[0]).toMatchObject({
      displayName: 'Pressing Specialist',
      enablement: enablements[0],
      stateCounts: { draft: 0, approved: 1, retired: 0 },
      offer: { kind: 'offered', version: '1.1.0' },
    });
    expect(result[1]).toMatchObject({
      enablement: { agentId: 'ca-head', etag: '' },
      stateCounts: { draft: 1, approved: 0, retired: 0 },
      offer: { kind: 'notOffered', reason: 'coachNotAvailable' },
    });
    expect(result[1].enablement.enabledVersion).toBeUndefined();
  });

  it('reports an agent without an enabled version as not offered', () => {
    const [first] = composeCatalogAgents(baseline, [], [], []);
    expect(first.offer).toEqual({ kind: 'notOffered', reason: 'noEnabledVersion' });
  });
});
