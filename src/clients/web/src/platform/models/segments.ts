export type SegmentState = 'absent' | 'materializing' | 'available' | 'failed';

export interface MaterializedSegment {
  recordingVersionId: string;
  timelineMappingDigest: string;
  segmentationPolicyDigest: string;
  /** The interval within the recording version. */
  segmentNumber: number;
  /** Known states are `SegmentState` values; unknown platform values stay as reported text. */
  state: SegmentState | string;
  /** Undefined when not reported. */
  mediaSizeBytes: number | undefined;
  /** Undefined when not reported; rendered as text. */
  failureReason: string | undefined;
  /** Undefined when not reported. */
  availableAt: Date | undefined;
  /** A waiting, ready, or claimed job or an ongoing materialization keeps the segment. */
  inUse: boolean;
}

/** The whole list in the platform's order; not paged. */
export interface SegmentList {
  matchId: string;
  segments: MaterializedSegment[];
}

export interface PurgeOutcome {
  matchId: string;
  purged: number;
  inUse: number;
}
