import { isUntypedNode, type Guid, type ParsableFactory } from '@microsoft/kiota-abstractions';
import {
  createMaterializedSegmentListDtoFromDiscriminatorValue,
  createSegmentPurgeResultDtoFromDiscriminatorValue,
  type MaterializedSegmentDto,
  type MaterializedSegmentListDto,
  type SegmentPurgeResultDto,
} from '../../generated/platform-api/models';
import { PlatformFailure } from '../failure';
import type { MaterializedSegment, PurgeOutcome, SegmentList } from '../models/segments';
import { sendFor, type PlatformApi } from './kiota';

export interface SegmentAdapter {
  list(matchId: string): Promise<SegmentList>;
  purge(matchId: string): Promise<PurgeOutcome>;
}

const incompatible = () => new PlatformFailure('incompatible');

/** Generated models may type counts as `integer | string`; anything that is not a finite integer is absent. */
function toInteger(value: unknown): number | undefined {
  if (isUntypedNode(value)) return toInteger(value.getValue());
  if (typeof value === 'number') return Number.isInteger(value) ? value : undefined;
  if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) {
    const parsed = Number(value.trim());
    return Number.isSafeInteger(parsed) ? parsed : undefined;
  }
  return undefined;
}

function requiredInteger(value: unknown): number {
  const result = toInteger(value);
  if (result === undefined) throw incompatible();
  return result;
}

function requiredString(value: string | null | undefined): string {
  if (!value) throw incompatible();
  return value;
}

function toDate(value: unknown): Date | undefined {
  if (value === null || value === undefined) return undefined;
  if (value instanceof Date && !Number.isNaN(value.getTime())) return value;
  throw incompatible();
}

function toSegment(dto: MaterializedSegmentDto): MaterializedSegment {
  if (typeof dto.inUse !== 'boolean') throw incompatible();
  return {
    recordingVersionId: requiredString(dto.recordingVersionId?.toString()),
    timelineMappingDigest: requiredString(dto.timelineMappingDigest),
    segmentationPolicyDigest: requiredString(dto.segmentationPolicyDigest),
    segmentNumber: requiredInteger(dto.segmentNumber),
    state: requiredString(dto.state),
    mediaSizeBytes: toInteger(dto.mediaSizeBytes),
    failureReason: dto.failureReason ?? undefined,
    availableAt: toDate(dto.availableAt),
    inUse: dto.inUse,
  };
}

export function createSegmentAdapter(api: PlatformApi): SegmentAdapter {
  const segments = (matchId: string) => api.client.api.v1.matches.byMatchId(matchId as Guid).materializedSegments;
  return {
    list: async (matchId) => {
      const dto = await sendFor<MaterializedSegmentListDto>(
        api,
        segments(matchId).toGetRequestInformation(),
        createMaterializedSegmentListDtoFromDiscriminatorValue as ParsableFactory<MaterializedSegmentListDto>,
      );
      return {
        matchId: requiredString(dto.matchId?.toString()),
        segments: (dto.segments ?? []).map(toSegment),
      };
    },
    purge: async (matchId) => {
      const dto = await sendFor<SegmentPurgeResultDto>(
        api,
        segments(matchId).purge.toPostRequestInformation(),
        createSegmentPurgeResultDtoFromDiscriminatorValue as ParsableFactory<SegmentPurgeResultDto>,
      );
      return {
        matchId: requiredString(dto.matchId?.toString()),
        purged: requiredInteger(dto.purged),
        inUse: requiredInteger(dto.inUse),
      };
    },
  };
}
