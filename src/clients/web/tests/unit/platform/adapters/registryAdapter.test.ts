import { PlatformFailure } from '../../../../src/platform/failure';
import { RegistryChangedError, RegistryRefusalError } from '../../../../src/platform/models/registry';
import { createRegistryAdapter, rowVersionOf } from '../../../../src/platform/adapters/registryAdapter';
import { bodyOf, header, ok, problem, setup } from './support';

const member = '00000000-0000-4000-8000-0000000000aa';
const digest = `sha256:${'a'.repeat(64)}`;
const manifest = {
  manifestVersion: '1.0.0',
  capability: { id: 'detect', version: '1.0.0', tier: 'low-level', executionScope: 'segment' },
  upstream: [{ capabilityId: 'decode', capabilityVersion: '1.0.0', schemaVersion: '1.0.0', kind: 'required' }],
  videoAccess: 'required',
  output: { schemaVersion: '1.2.0' },
  compatibleProfiles: ['detect-default'],
  model: { digest },
  runtime: { constraints: ['linux'], accelerators: ['cpu'] },
  resources: { memory: '4Gi' },
  requirements: { minMaxAttempts: 2, minLeaseSeconds: 30, minStaleTimeoutSeconds: 60, minExecutionTimeoutSeconds: 600 },
  determinism: { deterministic: true, idempotent: false },
};
const image = {
  imageDigest: digest,
  repository: 'registry.example/analysts/detect',
  manifestDigest: `sha256:${'b'.repeat(64)}`,
  capability: { id: 'detect', version: '1.0.0', tier: 'low-level', executionScope: 'segment' },
  modelDigest: digest,
  source: 'import',
  registeredAt: '2026-10-01T12:00:00Z',
  registeredBy: member,
  manifest,
};
const content = {
  capabilityId: 'detect',
  capabilityVersion: '1.0.0',
  modelDigest: digest,
  runtimeConstraints: ['linux'],
  accelerators: ['cpu'],
  resources: { memory: '4Gi' },
  maxAttempts: 2,
  leaseSeconds: 30,
  staleTimeoutSeconds: 60,
  executionTimeoutSeconds: 600,
  videoPolicy: 'required',
  inputSchemaVersions: { decode: '1.0.0' },
  outputSchemaVersion: '1.2.0',
};
const profile = {
  profileId: 'detect-default',
  version: 3,
  profileDigest: `sha256:${'c'.repeat(64)}`,
  imageDigest: digest,
  state: 'approved',
  content,
  createdAt: '2026-10-01T12:00:00Z',
  createdBy: member,
  approvedAt: '2026-10-02T12:00:00Z',
  approvedBy: member,
};
const workflow = {
  workflowId: 'default',
  workflowVersion: '2026.1',
  definitionDigest: `sha256:${'d'.repeat(64)}`,
  createdAt: '2026-10-01T12:00:00Z',
  createdBy: '00000000-0000-0000-0000-000000000000',
  definition: {
    requestedCapabilities: ['detect'],
    completionPolicy: 'all-requested-capabilities',
    segmentationPolicy: { version: 1, durationSeconds: 300, digest: `sha256:${'e'.repeat(64)}` },
    nodes: [
      { nodeKey: 'b', profileId: 'detect-default', profileVersion: 3, profileDigest: digest },
      { nodeKey: 'a', profileId: 'decode-default', profileVersion: 1, profileDigest: digest },
    ],
    edges: [
      { upstreamNodeKey: 'a', dependentNodeKey: 'b', kind: 'conditional', predicate: { kind: 'nodeAccepted', nodeKey: 'a' } },
      { upstreamNodeKey: 'a', dependentNodeKey: 'b', kind: 'required' },
    ],
  },
};
const defaults = { workflowId: 'default', workflowVersion: '2026.1', changedAt: '2026-10-03T12:00:00Z', changedBy: member };

