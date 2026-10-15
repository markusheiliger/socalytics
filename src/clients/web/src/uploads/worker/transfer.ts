import { GrantScheduler } from '../grantScheduler';
import { classifyStorageResponse, retryDelayMs } from '../retryPolicy';
import { partRange } from '../partPolicy';
import type { Journal } from './journal';
import type { MainToWorkerMessage, UploadDeclaration, UploadGrant, WorkerToMainMessage } from './protocol';

export const parallelParts = 4;
export const slotLockName = 'socalytics-upload-slot';
export const sessionLockName = (uploadSessionId: string): string => `socalytics-upload:${uploadSessionId}`;
const checksumHeader = 'x-amz-checksum-sha256';

export interface LockManagerLike {
  request(
    name: string,
    options: { ifAvailable?: boolean; signal?: AbortSignal },
    callback: (lock: unknown) => Promise<unknown>,
  ): Promise<unknown>;
}

export interface UploadRequest {
  readonly upload: { onprogress: ((event: { loaded: number }) => void) | null };
  readonly status: number;
  readonly responseText: string;
  onload: (() => void) | null;
  onerror: (() => void) | null;
  ontimeout: (() => void) | null;
  onabort: (() => void) | null;
  open(method: string, url: string): void;
  setRequestHeader(name: string, value: string): void;
  send(body: Blob): void;
  abort(): void;
}

export interface TransferDependencies {
  readonly post: (message: WorkerToMainMessage) => void;
  readonly journal: Journal;
  readonly locks?: LockManagerLike;
  readonly createRequest?: () => UploadRequest;
  readonly setTimer?: (callback: () => void, delayMs: number) => unknown;
  readonly clearTimer?: (handle: unknown) => void;
  readonly random?: () => number;
  readonly now?: () => number;
}

type Release = () => void;
type TransferMessage = Extract<MainToWorkerMessage, { type: 'transfer' }>;

// Resolves with a release function, or undefined when `ifAvailable` found the lock taken.
function acquire(locks: LockManagerLike, name: string, options: { ifAvailable?: boolean; signal?: AbortSignal }): Promise<Release | undefined> {
  return new Promise((resolve, reject) => {
    locks
      .request(name, options, (lock) => {
        if (!lock) {
          resolve(undefined);
          return Promise.resolve();
        }
        return new Promise<void>((release) => resolve(() => release()));
      })
      .catch(reject);
  });
}

class TransferJob {
  private readonly confirmed: Set<number>;
  private readonly grants: GrantScheduler;
  private readonly inFlight = new Map<number, { request: UploadRequest; sent: number }>();
  private readonly attempts = new Map<number, number>();
  private readonly regranted = new Set<number>();
  private readonly waiting = new Map<number, unknown>();
  private awaitingGrants = false;
  private paused = true;
  private ended = false;
  private activation: AbortController | undefined;
  private sessionRelease: Release | undefined;
  private slotRelease: Release | undefined;

  constructor(
    readonly jobId: string,
    readonly uploadSessionId: string,
    private readonly file: Blob,
    private readonly declaration: UploadDeclaration,
    confirmedParts: readonly number[],
    private readonly deps: Required<Omit<TransferDependencies, 'locks'>> & Pick<TransferDependencies, 'locks'>,
    private readonly onEnded: (job: TransferJob) => void,
  ) {
    this.confirmed = new Set(confirmedParts);
    this.grants = new GrantScheduler(deps.now);
  }

  async start(): Promise<void> {
    if (this.file.size !== this.declaration.totalSizeBytes) {
      this.deps.post({ type: 'failed', jobId: this.jobId, reason: 'fileChanged' });
      this.end();
      return;
    }
    await this.activate();
  }

  addGrants(grants: readonly UploadGrant[]): void {
    this.grants.store(grants);
    this.awaitingGrants = false;
    this.pump();
  }

  pause(): void {
    if (this.ended || this.paused) {
      return;
    }
    this.paused = true;
    this.stopTransfers();
    this.releaseSlot();
  }

  resume(): void {
    if (this.ended || !this.paused) {
      return;
    }
    for (const part of [...this.attempts.keys()]) {
      this.attempts.delete(part);
    }
    void this.activate();
  }

  abort(): void {
    this.end();
  }

