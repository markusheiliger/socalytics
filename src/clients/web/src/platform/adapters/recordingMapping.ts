import type {
  CompletedUploadDto,
  RecordingDescriptorDto,
  RecordingSetVersionDto,
  RecordingVersionDto,
  TimelineMappingDto,
} from '../../generated/platform-api/models';
import { PlatformFailure } from '../failure';
import type {
  CompletedUpload,
  RecordingDescriptor,
  RecordingSetVersion,
  RecordingVersion,
  TimelineMapping,
  TimelineSpan,
} from '../models/recordings';

export function toDescriptor(dto: RecordingDescriptorDto | undefined | null): RecordingDescriptor {
  if (!dto || typeof dto.displayName !== 'string' || typeof dto.contentType !== 'string') throw new PlatformFailure('incompatible');
  return { displayName: dto.displayName, description: dto.description ?? undefined, contentType: dto.contentType };
}

export function toSpanBody(span: TimelineSpan) {
  return {
    mediaStartSeconds: span.mediaStartSeconds,
    mediaEndSeconds: span.mediaEndSeconds,
    matchStartSeconds: span.matchStartSeconds,
  };
}

export function toRecordingVersion(dto: RecordingVersionDto | undefined | null): RecordingVersion {
  if (
    !dto ||
    !dto.id ||
    !dto.matchId ||
    !dto.teamId ||
    !dto.uploadSessionId ||
    typeof dto.totalSizeBytes !== 'number' ||
    typeof dto.partSizeBytes !== 'number' ||
    typeof dto.partCount !== 'number' ||
    !dto.contentDigest ||
    !dto.createdAt
  ) {
    throw new PlatformFailure('incompatible');
  }
  return {
    id: dto.id,
    matchId: dto.matchId,
    teamId: dto.teamId,
    uploadSessionId: dto.uploadSessionId,
    recording: toDescriptor(dto.recording),
    totalSizeBytes: dto.totalSizeBytes,
    partSizeBytes: dto.partSizeBytes,
    partCount: dto.partCount,
    contentDigest: dto.contentDigest,
    createdAt: dto.createdAt,
  };
}

export function toTimelineMapping(dto: TimelineMappingDto | undefined | null): TimelineMapping {
  if (!dto || !dto.id || !dto.recordingVersionId || !dto.matchId || !dto.digest || !dto.createdAt || !Array.isArray(dto.spans)) {
    throw new PlatformFailure('incompatible');
  }
  return {
    id: dto.id,
    recordingVersionId: dto.recordingVersionId,
    matchId: dto.matchId,
    spans: dto.spans.map((s) => {
      if (typeof s.mediaStartSeconds !== 'number' || typeof s.mediaEndSeconds !== 'number' || typeof s.matchStartSeconds !== 'number') {
        throw new PlatformFailure('incompatible');
      }
      return { mediaStartSeconds: s.mediaStartSeconds, mediaEndSeconds: s.mediaEndSeconds, matchStartSeconds: s.matchStartSeconds };
    }),
    digest: dto.digest,
    createdAt: dto.createdAt,
  };
}

export function toRecordingSetVersion(dto: RecordingSetVersionDto | undefined | null): RecordingSetVersion {
  if (!dto || !dto.id || !dto.matchId || !dto.teamId || !dto.finalizedAt || !Array.isArray(dto.members)) {
    throw new PlatformFailure('incompatible');
  }
  return {
    id: dto.id,
    matchId: dto.matchId,
    teamId: dto.teamId,
    finalizedAt: dto.finalizedAt,
    members: dto.members.map((m) => {
      if (
        typeof m.position !== 'number' ||
        !m.recordingVersionId ||
        !m.recordingContentDigest ||
        !m.timelineMappingId ||
        !m.timelineMappingDigest
      ) {
        throw new PlatformFailure('incompatible');
      }
      return {
        position: m.position,
        recordingVersionId: m.recordingVersionId,
        recordingContentDigest: m.recordingContentDigest,
        timelineMappingId: m.timelineMappingId,
        timelineMappingDigest: m.timelineMappingDigest,
      };
    }),
  };
}

export function toCompletedUpload(dto: CompletedUploadDto | undefined): CompletedUpload {
  if (!dto) throw new PlatformFailure('incompatible');
  return { recordingVersion: toRecordingVersion(dto.recordingVersion), timelineMapping: toTimelineMapping(dto.timelineMapping) };
}
