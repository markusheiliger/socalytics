import type { EvidenceMediaPiece, EvidencePart } from '../../../../src/platform/models/analysis';
import {
  EXPIRY_MARGIN_MS,
  expiresWithinMargin,
  hasReachedEnd,
  isInsideFact,
  isPlayable,
  matchTimeAt,
  nextPlayablePart,
  orderParts,
  playableParts,
  seekTargetForMatchTime,
  seekTargetSeconds,
  stopTargetSeconds,
} from '../../../../src/features/analysis/evidenceTimeline';

function piece(startOffsetMs: number, endOffsetMs: number, expiresAt = '2026-10-23T12:05:00Z'): EvidenceMediaPiece {
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

function available(startMs: number, endMs: number, startOffsetMs = 1000, endOffsetMs = 1000 + (endMs - startMs)) {
  return { startMs, endMs, state: 'available', media: piece(startOffsetMs, endOffsetMs) } as EvidencePart & {
    media: EvidenceMediaPiece;
  };
}

describe('part order', () => {
  it('orders parts by match time and keeps only available parts with media as playable', () => {
    const parts: EvidencePart[] = [
      available(20000, 30000),
      { startMs: 10000, endMs: 20000, state: 'failed', reason: 'segment-media-invalid' },
      available(0, 10000),
      { startMs: 30000, endMs: 40000, state: 'available' },
      { startMs: 40000, endMs: 50000, state: 'not-covered' },
    ];
    expect(orderParts(parts).map((part) => part.startMs)).toEqual([0, 10000, 20000, 30000, 40000]);
    expect(playableParts(parts).map((part) => part.startMs)).toEqual([0, 20000]);
    expect(isPlayable(parts[3])).toBe(false);
  });

  it('does not change the input order', () => {
    const parts = [available(10000, 20000), available(0, 10000)];
    orderParts(parts);
    expect(parts[0].startMs).toBe(10000);
  });

  it('finds the next playable part after a match time, skipping unavailable parts', () => {
    const parts: EvidencePart[] = [
      available(0, 10000),
      { startMs: 10000, endMs: 20000, state: 'failed' },
      available(20000, 30000),
    ];
    expect(nextPlayablePart(parts, 0)?.startMs).toBe(0);
    expect(nextPlayablePart(parts, 10000)?.startMs).toBe(20000);
    expect(nextPlayablePart(parts, 30000)).toBeUndefined();
  });
});

describe('seek and stop targets', () => {
  it('adds the offsets to the first seekable time', () => {
    const media = piece(295040, 300040);
    expect(seekTargetSeconds(0, media)).toBeCloseTo(295.04);
    expect(seekTargetSeconds(2.5, media)).toBeCloseTo(297.54);
    expect(stopTargetSeconds(2.5, media)).toBeCloseTo(302.54);
  });

  it('detects the end of a piece', () => {
    const media = piece(1000, 6000);
    expect(hasReachedEnd(0.5, media, 6.4)).toBe(false);
    expect(hasReachedEnd(0.5, media, 6.5)).toBe(true);
    expect(hasReachedEnd(0.5, media, 7)).toBe(true);
  });
});

describe('match-time position', () => {
  const part = available(100000, 110000, 4000, 14000);

  it('maps the media time to match time', () => {
    expect(matchTimeAt(0, part, 4)).toBe(100000);
    expect(matchTimeAt(0, part, 6.5)).toBe(102500);
    expect(matchTimeAt(1, part, 6.5)).toBe(101500);
  });

  it('clamps to the part window', () => {
    expect(matchTimeAt(0, part, 0)).toBe(100000);
    expect(matchTimeAt(0, part, 30)).toBe(110000);
  });

  it('maps a match time back to a seek target for resumption', () => {
    expect(seekTargetForMatchTime(0, part, 102500)).toBeCloseTo(6.5);
    expect(seekTargetForMatchTime(1, part, 102500)).toBeCloseTo(7.5);
    expect(seekTargetForMatchTime(0, part, 90000)).toBeCloseTo(4);
    expect(seekTargetForMatchTime(0, part, 150000)).toBeCloseTo(14);
  });
});

describe('inside-fact test', () => {
  const fact = { startMs: 5000, endMs: 8000 };

  it('treats the fact window as half-open', () => {
    expect(isInsideFact(fact, 4999)).toBe(false);
    expect(isInsideFact(fact, 5000)).toBe(true);
    expect(isInsideFact(fact, 7999)).toBe(true);
    expect(isInsideFact(fact, 8000)).toBe(false);
  });

  it('is never inside without a fact', () => {
    expect(isInsideFact(undefined, 5000)).toBe(false);
  });
});

describe('expiry margin', () => {
  const now = Date.parse('2026-10-23T12:00:00Z');

  it('uses a margin of 30 seconds', () => {
    expect(EXPIRY_MARGIN_MS).toBe(30000);
  });

  it('flags links that expire within the margin or are already expired', () => {
    expect(expiresWithinMargin(piece(0, 1, '2026-10-23T12:00:29Z'), now)).toBe(true);
    expect(expiresWithinMargin(piece(0, 1, '2026-10-23T12:00:30Z'), now)).toBe(true);
    expect(expiresWithinMargin(piece(0, 1, '2026-10-23T11:59:00Z'), now)).toBe(true);
    expect(expiresWithinMargin(piece(0, 1, '2026-10-23T12:00:31Z'), now)).toBe(false);
  });

  it('treats an unreadable expiry as expired', () => {
    expect(expiresWithinMargin(piece(0, 1, 'soon'), now)).toBe(true);
  });
});
