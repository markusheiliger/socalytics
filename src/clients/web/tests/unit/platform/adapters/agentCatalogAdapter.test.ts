import { PlatformFailure } from '../../../../src/platform/failure';
import { createAgentCatalogAdapter } from '../../../../src/platform/adapters/agentCatalogAdapter';
import { bodyOf, header, ok, problem, setup } from './support';

const member = '00000000-0000-4000-8000-0000000000aa';
const digest = (c: string) => `sha256:${c.repeat(64)}`;

const withHeaders = (status: number, body: unknown, headers: Record<string, string>) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

const problemWith = (status: number, body: Record<string, unknown>) =>
  new Response(JSON.stringify({ status, ...body }), { status, headers: { 'Content-Type': 'application/problem+json' } });

const agentVersion = {
  agentId: 'specialist-tactics',
  version: '0.1.0',
  digest: digest('a'),
  group: 'specialist',
  state: 'approved',
  enabled: true,
  registeredBy: 'system:seed',
  registeredAt: '2026-10-01T12:00:00Z',
  approvedBy: member,
  approvedAt: '2026-10-02T12:00:00Z',
  selfApproval: false,
  differentiationAcknowledged: true,
  document: {
    schemaVersion: '1.0.0',
    kind: 'agent-version',
    agentId: 'specialist-tactics',
    version: '0.1.0',
    group: 'specialist',
    displayName: 'Tactics',
    disclosure: 'AI generated',
    futureField: 'kept',
    artifacts: {
      promptTemplate: { id: 'p', version: '1.0.0', digest: digest('1') },
      modelSelection: { id: 'm', version: '1.0.0', digest: digest('2') },
      toolPolicy: { id: 't', version: '1.0.0', digest: digest('3') },
      safetyPolicy: { id: 's', version: '1.0.0', digest: digest('4') },
    },
  },
  distances: [{ agentId: 'coach-a', agentVersion: '1.0.0', profileId: 'prof', profileVersion: '1.0.0', distance: 0.25, reviewRequired: true }],
};

const artifact = (kind: string, document: Record<string, unknown>) => ({
  kind,
  id: 'doc-1',
  version: '1.0.0',
  digest: digest('b'),
  registeredAt: '2026-10-01T12:00:00Z',
  document: { schemaVersion: '1.0.0', kind, id: 'doc-1', version: '1.0.0', ...document },
});