  private async activate(): Promise<void> {
    if (this.activation) {
      return;
    }
    const activation = new AbortController();
    this.activation = activation;
    const locks = this.deps.locks;
    try {
      if (locks) {
        if (!this.sessionRelease) {
          this.sessionRelease = await acquire(locks, sessionLockName(this.uploadSessionId), { ifAvailable: true });
          if (!this.sessionRelease) {
            if (!this.ended) {
              this.deps.post({ type: 'paused', jobId: this.jobId, reason: 'runningElsewhere' });
            }
            return;
          }
        }
        if (this.ended || activation.signal.aborted) {
          return;
        }
        this.slotRelease = await acquire(locks, slotLockName, { signal: activation.signal });
        if (this.ended || activation.signal.aborted) {
          this.releaseSlot();
          return;
        }
      }
    } catch {
      // The wait for the slot was aborted by pause or abort.
      return;
    } finally {
      if (this.activation === activation) {
        this.activation = undefined;
      }
      if (this.ended) {
        this.releaseLocks();
      }
    }
    this.paused = false;
    this.pump();
  }

  private pending(): number[] {
    const result: number[] = [];
    for (let partNumber = 1; partNumber <= this.declaration.partCount; partNumber++) {
      if (!this.confirmed.has(partNumber)) {
        result.push(partNumber);
      }
    }
    return result;
  }

  private pump(): void {
    if (this.ended || this.paused) {
      return;
    }
    const pending = this.pending();
    if (pending.length === 0) {
      if (this.inFlight.size === 0) {
        this.deps.post({ type: 'transferred', jobId: this.jobId });
        this.end();
      }
      return;
    }

    const needed = this.grants.partsNeeded(pending.filter((part) => !this.inFlight.has(part)));
    if (needed.length > 0 && !this.awaitingGrants) {
      this.awaitingGrants = true;
      this.deps.post({ type: 'needGrants', jobId: this.jobId, partNumbers: needed });
    }

    for (const partNumber of pending) {
      if (this.inFlight.size >= parallelParts) {
        break;
      }
      if (this.inFlight.has(partNumber) || this.waiting.has(partNumber)) {
        continue;
      }
      const grant = this.grants.usable(partNumber);
      if (grant) {
        this.send(partNumber, grant);
      }
    }
  }

  private send(partNumber: number, grant: UploadGrant): void {
    const range = partRange(partNumber, this.declaration.totalSizeBytes, this.declaration.partSizeBytes);
    const request = this.deps.createRequest();
    const entry = { request, sent: 0 };
    this.inFlight.set(partNumber, entry);

    const settle = (): boolean => {
      if (this.inFlight.get(partNumber) !== entry) {
        return false;
      }
      this.inFlight.delete(partNumber);
      return true;
    };

    request.upload.onprogress = (event) => {
      if (this.inFlight.get(partNumber) === entry) {
        entry.sent = Math.min(event.loaded, range.end - range.start);
        this.postProgress();
      }
    };
    request.onload = () => {
      if (!settle()) {
        return;
      }
      if (request.status >= 200 && request.status < 300) {
        void this.confirm(partNumber);
      } else {
        this.fail(partNumber, request.status, request.responseText);
      }
    };
    const networkFailure = () => {
      if (settle()) {
        this.fail(partNumber, 0);
      }
    };
    request.onerror = networkFailure;
    request.ontimeout = networkFailure;
    request.onabort = () => {
      settle();
    };

    request.open('PUT', grant.url);
    const checksum = Object.entries(grant.requiredHeaders).find(([name]) => name.toLowerCase() === checksumHeader);
    if (checksum) {
      request.setRequestHeader(checksumHeader, checksum[1]);
    }
    request.send(this.file.slice(range.start, range.end, ''));
  }

  private async confirm(partNumber: number): Promise<void> {
    this.confirmed.add(partNumber);
    this.attempts.delete(partNumber);
    this.regranted.delete(partNumber);
    this.grants.invalidate(partNumber);
    const parts = [...this.confirmed].sort((a, b) => a - b);
    try {
      await this.deps.journal.confirmParts(this.uploadSessionId, parts);
    } catch {
      if (!this.ended && !this.paused) {
        this.postProgress();
        this.pauseFor('storageUnavailable');
      }
      return;
    }
    this.postProgress();
    this.pump();
  }

