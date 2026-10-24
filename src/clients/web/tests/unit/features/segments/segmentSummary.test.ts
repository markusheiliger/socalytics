import { segmentsKey } from '../../../../src/features/segments/segmentKeys';
import { summarizeSegments } from '../../../../src/features/segments/segmentSummary';
import type { MaterializedSegment } from '../../../../src/platform/models/segments';

function segment(state: string, inUse = false, segmentNumber = 0): MaterializedSegment {
  return {
    recordingVersionId: 'rv',
    timelineMappingDigest: 'tm',
    segmentationPolicyDigest: 'sp',
    segmentNumber,
    state,
    mediaSizeBytes: undefined,
    failureReason: undefined,
    availableAt: undefined,
    inUse,
  };
}

describe('segment query key', () => {
  it('is scoped to the match', () => {
    expect(segmentsKey('m1')).toEqual(['matches', 'segments', 'm1']);
  });
});

describe('summarizeSegments', () => {
  it('summarizes an empty list', () => {
    expect(summarizeSegments([])).toEqual({
      total: 0, absent: 0, materializing: 0, available: 0, failed: 0, other: 0, inUse: 0, purgeable: 0,
    });
  });

  it('counts states, in-use, and purgeable segments', () => {
    const summary = summarizeSegments([
      segment('available'),
      segment('available', true),
      segment('failed'),
      segment('failed', true),
      segment('absent'),
      segment('materializing', true),
    ]);
    expect(summary).toEqual({
      total: 6, absent: 1, materializing: 1, available: 2, failed: 2, other: 0, inUse: 3, purgeable: 2,
    });
  });

  it('never counts materializing segments as purgeable', () => {
    const summary = summarizeSegments([segment('materializing'), segment('materializing', true)]);
    expect(summary.materializing).toBe(2);
    expect(summary.purgeable).toBe(0);
  });

  it('counts an unknown state as other and never as purgeable', () => {
    const summary = summarizeSegments([segment('quarantined'), segment('quarantined', true)]);
    expect(summary.other).toBe(2);
    expect(summary.total).toBe(2);
    expect(summary.inUse).toBe(1);
    expect(summary.purgeable).toBe(0);
  });
});
