import { describe, expect, it } from 'vitest';
import { triageKeys } from '../../../../src/features/triage/triageKeys';

describe('triageKeys', () => {
  it('builds the documented query keys', () => {
    expect(triageKeys.agents()).toEqual(['triage', 'agents']);
    expect(triageKeys.conversations('m1')).toEqual(['triage', 'conversations', 'm1']);
    expect(triageKeys.conversation('c1')).toEqual(['triage', 'conversation', 'c1']);
    expect(triageKeys.turns('c1')).toEqual(['triage', 'turns', 'c1']);
    expect(triageKeys.invocation('i1')).toEqual(['triage', 'invocation', 'i1']);
    expect(triageKeys.lineage('i1')).toEqual(['triage', 'lineage', 'i1']);
  });

  it('keeps keys of different entities distinct', () => {
    expect(triageKeys.turns('x')).not.toEqual(triageKeys.conversation('x'));
    expect(triageKeys.invocation('x')).not.toEqual(triageKeys.lineage('x'));
  });

  it('allows invalidating everything with the shared prefix', () => {
    for (const key of [
      triageKeys.agents(),
      triageKeys.conversations('m'),
      triageKeys.conversation('c'),
      triageKeys.turns('c'),
      triageKeys.invocation('i'),
      triageKeys.lineage('i'),
    ]) {
      expect(key[0]).toBe('triage');
    }
  });
});
