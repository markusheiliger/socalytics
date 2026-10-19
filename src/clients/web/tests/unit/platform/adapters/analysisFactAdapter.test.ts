import { createAnalysisFactAdapter, toJsonValue } from '../../../../src/platform/adapters/analysisFactAdapter';
import { createUntypedObject, createUntypedString, createUntypedArray, createUntypedNumber, createUntypedNull, createUntypedBoolean } from '@microsoft/kiota-abstractions';
import { ids, ok, problem, setup } from './support';

const provenance = {
  workflow: { id: 'default', version: '1' },
  analystProfile: { id: 'profile', digest: 'sha256:profile' },
  imageDigest: 'sha256:image',
  modelDigest: 'sha256:model',
};

const entry = {
  nodeKey: 'n1',
  capability: { id: 'cap', version: '2' },
  schemaVersion: '1',
  outputType: 'events',
  executionScope: 'Match',
  required: true,
  factCount: 3,
  provenance,
  coverage: { accepted: 1, missing: 2, failed: 3, blocked: 4, skipped: 5 },
};

const page = {
  run: { runId: ids.run, matchId: ids.match, teamId: ids.team, state: 'Running' },
  stream: {
    nodeKey: 'n1',
    capability: { id: 'cap', version: '2' },
    schemaVersion: '1',
    outputType: 'events',
    executionScope: 'Match',
    provenance,
  },
  range: { fromMs: 0, toMs: 60000 },
  items: [
    { acceptedResultId: ids.set, ordinal: 1, kind: 'pass', startMs: 10, endMs: 20, confidence: 0.5, payload: { a: 1, b: [true, null, 'x'], c: { d: 'e' } } },
  ],
  coverage: [{ startMs: 0, endMs: 60000, state: 'accepted' }],
  continuationToken: 'next',
};

const request = { runId: ids.run, nodeKey: 'n1', capabilityId: 'cap', capabilityVersion: '2', schemaVersion: '1' };

