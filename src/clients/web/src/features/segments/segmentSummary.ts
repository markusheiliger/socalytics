import type { MaterializedSegment } from '../../platform/models/segments';

export interface SegmentSummary {
  total: number;
  absent: number;
  materializing: number;
  available: number;
  failed: number;
  other: number;
  inUse: number;
  purgeable: number;
}

export function summarizeSegments(segments: readonly MaterializedSegment[]): SegmentSummary {
  const summary: SegmentSummary = {
    total: segments.length,
    absent: 0,
    materializing: 0,
    available: 0,
    failed: 0,
    other: 0,
    inUse: 0,
    purgeable: 0,
  };

  for (const segment of segments) {
    switch (segment.state) {
      case 'absent':
      case 'materializing':
      case 'available':
      case 'failed':
        summary[segment.state] += 1;
        break;
      default:
        summary.other += 1;
    }
    if (segment.inUse) {
      summary.inUse += 1;
    } else if (segment.state === 'available' || segment.state === 'failed') {
      summary.purgeable += 1;
    }
  }

  return summary;
}
