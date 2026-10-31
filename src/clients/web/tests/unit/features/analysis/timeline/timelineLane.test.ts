import { deriveLaneState, isLanePartial, laneAllowance, unknownLaneStates, type LaneLoadStatus } from '../../../../../src/features/analysis/timeline/timelineLane';
import type { CoverageWindow } from '../../../../../src/platform/models/analysis';

const accepted: CoverageWindow = { startMs: 0, endMs: 1000, state: 'Accepted' };
const notAnalysed: CoverageWindow = { startMs: 1000, endMs: 2000, state: 'NotAnalysed' };

describe('laneAllowance', () => {
  it('allows 5 000 facts for 1 and 4 lanes and 2 000 for 10 lanes', () => {
    expect(laneAllowance(1)).toBe(5000);
    expect(laneAllowance(4)).toBe(5000);
    expect(laneAllowance(10)).toBe(2000);
  });

  it('floors the share of the total', () => {
    expect(laneAllowance(7)).toBe(2857);
  });
});

describe('isLanePartial', () => {
  it('is partial only when fewer facts were loaded than reported', () => {
    expect(isLanePartial(4999, 5001)).toBe(true);
    expect(isLanePartial(10, 10)).toBe(false);
    expect(isLanePartial(0, 0)).toBe(false);
  });
});

describe('deriveLaneState', () => {
  const base = { status: 'loaded' as LaneLoadStatus, runState: 'Completed', loadedFactCount: 0, windows: [accepted] };

  it.each<LaneLoadStatus>(['loading', 'capabilityNotInRun', 'unavailable', 'error', 'connectionRequired'])('passes the load status %s through', (status) => {
    expect(deriveLaneState({ ...base, status, loadedFactCount: 3 })).toBe(status);
  });

  it('is ready for a lane with facts, whatever the run state', () => {
    for (const runState of ['Requested', 'Running', 'Completed', 'Failed', 'Cancelled', 'Odd']) {
      expect(deriveLaneState({ ...base, runState, loadedFactCount: 2, windows: [] })).toBe('ready');
    }
  });

  it('is inProgress for a known Requested or Running run without facts, winning over complete and no coverage', () => {
    for (const runState of ['Requested', 'Running', 'running']) {
      expect(deriveLaneState({ ...base, runState })).toBe('inProgress');
      expect(deriveLaneState({ ...base, runState, windows: [] })).toBe('inProgress');
    }
  });

  it.each(['Failed', 'Cancelled', 'PartiallyCompleted', 'Completed', 'SomethingNew', undefined])('never shows the final run state %s as inProgress', (runState) => {
    expect(deriveLaneState({ ...base, runState })).not.toBe('inProgress');
    expect(deriveLaneState({ ...base, runState, windows: [] })).not.toBe('inProgress');
  });

  it('is completeWithoutFacts when the run is final and every window is accepted', () => {
    expect(deriveLaneState(base)).toBe('completeWithoutFacts');
    expect(deriveLaneState({ ...base, runState: 'Failed', windows: [accepted, { ...accepted, state: 'accepted' }] })).toBe('completeWithoutFacts');
  });

  it('is noCoverage without windows and facts in a final run', () => {
    expect(deriveLaneState({ ...base, windows: [] })).toBe('noCoverage');
    expect(deriveLaneState({ ...base, runState: 'Cancelled', windows: [] })).toBe('noCoverage');
  });

  it('is not completeWithoutFacts when a window is not accepted or has an unknown state', () => {
    expect(deriveLaneState({ ...base, windows: [accepted, notAnalysed] })).toBe('ready');
    expect(deriveLaneState({ ...base, windows: [accepted, { ...accepted, state: 'Mystery' }] })).toBe('ready');
  });
});

describe('unknownLaneStates', () => {
  it('returns nothing for known states in any case', () => {
    expect(unknownLaneStates('Running', 'Active')).toEqual({});
    expect(unknownLaneStates('PartiallyCompleted', 'blocked')).toEqual({});
    expect(unknownLaneStates(undefined, undefined)).toEqual({});
  });

  it('keeps unknown run and node states raw', () => {
    expect(unknownLaneStates('Paused', 'Hovering')).toEqual({ runState: 'Paused', nodeState: 'Hovering' });
    expect(unknownLaneStates('Completed', 'Hovering')).toEqual({ nodeState: 'Hovering' });
  });
});
