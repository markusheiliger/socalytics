import { describe, expect, it } from 'vitest';
import { enumerationText, formatDuration, formatTimestamp } from '../../../../src/features/operations/formatOperations';

describe('formatTimestamp', () => {
  it('keeps the exact ISO value next to the local text', () => {
    const result = formatTimestamp(new Date('2026-10-17T21:51:12.345Z'));
    expect(result?.iso).toBe('2026-10-17T21:51:12.345Z');
    expect(result?.text).toContain('2026');
  });

  it('returns undefined for absent and invalid values', () => {
    expect(formatTimestamp(undefined)).toBeUndefined();
    expect(formatTimestamp(new Date('nope'))).toBeUndefined();
  });
});

describe('formatDuration', () => {
  it.each([
    [0, '0 s'],
    [59, '59 s'],
    [60, '1 min'],
    [61, '1 min 1 s'],
    [3_600, '1 h'],
    [7_500, '2 h 5 min'],
    [86_400, '1 d'],
    [90_061, '1 d 1 h'],
    [172_859, '2 d'],
  ])('formats %d seconds as %s', (seconds, text) => {
    expect(formatDuration(seconds)?.text).toBe(text);
  });

  it('keeps the exact seconds available', () => {
    expect(formatDuration(7_500)?.exact).toBe('7500 s');
  });

  it('truncates fractions and clamps negatives', () => {
    expect(formatDuration(59.9)?.text).toBe('59 s');
    expect(formatDuration(-5)?.text).toBe('0 s');
  });

  it('returns undefined for absent and non-finite values', () => {
    expect(formatDuration(undefined)).toBeUndefined();
    expect(formatDuration(Number.NaN)).toBeUndefined();
  });
});

describe('enumerationText', () => {
  const known = ['pending', 'failed'];
  const translate = (v: string) => `T:${v}`;

  it('words known values', () => {
    expect(enumerationText('failed', known, translate)).toBe('T:failed');
  });

  it('falls back to the raw text for unknown values', () => {
    expect(enumerationText('mystery', known, translate)).toBe('mystery');
  });

  it('returns undefined for absent values', () => {
    expect(enumerationText(undefined, known, translate)).toBeUndefined();
    expect(enumerationText('', known, translate)).toBeUndefined();
  });
});
