import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  initialPlaybackModel,
  MAX_AUTOMATIC_RESOLUTIONS,
  playbackReducer,
  playbackStatus,
  useEvidencePlayback,
} from '../../../../src/features/analysis/useEvidencePlayback';
import type { AnalysisEvidenceAdapter } from '../../../../src/platform/adapters/analysisEvidenceAdapter';
import type {
  EvidenceMediaPiece,
  EvidenceOutcome,
  EvidencePart,
  EvidenceRequest,
  EvidenceResolution,
} from '../../../../src/platform/models/analysis';

const NOW = Date.parse('2026-10-23T12:00:00Z');

function piece(startOffsetMs = 1000, endOffsetMs = 11000, expiresAt = '2026-10-23T12:05:00Z'): EvidenceMediaPiece {
  return {
    url: 'https://objects.example/segment.mp4',
    expiresAt,
    mediaType: 'video/mp4',
    mediaDigest: 'sha-256:abc',
    mediaSizeBytes: 10,
    startOffsetMs,
    endOffsetMs,
    recordingVersionId: 'rv-1',
  };
}

const available = (startMs: number, endMs: number, expiresAt?: string): EvidencePart => ({
  startMs,
  endMs,
  state: 'available',
  media: piece(1000, 1000 + endMs - startMs, expiresAt),
});

function resolutionOf(parts: EvidencePart[], extra: Partial<EvidenceResolution> = {}): EvidenceResolution {
  return {
    run: { runId: 'run-1', matchId: 'm', teamId: 't' },
    requested: { fromMs: parts[0].startMs, toMs: parts[parts.length - 1].endMs },
    fact: { startMs: 15000, endMs: 20000 },
    shortened: false,
    parts,
    ...extra,
  };
}

const found = (parts: EvidencePart[], extra?: Partial<EvidenceResolution>): EvidenceOutcome => ({
  kind: 'resolution',
  resolution: resolutionOf(parts, extra),
});

function setup(...outcomes: EvidenceOutcome[]) {
  const requests: EvidenceRequest[] = [];
  let index = 0;
  const adapter: AnalysisEvidenceAdapter = {
    resolve: vi.fn(async (_runId, request) => {
      requests.push(request);
      return outcomes[Math.min(index++, outcomes.length - 1)];
    }),
  };
  const hook = renderHook(() => useEvidencePlayback(adapter, 'run-1', { acceptedResultId: 'r1', ordinal: 3 }, { now: () => NOW }));
  return { adapter, requests, hook };
}

const flush = () => act(async () => { await vi.advanceTimersByTimeAsync(0); });
const tick = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms); });

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe('playback reducer', () => {
  it('maps resolutions and failed outcomes to states', () => {
    const resolved = playbackReducer(initialPlaybackModel, {
      type: 'outcome',
      outcome: found([available(10000, 20000)]),
    });
    expect(playbackStatus(resolved)).toBe('playing');
    expect(resolved.matchMs).toBe(10000);

    const cases: [EvidenceOutcome, string][] = [
      [{ kind: 'notFound' }, 'notFound'],
      [{ kind: 'rateLimited', retryAfterSeconds: 7 }, 'rateLimited'],
      [{ kind: 'offline' }, 'offline'],
      [{ kind: 'storageUnavailable' }, 'error'],
      [{ kind: 'error' }, 'error'],
    ];
    for (const [outcome, status] of cases) {
      expect(playbackStatus(playbackReducer(initialPlaybackModel, { type: 'outcome', outcome }))).toBe(status);
    }
    expect(
      playbackReducer(initialPlaybackModel, { type: 'outcome', outcome: { kind: 'rateLimited', retryAfterSeconds: 7 } }).retryAfterSeconds,
    ).toBe(7);
  });

  it('distinguishes preparing from unavailable parts and keeps unknown states unavailable', () => {
    const status = (state: string) =>
      playbackStatus(playbackReducer(initialPlaybackModel, { type: 'outcome', outcome: found([{ startMs: 0, endMs: 5000, state }]) }));
    expect(status('materializing')).toBe('preparing');
    expect(status('rematerializing')).toBe('preparing');
    expect(status('failed')).toBe('partUnavailable');
    expect(status('not-covered')).toBe('partUnavailable');
    expect(status('something-new')).toBe('partUnavailable');
  });

  it('continues at the end of the unavailable part', () => {
    const model = playbackReducer(initialPlaybackModel, {
      type: 'outcome',
      outcome: found([{ startMs: 0, endMs: 5000, state: 'failed', reason: 'extraction-failed' }, available(5000, 10000)]),
    });
    const next = playbackReducer(model, { type: 'continue' });
    expect(next.matchMs).toBe(5000);
    expect(playbackStatus(next)).toBe('playing');
  });
});

