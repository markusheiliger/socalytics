import 'fake-indexeddb/auto';
import { File } from './nodeFile';
import { IDBFactory } from 'fake-indexeddb';
import { beforeEach, describe, expect, it } from 'vitest';
import { createJournal, type JournalRecord } from '../../../../src/uploads/worker/journal';
import type { WorkerToMainMessage } from '../../../../src/uploads/worker/protocol';
import { createUploadWorkerHandler } from '../../../../src/uploads/worker/uploadWorker';

const record = (sessionId: string, memberId = 'm1'): JournalRecord => ({
  memberId, matchId: 'x', teamId: 't', uploadSessionId: sessionId, startKey: `k${sessionId}`, completionKey: 'c', fileName: 'a', fileSizeBytes: 1,
  fileLastModified: 1, partSizeBytes: 1, partCount: 1, contentDigest: 'd', confirmedParts: [], updatedAt: 'now',
});

describe('upload worker dispatch', () => {
  beforeEach(() => {
    globalThis.indexedDB = new IDBFactory();
  });

  it('prepares and verifies a file', async () => {
    const posted: WorkerToMainMessage[] = [];
    const handle = createUploadWorkerHandler({ post: (m) => posted.push(m) });
    const file = new File([new Uint8Array(20)], 'a.mp4');
    await handle({ type: 'prepare', jobId: 'j', file, partSizeBytes: 10 });
    const prepared = posted.find((m) => m.type === 'prepared');
    expect(prepared).toMatchObject({ partCount: 2, partSizeBytes: 10 });
    expect(posted.filter((m) => m.type === 'prepareProgress')).toHaveLength(2);

    await handle({ type: 'verify', jobId: 'v', file, partSizeBytes: 10, expectedContentDigest: (prepared as { contentDigest: string }).contentDigest });
    await handle({ type: 'verify', jobId: 'v2', file, partSizeBytes: 10, expectedContentDigest: 'other' });
    expect(posted).toContainEqual({ type: 'verified', jobId: 'v' });
    expect(posted).toContainEqual({ type: 'verifyFailed', jobId: 'v2', reason: 'fileMismatch' });
  });

  it('replies prepareFailed for an unreadable file', async () => {
    const posted: WorkerToMainMessage[] = [];
    const handle = createUploadWorkerHandler({ post: (m) => posted.push(m) });
    await handle({ type: 'prepare', jobId: 'j', file: new File([], 'empty') });
    expect(posted).toEqual([{ type: 'prepareFailed', jobId: 'j', reason: 'readError' }]);
  });

  it('puts, lists, deletes, and purges journal records', async () => {
    const posted: WorkerToMainMessage[] = [];
    const handle = createUploadWorkerHandler({ post: (m) => posted.push(m), journal: createJournal() });
    await handle({ type: 'journalPut', record: record('s1') });
    await handle({ type: 'journalPut', record: record('s2') });
    await handle({ type: 'journalPut', record: record('s3', 'm2') });
    await handle({ type: 'journalDelete', memberId: 'm1', uploadSessionId: 's1' });
    await handle({ type: 'journalList', memberId: 'm1' });
    expect(posted.at(-1)).toMatchObject({ type: 'journalRecords', memberId: 'm1' });
    expect((posted.at(-1) as unknown as { records: JournalRecord[] }).records.map((r) => r.uploadSessionId)).toEqual(['s2']);
    await handle({ type: 'purgeMember', memberId: 'm1' });
    expect(posted.at(-1)).toEqual({ type: 'purged', memberId: 'm1' });
    await handle({ type: 'journalList', memberId: 'm2' });
    expect((posted.at(-1) as unknown as { records: JournalRecord[] }).records).toHaveLength(1);
  });

  it('purgeMember aborts the transfers of the member', async () => {
    const posted: WorkerToMainMessage[] = [];
    const handle = createUploadWorkerHandler({
      post: (m) => posted.push(m),
      journal: createJournal(),
      transfer: { createRequest: () => { throw new Error('unused'); } },
    });
    await handle({ type: 'journalPut', record: record('s1') });
    const file = new File([new Uint8Array(10)], 'a');
    await handle({
      type: 'transfer', jobId: 'j', uploadSessionId: 's1', file, confirmedParts: [],
      declaration: { totalSizeBytes: 10, partSizeBytes: 10, partCount: 1, partDigests: ['d'], contentDigest: 'd' },
    });
    await handle({ type: 'purgeMember', memberId: 'm1' });
    posted.length = 0;
    await handle({ type: 'grants', jobId: 'j', grants: [] });
    expect(posted).toEqual([]);
  });

  it('reports journalFailed when storage is unavailable', async () => {
    const posted: WorkerToMainMessage[] = [];
    const journal = { put: () => Promise.reject(new Error('quota')) } as unknown as ReturnType<typeof createJournal>;
    const handle = createUploadWorkerHandler({ post: (m) => posted.push(m), journal });
    await handle({ type: 'journalPut', record: record('s1') });
    expect(posted).toEqual([{ type: 'journalFailed', operation: 'journalPut', reason: 'storageUnavailable' }]);
  });
});
