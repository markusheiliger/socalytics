import { describe, expect, it } from 'vitest';
import { initialPollDelay, nextPollDelay } from '../../../../src/features/triage/pollDelay';

describe('initialPollDelay', () => {
  it('defaults to 2 seconds', () => {
    expect(initialPollDelay()).toBe(2000);
  });

  it('uses the platform interval', () => {
    expect(initialPollDelay(5)).toBe(5000);
  });

  it.each([0, -1, Number.NaN, Number.POSITIVE_INFINITY])('falls back to the default for %s', (value) => {
    expect(initialPollDelay(value)).toBe(2000);
  });

  it('never exceeds the 15 second cap', () => {
    expect(initialPollDelay(60)).toBe(15000);
  });
});

describe('nextPollDelay', () => {
  it('multiplies by 1.5 while unchanged', () => {
    expect(nextPollDelay(2000, false)).toBe(3000);
    expect(nextPollDelay(3000, false)).toBe(4500);
  });

  it('caps at 15 seconds', () => {
    expect(nextPollDelay(12000, false)).toBe(15000);
    expect(nextPollDelay(15000, false)).toBe(15000);
  });

  it('resets to the default interval on change', () => {
    expect(nextPollDelay(10000, true)).toBe(2000);
  });

  it('resets to the platform interval on change', () => {
    expect(nextPollDelay(10000, true, 5000)).toBe(5000);
  });

  it('reaches the cap in a bounded number of unchanged answers', () => {
    let delay = initialPollDelay();
    for (let i = 0; i < 6; i += 1) {
      delay = nextPollDelay(delay, false);
    }
    expect(delay).toBe(15000);
  });
});