describe('agentCatalogAdapter reads', () => {
  it('maps the baseline', async () => {
    const { api, calls } = setup(() =>
      ok(200, {
        schemaVersion: '1.0.0',
        kind: 'catalog-baseline',
        toolAllowlist: ['read-facts'],
        agents: [{ agentId: 'specialist-tactics', group: 'specialist', domain: 'tactics', displayName: 'Tactics', permittedTools: ['read-facts'] }],
      }),
    );
    const baseline = await createAgentCatalogAdapter(api).getBaseline();
    expect(calls[0].url).toContain('/api/v1/agent-catalog/baseline');
    expect(baseline).toEqual({
      version: '1.0.0',
      tools: ['read-facts'],
      agents: [{ agentId: 'specialist-tactics', group: 'specialist', domain: 'tactics', displayName: 'Tactics', toolBaseline: ['read-facts'] }],
    });
  });

  it('maps available agents and keeps unknown groups raw in the baseline', async () => {
    const { api } = setup((call) =>
      call.url.includes('/agents')
        ? ok(200, { agents: [{ agentId: 'specialist-tactics', group: 'specialist', enabledVersion: '0.1.0' }] })
        : ok(200, { schemaVersion: '1.0.0', toolAllowlist: [], agents: [{ agentId: 'x', group: 'referee', displayName: 'X' }] }),
    );
    const adapter = createAgentCatalogAdapter(api);
    expect(await adapter.listAvailableAgents()).toEqual([{ agentId: 'specialist-tactics', version: '0.1.0' }]);
    expect((await adapter.getBaseline()).agents[0].group).toBe('referee');
  });

  it('maps enablements with the seed and members', async () => {
    const { api } = setup((call) =>
      call.url.includes('/members')
        ? ok(200, { items: [{ id: member, accountName: 'Ada', membershipStatus: 'active', passwordSet: true, lockedOut: false, createdAt: '2026-10-01T12:00:00Z', version: 1 }] })
        : ok(200, {
            items: [
              { agentId: 'specialist-tactics', group: 'specialist', enabledVersion: '0.1.0', changedBy: 'system:seed', changedAt: '2026-10-01T12:00:00Z', etag: '"3"' },
              { agentId: 'coach-a', group: 'coach', enabledVersion: null, changedBy: member, changedAt: '2026-10-02T12:00:00Z', etag: '"1"' },
            ],
          }),
    );
    const [seeded, byMember] = await createAgentCatalogAdapter(api).listEnablements();
    expect(seeded).toMatchObject({ agentId: 'specialist-tactics', enabledVersion: '0.1.0', etag: '"3"', changedBy: { id: 'system:seed', kind: 'seed' } });
    expect(byMember.enabledVersion).toBeUndefined();
    expect(byMember.changedBy).toMatchObject({ id: member, displayName: 'Ada', kind: 'member' });
  });

  it('lists agent versions with filters and keeps unknown states raw', async () => {
    const { api, calls } = setup(() =>
      ok(200, { items: [{ agentId: 'a', version: '1.0.0', digest: digest('a'), group: 'specialist', state: 'archived', enabled: false, registeredAt: '2026-10-01T12:00:00Z' }] }),
    );
    const items = await createAgentCatalogAdapter(api).listAgentVersions({ agentId: 'a', state: 'draft' });
    expect(calls[0].url).toContain('agentId=a');
    expect(calls[0].url).toContain('state=draft');
    expect(items[0]).toMatchObject({ state: 'archived', enabled: false });
  });

  it('maps an agent version with its etag, references, distances, and members', async () => {
    const { api } = setup((call) =>
      call.url.includes('/members') ? ok(200, { items: [] }) : withHeaders(200, agentVersion, { ETag: '"7"' }),
    );
    const version = await createAgentCatalogAdapter(api).getAgentVersion('specialist-tactics', '0.1.0');
    expect(version.etag).toBe('"7"');
    expect(version.displayName).toBe('Tactics');
    expect(version.disclosure).toBe('AI generated');
    expect(version.registeredBy).toEqual({ id: 'system:seed', kind: 'seed' });
    expect(version.approvedBy).toEqual({ id: member, kind: 'unknown' });
    expect(version.artifacts.map((a) => a.kind)).toEqual(['prompt-template', 'model-selection', 'tool-policy', 'safety-policy']);
    expect(version.artifacts[0]).toEqual({ kind: 'prompt-template', id: 'p', version: '1.0.0', digest: digest('1') });
    expect(version.distances).toEqual([
      { agentId: 'coach-a', agentVersion: '1.0.0', profileId: 'prof', profileVersion: '1.0.0', distance: 0.25, reviewRequired: true },
    ]);
  });

  it('lists artifacts of a kind by its plural segment and maps each kind document', async () => {
    const documents: Record<string, Record<string, unknown>> = {
      'prompt-template': { group: 'specialist', responseContract: 'advice', text: 'Hi {{x}} <b>', slots: ['x'], dimensionSetMajor: 1 },
      'model-selection': { provider: 'future-provider', model: 'm', deployment: 'd', parameters: { temperature: 0.2, reasoningEffort: 'extreme' } },
      'tool-policy': { tools: ['read-facts'] },
      'safety-policy': { group: 'coach', routes: ['user-to-coach'], hardBoundaries: ['h'], escalationCategories: [], refusalCategories: ['r'], rules: ['rule'] },
      'dimension-set': { dimensions: [{ id: 'd1', meaning: 'm', acceptedEvidence: ['e'] }] },
      'philosophy-profile': {
        dimensionSet: { id: 'ds', version: '1.0.0' },
        weights: { d1: { weight: 60, rationale: 'r', provenance: 'p' } },
        author: 'a',
        reviewers: { architecture: ['x'], footballDomain: ['y'] },
        supersedes: { version: '0.9.0' },
      },
      'modifier-policy': { dimensionSetMajor: 1, perDimensionBound: 5, maxPositiveMovement: 3, contextCategories: ['score-state'], budgetNeutral: true },
    };
    for (const [kind, document] of Object.entries(documents)) {
      const { api, calls } = setup(() => ok(200, artifact(kind, document)));
      const record = await createAgentCatalogAdapter(api).getArtifact(kind, 'doc-1', '1.0.0');
      expect(calls[0].url).toContain(`/artifacts/${kind.replace(/y$/, 'ie')}s/doc-1/versions/1.0.0`);
      expect(record.kind).toBe(kind);
      expect(record.document).toMatchObject({ ...document, kind });
    }
  });

  it('keeps the distances of a profile and unknown document fields', async () => {
    const { api } = setup(() =>
      ok(200, {
        ...artifact('tool-policy', { tools: ['a'], futureField: { nested: [1, 'two'] } }),
        distances: [{ agentId: 'a', agentVersion: '1.0.0', profileId: 'p', profileVersion: '1.0.0', distance: 0.5, reviewRequired: false }],
      }),
    );
    const record = await createAgentCatalogAdapter(api).getArtifact('tool-policy', 'doc-1', '1.0.0');
    expect(record.document).toMatchObject({ futureField: { nested: [1, 'two'] } });
    expect(record.distances).toEqual([{ agentId: 'a', agentVersion: '1.0.0', profileId: 'p', profileVersion: '1.0.0', distance: 0.5, reviewRequired: false }]);
  });

  it('keeps an unknown artifact kind with a raw document', async () => {
    const { api } = setup(() => ok(200, artifact('future-kind', { anything: 1 })));
    const record = await createAgentCatalogAdapter(api).getArtifact('future-kind', 'doc-1', '1.0.0');
    expect(record.document).toMatchObject({ anything: 1 });
  });

  it('lists artifacts with the id filter', async () => {
    const { api, calls } = setup(() =>
      ok(200, { items: [{ kind: 'tool-policy', id: 'doc-1', version: '1.0.0', digest: digest('b'), registeredBy: 'system:seed', registeredAt: '2026-10-01T12:00:00Z' }] }),
    );
    const items = await createAgentCatalogAdapter(api).listArtifacts('tool-policy', { id: 'doc-1' });
    expect(calls[0].url).toContain('/artifacts/tool-policies');
    expect(calls[0].url).toContain('id=doc-1');
    expect(items[0].registeredBy).toEqual({ id: 'system:seed', kind: 'seed' });
  });

  it('leaves 401, 403, and 5xx to the shared failures and does not hide 404', async () => {
    for (const [status, kind] of [[401, 'sessionEnded'], [403, 'notPermitted'], [500, 'unexpected']] as const) {
      const { api } = setup(() => problem(status, 'x'));
      await expect(createAgentCatalogAdapter(api).getBaseline()).rejects.toMatchObject({ kind });
    }
    const { api } = setup(() => problem(404, 'not-found'));
    await expect(createAgentCatalogAdapter(api).getAgentVersion('a', '1.0.0')).rejects.toMatchObject({ status: 404 });
  });

  it('reports an incompatible response', async () => {
    const { api } = setup(() => ok(200, { items: [{ agentId: 'a' }] }));
    await expect(createAgentCatalogAdapter(api).listAgentVersions()).rejects.toBeInstanceOf(PlatformFailure);
  });
});

