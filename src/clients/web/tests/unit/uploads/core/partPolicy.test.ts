import { MiB, maxPartCount, partRange, planParts } from '../../../../src/uploads/partPolicy';

describe('planParts', () => {
  it('uses one part whose size equals the file for files up to 16 MiB', () => {
    expect(planParts(1)).toEqual({ partSizeBytes: 1, partCount: 1 });
    expect(planParts(1234567)).toEqual({ partSizeBytes: 1234567, partCount: 1 });
    expect(planParts(16 * MiB)).toEqual({ partSizeBytes: 16 * MiB, partCount: 1 });
  });

  it('uses 16 MiB parts above 16 MiB', () => {
    expect(planParts(16 * MiB + 1)).toEqual({ partSizeBytes: 16 * MiB, partCount: 2 });
    expect(planParts(40 * MiB)).toEqual({ partSizeBytes: 16 * MiB, partCount: 3 });
    expect(planParts(1024 * MiB)).toEqual({ partSizeBytes: 16 * MiB, partCount: 64 });
  });

  it('keeps exactly 10 000 parts at 16 MiB', () => {
    expect(planParts(10_000 * 16 * MiB)).toEqual({ partSizeBytes: 16 * MiB, partCount: 10_000 });
  });

  it('raises the part size to the next whole MiB beyond 10 000 parts', () => {
    const plan = planParts(10_000 * 16 * MiB + 1);
    expect(plan.partSizeBytes).toBe(17 * MiB);
    expect(plan.partCount).toBeLessThanOrEqual(maxPartCount);
    expect(plan.partSizeBytes % MiB).toBe(0);
  });

  it('never exceeds the part limit for very large files', () => {
    for (const total of [200 * 1024 * MiB, 1024 * 1024 * MiB + 12345]) {
      const plan = planParts(total);
      expect(plan.partCount).toBeLessThanOrEqual(maxPartCount);
      expect(plan.partSizeBytes * plan.partCount).toBeGreaterThanOrEqual(total);
    }
  });

  it('rejects empty and invalid sizes', () => {
    expect(() => planParts(0)).toThrow(RangeError);
    expect(() => planParts(-1)).toThrow(RangeError);
    expect(() => planParts(1.5)).toThrow(RangeError);
  });
});

describe('partRange', () => {
  it('returns 1-based part ranges with a short final part', () => {
    const total = 40 * MiB;
    const size = 16 * MiB;
    expect(partRange(1, total, size)).toEqual({ partNumber: 1, start: 0, end: size });
    expect(partRange(3, total, size)).toEqual({ partNumber: 3, start: 2 * size, end: total });
  });

  it('refuses parts outside the file', () => {
    expect(() => partRange(0, 10, 5)).toThrow(RangeError);
    expect(() => partRange(3, 10, 5)).toThrow(RangeError);
  });
});
