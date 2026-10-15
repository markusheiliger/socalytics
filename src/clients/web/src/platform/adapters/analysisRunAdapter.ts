import {
  createAnalysisRunSummaryPageDtoFromDiscriminatorValue,
  type AnalysisRunSummaryDto,
  type AnalysisRunSummaryPageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type { Page, PageRequest } from '../models/matches';
import type { AnalysisRunSummary } from '../models/recordings';
import { pageQuery } from './matchAdapter';
import { sendFor, type PlatformApi } from './kiota';

export interface AnalysisRunAdapter {
  listByMatch(matchId: string, page?: PageRequest): Promise<Page<AnalysisRunSummary>>;
}

export function toAnalysisRun(dto: AnalysisRunSummaryDto | undefined): AnalysisRunSummary {
  if (!dto || !dto.runId || !dto.recordingSetVersionId || !dto.workflowId || !dto.workflowVersion || !dto.state || !dto.createdAt) {
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
  };
}

export function createAnalysisRunAdapter(api: PlatformApi): AnalysisRunAdapter {
  const factory = createAnalysisRunSummaryPageDtoFromDiscriminatorValue as ParsableFactory<AnalysisRunSummaryPageDto>;
  return {
    listByMatch: async (matchId, page) => {
      const info = api.client.api.v1.matches.byMatchId(matchId as Guid).analysisRuns.toGetRequestInformation({ queryParameters: pageQuery(page) });
      const result = await sendFor<AnalysisRunSummaryPageDto>(api, info, factory);
      return { items: (result.items ?? []).map(toAnalysisRun), continuationToken: result.continuationToken ?? undefined };
    },
  };
}
