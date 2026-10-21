import type { AnalysisRunStatus } from '../../platform/models/analysis';
import { RUN_REFRESH_MS } from '../lineage/AnalysisRunStates';

export const analysisRunKey = (runId: string) => ['analysis', 'run', runId] as const;

export const analysisStreamsKey = (runId: string) => ['analysis', 'streams', runId] as const;

export const analysisFactsKey = (runId: string, nodeKey: string, fromMs: number | undefined, toMs: number | undefined, token: string | undefined) =>
  ['analysis', 'facts', runId, nodeKey, fromMs, toMs, token] as const;

/** A run is in progress while `Requested` or `Running` (matched case-insensitively). */
export function isRunInProgress(run: Pick<AnalysisRunStatus, 'state'> | undefined): boolean {
  const state = run?.state.toLowerCase();
  return state === 'requested' || state === 'running';
}

export function runRefreshInterval(run: Pick<AnalysisRunStatus, 'state'> | undefined): number | false {
  return isRunInProgress(run) ? RUN_REFRESH_MS : false;
}
