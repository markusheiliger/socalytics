import 'fake-indexeddb/auto';
import { IDBFactory } from 'fake-indexeddb';
import { beforeEach, describe, expect, it } from 'vitest';
import { createJournal, journalDatabaseName, type JournalRecord } from '../../../../src/uploads/worker/journal';

function record(overrides: Partial<JournalRecord> = {}): JournalRecord {
  return {
    memberId: 'm1',
    matchId: 'match1',
    teamId: 't1',
    startKey: 'start-1',
    completionKey: 'complete-1',
    fileName: 'a.mp4',
    fileSizeBytes: 100,
    fileLastModified: 5,
    partSizeBytes: 100,
    partCount: 1,
    contentDigest: 'sha-256-parts:100:1:abc',
    confirmedParts: [],
    updatedAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  };
}

describe('upload journal', () => {
  beforeEach(() => {
    globalThis.indexedDB = new IDBFactory();
  });

  it('stores a record under its start key until the session exists, then under the session', async () => {
    const journal = createJournal();
    await journal.put(record());
    expect(await journal.list('m1')).toHaveLength(1);

    await journal.put(record({ uploadSessionId: 's1', sessionExpiresAt: '2026-02-01T00:00:00Z' }));
    const records = await journal.list('m1');
    expect(records).toHaveLength(1);
    expect(records[0].uploadSessionId).toBe('s1');
  });

  it('lists by member and optionally by match', async () => {
    const journal = createJournal();
    await journal.put(record({ uploadSessionId: 's1' }));
    await journal.put(record({ uploadSessionId: 's2', matchId: 'match2', startKey: 'start-2' }));
    await journal.put(record({ uploadSessionId: 's3', memberId: 'm2', startKey: 'start-3' }));
    expect(await journal.list('m1')).toHaveLength(2);
    expect((await journal.list('m1', 'match2')).map((r) => r.uploadSessionId)).toEqual(['s2']);
    expect(await journal.list('m2', 'match2')).toEqual([]);
  });

  it('stores only the documented fields and sorted confirmed parts', async () => {
    const journal = createJournal();
    const dirty = { ...record({ uploadSessionId: 's1', confirmedParts: [3, 1, 3, 2] }), url: 'https://x', requiredHeaders: { a: 'b' } };
    await journal.put(dirty as JournalRecord);
    const [stored] = await journal.list('m1');
    expect(Object.keys(stored).sort()).toEqual(
      [
        'completionKey', 'confirmedParts', 'contentDigest', 'fileLastModified', 'fileName', 'fileSizeBytes', 'matchId', 'memberId',
        'partCount', 'partSizeBytes', 'startKey', 'teamId', 'updatedAt', 'uploadSessionId',
      ].sort(),
    );
    expect(stored.confirmedParts).toEqual([1, 2, 3]);
  });

  it('confirms parts of one session', async () => {
    const journal = createJournal();
    await journal.put(record({ uploadSessionId: 's1' }));
    expect(await journal.confirmParts('s1', [2, 1])).toBe(true);
    expect(await journal.confirmParts('missing', [1])).toBe(false);
    expect((await journal.list('m1'))[0].confirmedParts).toEqual([1, 2]);
  });

  it('deletes one record and purges all records of one member only', async () => {
    const journal = createJournal();
    await journal.put(record({ uploadSessionId: 's1' }));
    await journal.put(record({ uploadSessionId: 's2', startKey: 'start-2' }));
    await journal.put(record({ uploadSessionId: 's3', memberId: 'm2', startKey: 'start-3' }));
    await journal.delete('m1', 's1');
    expect((await journal.list('m1')).map((r) => r.uploadSessionId)).toEqual(['s2']);
    await journal.purgeMember('m1');
    expect(await journal.list('m1')).toEqual([]);
    expect(await journal.list('m2')).toHaveLength(1);
  });

  it('uses the documented database, store, and index', async () => {
    const journal = createJournal();
    await journal.put(record());
    const db = await new Promise<IDBDatabase>((resolve) => {
      const request = indexedDB.open(journalDatabaseName);
      request.onsuccess = () => resolve(request.result);
    });
    const store = db.transaction('uploads').objectStore('uploads');
    expect(store.keyPath).toBeNull();
    expect(Array.from(store.indexNames)).toEqual(['memberMatch']);
    expect(store.index('memberMatch').keyPath).toEqual(['memberId', 'matchId']);
    db.close();
  });
});
