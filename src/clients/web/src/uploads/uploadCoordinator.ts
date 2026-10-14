import type { RecordingLineageAdapter } from '../platform/adapters/recordingLineageAdapter';
import type { RecordingUploadAdapter } from '../platform/adapters/recordingUploadAdapter';
import { toPlatformFailure, type PlatformFailure, type PlatformFailureKind } from '../platform/failure';
import type { PartGrant, TimelineSpan } from '../platform/models/recordings';
import {
  canTransition,
  finalStates,
  transition,
  type PausedReason,
  type UploadJobReason,
  type UploadJobState,
} from './uploadJob';
import type { JournalRecord } from './worker/journal';
import type { MainToWorkerMessage, UploadDeclaration, UploadGrant, WorkerToMainMessage } from './worker/protocol';

export interface WorkerLike {
  postMessage(message: MainToWorkerMessage): void;
  onmessage: ((event: { data: WorkerToMainMessage }) => void) | null;
  terminate?(): void;
}

export interface DeclarationSummary {
  readonly totalSizeBytes: number;
  readonly partSizeBytes: number;
  readonly partCount: number;
  readonly contentDigest: string;
}

// The in-memory upload job of data-model.md § Upload job. Snapshots are immutable; every change makes a new object.
export interface UploadJob {
  readonly localId: string;
  readonly matchId: string;
  readonly teamId: string;
  readonly memberId: string;
  readonly file?: File;
  readonly fileName: string;
  readonly fileSizeBytes: number;
  readonly fileLastModified: number;
  readonly displayName: string;
  readonly description?: string;
  readonly contentType: string;
  readonly durationSeconds?: number;
  readonly declaration?: UploadDeclaration;
  readonly summary?: DeclarationSummary;
  readonly uploadSessionId?: string;
  readonly sessionExpiresAt?: string;
  readonly confirmedParts: readonly number[];
  readonly bytesHashed: number;
  readonly bytesConfirmed: number;
  readonly bytesInFlight: number;
  readonly mapping?: readonly TimelineSpan[];
  readonly startKey: string;
  readonly completionKey: string;
  readonly state: UploadJobState;
  readonly reason?: UploadJobReason;
  /** Every part is confirmed as stored. */
  readonly transferred: boolean;
  /** Set while a start waits for the platform, object storage, or the session to recover. */
  readonly waiting?: 'unreachable' | 'storageUnavailable' | 'sessionEnded';
  /** The platform failure that ended or interrupted the job, for the screens' message. */
  readonly failureKind?: PlatformFailureKind;
  readonly failureCode?: string;
}

export interface AddFileInput {
  file: File;
  displayName: string;
  description?: string;
  durationSeconds?: number;
}

export interface FileRefusal {
  file: File;
  reason: 'notVideo';
}

export interface AddFilesResult {
  added: string[];
  refused: FileRefusal[];
}

export interface CoordinatorDependencies {
  memberId: string;
  uploads: RecordingUploadAdapter;
  lineage: Pick<RecordingLineageAdapter, 'getLineage'>;
  createWorker: () => WorkerLike;
  newKey?: () => string;
  setTimer?: (callback: () => void, delayMs: number) => unknown;
  clearTimer?: (handle: unknown) => void;
  storageRetryMs?: number;
}

export interface UploadCoordinator {
  subscribe(listener: () => void): () => void;
  getSnapshot(): readonly UploadJob[];
  /** False once the browser refused a journal operation: uploads still run but cannot be resumed after a reload. */
  journalAvailable(): boolean;
  loadJournal(matchId: string): Promise<void>;
  addFiles(matchId: string, teamId: string, inputs: readonly AddFileInput[]): AddFilesResult;
  setMapping(jobId: string, mapping: readonly TimelineSpan[]): void;
  retry(jobId: string): void;
  pause(jobId: string): void;
  resume(jobId: string): void;
  removeFromQueue(jobId: string): boolean;
  reselectFile(jobId: string, file: File): Promise<void>;
  abandon(jobId: string): void;
  acknowledgeExpiry(jobId: string): void;
  purgeMember(): Promise<void>;
  setUnreachable(unreachable: boolean): void;
  setSessionActive(active: boolean): void;
  dispose(): void;
}

