import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import {
  createEvidenceMediaResponseFromDiscriminatorValue,
  type EvidenceMediaDto,
  type EvidenceMediaResponse,
  type EvidencePartDto,
  type ResolveEvidenceMediaRequest,
} from '../../generated/platform-api/models';
import { PlatformFailure } from '../failure';
import type { EvidenceMediaPiece, EvidenceOutcome, EvidencePart, EvidenceRequest, EvidenceResolution } from '../models/analysis';
import { sendFor, type PlatformApi } from './kiota';

export interface AnalysisEvidenceAdapter {
  resolve(runId: string, request: EvidenceRequest): Promise<EvidenceOutcome>;
}

const incompatible = () => new PlatformFailure('incompatible');

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function toMedia(dto: EvidenceMediaDto | null | undefined): EvidenceMediaPiece {
  if (
    !dto?.url || !dto.expiresAt || !dto.mediaType || !dto.mediaDigest || !dto.recordingVersionId ||
    !isFiniteNumber(dto.mediaSizeBytes) || !isFiniteNumber(dto.startOffsetMs) || !isFiniteNumber(dto.endOffsetMs)
  ) {
    throw incompatible();
  }
  return {
    url: dto.url,
    expiresAt: dto.expiresAt,
    mediaType: dto.mediaType,
    mediaDigest: dto.mediaDigest,
    mediaSizeBytes: dto.mediaSizeBytes,
    startOffsetMs: dto.startOffsetMs,
    endOffsetMs: dto.endOffsetMs,
    recordingVersionId: dto.recordingVersionId.toString(),
  };
}

function toPart(dto: EvidencePartDto): EvidencePart {
  if (!isFiniteNumber(dto.startMs) || !isFiniteNumber(dto.endMs) || !dto.state) throw incompatible();
  const part: EvidencePart = { startMs: dto.startMs, endMs: dto.endMs, state: dto.state };
  if (dto.reason) part.reason = dto.reason;
  if (isFiniteNumber(dto.retryAfterSeconds)) part.retryAfterSeconds = dto.retryAfterSeconds;
  if (dto.state === 'available') part.media = toMedia(dto.media);
  return part;
}

function toResolution(dto: EvidenceMediaResponse): EvidenceResolution {
  const { run, requested, fact } = dto;
  if (
    !run?.runId || !run.matchId || !run.teamId || !requested || !isFiniteNumber(requested.fromMs) || !isFiniteNumber(requested.toMs) ||
    typeof dto.shortened !== 'boolean' || !Array.isArray(dto.parts)
  ) {
    throw incompatible();
  }
  if (fact && (!isFiniteNumber(fact.startMs) || !isFiniteNumber(fact.endMs))) throw incompatible();
  return {
    run: { runId: run.runId.toString(), matchId: run.matchId.toString(), teamId: run.teamId.toString() },
    requested: { fromMs: requested.fromMs, toMs: requested.toMs },
    fact: fact ? { startMs: fact.startMs as number, endMs: fact.endMs as number } : undefined,
    shortened: dto.shortened,
    parts: dto.parts.map(toPart),
  };
}

function toBody(request: EvidenceRequest): ResolveEvidenceMediaRequest {
  return {
    fact: request.fact ? { ...request.fact, acceptedResultId: request.fact.acceptedResultId as Guid } : undefined,
    range: request.range,
    retryFailed: request.retryFailed,
  };
}

export function createAnalysisEvidenceAdapter(api: PlatformApi): AnalysisEvidenceAdapter {
  const factory = createEvidenceMediaResponseFromDiscriminatorValue as ParsableFactory<EvidenceMediaResponse>;
  return {
    resolve: async (runId, request) => {
      const builder = api.client.api.v1.analysis.runs.byRunId(runId as Guid).evidenceMedia;
      let result: EvidenceMediaResponse;
      try {
        result = await sendFor<EvidenceMediaResponse>(api, builder.toPostRequestInformation(toBody(request)), factory);
      } catch (error) {
        if (!(error instanceof PlatformFailure)) throw error;
        if (error.status === 404) return { kind: 'notFound' };
        if (error.status === 429) return { kind: 'rateLimited', retryAfterSeconds: error.retryAfterSeconds };
        if (error.status === 503) return { kind: 'storageUnavailable' };
        // Fetch failures carry no status; only they mean the connection is missing.
        if (error.kind === 'unreachable' && error.status === undefined) return { kind: 'offline' };
        if (error.kind === 'sessionEnded' || error.kind === 'sessionProblem' || error.kind === 'passwordChangeRequired' || error.kind === 'notPermitted') throw error;
        return { kind: 'error' };
      }
      return { kind: 'resolution', resolution: toResolution(result) };
    },
  };
}
