import { createAnalysisEvidenceAdapter } from '../../../../src/platform/adapters/analysisEvidenceAdapter';
import { PlatformFailure } from '../../../../src/platform/failure';
import { bodyOf, ids, ok, problem, setup } from './support';

const media = {
  url: 'https://storage.example/segment.mp4?sig=1',
  expiresAt: '2026-10-17T12:05:00.000Z',
  mediaType: 'video/mp4',
  mediaDigest: 'sha-256:abc',
  mediaSizeBytes: 1234,
  startOffsetMs: 295040,
  endOffsetMs: 300040,
  recordingVersionId: ids.version,
};

const response = {
  run: { runId: ids.run, matchId: ids.match, teamId: ids.team },
  requested: { fromMs: 295000, toMs: 310040 },
  fact: { startMs: 300000, endMs: 305040 },
  shortened: false,
  parts: [
    { startMs: 295000, endMs: 300000, state: 'available', media },
    { startMs: 300000, endMs: 305000, state: 'rematerializing', retryAfterSeconds: 5 },
    { startMs: 305000, endMs: 308000, state: 'failed', reason: 'extraction-failed' },
    { startMs: 308000, endMs: 309000, state: 'something-new' },
    { startMs: 309000, endMs: 310040, state: 'not-covered' },
  ],
};

const factRequest = { fact: { acceptedResultId: ids.set, ordinal: 3, contextBeforeMs: 5000, contextAfterMs: 5000 } };

describe('analysisEvidenceAdapter', () => {
  it('posts a fact request and maps the resolution', async () => {
    const { api, calls } = setup(() => ok(200, response));
    const outcome = await createAnalysisEvidenceAdapter(api).resolve(ids.run, { ...factRequest, retryFailed: true });
    expect(calls[0].url).toContain(`/api/v1/analysis/runs/${ids.run}/evidence-media`);
    expect(calls[0].init.method).toBe('POST');
    expect(bodyOf(calls[0])).toEqual({ ...factRequest, fact: { ...factRequest.fact }, retryFailed: true });
    expect(outcome).toEqual({
      kind: 'resolution',
      resolution: {
        run: { runId: ids.run, matchId: ids.match, teamId: ids.team },
        requested: { fromMs: 295000, toMs: 310040 },
        fact: { startMs: 300000, endMs: 305040 },
        shortened: false,
        parts: [
          { startMs: 295000, endMs: 300000, state: 'available', media },
          { startMs: 300000, endMs: 305000, state: 'rematerializing', retryAfterSeconds: 5 },
          { startMs: 305000, endMs: 308000, state: 'failed', reason: 'extraction-failed' },
          { startMs: 308000, endMs: 309000, state: 'something-new' },
          { startMs: 309000, endMs: 310040, state: 'not-covered' },
        ],
      },
    });
  });

  it('posts a range request without a fact', async () => {
    const { api, calls } = setup(() => ok(200, { ...response, fact: undefined }));
    const outcome = await createAnalysisEvidenceAdapter(api).resolve(ids.run, { range: { fromMs: 1, toMs: 2 } });
    expect(bodyOf(calls[0])).toEqual({ range: { fromMs: 1, toMs: 2 } });
    expect(outcome.kind === 'resolution' && outcome.resolution.fact).toBeUndefined();
  });

  it('refuses an available part without media as incompatible', async () => {
    const { api } = setup(() => ok(200, { ...response, parts: [{ startMs: 0, endMs: 1, state: 'available' }] }));
    await expect(createAnalysisEvidenceAdapter(api).resolve(ids.run, factRequest)).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('answers not found', async () => {
    const { api } = setup(() => problem(404, 'analysis-run-not-found'));
    expect(await createAnalysisEvidenceAdapter(api).resolve(ids.run, factRequest)).toEqual({ kind: 'notFound' });
  });

  it('answers rate limited with the delay', async () => {
    const { api } = setup(
      () =>
        new Response(JSON.stringify({ status: 429, code: 'evidence-rate-limited' }), {
          status: 429,
          headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '12' },
        }),
    );
    expect(await createAnalysisEvidenceAdapter(api).resolve(ids.run, factRequest)).toEqual({ kind: 'rateLimited', retryAfterSeconds: 12 });
  });

  it('answers storage unavailable', async () => {
    const { api } = setup(() => problem(503, 'evidence-storage-unavailable'));
    expect(await createAnalysisEvidenceAdapter(api).resolve(ids.run, factRequest)).toEqual({ kind: 'storageUnavailable' });
  });

  it('answers offline when the request cannot reach the platform', async () => {
    const { api } = setup(() => {
      throw new TypeError('Failed to fetch');
    });
    expect(await createAnalysisEvidenceAdapter(api).resolve(ids.run, factRequest)).toEqual({ kind: 'offline' });
  });

  it('answers error for other failures', async () => {
    const { api } = setup(() => problem(500, 'boom'));
    expect(await createAnalysisEvidenceAdapter(api).resolve(ids.run, factRequest)).toEqual({ kind: 'error' });
  });

  it('lets an ended session pass through', async () => {
    const { api } = setup(() => problem(401, 'unauthorized'));
    await expect(createAnalysisEvidenceAdapter(api).resolve(ids.run, factRequest)).rejects.toBeInstanceOf(PlatformFailure);
  });
});
