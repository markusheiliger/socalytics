import { classifyStorageResponse, isRetryableStatus, retryDelayMs } from '../../../../src/uploads/retryPolicy';

describe('retry policy', () => {
  it.each([0, 408, 429, 500, 502, 503, 599])('retries status %i', (status) => {
    expect(isRetryableStatus(status)).toBe(true);
  });

  it.each([200, 400, 401, 403, 404, 413, 415, 600])('does not retry status %i', (status) => {
    expect(isRetryableStatus(status)).toBe(false);
  });

  it('waits 1, 2, 4, 8, and 16 seconds', () => {
    expect([1, 2, 3, 4, 5].map((attempt) => retryDelayMs(attempt, () => 0.5))).toEqual([1000, 2000, 4000, 8000, 16000]);
  });

  it('applies up to 20 % jitter in both directions', () => {
    expect(retryDelayMs(1, () => 0)).toBe(800);
    expect(retryDelayMs(1, () => 1)).toBe(1200);
    expect(retryDelayMs(5, () => 0)).toBe(12800);
    expect(retryDelayMs(5, () => 1)).toBe(19200);
  });

  it('gives up after five retries', () => {
    expect(retryDelayMs(6)).toBeUndefined();
    expect(retryDelayMs(0)).toBeUndefined();
  });

  it('treats 400 BadDigest as a changed file and never retries it', () => {
    expect(classifyStorageResponse({ status: 400, body: '<Code>BadDigest</Code>' }, false)).toBe('fileChanged');
    expect(classifyStorageResponse({ status: 400, body: '<Code>BadDigest</Code>' }, true)).toBe('fileChanged');
    expect(classifyStorageResponse({ status: 400, body: '<Code>Other</Code>' }, false)).toBe('fatal');
  });

  it('re-grants once on 403', () => {
    expect(classifyStorageResponse({ status: 403 }, false)).toBe('regrant');
    expect(classifyStorageResponse({ status: 403 }, true)).toBe('fatal');
  });

  it('classifies retryable and fatal statuses', () => {
    expect(classifyStorageResponse({ status: 503 }, false)).toBe('retry');
    expect(classifyStorageResponse({ status: 0 }, false)).toBe('retry');
    expect(classifyStorageResponse({ status: 404 }, false)).toBe('fatal');
  });
});
