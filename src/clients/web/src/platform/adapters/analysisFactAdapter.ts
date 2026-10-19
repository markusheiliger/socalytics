import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { isUntypedNode } from '@microsoft/kiota-abstractions';
import {
  createFactPageDtoFromDiscriminatorValue,
  createFactStreamListDtoFromDiscriminatorValue,
  type FactItemDto,
  type FactPageDto,
  type FactProvenanceDto,
  type FactStreamEntryDto,
  type FactStreamListDto,
} from '../../generated/platform-api/models';
import { PlatformFailure } from '../failure';
import type {
  CoverageWindow,
  Fact,
  FactPage,
  FactStreamSummary,
  FactsOutcome,
  JsonValue,
  Provenance,
  StreamsOutcome,
} from '../models/analysis';
import { sendFor, type PlatformApi } from './kiota';

export interface FactRequest {
  runId: string;
  nodeKey: string;
  capabilityId: string;
  capabilityVersion: string;
  schemaVersion: string;
  fromMs?: number;
  toMs?: number;
  continuationToken?: string;
}

export interface AnalysisFactAdapter {
  listStreams(runId: string): Promise<StreamsOutcome>;
  listFacts(request: FactRequest): Promise<FactsOutcome>;
}

const incompatible = () => new PlatformFailure('incompatible');

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

/** Normalizes a Kiota untyped node, a model with `additionalData`, or a plain value into JSON (research.md § R4). */
export function toJsonValue(value: unknown): JsonValue {
  if (value === null) return null;
  if (typeof value === 'string' || typeof value === 'boolean') return value;
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) throw incompatible();
    return value;
  }
  if (Array.isArray(value)) return value.map(toJsonValue);
  if (typeof value === 'object') {
    if (isUntypedNode(value)) return toJsonValue(value.getValue());
    const record = value as Record<string, unknown>;
    const additional = record.additionalData;
    if (typeof additional === 'object' && additional !== null && !Array.isArray(additional) && Object.keys(record).every((key) => key === 'additionalData' || record[key] == null)) {
      return toJsonValue(additional);
    }
    const result: { [key: string]: JsonValue } = {};
    for (const [key, entry] of Object.entries(record)) result[key] = toJsonValue(entry);
    return result;
  }
  throw incompatible();
}

function toProvenance(dto: FactProvenanceDto | null | undefined): Provenance {
  if (!dto?.workflow?.id || !dto.workflow.version || !dto.analystProfile?.id || !dto.analystProfile.digest || !dto.imageDigest) {
    throw incompatible();
  }
  return {
    workflow: { id: dto.workflow.id, version: dto.workflow.version },
    analystProfile: { id: dto.analystProfile.id, digest: dto.analystProfile.digest },
    imageDigest: dto.imageDigest,
    modelDigest: dto.modelDigest ?? undefined,
  };
}

function toStreamSummary(dto: FactStreamEntryDto): FactStreamSummary {
  const c = dto.coverage;
  if (
    !dto.nodeKey || !dto.capability?.id || !dto.capability.version || !dto.schemaVersion || !dto.outputType || !dto.executionScope ||
    typeof dto.required !== 'boolean' || !isFiniteNumber(dto.factCount) || !c ||
    !isFiniteNumber(c.accepted) || !isFiniteNumber(c.missing) || !isFiniteNumber(c.failed) || !isFiniteNumber(c.blocked) || !isFiniteNumber(c.skipped)
  ) {
    throw incompatible();
  }
  return {
    nodeKey: dto.nodeKey,
    capabilityId: dto.capability.id,
    capabilityVersion: dto.capability.version,
    schemaVersion: dto.schemaVersion,
    outputType: dto.outputType,
    executionScope: dto.executionScope,
    required: dto.required,
    factCount: dto.factCount,
    provenance: toProvenance(dto.provenance),
    coverage: { accepted: c.accepted, missing: c.missing, failed: c.failed, blocked: c.blocked, skipped: c.skipped },
  };
}

