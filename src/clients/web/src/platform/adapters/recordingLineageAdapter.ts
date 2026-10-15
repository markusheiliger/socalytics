import {
  createRecordingLineageDtoFromDiscriminatorValue,
  createRecordingSetVersionDtoFromDiscriminatorValue,
  createRecordingVersionDtoFromDiscriminatorValue,
  createTimelineMappingDtoFromDiscriminatorValue,
  type RecordingLineageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type { FinalizeMember, Lineage, RecordingSetVersion, RecordingVersion, TimelineMapping, TimelineSpan } from '../models/recordings';
import { sendFor, withIdempotencyKey, withJson, type PlatformApi } from './kiota';
import { toRecordingSetVersion, toRecordingVersion, toSpanBody, toTimelineMapping } from './recordingMapping';

export interface RecordingLineageAdapter {
  getLineage(matchId: string): Promise<Lineage>;
  getVersion(matchId: string, id: string): Promise<RecordingVersion>;
  reviseMapping(matchId: string, versionId: string, mapping: TimelineSpan[], key: string): Promise<TimelineMapping>;
  finalize(matchId: string, members: FinalizeMember[], key: string): Promise<RecordingSetVersion>;
  getSet(matchId: string, id: string): Promise<RecordingSetVersion>;
}

export function createRecordingLineageAdapter(api: PlatformApi): RecordingLineageAdapter {
  const match = (matchId: string) => api.client.api.v1.matches.byMatchId(matchId as Guid);
  return {
    getLineage: async (matchId) => {
      const dto = await sendFor<RecordingLineageDto>(
        api,
        match(matchId).recordingLineage.toGetRequestInformation(),
        createRecordingLineageDtoFromDiscriminatorValue as ParsableFactory<RecordingLineageDto>,
      );
      if (!dto.matchId || !dto.teamId) throw new PlatformFailure('incompatible');
      return {
        matchId: dto.matchId,
        teamId: dto.teamId,
        recordingVersions: (dto.recordingVersions ?? []).map(toRecordingVersion),
        timelineMappings: (dto.timelineMappings ?? []).map(toTimelineMapping),
        recordingSetVersions: (dto.recordingSetVersions ?? []).map(toRecordingSetVersion),
      };
    },
    getVersion: async (matchId, id) =>
      toRecordingVersion(
        await sendFor(
          api,
          match(matchId).recordingVersions.byRecordingVersionId(id as Guid).toGetRequestInformation(),
          createRecordingVersionDtoFromDiscriminatorValue,
        ),
      ),
    reviseMapping: async (matchId, versionId, mapping, key) => {
      const builder = match(matchId).recordingVersions.byRecordingVersionId(versionId as Guid).timelineMappings;
      const info = withIdempotencyKey(withJson(builder.toPostRequestInformation(), { spans: mapping.map(toSpanBody) }), key);
      return toTimelineMapping(await sendFor(api, info, createTimelineMappingDtoFromDiscriminatorValue));
    },
    finalize: async (matchId, members, key) => {
      const info = withIdempotencyKey(
        withJson(match(matchId).recordingSetVersions.toPostRequestInformation(), {
          members: members.map((m) => ({ recordingVersionId: m.recordingVersionId, timelineMappingId: m.timelineMappingId })),
        }),
        key,
      );
      return toRecordingSetVersion(await sendFor(api, info, createRecordingSetVersionDtoFromDiscriminatorValue));
    },
    getSet: async (matchId, id) =>
      toRecordingSetVersion(
        await sendFor(
          api,
          match(matchId).recordingSetVersions.byRecordingSetVersionId(id as Guid).toGetRequestInformation(),
          createRecordingSetVersionDtoFromDiscriminatorValue,
        ),
      ),
  };
}