  private fail(partNumber: number, status: number, body?: string): void {
    if (this.ended) {
      return;
    }
    const outcome = classifyStorageResponse({ status, body }, this.regranted.has(partNumber));
    if (outcome === 'fileChanged') {
      this.deps.post({ type: 'failed', jobId: this.jobId, reason: 'fileChanged' });
      this.end();
      return;
    }
    if (outcome === 'regrant') {
      this.regranted.add(partNumber);
      this.grants.invalidate(partNumber);
      this.pump();
      return;
    }
    const attempt = (this.attempts.get(partNumber) ?? 0) + 1;
    this.attempts.set(partNumber, attempt);
    const delay = outcome === 'retry' ? retryDelayMs(attempt, this.deps.random) : undefined;
    this.deps.post({
      type: 'partFailed',
      jobId: this.jobId,
      partNumber,
      status,
      retryable: outcome === 'retry' && delay !== undefined,
      attempt,
    });
    if (delay === undefined) {
      this.pauseFor('retriesExhausted');
      return;
    }
    this.waiting.set(
      partNumber,
      this.deps.setTimer(() => {
        this.waiting.delete(partNumber);
        this.pump();
      }, delay),
    );
  }

  // A pause caused by the worker yields the transfer slot to the next queued file.
  private pauseFor(reason: 'retriesExhausted' | 'storageUnavailable'): void {
    this.pause();
    this.deps.post({ type: 'paused', jobId: this.jobId, reason });
  }

  private postProgress(): void {
    const { totalSizeBytes, partSizeBytes } = this.declaration;
    let bytesConfirmed = 0;
    for (const partNumber of this.confirmed) {
      const range = partRange(partNumber, totalSizeBytes, partSizeBytes);
      bytesConfirmed += range.end - range.start;
    }
    let bytesInFlight = 0;
    for (const entry of this.inFlight.values()) {
      bytesInFlight += entry.sent;
    }
    this.deps.post({
      type: 'transferProgress',
      jobId: this.jobId,
      bytesConfirmed,
      bytesInFlight,
      confirmedParts: [...this.confirmed].sort((a, b) => a - b),
    });
  }

  private stopTransfers(): void {
    this.activation?.abort();
    for (const handle of this.waiting.values()) {
      this.deps.clearTimer(handle);
    }
    this.waiting.clear();
    const entries = [...this.inFlight.values()];
    this.inFlight.clear();
    for (const entry of entries) {
      entry.request.abort();
    }
    this.awaitingGrants = false;
  }

  private releaseSlot(): void {
    this.slotRelease?.();
    this.slotRelease = undefined;
  }

  private releaseLocks(): void {
    this.releaseSlot();
    this.sessionRelease?.();
    this.sessionRelease = undefined;
  }

  // Grants are dropped and both locks released when the job ends.
  private end(): void {
    if (this.ended) {
      return;
    }
    this.ended = true;
    this.paused = true;
    this.stopTransfers();
    this.grants.clear();
    this.releaseLocks();
    this.onEnded(this);
  }
}

export interface TransferManager {
  transfer(message: TransferMessage): Promise<void>;
  grants(jobId: string, grants: readonly UploadGrant[]): void;
  pause(jobId: string): void;
  resume(jobId: string): void;
  abort(jobId: string): void;
  abortSessions(uploadSessionIds: readonly string[]): void;
}

export function createTransferManager(dependencies: TransferDependencies): TransferManager {
  const deps = {
    ...dependencies,
    createRequest: dependencies.createRequest ?? ((): UploadRequest => new XMLHttpRequest() as unknown as UploadRequest),
    setTimer: dependencies.setTimer ?? ((callback: () => void, delayMs: number): unknown => setTimeout(callback, delayMs)),
    clearTimer: dependencies.clearTimer ?? ((handle: unknown): void => clearTimeout(handle as ReturnType<typeof setTimeout>)),
    random: dependencies.random ?? Math.random,
    now: dependencies.now ?? Date.now,
  };
  const jobs = new Map<string, TransferJob>();

  return {
    async transfer(message) {
      jobs.get(message.jobId)?.abort();
      const job = new TransferJob(
        message.jobId,
        message.uploadSessionId,
        message.file,
        message.declaration,
        message.confirmedParts,
        deps,
        (ended) => {
          if (jobs.get(ended.jobId) === ended) {
            jobs.delete(ended.jobId);
          }
        },
      );
      jobs.set(message.jobId, job);
      // Activation may wait for the slot lock; it must not block the worker's message handling.
      void job.start();
    },
    grants: (jobId, grants) => jobs.get(jobId)?.addGrants(grants),
    pause: (jobId) => jobs.get(jobId)?.pause(),
    resume: (jobId) => jobs.get(jobId)?.resume(),
    abort: (jobId) => jobs.get(jobId)?.abort(),
    abortSessions(uploadSessionIds) {
      for (const job of [...jobs.values()]) {
        if (uploadSessionIds.includes(job.uploadSessionId)) {
          job.abort();
        }
      }
    },
  };
}
