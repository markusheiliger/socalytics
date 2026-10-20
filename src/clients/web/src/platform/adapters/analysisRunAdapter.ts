import {
  createAnalysisRunStatusDtoFromDiscriminatorValue,
  createAnalysisRunSummaryPageDtoFromDiscriminatorValue,
  type AnalysisNodeStatusDto,
  type AnalysisRunStatusDto,
  type AnalysisRunSummaryDto,
  type AnalysisRunSummaryPageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type { AnalysisRunStatus, NodeStatus, RunOutcome } from '../models/analysis';
import type { Page, PageRequest } from '../models/matches';
import type { AnalysisRunSummary } from '../models/recordings';
import { pageQuery } from './matchAdapter';
import { sendFor, withIfMatch, withJson, type PlatformApi } from './kiota';

export interface AnalysisRunAdapter {
  listByMatch(matchId: string, page?: PageRequest): Promise<Page<AnalysisRunSummary>>;
  getRun(runId: string): Promise<RunOutcome>;
  cancel(runId: string, version: number, reason?: string): Promise<AnalysisRunSummary>;
}

function toNodeStatus(dto: AnalysisNodeStatusDto | undefined): NodeStatus {
  const jobs = dto?.jobs;
  if (
    !dto || !dto.nodeKey || !dto.capabilityId || !dto.capabilityVersion || !dto.tier || !dto.executionScope || !dto.state || !jobs ||
    [jobs.waiting, jobs.ready, jobs.claimed, jobs.completed, jobs.failed, jobs.blocked].some((n) => typeof n !== 'number')
  ) {
    throw new PlatformFailure('incompatible');
  }
  return {
    nodeKey: dto.nodeKey,
    capabilityId: dto.capabilityId,
    capabilityVersion: dto.capabilityVersion,
    tier: dto.tier,
    executionScope: dto.executionScope,
    state: dto.state,
    blockedByNodeKey: dto.blockedByNodeKey ?? undefined,
    jobs: {
      waiting: jobs.waiting!,
      ready: jobs.ready!,
      claimed: jobs.claimed!,
      completed: jobs.completed!,
      failed: jobs.failed!,
      blocked: jobs.blocked!,
    },
  };
}

export function toAnalysisRunStatus(dto: AnalysisRunStatusDto | undefined): AnalysisRunStatus {
  if (
    !dto || !dto.runId || !dto.teamId || !dto.matchId || !dto.recordingSetVersionId || !dto.workflowId || !dto.workflowVersion ||
    !dto.state || !dto.createdAt || !dto.requestedCapabilities || !dto.nodes
  ) {
    throw new PlatformFailure('incompatible');
  }
  return {
    runId: dto.runId.toString(),
    teamId: dto.teamId.toString(),
    matchId: dto.matchId.toString(),
    recordingSetVersionId: dto.recordingSetVersionId.toString(),
    workflowId: dto.workflowId,
    workflowVersion: dto.workflowVersion,
    state: dto.state,
    failureReason: dto.failureReason ?? undefined,
    createdAt: dto.createdAt,
    runningAt: dto.runningAt ?? undefined,
    terminatedAt: dto.terminatedAt ?? undefined,
    requestedCapabilities: [...dto.requestedCapabilities],
    nodes: dto.nodes.map(toNodeStatus),
  };
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
  const runFactory = createAnalysisRunStatusDtoFromDiscriminatorValue as ParsableFactory<AnalysisRunStatusDto>;
  return {
    listByMatch: async (matchId, page) => {
      const info = api.client.api.v1.matches.byMatchId(matchId as Guid).analysisRuns.toGetRequestInformation({ queryParameters: pageQuery(page) });
      const result = await sendFor<AnalysisRunSummaryPageDto>(api, info, factory);
      return { items: (result.items ?? []).map(toAnalysisRun), continuationToken: result.continuationToken ?? undefined };
    },
    getRun: async (runId) => {
      const info = api.client.api.v1.analysis.runs.byRunId(runId as Guid).toGetRequestInformation();
      try {
        const result = await sendFor<AnalysisRunStatusDto>(api, info, runFactory);
        return { kind: 'found', run: toAnalysisRunStatus(result) };
      } catch (error) {
        if (error instanceof PlatformFailure && error.status === 404) return { kind: 'runNotFound' };
        throw error;
      }
    },
    cancel: async (runId, version, reason) => {
      const body = { reason: reason ?? null };
      const info = api.client.api.v1.analysis.runs.byRunId(runId as Guid).cancellation.toPostRequestInformation(body);
      return toAnalysisRun(await sendFor<AnalysisRunStatusDto>(api, withIfMatch(withJson(info, body), version), runFactory));
    },
  };
}
