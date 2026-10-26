import { isRunInProgress, type CoverageWindow } from '../../../platform/models/analysis';
import { MAX_FACTS_PER_STREAM, MAX_FACTS_TOTAL } from './timelineBins';

export type LaneState =
  | 'loading'
  | 'ready'
  | 'inProgress'
  | 'completeWithoutFacts'
  | 'noCoverage'
  | 'capabilityNotInRun'
  | 'unavailable'
  | 'error'
  | 'connectionRequired';

/** How the lane's data load ended; `loaded` means the pages were read. */
export type LaneLoadStatus = 'loading' | 'loaded' | 'capabilityNotInRun' | 'unavailable' | 'error' | 'connectionRequired';

const KNOWN_RUN_STATES = new Set(['requested', 'running', 'partiallycompleted', 'completed', 'failed', 'cancelled']);
const KNOWN_NODE_STATES = new Set(['waiting', 'active', 'completed', 'failed', 'blocked']);

/** The facts each lane may load: `min(5 000, floor(20 000 / lane count))`. */
export function laneAllowance(laneCount: number): number {
  return Math.min(MAX_FACTS_PER_STREAM, Math.floor(MAX_FACTS_TOTAL / Math.max(1, laneCount)));
}

/** A lane is partial when fewer facts were loaded than the stream reports. */
export function isLanePartial(loadedFactCount: number, reportedFactCount: number): boolean {
  return loadedFactCount < reportedFactCount;
}

export interface LaneStateInput {
  status: LaneLoadStatus;
  runState: string | undefined;
  loadedFactCount: number;
  windows: readonly CoverageWindow[];
}

/** Derives the lane state; only a known `Requested` or `Running` run state is in progress, every other value is final. */
export function deriveLaneState({ status, runState, loadedFactCount, windows }: LaneStateInput): LaneState {
  if (status !== 'loaded') return status;
  if (loadedFactCount > 0) return 'ready';
  if (isRunInProgress(runState)) return 'inProgress';
  if (windows.length === 0) return 'noCoverage';
  if (windows.every((window) => window.state.toLowerCase() === 'accepted')) return 'completeWithoutFacts';
  return 'ready';
}

export interface LaneRawStates {
  runState?: string;
  nodeState?: string;
}

/** Run and node states this module does not know, for the lane detail; never shown as zero. */
export function unknownLaneStates(runState: string | undefined, nodeState: string | undefined): LaneRawStates {
  const result: LaneRawStates = {};
  if (runState !== undefined && !KNOWN_RUN_STATES.has(runState.toLowerCase())) result.runState = runState;
  if (nodeState !== undefined && !KNOWN_NODE_STATES.has(nodeState.toLowerCase())) result.nodeState = nodeState;
  return result;
}
