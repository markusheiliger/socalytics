import {
  createAnalysisRunStatusDtoFromDiscriminatorValue,
  createAnalysisRunSummaryPageDtoFromDiscriminatorValue,
  type AnalysisRunStatusDto,
  type AnalysisRunSummaryDto,
  type AnalysisRunSummaryPageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type { Page, PageRequest } from '../models/matches';
import type { AnalysisRunSummary } from '../models/recordings';
import { pageQuery } from './matchAdapter';
import { sendFor, withIfMatch, withJson, type PlatformApi } from './kiota';

export interface AnalysisRunAdapter {
  listByMatch(matchId: string, page?: PageRequest): Promise<Page<AnalysisRunSummary>>;
  cancel(runId: string, version: number, reason?: string): Promise<AnalysisRunSummary>;
}

export function toAnalysisRun(dto: AnalysisRunSummaryDto | AnalysisRunStatusDto | undefined): AnalysisRunSummary {
  if (!dto || !dto.runId || !dto.recordingSetVersionId || !dto.workflowId || !dto.workflowVersion || !dto.state || !dto.createdAt || typeof dto.version !== 'number') {
    throw new PlatformFailure('incompatible');
  }
  return {
    runId: dto.runId.toString(),
    recordingSetVersionId: dto.recordingSetVersionId.toString(),
    workflowId: dto.workflowId,
    workflowVersion: dto.workflowVersion,
    state: dto.state,
    failureReason: dto.failureReason ?? undefined,
    createdAt: dto.createdAt,
    runningAt: dto.runningAt ?? undefined,
    terminatedAt: dto.terminatedAt ?? undefined,
    version: dto.version,
    cancelledBy:
      dto.cancelledBy?.memberId && dto.cancelledBy.accountName
        ? { memberId: dto.cancelledBy.memberId.toString(), accountName: dto.cancelledBy.accountName }
        : undefined,
    cancellationReason: dto.cancellationReason ?? undefined,
  };
}

export function createAnalysisRunAdapter(api: PlatformApi): AnalysisRunAdapter {
  const factory = createAnalysisRunSummaryPageDtoFromDiscriminatorValue as ParsableFactory<AnalysisRunSummaryPageDto>;
  const statusFactory = createAnalysisRunStatusDtoFromDiscriminatorValue as ParsableFactory<AnalysisRunStatusDto>;
  return {
    listByMatch: async (matchId, page) => {
      const info = api.client.api.v1.matches.byMatchId(matchId as Guid).analysisRuns.toGetRequestInformation({ queryParameters: pageQuery(page) });
      const result = await sendFor<AnalysisRunSummaryPageDto>(api, info, factory);
      return { items: (result.items ?? []).map(toAnalysisRun), continuationToken: result.continuationToken ?? undefined };
    },
    cancel: async (runId, version, reason) => {
      const body = { reason: reason ?? null };
      const info = api.client.api.v1.analysis.runs.byRunId(runId as Guid).cancellation.toPostRequestInformation(body);
      return toAnalysisRun(await sendFor<AnalysisRunStatusDto>(api, withIfMatch(withJson(info, body), version), statusFactory));
    },
  };
}