describe('agentCatalogAdapter changes', () => {
  it('registers an artifact unchanged and tells created from replayed', async () => {
    const document = { schemaVersion: '1.0.0', kind: 'tool-policy', id: 'doc-1', version: '1.0.0', tools: ['a'], extra: { z: 1, a: 2 } };
    let status = 201;
    const { api, calls } = setup(() => ok(status, artifact('tool-policy', { tools: ['a'] })));
    const adapter = createAgentCatalogAdapter(api);

    const created = await adapter.registerArtifact('tool-policy', document as never);
    expect(created.kind).toBe('created');
    expect(calls[0].init.method).toBe('POST');
    expect(calls[0].url).toContain('/artifacts/tool-policies');
    expect(bodyOf(calls[0])).toEqual(document);
    expect(header(calls[0], 'X-CSRF-Token')).toBe('csrf-1');

    status = 200;
    expect((await adapter.registerArtifact('tool-policy', document as never)).kind).toBe('replayed');
  });

  it('maps validation problems with form locations', async () => {
    const { api, calls } = setup(() =>
      problemWith(400, {
        code: 'agent-catalog-validation-failed',
        violations: [
          { code: 'unknown-tool', pointer: '/tools/2', detail: 'nope' },
          { code: 'schema-violation', pointer: '', detail: 'bad' },
          { code: 'future-code', pointer: '/weights/d1/weight', detail: 'x' },
        ],
      }),
    );
    const outcome = await createAgentCatalogAdapter(api).registerArtifact('tool-policy', { tools: [] } as never);
    expect(outcome).toEqual({
      kind: 'validationFailed',
      violations: [
        { code: 'unknown-tool', pointer: '/tools/2', detail: 'nope', location: 'entry:tools:2' },
        { code: 'schema-violation', pointer: '', detail: 'bad', location: 'summary' },
        { code: 'future-code', pointer: '/weights/d1/weight', detail: 'x', location: 'cell:d1:weight' },
      ],
    });
    expect(calls).toHaveLength(1);
  });

  it('maps an existing version with different content', async () => {
    const { api } = setup(() => problem(409, 'agent-catalog-version-exists'));
    expect(await createAgentCatalogAdapter(api).registerArtifact('tool-policy', {} as never)).toEqual({ kind: 'versionExists' });
    expect(await createAgentCatalogAdapter(api).registerAgentVersion({} as never)).toEqual({ kind: 'versionExists' });
  });

  it('registers an agent version', async () => {
    const { api, calls } = setup((call) => (call.url.includes('/members') ? ok(200, { items: [] }) : withHeaders(201, agentVersion, { ETag: '"1"' })));
    const document = agentVersion.document;
    const outcome = await createAgentCatalogAdapter(api).registerAgentVersion(document as never);
    expect(calls[0].url).toContain('/agent-catalog/agent-versions');
    expect(bodyOf(calls[0])).toEqual(document);
    expect(outcome).toMatchObject({ kind: 'created', value: { etag: '"1"', agentId: 'specialist-tactics' } });
  });

  it('approves with If-Match and the acknowledgement, and returns the new etag', async () => {
    const { api, calls } = setup((call) => (call.url.includes('/members') ? ok(200, { items: [] }) : withHeaders(200, agentVersion, { ETag: '"8"' })));
    const result = await createAgentCatalogAdapter(api).approveAgentVersion('specialist-tactics', '0.1.0', '"7"', { differentiationReviewAcknowledged: true });
    const call = calls[0];
    expect(call.url).toContain('/agent-versions/specialist-tactics/versions/0.1.0/approval');
    expect(header(call, 'If-Match')).toBe('"7"');
    expect(bodyOf(call)).toEqual({ differentiationReviewAcknowledged: true });
    expect(result).toMatchObject({ etag: '"8"' });
  });

  it('maps approval and retirement refusals and stale states, once', async () => {
    for (const [status, code, expected] of [
      [409, 'agent-version-not-draft', { kind: 'transitionRefused', code: 'agent-version-not-draft' }],
      [409, 'differentiation-review-required', { kind: 'transitionRefused', code: 'differentiation-review-required' }],
      [409, 'agent-version-not-approved', { kind: 'transitionRefused', code: 'agent-version-not-approved' }],
      [409, 'agent-version-enabled', { kind: 'transitionRefused', code: 'agent-version-enabled' }],
      [409, 'future-refusal', { kind: 'transitionRefused', code: 'future-refusal' }],
      [412, 'precondition-failed', { kind: 'changed' }],
      [428, 'precondition-required', { kind: 'changed' }],
    ] as const) {
      const { api, calls } = setup(() => problem(status, code));
      const adapter = createAgentCatalogAdapter(api);
      expect(await adapter.approveAgentVersion('a', '1.0.0', '"1"')).toEqual(expected);
      expect(await adapter.retireAgentVersion('a', '1.0.0', '"1"')).toEqual(expected);
      expect(calls).toHaveLength(2);
    }
  });

  it('retires with If-Match', async () => {
    const { api, calls } = setup((call) => (call.url.includes('/members') ? ok(200, { items: [] }) : withHeaders(200, { ...agentVersion, state: 'retired' }, { ETag: '"9"' })));
    const result = await createAgentCatalogAdapter(api).retireAgentVersion('specialist-tactics', '0.1.0', '"8"');
    expect(calls[0].url).toContain('/versions/0.1.0/retirement');
    expect(header(calls[0], 'If-Match')).toBe('"8"');
    expect(result).toMatchObject({ state: 'retired', etag: '"9"' });
  });

  it('leaves other failures of a change to the shared handling', async () => {
    for (const [status, kind] of [[401, 'sessionEnded'], [403, 'notPermitted'], [500, 'unexpected']] as const) {
      const { api } = setup(() => problem(status, 'x'));
      await expect(createAgentCatalogAdapter(api).approveAgentVersion('a', '1.0.0', '"1"')).rejects.toMatchObject({ kind });
    }
    const { api } = setup(() => problem(400, 'agent-catalog-validation-failed'));
    await expect(createAgentCatalogAdapter(api).retireAgentVersion('a', '1.0.0', '"1"')).rejects.toBeInstanceOf(PlatformFailure);
  });

  it('enables and disables with If-Match and the version in the body', async () => {
    const body = { agentId: 'specialist-tactics', group: 'specialist', enabledVersion: '0.1.0', changedBy: 'system:seed', changedAt: '2026-10-03T12:00:00Z' };
    const { api, calls } = setup(() => withHeaders(200, body, { ETag: '"4"' }));
    const adapter = createAgentCatalogAdapter(api);

    const enabled = await adapter.setEnablement('specialist-tactics', '0.1.0', '"3"');
    expect(calls[0].url).toContain('/enablements/specialist-tactics');
    expect(calls[0].init.method).toBe('PUT');
    expect(header(calls[0], 'If-Match')).toBe('"3"');
    expect(bodyOf(calls[0])).toEqual({ version: '0.1.0' });
    expect(enabled).toMatchObject({ enabledVersion: '0.1.0', etag: '"4"' });

    await adapter.setEnablement('specialist-tactics', null, '"4"');
    expect(bodyOf(calls[1])).toEqual({ version: null });
  });

  it('maps enablement refusals with their reasons and stale states', async () => {
    for (const reason of ['not-approved', 'retired', 'simulated-provider', 'future-reason']) {
      const { api } = setup(() => problemWith(409, { code: 'agent-version-not-enableable', reason }));
      expect(await createAgentCatalogAdapter(api).setEnablement('a', '1.0.0', '"1"')).toEqual({
        kind: 'enablementRefused',
        code: 'agent-version-not-enableable',
        reason,
      });
    }
    const coach = setup(() => problem(409, 'coach-agent-enablement-unsupported'));
    expect(await createAgentCatalogAdapter(coach.api).setEnablement('coach-a', '1.0.0', '"1"')).toEqual({
      kind: 'enablementRefused',
      code: 'coach-agent-enablement-unsupported',
    });
    const unknown = setup(() => problem(404, 'agent-not-found'));
    expect(await createAgentCatalogAdapter(unknown.api).setEnablement('zzz', '1.0.0', '"1"')).toEqual({
      kind: 'enablementRefused',
      code: 'agent-not-found',
    });
    for (const status of [412, 428]) {
      const { api, calls } = setup(() => problem(status, 'x'));
      expect(await createAgentCatalogAdapter(api).setEnablement('a', null, '"1"')).toEqual({ kind: 'changed' });
      expect(calls).toHaveLength(1);
    }
  });

  it('leaves 403 and 5xx of an enablement to the shared handling', async () => {
    const { api } = setup(() => problem(403, 'forbidden'));
    await expect(createAgentCatalogAdapter(api).setEnablement('a', null, '"1"')).rejects.toMatchObject({ kind: 'notPermitted' });
    const server = setup(() => problem(500, 'boom'));
    await expect(createAgentCatalogAdapter(server.api).setEnablement('a', null, '"1"')).rejects.toMatchObject({ kind: 'unexpected' });
  });
});
