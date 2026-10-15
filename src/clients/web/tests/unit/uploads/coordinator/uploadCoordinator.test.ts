import { describe, expect, it, vi } from 'vitest';
import { PlatformFailure } from '../../../../src/platform/failure';
import {
  createUploadCoordinator,
  isVideoFile,
  type CoordinatorDependencies,
  type UploadCoordinator,
  type UploadJob,
  type WorkerLike,
} from '../../../../src/uploads/uploadCoordinator';
import type { JournalRecord } from '../../../../src/uploads/worker/journal';
import type { MainToWorkerMessage, WorkerToMainMessage } from '../../../../src/uploads/worker/protocol';
import { File } from '../worker/nodeFile';

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

class FakeWorker implements WorkerLike {
  readonly posted: MainToWorkerMessage[] = [];
  onmessage: ((event: { data: WorkerToMainMessage }) => void) | null = null;
  terminated = false;
  postMessage(message: MainToWorkerMessage) {
    this.posted.push(message);
  }
  terminate() {
    this.terminated = true;
  }
  emit(message: WorkerToMainMessage) {
    this.onmessage?.({ data: message });
  }
  ofType<T extends MainToWorkerMessage['type']>(type: T) {
    return this.posted.filter((m) => m.type === type) as Extract<MainToWorkerMessage, { type: T }>[];
  }
}

const session = (overrides: Record<string, unknown> = {}) => ({
  id: 'session-1',
  matchId: 'match-1',
  teamId: 'team-1',
  state: 'pending' as const,
  recording: { displayName: 'First half', contentType: 'video/mp4' },
  totalSizeBytes: 10,
  partSizeBytes: 10,
  partCount: 1,
  contentDigest: 'sha-256:abc',
  createdAt: new Date('2030-01-01T00:00:00Z'),
  expiresAt: new Date('2030-01-02T00:00:00Z'),
  version: 1,
  ...overrides,
});

const grant = (partNumber: number) => ({
  partNumber,
  method: 'PUT',
  url: `https://storage.example/part/${partNumber}`,
  requiredHeaders: { 'x-amz-checksum-sha256': 'x' },
  expiresAt: new Date('2030-01-01T01:00:00Z'),
});

const mapping = [{ mediaStartSeconds: 0, mediaEndSeconds: 10, matchStartSeconds: 0 }];
const video = (name = 'half.mp4', size = 10) => new File([new Uint8Array(size)], name, { type: 'video/mp4', lastModified: 5 });

function setup(overrides: Partial<CoordinatorDependencies> = {}) {
  const worker = new FakeWorker();
  let counter = 0;
  const uploads = {
    start: vi.fn(async (..._args: unknown[]) => ({ session: session(), grants: [grant(1)] })),
    getSession: vi.fn(async (..._args: unknown[]) => session()),
    issueGrants: vi.fn(async (_m: string, _s: string, parts: number[]) => parts.map(grant)),
    complete: vi.fn(async (..._args: unknown[]) => undefined),
  };
  const lineage = { getLineage: vi.fn(async (..._args: unknown[]) => ({}) as never) };
  const coordinator: UploadCoordinator = createUploadCoordinator({
    memberId: 'member-1',
    uploads: uploads as never,
    lineage: lineage as never,
    createWorker: () => worker,
    newKey: () => `key-${++counter}`,
    ...overrides,
  });
  const job = (id: string): UploadJob => coordinator.getSnapshot().find((j) => j.localId === id)!;
  const only = (): UploadJob => coordinator.getSnapshot()[0]!;
  const add = (file = video()) => {
    const { added } = coordinator.addFiles('match-1', 'team-1', [{ file, displayName: 'First half' }]);
    return added[0]!;
  };
  const prepare = async (id: string) => {
    worker.emit({ type: 'prepared', jobId: id, partDigests: ['sha-256:p1'], contentDigest: 'sha-256:abc', partSizeBytes: 10, partCount: 1 });
    await flush();
  };
  const transferring = async () => {
    const id = add();
    await prepare(id);
    return id;
  };
  return { worker, uploads, lineage, coordinator, job, only, add, prepare, transferring };
}

