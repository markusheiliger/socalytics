import type { EvidenceMediaPiece, EvidencePart } from '../../platform/models/analysis';

export const EXPIRY_MARGIN_MS = 30_000;

export type PlayablePart = EvidencePart & { media: EvidenceMediaPiece };

export function isPlayable(part: EvidencePart): part is PlayablePart {
  return part.state === 'available' && part.media !== undefined;
}

// Parts are half-open match-time windows; playback order is match-time order.
export function orderParts(parts: readonly EvidencePart[]): EvidencePart[] {
  return [...parts].sort((a, b) => a.startMs - b.startMs || a.endMs - b.endMs);
}

export function playableParts(parts: readonly EvidencePart[]): PlayablePart[] {
  return orderParts(parts).filter(isPlayable);
}

/** The first playable part that ends after `matchMs`, so playback can continue past an unavailable part. */
export function nextPlayablePart(parts: readonly EvidencePart[], matchMs: number): PlayablePart | undefined {
  return playableParts(parts).find((part) => part.endMs > matchMs);
}

// Offsets are measured from the media's first seekable time, in seconds.
export function seekTargetSeconds(firstSeekableSeconds: number, piece: EvidenceMediaPiece): number {
  return firstSeekableSeconds + piece.startOffsetMs / 1000;
}

export function stopTargetSeconds(firstSeekableSeconds: number, piece: EvidenceMediaPiece): number {
  return firstSeekableSeconds + piece.endOffsetMs / 1000;
}

export function seekTargetForMatchTime(firstSeekableSeconds: number, part: PlayablePart, matchMs: number): number {
  const clamped = Math.min(Math.max(matchMs, part.startMs), part.endMs);
  return seekTargetSeconds(firstSeekableSeconds, part.media) + (clamped - part.startMs) / 1000;
}

export function matchTimeAt(firstSeekableSeconds: number, part: PlayablePart, currentTimeSeconds: number): number {
  const elapsedMs = (currentTimeSeconds - seekTargetSeconds(firstSeekableSeconds, part.media)) * 1000;
  return Math.min(Math.max(part.startMs + elapsedMs, part.startMs), part.endMs);
}

export function hasReachedEnd(firstSeekableSeconds: number, piece: EvidenceMediaPiece, currentTimeSeconds: number): boolean {
  return currentTimeSeconds >= stopTargetSeconds(firstSeekableSeconds, piece);
}

export function isInsideFact(fact: { startMs: number; endMs: number } | undefined, matchMs: number): boolean {
  return fact !== undefined && matchMs >= fact.startMs && matchMs < fact.endMs;
}

export function expiresWithinMargin(piece: EvidenceMediaPiece, nowMs: number, marginMs: number = EXPIRY_MARGIN_MS): boolean {
  const expiresAtMs = Date.parse(piece.expiresAt);
  return Number.isNaN(expiresAtMs) || expiresAtMs - nowMs <= marginMs;
}
