import { fullPayloadText, summarizePayload } from '../../../../src/features/analysis/payloadSummary';
import type { JsonValue } from '../../../../src/platform/models/analysis';

describe('summarizePayload', () => {
  it('lists top-level scalar fields in returned order', () => {
    expect(summarizePayload({ b: 1, a: 'x', c: true, d: null })).toBe('b: 1, a: x, c: true, d: null');
  });

  it('summarizes nested values by size', () => {
    expect(summarizePayload({ box: { x: 1, y: 2, z: { deep: 1 } }, ids: [1, 2, 3], none: [] })).toBe(
      'box: {3 fields}, ids: [3 items], none: [0 items]',
    );
  });

  it('shows at most six fields and counts the remainder', () => {
    const payload: Record<string, JsonValue> = {};
    for (let i = 1; i <= 9; i += 1) payload[`f${i}`] = i;
    expect(summarizePayload(payload)).toBe('f1: 1, f2: 2, f3: 3, f4: 4, f5: 5, f6: 6, +3 more');
  });

  it('cuts long scalars to 60 characters', () => {
    const summary = summarizePayload({ text: 'a'.repeat(500) });
    expect(summary).toBe(`text: ${'a'.repeat(59)}…`);
    expect(Array.from(summary.slice(6))).toHaveLength(60);
    expect(summarizePayload({ text: 'b'.repeat(60) })).toBe(`text: ${'b'.repeat(60)}`);
  });

  it('stays bounded for a large payload with deep nesting', () => {
    let nested: JsonValue = 'leaf';
    for (let i = 0; i < 200; i += 1) nested = { next: nested };
    const payload: Record<string, JsonValue> = { nested };
    for (let i = 0; i < 5000; i += 1) payload[`k${i}`] = 'v'.repeat(1000);
    const summary = summarizePayload(payload);
    expect(summary.startsWith('nested: {1 fields}, k0: ')).toBe(true);
    expect(summary.endsWith(', +4995 more')).toBe(true);
    expect(summary.length).toBeLessThan(6 * 80);
  });

  it('keeps markup as plain text', () => {
    const summary = summarizePayload({ '<b>name</b>': '<img src=x onerror=alert(1)>' });
    expect(summary).toBe('<b>name</b>: <img src=x onerror=alert(1)>');
  });

  it('handles non-object payloads', () => {
    expect(summarizePayload('text')).toBe('text');
    expect(summarizePayload(42)).toBe('42');
    expect(summarizePayload(null)).toBe('null');
    expect(summarizePayload([1, 2])).toBe('[2 items]');
    expect(summarizePayload({})).toBe('{0 fields}');
  });
});

describe('fullPayloadText', () => {
  it('returns indented JSON exactly as returned', () => {
    const payload = { a: [1, { b: '<script>x</script>' }], c: null };
    expect(fullPayloadText(payload)).toBe(JSON.stringify(payload, null, 2));
    expect(fullPayloadText(payload)).toContain('<script>x</script>');
  });
});
