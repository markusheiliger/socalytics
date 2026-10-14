import { GrantScheduler, maxPartsAhead, renewWithinMs, type StoredGrant } from '../../../../src/uploads/grantScheduler';

const now = Date.parse('2026-10-14T10:00:00Z');

function grant(partNumber: number, expiresInMs: number): StoredGrant {
  return {
    partNumber,
    url: `https://storage.test/part/${partNumber}`,
    requiredHeaders: { 'x-amz-checksum-sha256': 'x' },
    expiresAt: new Date(now + expiresInMs).toISOString(),
  };
}

const range = (count: number, from = 1) => Array.from({ length: count }, (_, i) => from + i);

describe('GrantScheduler', () => {
  it('asks for at most 32 parts ahead', () => {
    const scheduler = new GrantScheduler(() => now);
    expect(scheduler.partsNeeded(range(100))).toEqual(range(maxPartsAhead));
  });

  it('does not ask again for usable grants', () => {
    const scheduler = new GrantScheduler(() => now);
    scheduler.store([grant(1, 900_000), grant(2, 900_000)]);
    expect(scheduler.partsNeeded(range(4))).toEqual([3, 4]);
    expect(scheduler.usable(1)?.url).toBe('https://storage.test/part/1');
  });

  it('renews grants that expire within 60 seconds', () => {
    const scheduler = new GrantScheduler(() => now);
    scheduler.store([grant(1, renewWithinMs), grant(2, renewWithinMs + 1), grant(3, -1)]);
    expect(scheduler.usable(1)).toBeUndefined();
    expect(scheduler.usable(2)).toBeDefined();
    expect(scheduler.partsNeeded([1, 2, 3])).toEqual([1, 3]);
  });

  it('renews with time passing', () => {
    let time = now;
    const scheduler = new GrantScheduler(() => time);
    scheduler.store([grant(1, 120_000)]);
    expect(scheduler.partsNeeded([1])).toEqual([]);
    time += 61_000;
    expect(scheduler.partsNeeded([1])).toEqual([1]);
  });

  it('forgets a refused grant and can clear everything', () => {
    const scheduler = new GrantScheduler(() => now);
    scheduler.store([grant(1, 900_000), grant(2, 900_000)]);
    scheduler.invalidate(1);
    expect(scheduler.partsNeeded([1, 2])).toEqual([1]);
    scheduler.clear();
    expect(scheduler.partsNeeded([1, 2])).toEqual([1, 2]);
  });
});
