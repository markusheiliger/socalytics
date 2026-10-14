import type { JournalRecord } from './journal';

export interface UploadDeclaration {
  readonly totalSizeBytes: number;
  readonly partSizeBytes: number;
  readonly partCount: number;
  readonly partDigests: readonly string[];
  readonly contentDigest: string;
}

export interface UploadGrant {
  readonly partNumber: number;
  readonly url: string;
  readonly requiredHeaders: Readonly<Record<string, string>>;
  readonly expiresAt: string;
}

export type PrepareFailedReason = 'readError' | 'cancelled';
export type VerifyFailedReason = 'fileMismatch' | 'readError' | 'cancelled';
export type WorkerPausedReason = 'retriesExhausted' | 'storageUnavailable' | 'runningElsewhere';
export type JournalOperation = 'journalPut' | 'journalList' | 'journalDelete' | 'purgeMember';

// contracts/upload-worker.md: main thread → worker.
export type MainToWorkerMessage =
  | { readonly type: 'prepare'; readonly jobId: string; readonly file: File; readonly partSizeBytes?: number }
  | {
      readonly type: 'verify';
      readonly jobId: string;
      readonly file: File;
      readonly partSizeBytes: number;
      readonly expectedContentDigest: string;
    }
  | { readonly type: 'journalPut'; readonly record: JournalRecord }
  | { readonly type: 'journalList'; readonly memberId: string; readonly matchId?: string }
  | { readonly type: 'journalDelete'; readonly memberId: string; readonly uploadSessionId: string }
  | { readonly type: 'purgeMember'; readonly memberId: string }
  | {
      readonly type: 'transfer';
      readonly jobId: string;
      readonly uploadSessionId: string;
      readonly file: File;
      readonly declaration: UploadDeclaration;
      readonly confirmedParts: readonly number[];
    }
  | { readonly type: 'grants'; readonly jobId: string; readonly grants: readonly UploadGrant[] }
  | { readonly type: 'pause'; readonly jobId: string }
  | { readonly type: 'resume'; readonly jobId: string }
  | { readonly type: 'abort'; readonly jobId: string };

// contracts/upload-worker.md: worker → main thread.
export type WorkerToMainMessage =
  | {
      readonly type: 'prepared';
      readonly jobId: string;
      readonly partDigests: readonly string[];
      readonly contentDigest: string;
      readonly partSizeBytes: number;
      readonly partCount: number;
    }
  | { readonly type: 'prepareProgress'; readonly jobId: string; readonly bytesHashed: number; readonly totalBytes: number }
  | { readonly type: 'prepareFailed'; readonly jobId: string; readonly reason: PrepareFailedReason }
  | { readonly type: 'verified'; readonly jobId: string }
  | { readonly type: 'verifyFailed'; readonly jobId: string; readonly reason: VerifyFailedReason }
  | { readonly type: 'needGrants'; readonly jobId: string; readonly partNumbers: readonly number[] }
  | {
      readonly type: 'transferProgress';
      readonly jobId: string;
      readonly bytesConfirmed: number;
      readonly bytesInFlight: number;
      readonly confirmedParts: readonly number[];
    }
  | {
      readonly type: 'partFailed';
      readonly jobId: string;
      readonly partNumber: number;
      readonly status: number;
      readonly retryable: boolean;
      readonly attempt: number;
    }
  | { readonly type: 'paused'; readonly jobId: string; readonly reason: WorkerPausedReason }
  | { readonly type: 'transferred'; readonly jobId: string }
  | { readonly type: 'failed'; readonly jobId: string; readonly reason: 'fileChanged' }
  | { readonly type: 'journalRecords'; readonly memberId: string; readonly matchId?: string; readonly records: readonly JournalRecord[] }
  | { readonly type: 'purged'; readonly memberId: string }
  // The browser refused a journal operation (for example private browsing or a full disk).
  | { readonly type: 'journalFailed'; readonly operation: JournalOperation; readonly reason: 'storageUnavailable' };
