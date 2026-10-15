import { createRecordingLineageAdapter } from '../../../../src/platform/adapters/recordingLineageAdapter';
import { bodyOf, header, ids, ok, problem, setup } from './support';

const recording = { displayName: 'half1.mp4', contentType: 'video/mp4' };
const version = {
  id: ids.version,
  matchId: ids.match,
  teamId: ids.team,
  uploadSessionId: ids.session,
  recording,
  totalSizeBytes: 40,
  partSizeBytes: 40,
  partCount: 1,
  contentDigest: 'sha-256-parts:40:1:ab',
  createdAt: '2026-10-01T10:05:00Z',
};
const spans = [{ mediaStartSeconds: 0, mediaEndSeconds: 60, matchStartSeconds: 5 }];
const mapping = { id: ids.mapping, recordingVersionId: ids.version, matchId: ids.match, spans, digest: 'sha-256:aa', createdAt: '2026-10-01T10:05:00Z' };
const set = {
  id: ids.set,
  matchId: ids.match,
  teamId: ids.team,
  finalizedAt: '2026-10-01T12:00:00Z',
  members: [
    { position: 1, recordingVersionId: ids.version, recordingContentDigest: 'sha-256-parts:40:1:ab', timelineMappingId: ids.mapping, timelineMappingDigest: 'sha-256:aa' },
  ],
};

describe('recordingLineageAdapter', () => {
  it('reads the lineage', async () => {
    const { api, calls } = setup(() =>
      ok(200, { matchId: ids.match, teamId: ids.team, recordingVersions: [version], timelineMappings: [mapping], recordingSetVersions: [set] }),
    );
    const lineage = await createRecordingLineageAdapter(api).getLineage(ids.match);
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/matches/${ids.match}/recording-lineage`);
    expect(lineage.recordingVersions[0].id).toBe(ids.version);
    expect(lineage.timelineMappings[0].spans).toEqual(spans);
    expect(lineage.recordingSetVersions[0].members[0]).toMatchObject({ position: 1, timelineMappingId: ids.mapping });
  });

  it('reads a recording version', async () => {
    const { api, calls } = setup(() => ok(200, version));
    const result = await createRecordingLineageAdapter(api).getVersion(ids.match, ids.version);
    expect(calls[0].url).toContain(`/recording-versions/${ids.version}`);
    expect(result.recording).toEqual({ displayName: 'half1.mp4', description: undefined, contentType: 'video/mp4' });
  });

  it('revises a mapping with the caller key', async () => {
    const { api, calls } = setup(() => ok(201, mapping));
    const result = await createRecordingLineageAdapter(api).reviseMapping(ids.match, ids.version, spans, 'rev-1');
    expect(calls[0].url).toContain(`/recording-versions/${ids.version}/timeline-mappings`);
    expect(header(calls[0], 'Idempotency-Key')).toBe('rev-1');
    expect(bodyOf(calls[0])).toEqual({ spans });
    expect(result.id).toBe(ids.mapping);
  });

  it('keeps the platform reason for a span of a revision', async () => {
    const { api } = setup(() => problem(400, 'validation-failed', [{ field: 'spans[0].mediaEndSeconds', code: 'overlap' }]));
    const error = await createRecordingLineageAdapter(api).reviseMapping(ids.match, ids.version, spans, 'k').catch((e: unknown) => e);
    expect(error).toMatchObject({ kind: 'validation', fieldErrors: [{ field: 'spans[0].mediaEndSeconds', code: 'overlap' }] });
  });

  it('finalizes ordered members with the caller key', async () => {
    const { api, calls } = setup(() => ok(201, set));
    const result = await createRecordingLineageAdapter(api).finalize(
      ids.match,
      [{ recordingVersionId: ids.version, timelineMappingId: ids.mapping }],
      'fin-1',
    );
    expect(calls[0].url).toContain(`/matches/${ids.match}/recording-set-versions`);
    expect(header(calls[0], 'Idempotency-Key')).toBe('fin-1');
    expect(bodyOf(calls[0])).toEqual({ members: [{ recordingVersionId: ids.version, timelineMappingId: ids.mapping }] });
    expect(result.finalizedAt).toBeInstanceOf(Date);
  });

  it('reads a recording-set version', async () => {
    const { api, calls } = setup(() => ok(200, set));
    const result = await createRecordingLineageAdapter(api).getSet(ids.match, ids.set);
    expect(calls[0].url).toContain(`/recording-set-versions/${ids.set}`);
    expect(result.members).toHaveLength(1);
  });

  it('maps a key reuse to a programming error', async () => {
    const { api } = setup(() => problem(409, 'idempotency-key-reused'));
    await expect(createRecordingLineageAdapter(api).finalize(ids.match, [], 'k')).rejects.toMatchObject({ kind: 'programmingError' });
  });
});
