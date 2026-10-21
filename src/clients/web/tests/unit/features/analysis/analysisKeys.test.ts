import { analysisFactsKey, analysisRunKey, analysisStreamsKey, isRunInProgress, runRefreshInterval } from '../../../../src/features/analysis/analysisKeys';
import { RUN_REFRESH_MS } from '../../../../src/features/lineage/AnalysisRunStates';

describe('analysis query keys', () => {
  it('builds the run and streams keys', () => {
    expect(analysisRunKey('r1')).toEqual(['analysis', 'run', 'r1']);
    expect(analysisStreamsKey('r1')).toEqual(['analysis', 'streams', 'r1']);
  });

  it('builds the facts key with range and continuation token', () => {
    expect(analysisFactsKey('r1', 'n1', 1000, 2000, 'tok')).toEqual(['analysis', 'facts', 'r1', 'n1', 1000, 2000, 'tok']);
    expect(analysisFactsKey('r1', 'n1', undefined, undefined, undefined)).toEqual(['analysis', 'facts', 'r1', 'n1', undefined, undefined, undefined]);
  });

  it('differs per page token and range', () => {
    expect(analysisFactsKey('r1', 'n1', 0, 10, 'a')).not.toEqual(analysisFactsKey('r1', 'n1', 0, 10, 'b'));
    expect(analysisFactsKey('r1', 'n1', 0, 10, 'a')).not.toEqual(analysisFactsKey('r1', 'n1', 0, 11, 'a'));
  });
});

describe('runRefreshInterval', () => {
  it('refreshes at the existing interval while in progress', () => {
    expect(RUN_REFRESH_MS).toBe(10_000);
    expect(runRefreshInterval({ state: 'Requested' })).toBe(RUN_REFRESH_MS);
    expect(runRefreshInterval({ state: 'Running' })).toBe(RUN_REFRESH_MS);
    expect(runRefreshInterval({ state: 'running' })).toBe(RUN_REFRESH_MS);
  });

  it('does not refresh finished, unknown, or missing runs', () => {
    for (const state of ['PartiallyCompleted', 'Completed', 'Failed', 'Cancelled', 'Paused', '']) {
      expect(runRefreshInterval({ state })).toBe(false);
      expect(isRunInProgress({ state })).toBe(false);
    }
    expect(runRefreshInterval(undefined)).toBe(false);
  });
});
