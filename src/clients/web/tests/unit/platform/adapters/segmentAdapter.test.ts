import { createSegmentAdapter } from '../../../../src/platform/adapters/segmentAdapter';
import { ids, ok, problem, setup } from './support';

const segment = {
  recordingVersionId: ids.version,
  timelineMappingDigest: 'sha256:aaa',
  segmentationPolicyDigest: 'sha256:bbb',
  segmentNumber: 3,
  state: 'available',
  mediaSizeBytes: 2048,
  failureReason: null,
  availableAt: '2026-10-20T10:00:00Z',
  inUse: false,
};

describe('segmentAdapter', () => {
  it('maps the list with a single request', async () => {
    const { api, calls } = setup(() => ok(200, { matchId: ids.match, segments: [segment] }));
    const result = await createSegmentAdapter(api).list(ids.match);
    expect(calls).toHaveLength(1);
    expect(calls[0].init.method).toBe('GET');
    expect(calls[0].url).toContain(`/api/v1/matches/${ids.match}/materialized-segments`);
    expect(result).toEqual({
      matchId: ids.match,
      segments: [
        {
          recordingVersionId: ids.version,
          timelineMappingDigest: 'sha256:aaa',
          segmentationPolicyDigest: 'sha256:bbb',
          segmentNumber: 3,
          state: 'available',
          mediaSizeBytes: 2048,
          failureReason: undefined,
          availableAt: new Date('2026-10-20T10:00:00Z'),
          inUse: false,
        },
      ],
    });
  });

  it('maps nullable fields to undefined and keeps an unknown state verbatim', async () => {
    const { api } = setup(() =>
      ok(200, {
        matchId: ids.match,
        segments: [{ ...segment, state: 'Quarantined', mediaSizeBytes: null, availableAt: null, failureReason: 'disk <b>full</b>', inUse: true }],
      }),
    );
    const [mapped] = (await createSegmentAdapter(api).list(ids.match)).segments;
    expect(mapped.state).toBe('Quarantined');
    expect(mapped.mediaSizeBytes).toBeUndefined();
    expect(mapped.availableAt).toBeUndefined();
    expect(mapped.failureReason).toBe('disk <b>full</b>');
    expect(mapped.inUse).toBe(true);
  });

  it('returns an empty list', async () => {
    const { api } = setup(() => ok(200, { matchId: ids.match, segments: [] }));
    expect((await createSegmentAdapter(api).list(ids.match)).segments).toEqual([]);
  });

  it('purges with one POST without a body or If-Match', async () => {
    const { api, calls } = setup(() => ok(200, { matchId: ids.match, purged: 2, inUse: 1 }));
    const result = await createSegmentAdapter(api).purge(ids.match);
    expect(result).toEqual({ matchId: ids.match, purged: 2, inUse: 1 });
    expect(calls).toHaveLength(1);
    expect(calls[0].init.method).toBe('POST');
    expect(calls[0].url).toContain(`/api/v1/matches/${ids.match}/materialized-segments/purge`);
    expect(calls[0].init.body).toBeUndefined();
    const headers = calls[0].init.headers as Record<string, string> | Headers;
    const has = (name: string) => (headers instanceof Headers ? headers.has(name) : name in headers || name.toLowerCase() in headers);
    expect(has('If-Match')).toBe(false);
  });

  it('refuses with 401, 403, and 404 without retrying', async () => {
    for (const [status, kind] of [
      [401, 'sessionEnded'],
      [403, 'notPermitted'],
      [404, 'incompatible'],
    ] as const) {
      const { api, calls } = setup(() => problem(status, 'x'));
      await expect(createSegmentAdapter(api).list(ids.match)).rejects.toMatchObject({ kind, status });
      expect(calls).toHaveLength(1);
    }
  });

  it('refuses a purge with 503 after a single request', async () => {
    const { api, calls } = setup(
      () =>
        new Response(JSON.stringify({ status: 503, code: 'unavailable' }), {
          status: 503,
          headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '0' },
        }),
    );
    await expect(createSegmentAdapter(api).purge(ids.match)).rejects.toMatchObject({ kind: 'unreachable', status: 503 });
    expect(calls).toHaveLength(1);
  });

  it('refuses a purge with 401, 403, and 404', async () => {
    for (const status of [401, 403, 404]) {
      const { api } = setup(() => problem(status, 'x'));
      await expect(createSegmentAdapter(api).purge(ids.match)).rejects.toMatchObject({ status });
    }
  });
});
