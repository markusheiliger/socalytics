import { describe, expect, it } from 'vitest';
import { compareVersions, isVersion, parseVersion } from '../../../src/serviceWorker/version';

describe('release version', () => {
  it('parses YYYY.MMDD.HHMMSS into three integers', () => {
    expect(parseVersion('2026.1015.194113')).toEqual([2026, 1015, 194113]);
  });

  it.each(['', '2026.1015', '2026.1015.194113.1', '26.1015.194113', '2026.1015.19411', 'v2026.1015.194113', '2026-1015-194113', ' 2026.1015.194113', '2026.1015.194113\n', '2026.10a5.194113'])(
    'rejects %j',
    (value) => {
      expect(parseVersion(value)).toBeUndefined();
      expect(isVersion(value)).toBe(false);
    },
  );

  it('rejects values that are not strings', () => {
    expect(parseVersion(undefined)).toBeUndefined();
    expect(parseVersion(20261015)).toBeUndefined();
  });

  it('compares as integers, not as text', () => {
    expect(compareVersions('2026.1015.194113', '2026.1015.194113')).toBe(0);
    expect(compareVersions('2026.1015.194113', '2026.1015.194114')).toBe(-1);
    expect(compareVersions('2026.1016.000000', '2026.1015.235959')).toBe(1);
    expect(compareVersions('2027.0101.000000', '2026.1231.235959')).toBe(1);
    expect(compareVersions('2026.0101.000009', '2026.0101.000010')).toBe(-1);
  });

  it('throws when comparing a rejected shape', () => {
    expect(() => compareVersions('1.2.3', '2026.1015.194113')).toThrow(RangeError);
    expect(() => compareVersions('2026.1015.194113', '')).toThrow(RangeError);
  });
});