describe('upload coordinator', () => {
  it('recognises video files by type', () => {
    expect(isVideoFile(video())).toBe(true);
    expect(isVideoFile(new File(['x'], 'a.txt', { type: 'text/plain' }))).toBe(false);
    expect(isVideoFile(new File(['x'], 'a'))).toBe(false);
  });

  it('refuses non-video files and empty types without creating a job', () => {
    const { coordinator, worker } = setup();
    const result = coordinator.addFiles('match-1', 'team-1', [
      { file: new File(['x'], 'a.txt', { type: 'text/plain' }), displayName: 'a' },
      { file: new File(['x'], 'b'), displayName: 'b' },
    ]);
    expect(result.added).toEqual([]);
    expect(result.refused.map((r) => r.reason)).toEqual(['notVideo', 'notVideo']);
    expect(coordinator.getSnapshot()).toEqual([]);
    expect(worker.posted).toEqual([]);
  });

  it('prepares, journals the start key before starting, and transfers', async () => {
    const { worker, uploads, only, add, prepare } = setup();
    let postedBeforeStart = 0;
    uploads.start.mockImplementationOnce(async () => {
      postedBeforeStart = worker.ofType('journalPut').length;
      return { session: session(), grants: [grant(1)] };
    });
    const id = add();
    expect(only().state).toBe('preparing');
    expect(worker.ofType('prepare')).toHaveLength(1);

    worker.emit({ type: 'prepareProgress', jobId: id, bytesHashed: 4, totalBytes: 10 });
    expect(only().bytesHashed).toBe(4);

    await prepare(id);
    const put = worker.ofType('journalPut');
    expect(postedBeforeStart).toBe(1);
    expect(put[0]!.record.startKey).toBe(only().startKey);
    expect(put[0]!.record.uploadSessionId).toBeUndefined();
    expect(uploads.start).toHaveBeenCalledTimes(1);
    expect(uploads.start.mock.calls[0]![3]).toBe(only().startKey);
    expect(uploads.start.mock.calls[0]![1]).toMatchObject({ displayName: 'First half', contentType: 'video/mp4' });

    expect(only().state).toBe('transferring');
    expect(only().uploadSessionId).toBe('session-1');
    expect(worker.ofType('journalPut').at(-1)!.record.uploadSessionId).toBe('session-1');
    expect(worker.ofType('transfer')[0]!.uploadSessionId).toBe('session-1');
    expect(worker.ofType('grants')[0]!.grants[0]!.expiresAt).toBe('2030-01-01T01:00:00.000Z');
  });

  it('answers needGrants through the platform', async () => {
    const { worker, uploads, transferring } = setup();
    const id = await transferring();
    worker.emit({ type: 'needGrants', jobId: id, partNumbers: [2, 3] });
    await flush();
    expect(uploads.issueGrants).toHaveBeenCalledWith('match-1', 'session-1', [2, 3]);
    expect(worker.ofType('grants').at(-1)!.grants.map((g) => g.partNumber)).toEqual([2, 3]);
  });

  it('pauses when granting fails because the platform is unreachable', async () => {
    const { worker, uploads, transferring, job } = setup();
    const id = await transferring();
    uploads.issueGrants.mockRejectedValueOnce(new PlatformFailure('unreachable'));
    worker.emit({ type: 'needGrants', jobId: id, partNumbers: [1] });
    await flush();
    expect(job(id)).toMatchObject({ state: 'paused', reason: 'unreachable', failureKind: 'unreachable' });
  });

  it('pauses without losing the job when a request is blocked by a required update', async () => {
    const { worker, uploads, transferring, job } = setup();
    const id = await transferring();
    uploads.issueGrants.mockRejectedValueOnce(new PlatformFailure('updateRequired'));
    worker.emit({ type: 'needGrants', jobId: id, partNumbers: [1] });
    await flush();
    expect(job(id)).toMatchObject({ state: 'paused', reason: 'unreachable', failureKind: 'updateRequired' });
  });

  it('pauses a transferring job when set unreachable, which a required update does', async () => {
    const { transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setUnreachable(true);
    expect(job(id)).toMatchObject({ state: 'paused', reason: 'unreachable' });
  });

  it('completes with the completion key when the mapping exists first', async () => {
    const { worker, uploads, transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setMapping(id, mapping);
    worker.emit({ type: 'transferProgress', jobId: id, bytesConfirmed: 5, bytesInFlight: 2, confirmedParts: [1] });
    expect(job(id)).toMatchObject({ bytesConfirmed: 5, bytesInFlight: 2, confirmedParts: [1] });
    worker.emit({ type: 'transferred', jobId: id });
    await flush();
    expect(uploads.complete).toHaveBeenCalledWith('match-1', 'session-1', mapping, job(id).completionKey);
    expect(worker.ofType('journalDelete')).toHaveLength(1);
    expect(job(id).state).toBe('completed');
  });

  it('waits for the mapping after the transfer and completes when it is set', async () => {
    const { worker, uploads, transferring, job, coordinator } = setup();
    const id = await transferring();
    worker.emit({ type: 'transferred', jobId: id });
    expect(job(id)).toMatchObject({ state: 'awaitingMapping', transferred: true });
    expect(uploads.complete).not.toHaveBeenCalled();
    coordinator.setMapping(id, mapping);
    await flush();
    expect(uploads.complete).toHaveBeenCalledTimes(1);
    expect(job(id).state).toBe('completed');
  });

  it('counts a completion replay as completed after reading the lineage', async () => {
    const { worker, uploads, lineage, transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setMapping(id, mapping);
    uploads.complete.mockRejectedValueOnce(new PlatformFailure('uploadCompleted', { status: 409 }));
    worker.emit({ type: 'transferred', jobId: id });
    await flush();
    expect(lineage.getLineage).toHaveBeenCalledWith('match-1');
    expect(job(id).state).toBe('completed');
  });

  it.each(['unreachable', 'storageUnavailable', 'sessionEnded'] as const)(
    'pauses a completion that fails with %s',
    async (kind) => {
      const { worker, uploads, transferring, job, coordinator } = setup();
      const id = await transferring();
      coordinator.setMapping(id, mapping);
      uploads.complete.mockRejectedValueOnce(new PlatformFailure(kind));
      worker.emit({ type: 'transferred', jobId: id });
      await flush();
      expect(job(id)).toMatchObject({ state: 'paused', reason: kind, transferred: true });
    },
  );

  it('completes again from a transferred pause with the same key and without transferring', async () => {
    const { worker, uploads, transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setMapping(id, mapping);
    uploads.complete.mockRejectedValueOnce(new PlatformFailure('unreachable'));
    worker.emit({ type: 'transferred', jobId: id });
    await flush();
    const transfers = worker.ofType('transfer').length;
    coordinator.resume(id);
    await flush();
    expect(worker.ofType('transfer')).toHaveLength(transfers);
    expect(uploads.complete).toHaveBeenCalledTimes(2);
    expect(uploads.complete.mock.calls[1]![3]).toBe(uploads.complete.mock.calls[0]![3]);
    expect(job(id).state).toBe('completed');
  });

  it('refuses with partsIncomplete or partMismatch and clears fully confirmed parts', async () => {
    const { worker, uploads, transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setMapping(id, mapping);
    worker.emit({ type: 'transferProgress', jobId: id, bytesConfirmed: 10, bytesInFlight: 0, confirmedParts: [1] });
    uploads.complete.mockRejectedValueOnce(new PlatformFailure('uploadPartsRefused', { code: 'upload-parts-incomplete' }));
    worker.emit({ type: 'transferred', jobId: id });
    await flush();
    expect(job(id)).toMatchObject({ state: 'refused', reason: 'partsIncomplete', confirmedParts: [], transferred: false });

    coordinator.retry(id);
    expect(job(id).state).toBe('transferring');
    uploads.complete.mockRejectedValueOnce(new PlatformFailure('uploadPartsRefused', { code: 'upload-part-mismatch' }));
    worker.emit({ type: 'transferred', jobId: id });
    await flush();
    expect(job(id)).toMatchObject({ state: 'refused', reason: 'partMismatch' });
  });

  it.each([
    ['upload-object-mismatch', 'objectMismatch'],
    ['integrity-evidence-unavailable', 'integrityUnavailable'],
  ] as const)('fails an integrity failure with code %s', async (code, reason) => {
    const { worker, uploads, transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setMapping(id, mapping);
    uploads.complete.mockRejectedValueOnce(new PlatformFailure('uploadIntegrityFailed', { code }));
    worker.emit({ type: 'transferred', jobId: id });
    await flush();
    expect(job(id)).toMatchObject({ state: 'failed', reason });
  });

  it('ends an expired session as expired and keeps its journal record until acknowledged', async () => {
    const { worker, uploads, transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setMapping(id, mapping);
    uploads.complete.mockRejectedValueOnce(new PlatformFailure('uploadExpired'));
    worker.emit({ type: 'transferred', jobId: id });
    await flush();
    expect(job(id).state).toBe('expired');
    expect(worker.ofType('journalDelete')).toHaveLength(0);
    coordinator.acknowledgeExpiry(id);
    expect(worker.ofType('journalDelete')).toHaveLength(1);
    expect(coordinator.getSnapshot()).toEqual([]);
  });

  it('marks every job of the team notPermitted', async () => {
    const { uploads, add, prepare, coordinator } = setup();
    const first = add();
    add(video('second.mp4'));
    uploads.start.mockRejectedValueOnce(new PlatformFailure('notPermitted', { status: 403 }));
    await prepare(first);
    expect(coordinator.getSnapshot().map((j) => j.state)).toEqual(['notPermitted', 'notPermitted']);
  });

  it('cancels a start the platform refuses as invalid', async () => {
    const { worker, uploads, add, prepare, only } = setup();
    uploads.start.mockRejectedValueOnce(new PlatformFailure('validation', { status: 422 }));
    await prepare(add());
    expect(only()).toMatchObject({ state: 'cancelled', failureKind: 'validation' });
    expect(worker.ofType('journalDelete')).toHaveLength(1);
  });

  it('keeps a failed start ready and retries it with the same start key', async () => {
    const { uploads, add, prepare, only, coordinator } = setup();
    uploads.start.mockRejectedValueOnce(new PlatformFailure('unreachable'));
    await prepare(add());
    expect(only()).toMatchObject({ state: 'ready', waiting: 'unreachable' });
    coordinator.setUnreachable(true);
    coordinator.setUnreachable(false);
    await flush();
    expect(uploads.start).toHaveBeenCalledTimes(2);
    expect(uploads.start.mock.calls[1]![3]).toBe(uploads.start.mock.calls[0]![3]);
    expect(only().state).toBe('transferring');
  });

  it('retries a storage-unavailable start after the retry interval', async () => {
    let timer: (() => void) | undefined;
    const { uploads, add, prepare, only } = setup({
      setTimer: (callback) => {
        timer = callback;
        return 1;
      },
      clearTimer: () => undefined,
    });
    uploads.start.mockRejectedValueOnce(new PlatformFailure('storageUnavailable', { status: 503 }));
    await prepare(add());
    expect(only()).toMatchObject({ state: 'ready', waiting: 'storageUnavailable' });
    timer!();
    await flush();
    expect(uploads.start).toHaveBeenCalledTimes(2);
    expect(only().state).toBe('transferring');
  });

  it('keeps a start that failed with an ended session ready and resumes after sign-in', async () => {
    const { uploads, add, prepare, only, coordinator } = setup();
    uploads.start.mockRejectedValueOnce(new PlatformFailure('sessionEnded', { status: 401 }));
    await prepare(add());
    expect(only()).toMatchObject({ state: 'ready', waiting: 'sessionEnded' });
    coordinator.setSessionActive(false);
    coordinator.setSessionActive(true);
    await flush();
    expect(only().state).toBe('transferring');
  });

  it('pauses transfers while the platform is unreachable and resumes afterwards', async () => {
    const { worker, transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setUnreachable(true);
    expect(job(id)).toMatchObject({ state: 'paused', reason: 'unreachable' });
    expect(worker.ofType('abort')).toHaveLength(1);
    coordinator.setUnreachable(false);
    expect(job(id).state).toBe('transferring');
    expect(worker.ofType('transfer')).toHaveLength(2);
  });

  it('pauses on sessionEnded and resumes when the session is active again', async () => {
    const { transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.setSessionActive(false);
    expect(job(id)).toMatchObject({ state: 'paused', reason: 'sessionEnded' });
    coordinator.setSessionActive(true);
    expect(job(id).state).toBe('transferring');
  });

  it('follows worker pauses and retries storageUnavailable on a timer', async () => {
    let timer: (() => void) | undefined;
    const { worker, transferring, job } = setup({
      setTimer: (callback) => {
        timer = callback;
        return 1;
      },
      clearTimer: () => undefined,
    });
    const id = await transferring();
    worker.emit({ type: 'paused', jobId: id, reason: 'storageUnavailable' });
    expect(job(id)).toMatchObject({ state: 'paused', reason: 'storageUnavailable' });
    timer!();
    expect(job(id).state).toBe('transferring');
  });

  it('pauses after retries are exhausted and lets the member retry', async () => {
    const { worker, transferring, job, coordinator } = setup();
    const id = await transferring();
    worker.emit({ type: 'paused', jobId: id, reason: 'retriesExhausted' });
    expect(job(id)).toMatchObject({ state: 'paused', reason: 'retriesExhausted' });
    coordinator.retry(id);
    expect(job(id).state).toBe('transferring');
  });

  it('lets the member pause and resume', async () => {
    const { transferring, job, coordinator } = setup();
    const id = await transferring();
    coordinator.pause(id);
    expect(job(id)).toMatchObject({ state: 'paused', reason: 'memberRequested' });
    coordinator.resume(id);
    expect(job(id).state).toBe('transferring');
  });

  it('fails a changed file', async () => {
    const { worker, transferring, job } = setup();
    const id = await transferring();
    worker.emit({ type: 'failed', jobId: id, reason: 'fileChanged' });
    expect(job(id)).toMatchObject({ state: 'failed', reason: 'fileChanged' });
    expect(worker.ofType('journalDelete')).toHaveLength(1);
  });

  it('transfers one job at a time and starts the next after completion', async () => {
    const { worker, uploads, add, prepare, job, coordinator } = setup();
    uploads.start
      .mockResolvedValueOnce({ session: session({ id: 'session-1' }), grants: [grant(1)] })
      .mockResolvedValueOnce({ session: session({ id: 'session-2' }), grants: [grant(1)] });
    const first = add();
    const second = add(video('second.mp4'));
    await prepare(first);
    await prepare(second);
    expect(job(first).state).toBe('transferring');
    expect(job(second).state).toBe('queued');
    coordinator.setMapping(first, mapping);
    worker.emit({ type: 'transferred', jobId: first });
    await flush();
    expect(job(second).state).toBe('transferring');
  });

  it('removes only jobs that have no upload session yet', async () => {
    const { worker, add, only, coordinator, transferring } = setup();
    const id = add();
    expect(coordinator.removeFromQueue(id)).toBe(true);
    expect(only().state).toBe('cancelled');
    expect(worker.ofType('abort')).toHaveLength(1);
    const uploading = await transferring();
    expect(coordinator.removeFromQueue(uploading)).toBe(false);
  });

  it('abandons a job, forgets its journal record, and drops it', async () => {
    const { worker, transferring, coordinator } = setup();
    const id = await transferring();
    coordinator.abandon(id);
    expect(coordinator.getSnapshot()).toEqual([]);
    expect(worker.ofType('journalDelete')[0]).toMatchObject({ memberId: 'member-1', uploadSessionId: 'session-1' });
    expect(worker.ofType('abort')).toHaveLength(1);
  });

  it('purges the member and resolves when the worker confirms', async () => {
    const { worker, transferring, coordinator } = setup();
    await transferring();
    const purged = coordinator.purgeMember();
    expect(coordinator.getSnapshot()).toEqual([]);
    expect(worker.ofType('purgeMember')[0]).toMatchObject({ memberId: 'member-1' });
    worker.emit({ type: 'purged', memberId: 'member-1' });
    await expect(purged).resolves.toBeUndefined();
  });

  describe('after a reload', () => {
    const record = (overrides: Partial<JournalRecord> = {}): JournalRecord => ({
      memberId: 'member-1',
      matchId: 'match-1',
      teamId: 'team-1',
      uploadSessionId: 'session-1',
      startKey: 'start-1',
      completionKey: 'completion-1',
      fileName: 'half.mp4',
      fileSizeBytes: 10,
      fileLastModified: 5,
      partSizeBytes: 10,
      partCount: 1,
      contentDigest: 'sha-256:abc',
      confirmedParts: [],
      sessionExpiresAt: '2030-01-02T00:00:00.000Z',
      updatedAt: '2030-01-01T00:00:00.000Z',
      ...overrides,
    });
    const load = async (ctx: ReturnType<typeof setup>, records: JournalRecord[]) => {
      const loaded = ctx.coordinator.loadJournal('match-1');
      expect(ctx.worker.ofType('journalList').at(-1)).toMatchObject({ memberId: 'member-1', matchId: 'match-1' });
      ctx.worker.emit({ type: 'journalRecords', memberId: 'member-1', matchId: 'match-1', records });
      await loaded;
      await flush();
    };

    it('shows journal records as jobs that need their file and reads the session', async () => {
      const ctx = setup();
      await load(ctx, [record()]);
      expect(ctx.only()).toMatchObject({ state: 'needsFile', displayName: 'First half', contentType: 'video/mp4' });
      expect(ctx.uploads.getSession).toHaveBeenCalledWith('match-1', 'session-1');
    });

    it('ignores records of other members and duplicates', async () => {
      const ctx = setup();
      await load(ctx, [record(), record({ memberId: 'other', startKey: 'start-2', uploadSessionId: 'session-2' })]);
      await load(ctx, [record()]);
      expect(ctx.coordinator.getSnapshot()).toHaveLength(1);
    });

    it('shows completed and expired sessions as such', async () => {
      const ctx = setup();
      ctx.uploads.getSession
        .mockResolvedValueOnce(session({ state: 'completed' }))
        .mockResolvedValueOnce(session({ id: 'session-2', state: 'expired' }));
      await load(ctx, [record(), record({ startKey: 'start-2', uploadSessionId: 'session-2' })]);
      expect(ctx.coordinator.getSnapshot().map((j) => j.state).sort()).toEqual(['completed', 'expired']);
    });

    it('verifies a reselected file and queues the job', async () => {
      const ctx = setup();
      await load(ctx, [record({ confirmedParts: [1] })]);
      const id = ctx.only().localId;
      await ctx.coordinator.reselectFile(id, video());
      expect(ctx.only().state).toBe('preparing');
      expect(ctx.worker.ofType('verify')[0]).toMatchObject({ expectedContentDigest: 'sha-256:abc', partSizeBytes: 10 });
      ctx.worker.emit({ type: 'verified', jobId: id });
      expect(ctx.only().state).toBe('transferring');
      expect(ctx.worker.ofType('transfer').at(-1)!.confirmedParts).toEqual([1]);
    });

    it('refuses a file of another size at once and one of another content after verifying', async () => {
      const ctx = setup();
      await load(ctx, [record()]);
      const id = ctx.only().localId;
      await ctx.coordinator.reselectFile(id, video('half.mp4', 11));
      expect(ctx.only()).toMatchObject({ state: 'refused', reason: 'fileMismatch' });
      expect(ctx.worker.ofType('verify')).toHaveLength(0);
      await ctx.coordinator.reselectFile(id, video());
      ctx.worker.emit({ type: 'verifyFailed', jobId: id, reason: 'digestMismatch' } as unknown as WorkerToMainMessage);
      expect(ctx.only()).toMatchObject({ state: 'refused', reason: 'fileMismatch' });
    });

    it('prepares a reselected file of a record without a session and starts with its start key', async () => {
      const ctx = setup();
      await load(ctx, [record({ uploadSessionId: undefined })]);
      const id = ctx.only().localId;
      await ctx.coordinator.reselectFile(id, video());
      expect(ctx.worker.ofType('prepare').at(-1)).toMatchObject({ partSizeBytes: 10 });
      await ctx.prepare(id);
      expect(ctx.uploads.start.mock.calls[0]![3]).toBe('start-1');
    });

    it('marks the team notPermitted when reading the session is forbidden', async () => {
      const ctx = setup();
      ctx.uploads.getSession.mockRejectedValueOnce(new PlatformFailure('notPermitted', { status: 403 }));
      await load(ctx, [record()]);
      expect(ctx.only().state).toBe('notPermitted');
    });

    it('flags a journal the browser refused', () => {
      const ctx = setup();
      expect(ctx.coordinator.journalAvailable()).toBe(true);
      ctx.add();
      ctx.worker.emit({ type: 'journalFailed', operation: 'journalPut', reason: 'storageUnavailable' });
      expect(ctx.coordinator.journalAvailable()).toBe(false);
    });
  });

  it('notifies subscribers with immutable snapshots and stops after dispose', () => {
    const { add, coordinator, worker } = setup();
    const listener = vi.fn();
    const unsubscribe = coordinator.subscribe(listener);
    const before = coordinator.getSnapshot();
    add();
    expect(listener).toHaveBeenCalled();
    expect(coordinator.getSnapshot()).not.toBe(before);
    unsubscribe();
    const calls = listener.mock.calls.length;
    add(video('b.mp4'));
    expect(listener.mock.calls.length).toBe(calls);
    coordinator.dispose();
    expect(worker.terminated).toBe(true);
  });
});