const storageRetryDefaultMs = 30_000;
const connectivityPausable: readonly UploadJobState[] = ['queued', 'transferring', 'awaitingMapping'];

export function isVideoFile(file: File): boolean {
  return file.type.startsWith('video/');
}

function toWorkerGrants(grants: readonly PartGrant[]): UploadGrant[] {
  return grants.map((grant) => ({
    partNumber: grant.partNumber,
    url: grant.url,
    requiredHeaders: grant.requiredHeaders,
    expiresAt: grant.expiresAt.toISOString(),
  }));
}

export function createUploadCoordinator(dependencies: CoordinatorDependencies): UploadCoordinator {
  const { memberId, uploads, lineage } = dependencies;
  const newKey = dependencies.newKey ?? (() => crypto.randomUUID());
  const setTimer = dependencies.setTimer ?? ((callback, delayMs) => setTimeout(callback, delayMs));
  const clearTimer = dependencies.clearTimer ?? ((handle) => clearTimeout(handle as ReturnType<typeof setTimeout>));
  const storageRetryMs = dependencies.storageRetryMs ?? storageRetryDefaultMs;

  const jobs = new Map<string, UploadJob>();
  // Grants are secrets: they stay in memory and are dropped when the job stops.
  const startGrants = new Map<string, UploadGrant[]>();
  const epochs = new Map<string, number>();
  const listeners = new Set<() => void>();
  const pendingLists = new Map<string, Array<() => void>>();
  let pendingPurge: Array<() => void> = [];
  let snapshot: readonly UploadJob[] = [];
  let worker: WorkerLike | undefined;
  let unreachable = false;
  let sessionActive = true;
  let journalOk = true;
  let storageTimer: unknown;
  let disposed = false;

  const get = (id: string) => jobs.get(id);

  const notify = () => {
    snapshot = [...jobs.values()];
    for (const listener of [...listeners]) {
      listener();
    }
  };

  const set = (id: string, patch: Partial<UploadJob>): UploadJob | undefined => {
    const current = jobs.get(id);
    if (!current) {
      return undefined;
    }
    const next = { ...current, ...patch };
    jobs.set(id, next);
    notify();
    return next;
  };

  // Returns undefined when the change is not allowed from the current state.
  const move = (id: string, to: UploadJobState, reason?: UploadJobReason, patch: Partial<UploadJob> = {}) => {
    const current = jobs.get(id);
    if (!current || !canTransition(current.state, to)) {
      return undefined;
    }
    const status = transition(current, to, reason);
    return set(id, { ...patch, state: status.state, reason: status.reason });
  };

  const post = (message: MainToWorkerMessage) => {
    if (disposed) {
      return;
    }
    if (!worker) {
      worker = dependencies.createWorker();
      worker.onmessage = (event) => handle(event.data);
    }
    worker.postMessage(message);
  };

  const record = (job: UploadJob): JournalRecord => ({
    memberId: job.memberId,
    matchId: job.matchId,
    teamId: job.teamId,
    ...(job.uploadSessionId ? { uploadSessionId: job.uploadSessionId } : {}),
    startKey: job.startKey,
    completionKey: job.completionKey,
    fileName: job.fileName,
    fileSizeBytes: job.fileSizeBytes,
    fileLastModified: job.fileLastModified,
    partSizeBytes: job.summary?.partSizeBytes ?? 0,
    partCount: job.summary?.partCount ?? 0,
    contentDigest: job.summary?.contentDigest ?? '',
    confirmedParts: job.confirmedParts,
    ...(job.sessionExpiresAt ? { sessionExpiresAt: job.sessionExpiresAt } : {}),
    updatedAt: new Date().toISOString(),
  });

  const persist = (job: UploadJob) => post({ type: 'journalPut', record: record(job) });
  const forget = (job: UploadJob) =>
    post({ type: 'journalDelete', memberId: job.memberId, uploadSessionId: job.uploadSessionId ?? job.startKey });

  const stopWorkerJob = (id: string) => {
    epochs.set(id, (epochs.get(id) ?? 0) + 1);
    startGrants.delete(id);
    post({ type: 'abort', jobId: id });
  };

  const scheduleStorageRetry = () => {
    if (storageTimer !== undefined || disposed) {
      return;
    }
    const waiting = [...jobs.values()].some(
      (job) => (job.state === 'paused' && job.reason === 'storageUnavailable') || job.waiting === 'storageUnavailable',
    );
    if (!waiting) {
      return;
    }
    storageTimer = setTimer(() => {
      storageTimer = undefined;
      recover(['storageUnavailable']);
      scheduleStorageRetry();
    }, storageRetryMs);
  };

  const pauseJob = (id: string, reason: PausedReason) => {
    const job = get(id);
    if (!job) {
      return;
    }
    if (job.state === 'transferring') {
      stopWorkerJob(id);
    }
    if (move(id, 'paused', reason)) {
      startNext();
      scheduleStorageRetry();
    }
  };

  const endJob = (id: string, to: UploadJobState, reason?: UploadJobReason, patch: Partial<UploadJob> = {}) => {
    const job = get(id);
    if (!job) {
      return;
    }
    stopWorkerJob(id);
    const ended = move(id, to, reason, { ...patch, bytesInFlight: 0 });
    if (ended && to !== 'expired') {
      forget(job);
    }
    startNext();
  };

  const recover = (reasons: readonly PausedReason[]) => {
    for (const job of [...jobs.values()]) {
      if (job.state === 'paused' && job.reason && reasons.includes(job.reason as PausedReason)) {
        resumeJob(job.localId);
      } else if (job.state === 'ready' && job.waiting && reasons.includes(job.waiting)) {
        resumeJob(job.localId);
      }
    }
  };

  function startNext() {
    if ([...jobs.values()].some((job) => job.state === 'transferring')) {
      return;
    }
    const next = [...jobs.values()].find((job) => job.state === 'queued');
    if (!next || !next.file || !next.summary || !next.uploadSessionId) {
      return;
    }
    const begun = move(next.localId, 'transferring', undefined, { transferred: false });
    if (!begun) {
      return;
    }
    const declaration: UploadDeclaration = next.declaration ?? { ...next.summary, partDigests: [] };
    post({
      type: 'transfer',
      jobId: next.localId,
      uploadSessionId: next.uploadSessionId,
      file: next.file,
      declaration,
      confirmedParts: next.confirmedParts,
    });
    const grants = startGrants.get(next.localId);
    if (grants) {
      startGrants.delete(next.localId);
      post({ type: 'grants', jobId: next.localId, grants });
    }
  }

  function resumeJob(id: string) {
    const job = get(id);
    if (!job) {
      return;
    }
    if (job.state === 'ready' && job.waiting) {
      void startUpload(id);
    } else if (job.state === 'paused') {
      if (job.transferred) {
        finishTransfer(id);
      } else if (move(id, 'queued')) {
        startNext();
      }
    }
  }

  function finishTransfer(id: string) {
    const job = get(id);
    if (!job) {
      return;
    }
    if (job.mapping) {
      if (move(id, 'completing', undefined, { transferred: true })) {
        void runCompletion(id);
      }
    } else {
      move(id, 'awaitingMapping', undefined, { transferred: true });
    }
  }

  async function startUpload(id: string) {
    const job = get(id);
    if (!job?.declaration || !job.file) {
      return;
    }
    const starting = move(id, 'starting', undefined, { waiting: undefined, failureKind: undefined, failureCode: undefined });
    if (!starting) {
      return;
    }
    // The journal record with the start key is written before the platform sees the key.
    persist(starting);
    try {
      const started = await uploads.start(
        job.matchId,
        { displayName: job.displayName, description: job.description, contentType: job.contentType },
        {
          totalSizeBytes: job.declaration.totalSizeBytes,
          partSizeBytes: job.declaration.partSizeBytes,
          partDigests: [...job.declaration.partDigests],
        },
        job.startKey,
      );
      if (get(id)?.state !== 'starting') {
        return;
      }
      startGrants.set(id, toWorkerGrants(started.grants));
      const queued = move(id, 'queued', undefined, {
        uploadSessionId: started.session.id,
        sessionExpiresAt: started.session.expiresAt.toISOString(),
        confirmedParts: [],
      });
      if (queued) {
        persist(queued);
        startNext();
      }
    } catch (error) {
      if (get(id)?.state === 'starting') {
        applyFailure(id, toPlatformFailure(error), 'start');
      }
    }
  }

  async function runCompletion(id: string) {
    const job = get(id);
    if (!job?.mapping || !job.uploadSessionId) {
      return;
    }
    try {
      await uploads.complete(job.matchId, job.uploadSessionId, [...job.mapping], job.completionKey);
      if (get(id)?.state === 'completing') {
        endJob(id, 'completed');
      }
    } catch (error) {
      if (get(id)?.state === 'completing') {
        await applyFailure(id, toPlatformFailure(error), 'complete');
      }
    }
  }

  async function applyFailure(id: string, failure: PlatformFailure, where: 'start' | 'grants' | 'complete') {
    const job = get(id);
    if (!job) {
      return;
    }
    const info = { failureKind: failure.kind, failureCode: failure.code };
    switch (failure.kind) {
      case 'unreachable':
      case 'storageUnavailable':
      case 'sessionEnded': {
        const reason: PausedReason = failure.kind === 'storageUnavailable' ? 'storageUnavailable' : failure.kind === 'sessionEnded' ? 'sessionEnded' : 'unreachable';
        set(id, info);
        if (where === 'start') {
          move(id, 'ready', undefined, { waiting: reason });
          scheduleStorageRetry();
        } else {
          pauseJob(id, reason);
        }
        return;
      }
      case 'notPermitted':
        for (const other of [...jobs.values()]) {
          if (other.teamId === job.teamId && !finalStates.includes(other.state) && other.state !== 'notPermitted') {
            stopWorkerJob(other.localId);
            move(other.localId, 'notPermitted', undefined, other.localId === id ? info : {});
          }
        }
        startNext();
        return;
      case 'uploadExpired':
        endJob(id, 'expired', undefined, info);
        return;
      case 'uploadCompleted':
        if (where === 'complete') {
          // A replay: the first completion succeeded, so the lineage already holds the recording.
          try {
            await lineage.getLineage(job.matchId);
          } catch (error) {
            if (get(id)?.state === 'completing') {
              await applyFailure(id, toPlatformFailure(error), 'complete');
            }
            return;
          }
          if (get(id)?.state === 'completing') {
            endJob(id, 'completed');
          }
        } else {
          endJob(id, 'completed');
        }
        return;
      case 'uploadPartsRefused': {
        const partCount = job.summary?.partCount ?? 0;
        // The platform names no parts: a fully confirmed local record was stale, so every part is marked again.
        const confirmedParts = job.confirmedParts.length >= partCount ? [] : job.confirmedParts;
        const refused = move(id, 'refused', failure.code === 'upload-part-mismatch' ? 'partMismatch' : 'partsIncomplete', {
          ...info,
          confirmedParts,
          transferred: false,
        });
        if (refused) {
          persist(refused);
        }
        return;
      }
      case 'uploadIntegrityFailed':
        endJob(id, 'failed', failure.code === 'integrity-evidence-unavailable' ? 'integrityUnavailable' : 'objectMismatch', info);
        return;
      default:
        if (where === 'start') {
          // A declaration the platform refuses (validation, size, type) creates no upload.
          endJob(id, 'cancelled', undefined, info);
        } else {
          set(id, info);
          pauseJob(id, 'retriesExhausted');
        }
    }
  }

  function handle(message: WorkerToMainMessage) {
    switch (message.type) {
      case 'journalRecords': {
        const key = `${message.memberId}|${message.matchId ?? ''}`;
        const waiting = pendingLists.get(key) ?? [];
        pendingLists.delete(key);
        importRecords(message.records);
        for (const resolve of waiting) {
          resolve();
        }
        return;
      }
      case 'purged':
        for (const resolve of pendingPurge) {
          resolve();
        }
        pendingPurge = [];
        return;
      case 'journalFailed':
        journalOk = false;
        if (message.operation === 'journalList') {
          for (const waiting of pendingLists.values()) {
            for (const resolve of waiting) {
              resolve();
            }
          }
          pendingLists.clear();
        } else if (message.operation === 'purgeMember') {
          for (const resolve of pendingPurge) {
            resolve();
          }
          pendingPurge = [];
        }
        notify();
        return;
    }
    const id = message.jobId;
    const job = get(id);
    if (!job) {
      return;
    }
    switch (message.type) {
      case 'prepareProgress':
        set(id, { bytesHashed: message.bytesHashed });
        return;
      case 'prepared': {
        if (job.state !== 'preparing') {
          return;
        }
        const declaration: UploadDeclaration = {
          totalSizeBytes: job.fileSizeBytes,
          partSizeBytes: message.partSizeBytes,
          partCount: message.partCount,
          partDigests: message.partDigests,
          contentDigest: message.contentDigest,
        };
        // A journal record without a session carries the digest of the first selection: another file is refused.
        if (job.summary && job.summary.contentDigest !== '' && job.summary.contentDigest !== message.contentDigest) {
          move(id, 'refused', 'fileMismatch');
          return;
        }
        const ready = move(id, 'ready', undefined, {
          declaration,
          summary: {
            totalSizeBytes: job.fileSizeBytes,
            partSizeBytes: message.partSizeBytes,
            partCount: message.partCount,
            contentDigest: message.contentDigest,
          },
        });
        if (ready) {
          void startUpload(id);
        }
        return;
      }
      case 'prepareFailed':
        if (message.reason === 'cancelled') {
          if (job.state === 'preparing') {
            move(id, 'cancelled');
          }
        } else if (job.state === 'preparing') {
          move(id, 'refused', 'fileMismatch');
        }
        return;
      case 'verified':
        if (move(id, 'queued')) {
          startNext();
        }
        return;
      case 'verifyFailed':
        if (message.reason === 'cancelled') {
          return;
        }
        move(id, 'refused', 'fileMismatch');
        return;
      case 'needGrants': {
        if (job.state !== 'transferring' || !job.uploadSessionId) {
          return;
        }
        const epoch = epochs.get(id);
        const current = () => get(id)?.state === 'transferring' && epochs.get(id) === epoch;
        void (async () => {
          try {
            const grants = await uploads.issueGrants(job.matchId, job.uploadSessionId!, [...message.partNumbers]);
            if (current()) {
              post({ type: 'grants', jobId: id, grants: toWorkerGrants(grants) });
            }
          } catch (error) {
            if (current()) {
              await applyFailure(id, toPlatformFailure(error), 'grants');
            }
          }
        })();
        return;
      }
      case 'transferProgress':
        if (job.state === 'transferring') {
          set(id, {
            bytesConfirmed: message.bytesConfirmed,
            bytesInFlight: message.bytesInFlight,
            confirmedParts: message.confirmedParts,
          });
        }
        return;
      case 'partFailed':
        return;
      case 'paused':
        if (job.state === 'transferring') {
          pauseJob(id, message.reason);
        }
        return;
      case 'transferred':
        if (job.state === 'transferring') {
          stopWorkerJob(id);
          set(id, { bytesInFlight: 0, transferred: true });
          finishTransfer(id);
          startNext();
        }
        return;
      case 'failed':
        endJob(id, 'failed', message.reason);
        return;
    }
  }

  function importRecords(records: readonly JournalRecord[]) {
    let created: UploadJob[] = [];
    for (const entry of records) {
      const exists = [...jobs.values()].some((job) => job.startKey === entry.startKey || (entry.uploadSessionId && job.uploadSessionId === entry.uploadSessionId));
      if (exists || entry.memberId !== memberId) {
        continue;
      }
      const job: UploadJob = {
        localId: newKey(),
        matchId: entry.matchId,
        teamId: entry.teamId,
        memberId: entry.memberId,
        fileName: entry.fileName,
        fileSizeBytes: entry.fileSizeBytes,
        fileLastModified: entry.fileLastModified,
        displayName: entry.fileName,
        contentType: '',
        summary: {
          totalSizeBytes: entry.fileSizeBytes,
          partSizeBytes: entry.partSizeBytes,
          partCount: entry.partCount,
          contentDigest: entry.contentDigest,
        },
        uploadSessionId: entry.uploadSessionId,
        sessionExpiresAt: entry.sessionExpiresAt,
        confirmedParts: entry.confirmedParts,
        bytesHashed: 0,
        bytesConfirmed: 0,
        bytesInFlight: 0,
        startKey: entry.startKey,
        completionKey: entry.completionKey,
        state: 'needsFile',
        transferred: false,
      };
      jobs.set(job.localId, job);
      created = [...created, job];
    }
    if (created.length > 0) {
      notify();
      for (const job of created) {
        if (job.uploadSessionId) {
          void inspectSession(job.localId);
        }
      }
    }
  }

  // Shows completed, expired, and forbidden sessions as such, and reads the descriptor the journal does not keep.
  async function inspectSession(id: string): Promise<boolean> {
    const job = get(id);
    if (!job?.uploadSessionId) {
      return true;
    }
    try {
      const session = await uploads.getSession(job.matchId, job.uploadSessionId);
      if (get(id)?.state !== 'needsFile' && get(id)?.state !== 'refused') {
        return false;
      }
      if (session.state === 'completed') {
        endJob(id, 'completed');
        return false;
      }
      if (session.state === 'expired') {
        endJob(id, 'expired');
        return false;
      }
      set(id, {
        displayName: session.recording.displayName,
        description: session.recording.description,
        contentType: session.recording.contentType,
        sessionExpiresAt: session.expiresAt.toISOString(),
        summary: {
          totalSizeBytes: session.totalSizeBytes,
          partSizeBytes: session.partSizeBytes,
          partCount: session.partCount,
          contentDigest: session.contentDigest,
        },
      });
      return true;
    } catch (error) {
      const failure = toPlatformFailure(error);
      if (failure.kind === 'notPermitted') {
        await applyFailure(id, failure, 'grants');
      } else if (failure.status === 404) {
        endJob(id, 'expired');
      } else {
        set(id, { failureKind: failure.kind, failureCode: failure.code });
      }
      return false;
    }
  }

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getSnapshot: () => snapshot,
    journalAvailable: () => journalOk,

    loadJournal(matchId) {
      return new Promise<void>((resolve) => {
        const key = `${memberId}|${matchId}`;
        pendingLists.set(key, [...(pendingLists.get(key) ?? []), resolve]);
        post({ type: 'journalList', memberId, matchId });
      });
    },

    addFiles(matchId, teamId, inputs) {
      const result: AddFilesResult = { added: [], refused: [] };
      for (const input of inputs) {
        if (!isVideoFile(input.file)) {
          result.refused.push({ file: input.file, reason: 'notVideo' });
          continue;
        }
        const id = newKey();
        jobs.set(id, {
          localId: id,
          matchId,
          teamId,
          memberId,
          file: input.file,
          fileName: input.file.name,
          fileSizeBytes: input.file.size,
          fileLastModified: input.file.lastModified,
          displayName: input.displayName,
          description: input.description,
          contentType: input.file.type,
          durationSeconds: input.durationSeconds,
          confirmedParts: [],
          bytesHashed: 0,
          bytesConfirmed: 0,
          bytesInFlight: 0,
          startKey: newKey(),
          completionKey: newKey(),
          state: 'selected',
          transferred: false,
        });
        move(id, 'preparing');
        post({ type: 'prepare', jobId: id, file: input.file });
        result.added.push(id);
      }
      return result;
    },

    setMapping(jobId, mapping) {
      const job = set(jobId, { mapping });
      if (job?.state === 'awaitingMapping') {
        finishTransfer(jobId);
      }
    },

    retry(jobId) {
      const job = get(jobId);
      if (job?.state === 'refused' && job.reason !== 'fileMismatch') {
        if (move(jobId, 'queued')) {
          startNext();
        }
      } else {
        resumeJob(jobId);
      }
    },

    pause(jobId) {
      const job = get(jobId);
      if (job && (job.state === 'queued' || job.state === 'transferring')) {
        pauseJob(jobId, 'memberRequested');
      }
    },

    resume: resumeJob,

    removeFromQueue(jobId) {
      const job = get(jobId);
      if (!job || job.uploadSessionId || !(['preparing', 'ready', 'starting'] as UploadJobState[]).includes(job.state)) {
        return false;
      }
      post({ type: 'abort', jobId });
      if (job.state !== 'preparing') {
        forget(job);
      }
      return move(jobId, 'cancelled') !== undefined;
    },

    async reselectFile(jobId, file) {
      const job = get(jobId);
      if (!job || (job.state !== 'needsFile' && !(job.state === 'refused' && job.reason === 'fileMismatch'))) {
        return;
      }
      if (job.uploadSessionId && !(await inspectSession(jobId))) {
        return;
      }
      const current = get(jobId);
      const summary = current?.summary;
      if (!current || !summary || (current.state !== 'needsFile' && current.state !== 'refused')) {
        return;
      }
      if (!move(jobId, 'preparing', undefined, { file, bytesHashed: 0, failureKind: undefined, failureCode: undefined })) {
        return;
      }
      // A different size is refused at once, without hashing.
      if (file.size !== summary.totalSizeBytes) {
        move(jobId, 'refused', 'fileMismatch');
        return;
      }
      if (current.uploadSessionId) {
        post({
          type: 'verify',
          jobId,
          file,
          partSizeBytes: summary.partSizeBytes,
          expectedContentDigest: summary.contentDigest,
        });
      } else {
        post({ type: 'prepare', jobId, file, partSizeBytes: summary.partSizeBytes });
      }
    },

    abandon(jobId) {
      const job = get(jobId);
      if (!job || finalStates.includes(job.state)) {
        return;
      }
      stopWorkerJob(jobId);
      forget(job);
      move(jobId, 'abandoned');
      jobs.delete(jobId);
      notify();
      startNext();
    },

    acknowledgeExpiry(jobId) {
      const job = get(jobId);
      if (job?.state !== 'expired') {
        return;
      }
      forget(job);
      jobs.delete(jobId);
      notify();
    },

    purgeMember() {
      for (const id of [...jobs.keys()]) {
        stopWorkerJob(id);
      }
      jobs.clear();
      notify();
      return new Promise<void>((resolve) => {
        pendingPurge.push(resolve);
        post({ type: 'purgeMember', memberId });
      });
    },

    setUnreachable(value) {
      if (unreachable === value) {
        return;
      }
      unreachable = value;
      if (value) {
        for (const job of [...jobs.values()]) {
          if (connectivityPausable.includes(job.state)) {
            pauseJob(job.localId, 'unreachable');
          }
        }
      } else {
        recover(['unreachable']);
      }
    },

    setSessionActive(active) {
      if (sessionActive === active) {
        return;
      }
      sessionActive = active;
      if (!active) {
        for (const job of [...jobs.values()]) {
          if (connectivityPausable.includes(job.state)) {
            pauseJob(job.localId, 'sessionEnded');
          }
        }
      } else {
        recover(['sessionEnded']);
      }
    },

    dispose() {
      disposed = true;
      if (storageTimer !== undefined) {
        clearTimer(storageTimer);
        storageTimer = undefined;
      }
      worker?.terminate?.();
      worker = undefined;
      listeners.clear();
    },
  };
}
