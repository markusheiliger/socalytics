export const DEFAULT_POLL_DELAY_MS = 2000;
export const MAX_POLL_DELAY_MS = 15000;
const BACKOFF_FACTOR = 1.5;

/** First delay in milliseconds: the platform's suggested interval, or 2 seconds when it is absent or unusable. */
export function initialPollDelay(retryAfterSeconds?: number): number {
  if (retryAfterSeconds === undefined || !Number.isFinite(retryAfterSeconds) || retryAfterSeconds <= 0) {
    return DEFAULT_POLL_DELAY_MS;
  }
  return Math.min(retryAfterSeconds * 1000, MAX_POLL_DELAY_MS);
}

/**
 * Next delay in milliseconds: grows by 1.5 up to 15 seconds while the status is unchanged and
 * resets to the platform's interval (`baseDelay`, default 2 seconds) when it changed.
 */
export function nextPollDelay(current: number, changed: boolean, baseDelay: number = DEFAULT_POLL_DELAY_MS): number {
  if (changed) {
    return baseDelay;
  }
  return Math.min(current * BACKOFF_FACTOR, MAX_POLL_DELAY_MS);
}
