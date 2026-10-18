export interface RecordingDescriptor {
  displayName: string;
  description?: string;
  contentType: string;
}

export interface UploadDeclaration {
  totalSizeBytes: number;
  partSizeBytes: number;
  /** `sha-256:<64 lowercase hex>` per part. */
  partDigests: string[];
}

export type UploadSessionState = 'pending' | 'completed' | 'expired';

export interface UploadSession {
  id: string;
  matchId: string;
  teamId: string;
  state: UploadSessionState;
  recording: RecordingDescriptor;
  totalSizeBytes: number;
  partSizeBytes: number;
  partCount: number;
  contentDigest: string;
  createdAt: Date;
  expiresAt: Date;
  completedAt?: Date;
  expiredAt?: Date;
  version: number;
}

/** A pre-signed part grant; the URL and headers are secrets and stay in memory only. */
export interface PartGrant {
  partNumber: number;
  method: string;
  url: string;
  requiredHeaders: Record<string, string>;
  expiresAt: Date;
}

export interface StartedUpload {
  session: UploadSession;
  grants: PartGrant[];
}

export interface TimelineSpan {
  mediaStartSeconds: number;
  mediaEndSeconds: number;
  matchStartSeconds: number;
}

export interface TimelineMapping {
  id: string;
  recordingVersionId: string;
  matchId: string;
  spans: TimelineSpan[];
  digest: string;
  createdAt: Date;
}

export interface RecordingVersion {
  id: string;
  matchId: string;
  teamId: string;
  uploadSessionId: string;
  recording: RecordingDescriptor;
  totalSizeBytes: number;
  partSizeBytes: number;
  partCount: number;
  contentDigest: string;
  createdAt: Date;
}

export interface CompletedUpload {
  recordingVersion: RecordingVersion;
  timelineMapping: TimelineMapping;
}

export interface RecordingSetMember {
  position: number;
  recordingVersionId: string;
  recordingContentDigest: string;
  timelineMappingId: string;
  timelineMappingDigest: string;
}

export interface RecordingSetVersion {
  id: string;
  matchId: string;
  teamId: string;
  finalizedAt: Date;
  members: RecordingSetMember[];
}

export interface FinalizeMember {
  recordingVersionId: string;
  timelineMappingId: string;
}

export interface Lineage {
  matchId: string;
  teamId: string;
  recordingVersions: RecordingVersion[];
  timelineMappings: TimelineMapping[];
  recordingSetVersions: RecordingSetVersion[];
}

export interface AnalysisRunSummary {
  runId: string;
  recordingSetVersionId: string;
  workflowId: string;
  workflowVersion: string;
  state: string;
  failureReason?: string;
  createdAt: Date;
  runningAt?: Date;
  terminatedAt?: Date;
  version: number;
  cancelledBy?: { memberId: string; accountName: string };
  cancellationReason?: string;
}