describe('analysisFactAdapter', () => {
  it('lists the streams of a run', async () => {
    const { api, calls } = setup(() => ok(200, { run: page.run, items: [entry] }));
    const outcome = await createAnalysisFactAdapter(api).listStreams(ids.run);
    expect(calls[0].url).toContain(`/api/v1/analysis/runs/${ids.run}/fact-streams`);
    expect(outcome).toEqual({
      kind: 'found',
      streams: [
        {
          nodeKey: 'n1',
          capabilityId: 'cap',
          capabilityVersion: '2',
          schemaVersion: '1',
          outputType: 'events',
          executionScope: 'Match',
          required: true,
          factCount: 3,
          provenance: { ...provenance },
          coverage: { accepted: 1, missing: 2, failed: 3, blocked: 4, skipped: 5 },
        },
      ],
    });
  });

  it('maps provenance without a model digest', async () => {
    const { modelDigest: _unused, ...withoutModel } = provenance;
    const { api } = setup(() => ok(200, { run: page.run, items: [{ ...entry, provenance: withoutModel }] }));
    const outcome = await createAnalysisFactAdapter(api).listStreams(ids.run);
    expect(outcome.kind === 'found' && outcome.streams[0].provenance.modelDigest).toBeUndefined();
  });

  it('answers a missing run for the streams', async () => {
    const { api } = setup(() => problem(404, 'analysis-run-not-found'));
    expect(await createAnalysisFactAdapter(api).listStreams(ids.run)).toEqual({ kind: 'runNotFound' });
  });

  it('refuses a stream without coverage as incompatible', async () => {
    const { api } = setup(() => ok(200, { run: page.run, items: [{ ...entry, coverage: null }] }));
    await expect(createAnalysisFactAdapter(api).listStreams(ids.run)).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('passes other stream failures through the shared mapping', async () => {
    const { api } = setup(() => problem(403, 'forbidden'));
    await expect(createAnalysisFactAdapter(api).listStreams(ids.run)).rejects.toMatchObject({ kind: 'notPermitted' });
  });

  it('reads a page, always sending the node key and capability, and never the page size', async () => {
    const { api, calls } = setup(() => ok(200, page));
    const outcome = await createAnalysisFactAdapter(api).listFacts(request);
    const url = new URL(calls[0].url);
    expect(url.pathname).toBe(`/api/v1/analysis/runs/${ids.run}/facts`);
    expect(Object.fromEntries(url.searchParams)).toEqual({ nodeKey: 'n1', capabilityId: 'cap', capabilityVersion: '2', schemaVersion: '1' });
    expect(outcome.kind).toBe('found');
    if (outcome.kind !== 'found') return;
    expect(outcome.page.continuationToken).toBe('next');
    expect(outcome.page.stream).toMatchObject({ nodeKey: 'n1', capability: 'cap', provenance });
    expect(outcome.page.range).toEqual({ fromMs: 0, toMs: 60000 });
    expect(outcome.page.coverage).toEqual([{ startMs: 0, endMs: 60000, state: 'accepted' }]);
    expect(outcome.page.items[0]).toMatchObject({ acceptedResultId: ids.set, ordinal: 1, kind: 'pass', startMs: 10, endMs: 20, confidence: 0.5 });
    expect(outcome.page.items[0].payload).toEqual({ a: 1, b: [true, null, 'x'], c: { d: 'e' } });
  });

  it('maps null or absent range bounds of an unranged page to absent bounds', async () => {
    const nulls = setup(() => ok(200, { ...page, range: { fromMs: null, toMs: null } }));
    const first = await createAnalysisFactAdapter(nulls.api).listFacts(request);
    expect(first.kind === 'found' && first.page.range).toEqual({ fromMs: undefined, toMs: undefined });
    const partial = setup(() => ok(200, { ...page, range: { fromMs: 5 } }));
    const second = await createAnalysisFactAdapter(partial.api).listFacts(request);
    expect(second.kind === 'found' && second.page.range).toEqual({ fromMs: 5, toMs: undefined });
  });

  it('sends the range and the continuation token', async () => {
    const { api, calls } = setup(() => ok(200, { ...page, continuationToken: null }));
    const outcome = await createAnalysisFactAdapter(api).listFacts({ ...request, fromMs: 0, toMs: 5000, continuationToken: 'tok' });
    const params = new URL(calls[0].url).searchParams;
    expect(params.get('fromMs')).toBe('0');
    expect(params.get('toMs')).toBe('5000');
    expect(params.get('continuationToken')).toBe('tok');
    expect(params.has('pageSize')).toBe(false);
    expect(outcome.kind === 'found' && outcome.page.continuationToken).toBeUndefined();
  });

  it('answers a capability that is not in the run', async () => {
    const { api } = setup(() => problem(404, 'analysis-capability-not-in-run'));
    expect(await createAnalysisFactAdapter(api).listFacts(request)).toEqual({ kind: 'capabilityNotInRun' });
  });

  it('answers any other 404 as a missing run', async () => {
    const { api } = setup(() => problem(404, 'analysis-run-not-found'));
    expect(await createAnalysisFactAdapter(api).listFacts(request)).toEqual({ kind: 'runNotFound' });
  });

  it('answers a rejected continuation token', async () => {
    const { api } = setup(() => problem(400, 'validation-failed', [{ field: 'continuationToken', code: 'invalid' }]));
    expect(await createAnalysisFactAdapter(api).listFacts({ ...request, continuationToken: 'bad' })).toEqual({ kind: 'continuationRejected' });
  });

  it('throws an ambiguous capability as a programming error', async () => {
    const { api } = setup(() => problem(400, 'analysis-capability-ambiguous'));
    await expect(createAnalysisFactAdapter(api).listFacts(request)).rejects.toMatchObject({ kind: 'programmingError' });
  });

  it('passes other failures through the shared mapping', async () => {
    const forbidden = setup(() => problem(403, 'forbidden'));
    await expect(createAnalysisFactAdapter(forbidden.api).listFacts(request)).rejects.toMatchObject({ kind: 'notPermitted' });
    const broken = setup(() => problem(500, 'boom'));
    await expect(createAnalysisFactAdapter(broken.api).listFacts(request)).rejects.toMatchObject({ kind: 'unexpected' });
  });

  it('refuses a fact without a kind as incompatible', async () => {
    const { api } = setup(() => ok(200, { ...page, items: [{ ...page.items[0], kind: null }] }));
    await expect(createAnalysisFactAdapter(api).listFacts(request)).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('refuses a page without provenance as incompatible', async () => {
    const { api } = setup(() => ok(200, { ...page, stream: { ...page.stream, provenance: null } }));
    await expect(createAnalysisFactAdapter(api).listFacts(request)).rejects.toMatchObject({ kind: 'incompatible' });
  });
});

describe('toJsonValue', () => {
  it('normalizes untyped nodes', () => {
    const node = createUntypedObject({
      s: createUntypedString('x'),
      n: createUntypedNumber(1.5),
      b: createUntypedBoolean(false),
      z: createUntypedNull(),
      a: createUntypedArray([createUntypedNumber(1), createUntypedObject({ k: createUntypedString('v') })]),
    });
    expect(toJsonValue(node)).toEqual({ s: 'x', n: 1.5, b: false, z: null, a: [1, { k: 'v' }] });
  });

  it('normalizes a model with additional data', () => {
    expect(toJsonValue({ additionalData: { a: 1, nested: { b: [1, 'x'] } } })).toEqual({ a: 1, nested: { b: [1, 'x'] } });
  });

  it('normalizes plain values and keeps markup as text', () => {
    expect(toJsonValue('<b>hi</b>')).toBe('<b>hi</b>');
    expect(toJsonValue(null)).toBeNull();
    expect(toJsonValue([1, 'a'])).toEqual([1, 'a']);
  });

  it('refuses values that are not JSON', () => {
    expect(() => toJsonValue(undefined)).toThrow();
    expect(() => toJsonValue(Number.NaN)).toThrow();
    expect(() => toJsonValue(() => 1)).toThrow();
  });
});