function toFact(dto: FactItemDto): Fact {
  if (!dto.acceptedResultId || !isFiniteNumber(dto.ordinal) || !dto.kind || !isFiniteNumber(dto.startMs) || !isFiniteNumber(dto.endMs) || !isFiniteNumber(dto.confidence) || dto.payload === undefined) {
    throw incompatible();
  }
  return {
    acceptedResultId: dto.acceptedResultId.toString(),
    ordinal: dto.ordinal,
    kind: dto.kind,
    startMs: dto.startMs,
    endMs: dto.endMs,
    confidence: dto.confidence,
    payload: toJsonValue(dto.payload),
  };
}

function toCoverage(window: { startMs?: number | null; endMs?: number | null; state?: string | null }): CoverageWindow {
  if (!isFiniteNumber(window.startMs) || !isFiniteNumber(window.endMs) || !window.state) throw incompatible();
  return { startMs: window.startMs, endMs: window.endMs, state: window.state };
}

function toFactPage(dto: FactPageDto): FactPage {
  const { run, stream, range } = dto;
  if (
    !run?.runId || !run.matchId || !run.state || !stream?.nodeKey || !stream.capability?.id || !stream.schemaVersion ||
    !stream.outputType || !stream.executionScope || !range || !isFiniteNumber(range.fromMs) || !isFiniteNumber(range.toMs)
  ) {
    throw incompatible();
  }
  return {
    run: { runId: run.runId.toString(), matchId: run.matchId.toString(), state: run.state },
    stream: {
      nodeKey: stream.nodeKey,
      capability: stream.capability.id,
      schemaVersion: stream.schemaVersion,
      outputType: stream.outputType,
      executionScope: stream.executionScope,
      provenance: toProvenance(stream.provenance),
    },
    range: { fromMs: range.fromMs, toMs: range.toMs },
    items: (dto.items ?? []).map(toFact),
    coverage: (dto.coverage ?? []).map(toCoverage),
    continuationToken: dto.continuationToken ?? undefined,
  };
}

function isNotFound(error: unknown): error is PlatformFailure {
  return error instanceof PlatformFailure && error.status === 404;
}

export function createAnalysisFactAdapter(api: PlatformApi): AnalysisFactAdapter {
  const streamsFactory = createFactStreamListDtoFromDiscriminatorValue as ParsableFactory<FactStreamListDto>;
  const factsFactory = createFactPageDtoFromDiscriminatorValue as ParsableFactory<FactPageDto>;
  const run = (runId: string) => api.client.api.v1.analysis.runs.byRunId(runId as Guid);
  return {
    listStreams: async (runId) => {
      try {
        const result = await sendFor<FactStreamListDto>(api, run(runId).factStreams.toGetRequestInformation(), streamsFactory);
        return { kind: 'found', streams: (result.items ?? []).map(toStreamSummary) };
      } catch (error) {
        if (isNotFound(error)) return { kind: 'runNotFound' };
        throw error;
      }
    },
    listFacts: async (request) => {
      const queryParameters = {
        nodeKey: request.nodeKey,
        capabilityId: request.capabilityId,
        capabilityVersion: request.capabilityVersion,
        schemaVersion: request.schemaVersion,
        fromMs: request.fromMs === undefined ? undefined : String(request.fromMs),
        toMs: request.toMs === undefined ? undefined : String(request.toMs),
        continuationToken: request.continuationToken,
      };
      try {
        const result = await sendFor<FactPageDto>(api, run(request.runId).facts.toGetRequestInformation({ queryParameters }), factsFactory);
        return { kind: 'found', page: toFactPage(result) };
      } catch (error) {
        if (error instanceof PlatformFailure) {
          if (error.status === 404) {
            return { kind: error.code === 'analysis-capability-not-in-run' ? 'capabilityNotInRun' : 'runNotFound' };
          }
          if (error.status === 400) {
            if (error.code === 'analysis-capability-ambiguous') {
              throw new PlatformFailure('programmingError', {
                status: error.status,
                code: error.code,
                correlationId: error.correlationId,
                fieldErrors: error.fieldErrors,
              });
            }
            if (error.fieldErrors.some((e) => e.field === 'continuationToken')) return { kind: 'continuationRejected' };
          }
        }
        throw error;
      }
    },
  };
}
