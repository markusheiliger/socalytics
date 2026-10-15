import { createRecordingUploadAdapter } from '../../../../src/platform/adapters/recordingUploadAdapter';
import { bodyOf, header, ids, ok, problem, setup } from './support';

const recording = { displayName: 'half1.mp4', description: 'First half', contentType: 'video/mp4' };
const session = {
  id: ids.session,
  matchId: ids.match,
  teamId: ids.team,
  state: 'pending',
  recording,
  totalSizeBytes: 40,
  partSizeBytes: 40,
  partCount: 1,
  contentDigest: 'sha-256-parts:40:1:ab',
  createdAt: '2026-10-01T10:00:00Z',
  expiresAt: '2026-10-02T10:00:00Z',
  version: 1,
};
const grants = {
  expiresAt: '2026-10-01T10:15:00Z',
  parts: [{ partNumber: 1, method: 'PUT', url: 'http://store/part?X-Amz-Signature=s', requiredHeaders: { 'x-amz-checksum-sha256': 'abc=' } }],
};
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
const declaration = { totalSizeBytes: 40, partSizeBytes: 40, partDigests: ['sha-256:' + 'a'.repeat(64)] };

describe('recordingUploadAdapter', () => {
  it('starts an upload with the caller key and maps the session and grants', async () => {
    const { api, calls } = setup(() => ok(201, { session, grants }));
    const started = await createRecordingUploadAdapter(api).start(ids.match, { ...recording }, declaration, 'key-1');
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/matches/${ids.match}/upload-sessions`);
    expect(header(calls[0], 'Idempotency-Key')).toBe('key-1');
    expect(bodyOf(calls[0])).toEqual({ recording, ...declaration });
    expect(started.session).toMatchObject({ id: ids.session, state: 'pending', partCount: 1, version: 1 });
    expect(started.grants).toHaveLength(1);
    expect(started.grants[0]).toMatchObject({ partNumber: 1, method: 'PUT', url: grants.parts[0].url });
    expect(started.grants[0].requiredHeaders).toEqual({ 'x-amz-checksum-sha256': 'abc=' });
    expect(started.grants[0].expiresAt).toEqual(new Date(grants.expiresAt));
  });

  it('starts without grants when the platform sends none', async () => {
    const { api } = setup(() => ok(200, { session }));
    expect((await createRecordingUploadAdapter(api).start(ids.match, recording, declaration, 'k')).grants).toEqual([]);
  });

  it('reads a session', async () => {
    const { api, calls } = setup(() => ok(200, { ...session, state: 'completed', completedAt: '2026-10-01T11:00:00Z' }));
    const result = await createRecordingUploadAdapter(api).getSession(ids.match, ids.session);
    expect(calls[0].url).toContain(`/upload-sessions/${ids.session}`);
    expect(result.state).toBe('completed');
    expect(result.completedAt).toBeInstanceOf(Date);
  });

  it('issues grants for the given part numbers without an idempotency key', async () => {
    const { api, calls } = setup(() => ok(200, grants));
    const result = await createRecordingUploadAdapter(api).issueGrants(ids.match, ids.session, [1, 3]);
    expect(calls[0].url).toContain(`/upload-sessions/${ids.session}/grants`);
    expect(bodyOf(calls[0])).toEqual({ partNumbers: [1, 3] });
    expect(header(calls[0], 'Idempotency-Key')).toBeUndefined();
    expect(result[0].partNumber).toBe(1);
  });

  it('completes with the mapping and the caller key', async () => {
    const { api, calls } = setup(() => ok(200, { recordingVersion: version, timelineMapping: mapping }));
    const result = await createRecordingUploadAdapter(api).complete(ids.match, ids.session, spans, 'key-2');
    expect(calls[0].url).toContain(`/upload-sessions/${ids.session}/completion`);
    expect(header(calls[0], 'Idempotency-Key')).toBe('key-2');
    expect(bodyOf(calls[0])).toEqual({ timelineMapping: { spans } });
    expect(result.recordingVersion.id).toBe(ids.version);
    expect(result.timelineMapping.spans).toEqual(spans);
  });

  it('refuses an unknown session state as incompatible', async () => {
    const { api } = setup(() => ok(200, { ...session, state: 'weird' }));
    await expect(createRecordingUploadAdapter(api).getSession(ids.match, ids.session)).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it.each([
    ['400 declaration', () => problem(400, 'upload-declaration-invalid', [{ field: 'partDigests', code: 'invalid' }]), 'validation'],
    ['413', () => problem(413, 'too-large'), 'tooLarge'],
    ['415', () => problem(415, 'unsupported-type'), 'unsupportedType'],
    ['409 expired', () => problem(409, 'upload-session-expired'), 'uploadExpired'],
    ['409 completed', () => problem(409, 'upload-session-completed'), 'uploadCompleted'],
    ['409 parts incomplete', () => problem(409, 'upload-parts-incomplete'), 'uploadPartsRefused'],
    ['409 part mismatch', () => problem(409, 'upload-part-mismatch'), 'uploadPartsRefused'],
    ['409 object mismatch', () => problem(409, 'upload-object-mismatch'), 'uploadIntegrityFailed'],
    ['409 integrity evidence', () => problem(409, 'integrity-evidence-unavailable'), 'uploadIntegrityFailed'],
    [
      '503',
      () => new Response(JSON.stringify({ status: 503, code: 'storage-unavailable' }), { status: 503, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '0' } }),
      'storageUnavailable',
    ],
    ['400 key missing', () => problem(400, 'idempotency-key-missing'), 'programmingError'],
    ['409 key reused', () => problem(409, 'idempotency-key-reused'), 'programmingError'],
  ])('maps %s from an upload operation', async (_name, respond, kind) => {
    const { api } = setup(respond);
    const error = await createRecordingUploadAdapter(api).start(ids.match, recording, declaration, 'k').catch((e: unknown) => e);
    expect((error as { kind: string }).kind).toBe(kind);
  });

  it('keeps the field errors of an invalid declaration', async () => {
    const { api } = setup(() => problem(400, 'upload-declaration-invalid', [{ field: 'partDigests', code: 'invalid' }]));
    const error = await createRecordingUploadAdapter(api).start(ids.match, recording, declaration, 'k').catch((e: unknown) => e);
    expect((error as { fieldErrors: unknown }).fieldErrors).toEqual([{ field: 'partDigests', code: 'invalid' }]);
  });
});
