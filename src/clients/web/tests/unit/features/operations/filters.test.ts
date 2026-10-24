import { describe, expect, it } from 'vitest';
import {
  filterKey,
  parseOutboxFilters,
  parseReceiptFilters,
  writeOutboxFilters,
  writeReceiptFilters,
} from '../../../../src/features/operations/operationsFilters';

describe('operations filters', () => {
  it('parses known outbox values', () => {
    const params = new URLSearchParams('state=failed&messageType=analysis.ready-work');
    expect(parseOutboxFilters(params)).toEqual({ state: 'failed', messageType: 'analysis.ready-work' });
  });

  it('ignores unknown values', () => {
    const params = new URLSearchParams('state=bogus&messageType=other.type&outcome=nope');
    expect(parseOutboxFilters(params)).toEqual({});
    expect(parseReceiptFilters(params)).toEqual({});
  });

  it('parses a known receipt outcome', () => {
    expect(parseReceiptFilters(new URLSearchParams('outcome=rejected'))).toEqual({ outcome: 'rejected' });
  });

  it('writes filters back and drops cleared and unknown values', () => {
    const params = new URLSearchParams('state=bogus&messageType=analysis.ready-work&other=1');
    const next = writeOutboxFilters(params, { state: 'pending' });
    expect(next.get('state')).toBe('pending');
    expect(next.has('messageType')).toBe(false);
    expect(next.get('other')).toBe('1');
    expect(params.get('state')).toBe('bogus');
  });

  it('writes and clears the receipt outcome', () => {
    const set = writeReceiptFilters(new URLSearchParams(), { outcome: 'run-created' });
    expect(set.toString()).toBe('outcome=run-created');
    expect(writeReceiptFilters(set, {}).toString()).toBe('');
  });

  it('builds a stable key independent of property order', () => {
    expect(filterKey({ state: 'pending', messageType: 'analysis.ready-work' })).toBe(
      filterKey({ messageType: 'analysis.ready-work', state: 'pending' }),
    );
    expect(filterKey({})).not.toBe(filterKey({ state: 'pending' }));
  });
});