// Kiota retries 503 and 504; a zero Retry-After keeps the tests fast.
const unavailable = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '0' } });

const withHeaders = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

describe('registryAdapter', () => {
  describe('images', () => {
    it('lists images with the capability filter', async () => {
      const { api, calls } = setup(() =>
        ok(200, { items: [{ imageDigest: digest, capabilityId: 'detect', capabilityVersion: '1.0.0', registeredAt: '2026-10-01T12:00:00Z' }] }),
      );
      const items = await createRegistryAdapter(api).listImages({ capabilityId: 'detect' });
      expect(calls[0].url).toContain('/api/v1/registry/analyst-images');
      expect(calls[0].url).toContain('capabilityId=detect');
      expect(items).toEqual([
        { imageDigest: digest, capabilityId: 'detect', capabilityVersion: '1.0.0', registeredAt: new Date('2026-10-01T12:00:00Z') },
      ]);
    });

    it('maps the declaration and parses the manifest', async () => {
      const { api, calls } = setup(() => ok(200, image));
      const declaration = await createRegistryAdapter(api).getImage(digest);
      expect(decodeURIComponent(calls[0].url)).toContain(`/api/v1/registry/analyst-images/${digest}`);
      expect(declaration).toMatchObject({
        imageDigest: digest,
        manifestDigest: image.manifestDigest,
        capability: { id: 'detect', version: '1.0.0', tier: 'low-level', executionScope: 'segment' },
        modelDigest: digest,
        source: 'import',
        registeredBy: { id: member, kind: 'member' },
        upstream: [{ capabilityId: 'decode', capabilityVersion: '1.0.0', schemaVersion: '1.0.0', kind: 'required' }],
        videoAccess: 'required',
        outputSchemaVersion: '1.2.0',
        compatibleProfiles: ['detect-default'],
        runtime: { constraints: ['linux'], accelerators: ['cpu'] },
        resources: { memory: '4Gi' },
        requirements: { minMaxAttempts: 2, minLeaseSeconds: 30, minStaleTimeoutSeconds: 60, minExecutionTimeoutSeconds: 600 },
        determinism: { deterministic: true, idempotent: false },
      });
      expect(declaration.registeredAt).toBeInstanceOf(Date);
      expect(JSON.parse(declaration.manifestText!)).toEqual(manifest);
      expect(declaration.manifestText).toContain('\n  ');
    });

    it('leaves missing or mistyped manifest fields undefined and still loads', async () => {
      const broken = { ...manifest, upstream: 'x', videoAccess: 3, output: {}, compatibleProfiles: {}, runtime: { constraints: [] }, resources: 'x', requirements: 5, determinism: [] };
      const { api } = setup(() => ok(200, { ...image, manifest: broken }));
      const declaration = await createRegistryAdapter(api).getImage(digest);
      expect(declaration).toMatchObject({ imageDigest: digest });
      for (const key of ['upstream', 'videoAccess', 'outputSchemaVersion', 'compatibleProfiles', 'runtime', 'resources', 'requirements', 'determinism'] as const) {
        expect(declaration[key]).toBeUndefined();
      }
      expect(declaration.manifestText).toBeDefined();
    });

    it('loads a declaration without a manifest', async () => {
      const { manifest: _omitted, ...rest } = image;
      const { api } = setup(() => ok(200, rest));
      const declaration = await createRegistryAdapter(api).getImage(digest);
      expect(declaration.manifestText).toBeUndefined();
      expect(declaration.upstream).toBeUndefined();
    });

    it('shows the seed actor and keeps unknown enumerations raw', async () => {
      const { api } = setup(() =>
        ok(200, { ...image, registeredBy: '00000000-0000-0000-0000-000000000000', capability: { ...image.capability, tier: 'exotic' } }),
      );
      const declaration = await createRegistryAdapter(api).getImage(digest);
      expect(declaration.registeredBy).toEqual({ id: '', kind: 'seed' });
      expect(declaration.capability.tier).toBe('exotic');
    });

    it.each([
      ['identity', { ...image, imageDigest: null }],
      ['manifest digest', { ...image, manifestDigest: null }],
      ['capability', { ...image, capability: null }],
    ])('refuses a declaration without %s as incompatible', async (_name, body) => {
      const { api } = setup(() => ok(200, body));
      await expect(createRegistryAdapter(api).getImage(digest)).rejects.toMatchObject({ kind: 'incompatible' });
    });

    it('tells created from already registered by status', async () => {
      let status = 201;
      const { api, calls } = setup(() => ok(status, image));
      const adapter = createRegistryAdapter(api);
      const created = await adapter.registerImage(`registry.example/analysts/detect@${digest}`);
      expect(created.created).toBe(true);
      expect(bodyOf(calls[0])).toEqual({ reference: `registry.example/analysts/detect@${digest}` });
      status = 200;
      expect((await adapter.registerImage('x')).created).toBe(false);
    });

    it.each([
      ['reference-not-digest', 400],
      ['reference-not-allowed', 422],
      ['image-not-found', 404],
      ['registry-unavailable', 503],
      ['registry-unavailable', 502],
      ['digest-mismatch', 422],
      ['manifest-missing', 422],
      ['manifest-invalid', 422],
      ['labels-mismatch', 422],
      ['unsupported-media-type', 422],
    ])('translates the image refusal %s (%i)', async (code, status) => {
      const errors = [{ field: 'manifest.capability', code: 'required' }];
      const { api } = setup(() => unavailable(status, { status, code, errors }));
      const error = await createRegistryAdapter(api).registerImage('x').catch((e) => e);
      expect(error).toBeInstanceOf(RegistryRefusalError);
      expect(error.refusal).toEqual({
        operation: 'imageRefused',
        code,
        details: errors,
        retryable: code === 'registry-unavailable',
      });
    });

    it('keeps an unknown image refusal code raw', async () => {
      const { api } = setup(() => problem(422, 'brand-new-code'));
      const error = await createRegistryAdapter(api).registerImage('x').catch((e) => e);
      expect(error.refusal).toMatchObject({ operation: 'imageRefused', code: 'brand-new-code', retryable: false });
    });

    it.each([
      [403, 'notPermitted'],
      [429, 'incompatible'],
      [500, 'unexpected'],
      [503, 'unreachable'],
    ])('keeps shared failure %i without a Registry code a PlatformFailure', async (status, kind) => {
      const { api } = setup(() => unavailable(status, { status }));
      const error = await createRegistryAdapter(api).registerImage('x').catch((e) => e);
      expect(error).toBeInstanceOf(PlatformFailure);
      expect(error.kind).toBe(kind);
    });

    it('keeps a 429 with a problem code a PlatformFailure', async () => {
      const { api } = setup(() => problem(429, 'too-many-requests'));
      const error = await createRegistryAdapter(api).registerImage('x').catch((e) => e);
      expect(error).toBeInstanceOf(PlatformFailure);
    });
  });

  describe('profiles', () => {
    it('lists profiles with filters and no row version', async () => {
      const { api, calls } = setup(() => ok(200, { items: [profile] }));
      const items = await createRegistryAdapter(api).listProfiles({ state: 'approved', capabilityId: 'detect' });
      expect(calls[0].url).toContain('state=approved');
      expect(calls[0].url).toContain('capabilityId=detect');
      expect(items[0]).toMatchObject({
        profileId: 'detect-default',
        version: 3,
        rowVersion: undefined,
        state: 'approved',
        createdBy: { id: member, kind: 'member' },
        approvedBy: { id: member, kind: 'member' },
        content,
      });
      expect(items[0].approvedAt).toBeInstanceOf(Date);
      expect(items[0].retiredAt).toBeUndefined();
      expect(items[0].retiredBy).toBeUndefined();
    });

    it('reads the row version from the ETag', async () => {
      const { api, calls } = setup(() => withHeaders(200, profile, { ETag: '"7"' }));
      const loaded = await createRegistryAdapter(api).getProfile('detect-default', 3);
      expect(calls[0].url).toContain('/api/v1/registry/analyst-profiles/detect-default/versions/3');
      expect(loaded.rowVersion).toBe(7);
    });

    it('parses ETag forms', () => {
      expect(rowVersionOf('"12"')).toBe(12);
      expect(rowVersionOf('W/"12"')).toBeUndefined();
      expect(rowVersionOf('abc')).toBeUndefined();
      expect(rowVersionOf(undefined)).toBeUndefined();
    });

    it('refuses a profile without identity, digest, or state as incompatible', async () => {
      for (const patch of [{ profileId: null }, { profileDigest: null }, { state: null }, { imageDigest: null }, { content: null }]) {
        const { api } = setup(() => ok(200, { ...profile, ...patch }));
        await expect(createRegistryAdapter(api).getProfile('detect-default', 3)).rejects.toMatchObject({ kind: 'incompatible' });
      }
    });

    it('creates a profile and tells created from existing', async () => {
      let status = 201;
      const { api, calls } = setup(() => withHeaders(status, { ...profile, state: 'draft' }, { ETag: '"1"' }));
      const adapter = createRegistryAdapter(api);
      const draft = { imageDigest: digest, profileId: 'detect-default', content };
      const created = await adapter.createProfile(draft);
      expect(created.created).toBe(true);
      expect(created.profile).toMatchObject({ state: 'draft', rowVersion: 1 });
      expect(bodyOf(calls[0])).toEqual({ profileId: 'detect-default', imageDigest: digest, content });
      status = 200;
      expect((await adapter.createProfile(draft)).created).toBe(false);
    });

    it.each([
      ['profile-contradicts-declaration', 422],
      ['image-not-registered', 404],
      ['profile-content-exists', 409],
      ['validation-failed', 400],
    ])('translates the profile refusal %s', async (code, status) => {
      const errors = [{ field: 'maxAttempts', code: 'below-minimum' }];
      const { api } = setup(() => problem(status, code, errors));
      const error = await createRegistryAdapter(api)
        .createProfile({ imageDigest: digest, profileId: 'p', content })
        .catch((e) => e);
      expect(error).toBeInstanceOf(RegistryRefusalError);
      expect(error.refusal).toEqual({ operation: 'profileRefused', code, details: errors, retryable: false });
    });

    it('approves and retires with If-Match and the new row version', async () => {
      const { api, calls } = setup(() => withHeaders(200, profile, { ETag: '"8"' }));
      const adapter = createRegistryAdapter(api);
      const approved = await adapter.approveProfile('detect-default', 3, 7);
      expect(calls[0].url).toContain('/versions/3/approval');
      expect(calls[0].init.method).toBe('POST');
      expect(header(calls[0], 'If-Match')).toBe('"7"');
      expect(approved.rowVersion).toBe(8);
      await adapter.retireProfile('detect-default', 3, 8);
      expect(calls[1].url).toContain('/versions/3/retirement');
      expect(header(calls[1], 'If-Match')).toBe('"8"');
    });

    it.each(['profile-not-draft', 'profile-in-default-workflow', 'profile-already-retired', 'something-new'])(
      'translates the transition refusal %s',
      async (code) => {
        const { api } = setup(() => problem(409, code));
        const error = await createRegistryAdapter(api).retireProfile('p', 1, 1).catch((e) => e);
        expect(error).toBeInstanceOf(RegistryRefusalError);
        expect(error.refusal).toEqual({ operation: 'transitionRefused', code, details: [], retryable: false });
      },
    );

    it.each([412, 428])('translates %i on a transition to a changed record', async (status) => {
      const { api } = setup(() => problem(status, 'precondition'));
      await expect(createRegistryAdapter(api).approveProfile('p', 1, 1)).rejects.toBeInstanceOf(RegistryChangedError);
    });

    it('keeps 403 and server errors on a transition shared failures', async () => {
      for (const [status, kind] of [[403, 'notPermitted'], [500, 'unexpected']] as const) {
        const { api } = setup(() => problem(status, 'whatever'));
        const error = await createRegistryAdapter(api).approveProfile('p', 1, 1).catch((e) => e);
        expect(error).toBeInstanceOf(PlatformFailure);
        expect(error.kind).toBe(kind);
      }
    });
  });

  describe('workflows', () => {
    it('lists workflows without the default flag', async () => {
      const { api } = setup(() =>
        ok(200, { items: [{ workflowId: 'default', workflowVersion: '2026.1', definitionDigest: digest, createdAt: '2026-10-01T12:00:00Z' }] }),
      );
      const items = await createRegistryAdapter(api).listWorkflows();
      expect(items).toEqual([
        { workflowId: 'default', workflowVersion: '2026.1', definitionDigest: digest, createdAt: new Date('2026-10-01T12:00:00Z') },
      ]);
    });

    it('maps a workflow with nodes sorted by key, edges in order, and the seed actor', async () => {
      const { api, calls } = setup(() => ok(200, workflow));
      const loaded = await createRegistryAdapter(api).getWorkflow('default', '2026.1');
      expect(calls[0].url).toContain('/api/v1/registry/workflows/default/versions/2026.1');
      expect(loaded.nodes.map((n) => n.nodeKey)).toEqual(['a', 'b']);
      expect(loaded.edges).toEqual([
        { upstreamNodeKey: 'a', dependentNodeKey: 'b', kind: 'conditional', predicate: { kind: 'nodeAccepted', nodeKey: 'a' } },
        { upstreamNodeKey: 'a', dependentNodeKey: 'b', kind: 'required' },
      ]);
      expect(loaded.segmentationPolicy).toEqual({ version: 1, durationSeconds: 300, digest: `sha256:${'e'.repeat(64)}` });
      expect(loaded.requestedCapabilities).toEqual(['detect']);
      expect(loaded.completionPolicy).toBe('all-requested-capabilities');
      expect(loaded.createdBy).toEqual({ id: '', kind: 'seed' });
    });

    it.each([
      ['workflow id', { workflowId: null }],
      ['digest', { definitionDigest: null }],
      ['definition', { definition: null }],
    ])('refuses a workflow without %s as incompatible', async (_name, patch) => {
      const { api } = setup(() => ok(200, { ...workflow, ...patch }));
      await expect(createRegistryAdapter(api).getWorkflow('default', '1')).rejects.toMatchObject({ kind: 'incompatible' });
    });

    it('defines a workflow', async () => {
      const { api, calls } = setup(() => ok(201, workflow));
      const draft = {
        workflowId: 'default',
        workflowVersion: '2026.1',
        requestedCapabilities: ['detect'],
        completionPolicy: 'all-requested-capabilities',
        segmentationPolicy: { version: 1, durationSeconds: 300, digest: digest },
        nodes: [{ nodeKey: 'a', profileId: 'p', profileVersion: 1, flagged: false }],
        edges: [{ upstreamNodeKey: 'a', dependentNodeKey: 'a', kind: 'conditional', predicate: { kind: 'capabilityRequested', capabilityId: 'detect' } }],
      };
      const defined = await createRegistryAdapter(api).defineWorkflow(draft);
      expect(defined.workflowVersion).toBe('2026.1');
      expect(bodyOf(calls[0])).toEqual({
        ...draft,
        nodes: [{ nodeKey: 'a', profileId: 'p', profileVersion: 1 }],
      });
    });

    it.each([
      ['workflow-invalid', 422],
      ['workflow-version-exists', 409],
      ['validation-failed', 400],
    ])('translates the workflow refusal %s', async (code, status) => {
      const errors = [{ field: 'nodes.a', code: 'profile-not-found' }];
      const { api } = setup(() => problem(status, code, errors));
      const error = await createRegistryAdapter(api)
        .defineWorkflow({
          workflowId: 'w',
          workflowVersion: '1',
          requestedCapabilities: [],
          completionPolicy: 'all-requested-capabilities',
          segmentationPolicy: { version: 1, durationSeconds: 1, digest },
          nodes: [],
          edges: [],
        })
        .catch((e) => e);
      expect(error).toBeInstanceOf(RegistryRefusalError);
      expect(error.refusal).toEqual({ operation: 'workflowRefused', code, details: errors, retryable: false });
    });
  });

  describe('default workflow', () => {
    it('reads the default with its row version', async () => {
      const { api } = setup(() => withHeaders(200, defaults, { ETag: '"4"' }));
      const loaded = await createRegistryAdapter(api).getDefault();
      expect(loaded).toMatchObject({ workflowId: 'default', workflowVersion: '2026.1', rowVersion: 4, changedBy: { id: member, kind: 'member' } });
      expect(loaded!.changedAt).toBeInstanceOf(Date);
    });

    it('answers null for 404', async () => {
      const { api } = setup(() => problem(404, 'default-workflow-not-found'));
      expect(await createRegistryAdapter(api).getDefault()).toBeNull();
    });

    it('keeps other failures of the default read shared', async () => {
      const { api } = setup(() => problem(403, 'forbidden'));
      await expect(createRegistryAdapter(api).getDefault()).rejects.toMatchObject({ kind: 'notPermitted' });
    });

    it('sends If-Match only when a row version is given', async () => {
      const { api, calls } = setup(() => withHeaders(200, defaults, { ETag: '"5"' }));
      const adapter = createRegistryAdapter(api);
      const target = { workflowId: 'default', workflowVersion: '2026.2' };
      const first = await adapter.setDefault(target);
      expect(header(calls[0], 'If-Match')).toBeUndefined();
      expect(bodyOf(calls[0])).toEqual(target);
      expect(calls[0].init.method).toBe('PUT');
      expect(first.rowVersion).toBe(5);
      await adapter.setDefault(target, 4);
      expect(header(calls[1], 'If-Match')).toBe('"4"');
    });

    it('translates 422 workflow-not-resolvable to a default refusal', async () => {
      const errors = [{ field: 'nodes.a', code: 'profile-not-approved' }];
      const { api } = setup(() => problem(422, 'workflow-not-resolvable', errors));
      const error = await createRegistryAdapter(api).setDefault({ workflowId: 'w', workflowVersion: '1' }, 1).catch((e) => e);
      expect(error).toBeInstanceOf(RegistryRefusalError);
      expect(error.refusal).toEqual({ operation: 'defaultRefused', code: 'workflow-not-resolvable', details: errors, retryable: false });
    });

    it.each([412, 428])('translates %i on the default to a changed record', async (status) => {
      const { api } = setup(() => problem(status, 'precondition'));
      await expect(createRegistryAdapter(api).setDefault({ workflowId: 'w', workflowVersion: '1' })).rejects.toBeInstanceOf(RegistryChangedError);
    });

    it('keeps 503 and 429 on the default shared failures', async () => {
      for (const [status, kind] of [[503, 'unreachable'], [429, 'incompatible']] as const) {
        const { api } = setup(() => unavailable(status, {}));
        const error = await createRegistryAdapter(api).setDefault({ workflowId: 'w', workflowVersion: '1' }, 1).catch((e) => e);
        expect(error).toBeInstanceOf(PlatformFailure);
        expect(error.kind).toBe(kind);
      }
    });

    it('refuses a default without identity as incompatible', async () => {
      const { api } = setup(() => ok(200, { ...defaults, workflowId: null }));
      await expect(createRegistryAdapter(api).getDefault()).rejects.toMatchObject({ kind: 'incompatible' });
    });
  });
});
