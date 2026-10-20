export type RangeError = 'invalidTime' | 'negative' | 'notIncreasing';

export type RangeValidation =
  | { ok: true; range: { fromMs?: number; toMs?: number } }
  | { ok: false; error: RangeError; side: 'from' | 'to' | 'range' };

const CLOCK = /^(\d+):([0-5]\d)(?:\.(\d{3}))?$/;

export function formatMatchClock(ms: number): string {
  const total = Math.max(0, Math.trunc(ms));
  const millis = total % 1000;
  const totalSeconds = Math.floor(total / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = String(totalSeconds % 60).padStart(2, '0');
  const base = `${minutes}:${seconds}`;
  return millis === 0 ? base : `${base}.${String(millis).padStart(3, '0')}`;
}

export function formatMatchRange(startMs: number, endMs: number): { text: string; label: string } {
  const from = formatMatchClock(startMs);
  const to = formatMatchClock(endMs);
  return { text: `${from}–${to}`, label: `from ${from} to ${to} (end excluded)` };
}

export function parseMatchClock(text: string): number | undefined {
  const match = CLOCK.exec(text.trim());
  if (!match) return undefined;
  return (Number(match[1]) * 60 + Number(match[2])) * 1000 + (match[3] ? Number(match[3]) : 0);
}

type Side = { ms?: number } | { error: RangeError };

function readSide(value: string | number | undefined): Side {
  if (value === undefined) return {};
  if (typeof value === 'number') {
    if (!Number.isInteger(value)) return { error: 'invalidTime' };
    return value < 0 ? { error: 'negative' } : { ms: value };
  }
  const text = value.trim();
  if (text === '') return {};
  if (text.startsWith('-')) return { error: 'negative' };
  const ms = parseMatchClock(text);
  return ms === undefined ? { error: 'invalidTime' } : { ms };
}

// An empty or undefined side is unbounded; numbers are milliseconds, strings are clock texts.
export function validateRange(from: string | number | undefined, to: string | number | undefined): RangeValidation {
  const start = readSide(from);
  if ('error' in start) return { ok: false, error: start.error, side: 'from' };
  const end = readSide(to);
  if ('error' in end) return { ok: false, error: end.error, side: 'to' };
  if (start.ms !== undefined && end.ms !== undefined && end.ms <= start.ms) {
    return { ok: false, error: 'notIncreasing', side: 'range' };
  }
  const range: { fromMs?: number; toMs?: number } = {};
  if (start.ms !== undefined) range.fromMs = start.ms;
  if (end.ms !== undefined) range.toMs = end.ms;
  return { ok: true, range };
}
