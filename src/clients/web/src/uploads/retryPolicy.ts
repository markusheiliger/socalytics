export const maxRetries = 5;
export const retryBaseDelaysMs: readonly number[] = [1000, 2000, 4000, 8000, 16000];
export const jitterRatio = 0.2;

// Status 0 is a network error.
export function isRetryableStatus(status: number): boolean {
  return status === 0 || status === 408 || status === 429 || (status >= 500 && status <= 599);
}

// `attempt` is the number of failed attempts so far (1-based); undefined means no retry is left.
export function retryDelayMs(attempt: number, random: () => number = Math.random): number | undefined {
  if (!Number.isInteger(attempt) || attempt < 1 || attempt > maxRetries) {
    return undefined;
  }
  const jitter = 1 + (random() * 2 - 1) * jitterRatio;
  return Math.round(retryBaseDelaysMs[attempt - 1] * jitter);
}

export type StorageOutcome = 'retry' | 'fileChanged' | 'regrant' | 'fatal';

export interface StorageResponse {
  readonly status: number;
  readonly body?: string;
}

export function classifyStorageResponse(response: StorageResponse, alreadyRegranted: boolean): StorageOutcome {
  if (response.status === 400 && response.body?.includes('BadDigest')) {
    return 'fileChanged';
  }
  if (response.status === 403 && !alreadyRegranted) {
    return 'regrant';
  }
  return isRetryableStatus(response.status) ? 'retry' : 'fatal';
}
