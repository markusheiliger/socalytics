export const journalDatabaseName = 'socalytics-upload-journal';
export const journalStoreName = 'uploads';
export const journalMatchIndexName = 'memberMatch';

// data-model.md § Journal record: exactly these fields. Never grants, signatures, headers, secrets, or file contents.
export interface JournalRecord {
  readonly memberId: string;
  readonly matchId: string;
  readonly teamId: string;
  readonly uploadSessionId?: string;
  readonly startKey: string;
  readonly completionKey: string;
  readonly fileName: string;
  readonly fileSizeBytes: number;
  readonly fileLastModified: number;
  readonly partSizeBytes: number;
  readonly partCount: number;
  readonly contentDigest: string;
  readonly confirmedParts: readonly number[];
  readonly sessionExpiresAt?: string;
  readonly updatedAt: string;
}

export interface Journal {
  put(record: JournalRecord): Promise<void>;
  list(memberId: string, matchId?: string): Promise<JournalRecord[]>;
  // A record without a session yet is stored under its start key, so `uploadSessionId` may be the start key.
  delete(memberId: string, uploadSessionId: string): Promise<void>;
  purgeMember(memberId: string): Promise<void>;
  // Updates the confirmed parts of the record of a session; false when there is no such record.
  confirmParts(uploadSessionId: string, confirmedParts: readonly number[]): Promise<boolean>;
}

// Copies exactly the documented fields, so nothing else (grants, headers) can reach IndexedDB.
function pick(record: JournalRecord): JournalRecord {
  const clean: Record<string, unknown> = {
    memberId: record.memberId,
    matchId: record.matchId,
    teamId: record.teamId,
    startKey: record.startKey,
    completionKey: record.completionKey,
    fileName: record.fileName,
    fileSizeBytes: record.fileSizeBytes,
    fileLastModified: record.fileLastModified,
    partSizeBytes: record.partSizeBytes,
    partCount: record.partCount,
    contentDigest: record.contentDigest,
    confirmedParts: [...new Set(record.confirmedParts)].sort((a, b) => a - b),
    updatedAt: record.updatedAt,
  };
  if (record.uploadSessionId !== undefined) {
    clean.uploadSessionId = record.uploadSessionId;
  }
  if (record.sessionExpiresAt !== undefined) {
    clean.sessionExpiresAt = record.sessionExpiresAt;
  }
  return clean as unknown as JournalRecord;
}

function result<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('IndexedDB request failed.'));
  });
}

function completed(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error ?? new Error('IndexedDB transaction failed.'));
    transaction.onabort = () => reject(transaction.error ?? new Error('IndexedDB transaction aborted.'));
  });
}

// Arrays sort after strings, so this range covers every key that starts with the member.
function memberRange(memberId: string): IDBKeyRange {
  return IDBKeyRange.bound([memberId], [memberId, []]);
}

// The store key is [memberId, uploadSessionId]. A record without a session yet (state `starting`) is stored under
// [memberId, startKey] until the session identity arrives, because an IndexedDB key cannot contain an absent value.
function keyOf(record: JournalRecord): [string, string] {
  return [record.memberId, record.uploadSessionId ?? record.startKey];
}

export function createJournal(factory: IDBFactory = indexedDB): Journal {
  let opened: Promise<IDBDatabase> | undefined;

  const open = (): Promise<IDBDatabase> => {
    opened ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = factory.open(journalDatabaseName, 1);
      request.onupgradeneeded = () => {
        const store = request.result.createObjectStore(journalStoreName);
        store.createIndex(journalMatchIndexName, ['memberId', 'matchId']);
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error ?? new Error('IndexedDB could not be opened.'));
      request.onblocked = () => reject(new Error('IndexedDB is blocked.'));
    }).catch((error: unknown) => {
      opened = undefined;
      throw error;
    });
    return opened;
  };

  return {
    async put(record) {
      const clean = pick(record);
      const db = await open();
      const transaction = db.transaction(journalStoreName, 'readwrite');
      const store = transaction.objectStore(journalStoreName);
      if (clean.uploadSessionId !== undefined) {
        store.delete([clean.memberId, clean.startKey]);
      }
      store.put(clean, keyOf(clean));
      await completed(transaction);
    },

    async list(memberId, matchId) {
      const db = await open();
      const store = db.transaction(journalStoreName, 'readonly').objectStore(journalStoreName);
      const records =
        matchId === undefined
          ? await result(store.getAll(memberRange(memberId)))
          : await result(store.index(journalMatchIndexName).getAll(IDBKeyRange.only([memberId, matchId])));
      return records as JournalRecord[];
    },

    async delete(memberId, uploadSessionId) {
      const db = await open();
      const transaction = db.transaction(journalStoreName, 'readwrite');
      transaction.objectStore(journalStoreName).delete([memberId, uploadSessionId]);
      await completed(transaction);
    },

    async purgeMember(memberId) {
      const db = await open();
      const transaction = db.transaction(journalStoreName, 'readwrite');
      transaction.objectStore(journalStoreName).delete(memberRange(memberId));
      await completed(transaction);
    },

    async confirmParts(uploadSessionId, confirmedParts) {
      const db = await open();
      const transaction = db.transaction(journalStoreName, 'readwrite');
      let found = false;
      const cursorRequest = transaction.objectStore(journalStoreName).openCursor();
      cursorRequest.onsuccess = () => {
        const cursor = cursorRequest.result;
        if (!cursor) {
          return;
        }
        const value = cursor.value as JournalRecord;
        if (value.uploadSessionId === uploadSessionId) {
          found = true;
          cursor.update(pick({ ...value, confirmedParts, updatedAt: new Date().toISOString() }));
        } else {
          cursor.continue();
        }
      };
      await completed(transaction);
      return found;
    },
  };
}
