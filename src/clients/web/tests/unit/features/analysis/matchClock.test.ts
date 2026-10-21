import { formatMatchClock, formatMatchRange, parseMatchClock, validateRange } from '../../../../src/features/analysis/matchClock';

describe('formatMatchClock', () => {
  it('formats minutes and seconds', () => {
    expect(formatMatchClock(0)).toBe('0:00');
    expect(formatMatchClock(5000)).toBe('0:05');
    expect(formatMatchClock(754000)).toBe('12:34');
  });

  it('shows milliseconds only when not zero', () => {
    expect(formatMatchClock(750)).toBe('0:00.750');
    expect(formatMatchClock(12 * 60000 + 30500)).toBe('12:30.500');
    expect(formatMatchClock(1001)).toBe('0:01.001');
  });

  it('keeps unbounded minutes beyond 90 minutes', () => {
    expect(formatMatchClock(95 * 60000 + 7000)).toBe('95:07');
    expect(formatMatchClock(120 * 60000 + 59999)).toBe('120:59.999');
  });
});

describe('formatMatchRange', () => {
  it('gives text and an accessible label that excludes the end', () => {
    expect(formatMatchRange(0, 91 * 60000)).toEqual({
      text: '0:00–91:00',
      label: 'from 0:00 to 91:00 (end excluded)',
    });
  });
});

describe('parseMatchClock', () => {
  it('accepts m:ss and m:ss.SSS', () => {
    expect(parseMatchClock('0:05')).toBe(5000);
    expect(parseMatchClock('12:30.500')).toBe(750500);
    expect(parseMatchClock(' 100:00 ')).toBe(6000000);
  });

  it.each(['', '5', '1:5', '1:60', '1:05.5', '1:05.5000', '-1:00', '1:05.', 'a:bc', '1:2:3'])('rejects %j', (text) => {
    expect(parseMatchClock(text)).toBeUndefined();
  });

  it('round-trips with formatMatchClock', () => {
    for (const ms of [0, 1, 999, 59999, 60000, 5_400_001, 7_200_000]) {
      expect(parseMatchClock(formatMatchClock(ms))).toBe(ms);
    }
  });
});

describe('validateRange', () => {
  it('treats empty sides as unbounded', () => {
    expect(validateRange('', '')).toEqual({ ok: true, range: {} });
    expect(validateRange(undefined, undefined)).toEqual({ ok: true, range: {} });
    expect(validateRange('1:00', '')).toEqual({ ok: true, range: { fromMs: 60000 } });
    expect(validateRange('', '2:00.250')).toEqual({ ok: true, range: { toMs: 120250 } });
  });

  it('accepts an increasing range beyond 90 minutes', () => {
    expect(validateRange('91:00', '100:00')).toEqual({ ok: true, range: { fromMs: 5460000, toMs: 6000000 } });
  });

  it('accepts millisecond numbers from the address', () => {
    expect(validateRange(0, 1)).toEqual({ ok: true, range: { fromMs: 0, toMs: 1 } });
  });

  it('reports malformed sides', () => {
    expect(validateRange('abc', '')).toEqual({ ok: false, error: 'invalidTime', side: 'from' });
    expect(validateRange('', '1:75')).toEqual({ ok: false, error: 'invalidTime', side: 'to' });
    expect(validateRange(1.5, undefined)).toEqual({ ok: false, error: 'invalidTime', side: 'from' });
  });

  it('reports negative sides', () => {
    expect(validateRange('-0:05', '')).toEqual({ ok: false, error: 'negative', side: 'from' });
    expect(validateRange(undefined, -1)).toEqual({ ok: false, error: 'negative', side: 'to' });
  });

  it('reports a range that does not increase', () => {
    expect(validateRange('2:00', '2:00')).toEqual({ ok: false, error: 'notIncreasing', side: 'range' });
    expect(validateRange(5000, 1000)).toEqual({ ok: false, error: 'notIncreasing', side: 'range' });
  });
});
