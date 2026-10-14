import { createJournal, type Journal } from './journal';
import { HashingError, prepare, verify } from './hashing';
import type { MainToWorkerMessage, WorkerToMainMessage } from './protocol';
import { createTransferManager, type LockManagerLike, type TransferDependencies } from './transfer';

export interface UploadWorkerDependencies {
  readonly post: (message: WorkerToMainMessage) => void;
  readonly journal?: Journal;
  readonly transfer?: Omit<TransferDependencies, 'post' | 'journal'>;
}

export type UploadWorkerHandler = (message: MainToWorkerMessage) => Promise<void>;

// The worker never calls /api/v1/**; every platform call stays on the main thread (research.md § R1).
export function createUploadWorkerHandler(dependencies: UploadWorkerDependencies): UploadWorkerHandler {
  const { post } = dependencies;
  const journal = dependencies.journal ?? createJournal();
  const transfers = createTransferManager({ ...dependencies.transfer, post, journal });
  const hashing = new Map<string, AbortController>();

  const guarded = async (operation: 'journalPut' | 'journalList' | 'journalDelete' | 'purgeMember', action: () => Promise<void>) => {
    try {
      await action();
    } catch {
      post({ type: 'journalFailed', operation, reason: 'storageUnavailable' });
    }
  };

  const hashJob = async (jobId: string, run: (controller: AbortController) => Promise<void>, failed: (reason: 'readError' | 'cancelled') => WorkerToMainMessage) => {
    hashing.get(jobId)?.abort();
    const controller = new AbortController();
    hashing.set(jobId, controller);
    try {
      await run(controller);
    } catch (error) {
      post(failed(error instanceof HashingError ? error.reason : 'readError'));
    } finally {
      if (hashing.get(jobId) === controller) {
        hashing.delete(jobId);
      }
    }
  };

  return async (message) => {
    switch (message.type) {
      case 'prepare':
        await hashJob(
          message.jobId,
          async (controller) => {
            const prepared = await prepare(message.file, message.partSizeBytes, {
              signal: controller.signal,
              onProgress: (bytesHashed, totalBytes) => post({ type: 'prepareProgress', jobId: message.jobId, bytesHashed, totalBytes }),
            });
            post({ type: 'prepared', jobId: message.jobId, ...prepared });
          },
          (reason) => ({ type: 'prepareFailed', jobId: message.jobId, reason }),
        );
        return;
      case 'verify':
        await hashJob(
          message.jobId,
          async (controller) => {
            const matches = await verify(message.file, message.partSizeBytes, message.expectedContentDigest, { signal: controller.signal });
            post(matches ? { type: 'verified', jobId: message.jobId } : { type: 'verifyFailed', jobId: message.jobId, reason: 'fileMismatch' });
          },
          (reason) => ({ type: 'verifyFailed', jobId: message.jobId, reason }),
        );
        return;
      case 'journalPut':
        await guarded('journalPut', () => journal.put(message.record));
        return;
      case 'journalList':
        await guarded('journalList', async () => {
          const records = await journal.list(message.memberId, message.matchId);
          post({ type: 'journalRecords', memberId: message.memberId, matchId: message.matchId, records });
        });
        return;
      case 'journalDelete':
        await guarded('journalDelete', () => journal.delete(message.memberId, message.uploadSessionId));
        return;
      case 'purgeMember':
        await guarded('purgeMember', async () => {
          const records = await journal.list(message.memberId);
          transfers.abortSessions(records.flatMap((record) => (record.uploadSessionId ? [record.uploadSessionId] : [])));
          await journal.purgeMember(message.memberId);
          post({ type: 'purged', memberId: message.memberId });
        });
        return;
      case 'transfer':
        await transfers.transfer(message);
        return;
      case 'grants':
        transfers.grants(message.jobId, message.grants);
        return;
      case 'pause':
        transfers.pause(message.jobId);
        return;
      case 'resume':
        transfers.resume(message.jobId);
        return;
      case 'abort':
        hashing.get(message.jobId)?.abort();
        transfers.abort(message.jobId);
        return;
    }
  };
}

interface WorkerScope {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: MainToWorkerMessage }) => void): void;
  navigator?: { locks?: LockManagerLike };
}

// Runs only inside a dedicated worker; importing this module elsewhere (unit tests) has no side effect.
const scope = globalThis as unknown as Partial<WorkerScope> & { WorkerGlobalScope?: unknown };
if (typeof scope.WorkerGlobalScope !== 'undefined' && scope.addEventListener && scope.postMessage) {
  const worker = scope as WorkerScope;
  const handle = createUploadWorkerHandler({
    post: (message) => worker.postMessage(message),
    transfer: { locks: worker.navigator?.locks },
  });
  worker.addEventListener('message', (event) => {
    void handle(event.data);
  });
}