describe('useEvidencePlayback', () => {
  it('requests the fact with 5 seconds of context on each side and starts playing', async () => {
    const { requests, hook } = setup(found([available(10000, 25000)]));
    expect(hook.result.current.status).toBe('resolving');
    await flush();
    expect(requests).toEqual([{ fact: { acceptedResultId: 'r1', ordinal: 3, contextBeforeMs: 5000, contextAfterMs: 5000 } }]);
    expect(hook.result.current.status).toBe('playing');
    expect(hook.result.current.playing?.startMs).toBe(10000);
    expect(hook.result.current.insideFact).toBe(false);
    act(() => hook.result.current.setPosition(16000));
    expect(hook.result.current.insideFact).toBe(true);
  });

  it('advances across parts without member action and finishes', async () => {
    const { hook } = setup(found([available(10000, 15000), available(15000, 20000)]));
    await flush();
    expect(hook.result.current.nextPlayable?.startMs).toBe(15000);
    act(() => hook.result.current.advance());
    expect(hook.result.current.playing?.startMs).toBe(15000);
    act(() => hook.result.current.advance());
    expect(hook.result.current.finished).toBe(true);
  });

  it('reports the shortened notice', async () => {
    const { hook } = setup(found([available(10000, 15000)], { shortened: true }));
    await flush();
    expect(hook.result.current.shortened).toBe(true);
  });

  it('resolves again after the hint while preparing and plays once available', async () => {
    const preparing: EvidencePart = { startMs: 10000, endMs: 15000, state: 'materializing', retryAfterSeconds: 3 };
    const { requests, hook } = setup(found([preparing]), found([preparing]), found([available(10000, 15000)]));
    await flush();
    expect(hook.result.current.status).toBe('preparing');
    await tick(2900);
    expect(requests).toHaveLength(1);
    await tick(200);
    expect(requests).toHaveLength(2);
    expect(hook.result.current.status).toBe('preparing');
    await tick(3100);
    expect(requests).toHaveLength(3);
    expect(hook.result.current.status).toBe('playing');
    expect(requests.every((r) => r.retryFailed === undefined)).toBe(true);
  });

  it('stops after 20 automatic re-resolutions and offers Try again', async () => {
    const preparing: EvidencePart = { startMs: 10000, endMs: 15000, state: 'rematerializing', retryAfterSeconds: 1 };
    const { requests, hook } = setup(found([preparing]));
    await flush();
    for (let i = 0; i < 30; i += 1) await tick(1000);
    expect(requests).toHaveLength(1 + MAX_AUTOMATIC_RESOLUTIONS);
    expect(hook.result.current.automaticAttemptsExhausted).toBe(true);

    act(() => hook.result.current.tryAgain());
    await flush();
    expect(requests).toHaveLength(2 + MAX_AUTOMATIC_RESOLUTIONS);
    expect(requests[requests.length - 1].retryFailed).toBeUndefined();
    expect(hook.result.current.automaticAttemptsExhausted).toBe(false);
  });

  it('sets retryFailed only when trying again for a failed part', async () => {
    const failed: EvidencePart = { startMs: 10000, endMs: 15000, state: 'failed', reason: 'extraction-failed' };
    const notCovered: EvidencePart = { startMs: 10000, endMs: 15000, state: 'not-covered' };
    const first = setup(found([failed]), found([available(10000, 15000)]));
    await flush();
    expect(first.hook.result.current.status).toBe('partUnavailable');
    expect(first.hook.result.current.pending?.reason).toBe('extraction-failed');
    act(() => first.hook.result.current.tryAgain());
    await flush();
    expect(first.requests[1].retryFailed).toBe(true);
    expect(first.hook.result.current.status).toBe('playing');
    first.hook.unmount();

    const second = setup(found([notCovered]));
    await flush();
    act(() => second.hook.result.current.tryAgain());
    await flush();
    expect(second.requests[1].retryFailed).toBeUndefined();
  });

  it('renews once at the current match time after a media error and then fails', async () => {
    const { requests, hook } = setup(found([available(10000, 25000)]), found([available(10000, 25000)]));
    await flush();
    act(() => hook.result.current.setPosition(18000));
    act(() => hook.result.current.mediaFailed());
    expect(hook.result.current.status).toBe('renewing');
    await flush();
    expect(requests).toHaveLength(2);
    expect(hook.result.current.status).toBe('playing');
    expect(hook.result.current.matchMs).toBe(18000);

    act(() => hook.result.current.mediaFailed());
    await flush();
    expect(requests).toHaveLength(2);
    expect(hook.result.current.status).toBe('error');

    act(() => hook.result.current.tryAgain());
    await flush();
    expect(hook.result.current.status).toBe('playing');
    expect(hook.result.current.matchMs).toBe(18000);
  });

  it('resolves again before starting a piece whose link expires within 30 seconds', async () => {
    const expiring = available(10000, 20000, '2026-10-23T12:00:20Z');
    const { requests, hook } = setup(found([expiring]), found([available(10000, 20000)]));
    await flush();
    expect(requests).toHaveLength(2);
    expect(hook.result.current.status).toBe('playing');
    expect(hook.result.current.playing?.media.expiresAt).toBe('2026-10-23T12:05:00Z');
  });

  it('reports not found, rate limiting with the delay, offline, and errors', async () => {
    const notFound = setup({ kind: 'notFound' });
    await flush();
    expect(notFound.hook.result.current.status).toBe('notFound');

    const limited = setup({ kind: 'rateLimited', retryAfterSeconds: 12 }, found([available(10000, 15000)]));
    await flush();
    expect(limited.hook.result.current.status).toBe('rateLimited');
    expect(limited.hook.result.current.retryAfterSeconds).toBe(12);
    act(() => limited.hook.result.current.tryAgain());
    await flush();
    expect(limited.hook.result.current.status).toBe('playing');

    const offline = setup({ kind: 'offline' });
    await flush();
    expect(offline.hook.result.current.status).toBe('offline');

    const storage = setup({ kind: 'storageUnavailable' });
    await flush();
    expect(storage.hook.result.current.status).toBe('error');
  });

  it('turns an adapter exception into a recoverable error and ignores results after unmount', async () => {
    const adapter: AnalysisEvidenceAdapter = { resolve: vi.fn().mockRejectedValue(new Error('boom')) };
    const failing = renderHook(() => useEvidencePlayback(adapter, 'run-1', { acceptedResultId: 'r1', ordinal: 0 }));
    await flush();
    expect(failing.result.current.status).toBe('error');

    const { hook } = setup(found([available(10000, 15000)]));
    hook.unmount();
    await flush();
  });
});
