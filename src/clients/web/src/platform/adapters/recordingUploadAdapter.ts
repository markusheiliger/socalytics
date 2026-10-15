import {
  createCompletedUploadDtoFromDiscriminatorValue,
  createPartGrantBatchDtoFromDiscriminatorValue,
  createUploadSessionDtoFromDiscriminatorValue,
  createUploadSessionWithGrantsDtoFromDiscriminatorValue,
  type PartGrantBatchDto,
  type PartGrantDto,
  type UploadSessionDto,
  type UploadSessionWithGrantsDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type {
  CompletedUpload,
  PartGrant,
  RecordingDescriptor,
  StartedUpload,
  TimelineSpan,
  UploadDeclaration,
  UploadSession,
  UploadSessionState,
} from '../models/recordings';
import { sendFor, withIdempotencyKey, withJson, type PlatformApi } from './kiota';
import { toCompletedUpload, toDescriptor, toSpanBody } from './recordingMapping';

export interface RecordingUploadAdapter {
  start(matchId: string, descriptor: RecordingDescriptor, declaration: UploadDeclaration, key: string): Promise<StartedUpload>;
  getSession(matchId: string, id: string): Promise<UploadSession>;
  /** Grants are secrets: keep them in memory only. */
  issueGrants(matchId: string, id: string, partNumbers: number[]): Promise<PartGrant[]>;
  complete(matchId: string, id: string, mapping: TimelineSpan[], key: string): Promise<CompletedUpload>;
}

const STATES: readonly UploadSessionState[] = ['pending', 'completed', 'expired'];

export function toUploadSession(dto: UploadSessionDto | undefined | null): UploadSession {
  const state = STATES.find((s) => s === (dto?.state ?? '').toLowerCase());
  if (
    !dto ||
    !dto.id ||
    !dto.matchId ||
    !dto.teamId ||
    !state ||
    typeof dto.totalSizeBytes !== 'number' ||
    typeof dto.partSizeBytes !== 'number' ||
    typeof dto.partCount !== 'number' ||
    !dto.contentDigest ||
    !dto.createdAt ||
    !dto.expiresAt ||
    typeof dto.version !== 'number'
  ) {
    throw new PlatformFailure('incompatible');
  }
  return {
    id: dto.id,
    matchId: dto.matchId,
    teamId: dto.teamId,
    state,
    recording: toDescriptor(dto.recording),
    totalSizeBytes: dto.totalSizeBytes,
    partSizeBytes: dto.partSizeBytes,
    partCount: dto.partCount,
    contentDigest: dto.contentDigest,
    createdAt: dto.createdAt,
    expiresAt: dto.expiresAt,
    completedAt: dto.completedAt ?? undefined,
    expiredAt: dto.expiredAt ?? undefined,
    version: dto.version,
  };
}

function toGrant(dto: PartGrantDto, expiresAt: Date): PartGrant {
  if (typeof dto.partNumber !== 'number' || !dto.method || !dto.url) throw new PlatformFailure('incompatible');
  const headers: Record<string, string> = {};
  for (const [name, value] of Object.entries(dto.requiredHeaders?.additionalData ?? {})) {
    if (typeof value === 'string') headers[name] = value;
  }
  return { partNumber: dto.partNumber, method: dto.method, url: dto.url, requiredHeaders: headers, expiresAt };
}

export function toGrants(batch: PartGrantBatchDto | undefined | null): PartGrant[] {
  if (!batch) return [];
  const expiresAt = batch.expiresAt;
  if (!expiresAt) throw new PlatformFailure('incompatible');
  return (batch.parts ?? []).map((p) => toGrant(p, expiresAt));
}

export function createRecordingUploadAdapter(api: PlatformApi): RecordingUploadAdapter {
  const sessions = (matchId: string) => api.client.api.v1.matches.byMatchId(matchId as Guid).uploadSessions;
  const session = (matchId: string, id: string) => sessions(matchId).byUploadSessionId(id as Guid);
  const operations: RecordingUploadAdapter = {
    start: async (matchId, descriptor, declaration, key) => {
      const body = {
        recording: {
          displayName: descriptor.displayName,
          contentType: descriptor.contentType,
          ...(descriptor.description ? { description: descriptor.description } : {}),
        },
        totalSizeBytes: declaration.totalSizeBytes,
        partSizeBytes: declaration.partSizeBytes,
        partDigests: declaration.partDigests,
      };
      const info = withIdempotencyKey(withJson(sessions(matchId).toPostRequestInformation(), body), key);
      const result = await sendFor<UploadSessionWithGrantsDto>(
        api,
        info,
        createUploadSessionWithGrantsDtoFromDiscriminatorValue as ParsableFactory<UploadSessionWithGrantsDto>,
      );
      return { session: toUploadSession(result.session), grants: toGrants(result.grants) };
    },
    getSession: async (matchId, id) =>
      toUploadSession(await sendFor(api, session(matchId, id).toGetRequestInformation(), createUploadSessionDtoFromDiscriminatorValue)),
    issueGrants: async (matchId, id, partNumbers) => {
      const info = withJson(session(matchId, id).grants.toPostRequestInformation(), { partNumbers });
      return toGrants(await sendFor(api, info, createPartGrantBatchDtoFromDiscriminatorValue));
    },
    complete: async (matchId, id, mapping, key) => {
      const info = withIdempotencyKey(
        withJson(session(matchId, id).completion.toPostRequestInformation(), { timelineMapping: { spans: mapping.map(toSpanBody) } }),
        key,
      );
      return toCompletedUpload(await sendFor(api, info, createCompletedUploadDtoFromDiscriminatorValue));
    },
  };
  return {
    start: (...args) => storageAware(() => operations.start(...args)),
    getSession: (...args) => storageAware(() => operations.getSession(...args)),
    issueGrants: (...args) => storageAware(() => operations.issueGrants(...args)),
    complete: (...args) => storageAware(() => operations.complete(...args)),
  };
}

/** A 503 from an upload operation means the object store is unavailable, not that the platform is unreachable. */
async function storageAware<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    if (error instanceof PlatformFailure && error.status === 503) {
      throw new PlatformFailure('storageUnavailable', { status: 503, code: error.code, correlationId: error.correlationId });
    }
    throw error;
  }
}
