import type { PlatformFailure } from '../../platform/failure';
import {
  isRunInProgress,
  type CoverageWindow,
  type FactPage,
  type FactsOutcome,
  type FactStreamSummary,
  type RunOutcome,
  type StreamsOutcome,
} from '../../platform/models/analysis';

export type FactViewState =
  | { state: 'connectionRequired' }
  | { state: 'notFound' }
  | { state: 'notPermitted' }
  | { state: 'capabilityNotInRun' }
  | { state: 'invalidRange' }
  | { state: 'error' }
  | { state: 'loading' }
  | { state: 'facts'; incomplete: boolean }
  | { state: 'noFactsInRange' }
  | { state: 'inProgress' }
  | { state: 'incomplete' }
  | { state: 'completeWithoutFacts' };

export interface FactViewInput {
  /** False while the browser is offline. */
  online: boolean;
  routeMatchId: string;
  /** False when the member has no role on the run's team. */
  hasTeamRole: boolean;
  runOutcome?: RunOutcome;
  streamsOutcome?: StreamsOutcome;
  factsOutcome?: FactsOutcome;
  /** Failure of the run, stream, or fact request, when one occurred. */
  failure?: PlatformFailure | Error | null;
  /** Node key of the selected stream, when the address carries one. */
  nodeKey?: string;
  /** Precomputed validity of the address or form range; an empty range is valid. */
  rangeValid: boolean;
  /** True when the range is set (either side). */
  rangeSet: boolean;
  /** A request for the current selection is pending. */
  pending: boolean;
  page?: FactPage;
  /** The selected stream's summary, used for coverage counts while no page is loaded. */
  stream?: FactStreamSummary;
}

function isRefused(failure: FactViewInput['failure']): boolean {
  const candidate = failure as Partial<PlatformFailure> | null | undefined;
  return candidate?.kind === 'notPermitted' || candidate?.status === 403;
}

function windowsComplete(windows: CoverageWindow[]): boolean {
  return windows.every((window) => window.state.toLowerCase() === 'accepted');
}

function coverageComplete(input: FactViewInput): boolean {
  if (input.page) return windowsComplete(input.page.coverage);
  const counts = input.stream?.coverage;
  return !counts || counts.missing + counts.failed + counts.blocked + counts.skipped === 0;
}

export function deriveFactViewState(input: FactViewInput): FactViewState {
  if (!input.online) return { state: 'connectionRequired' };

  const run = input.runOutcome?.kind === 'found' ? input.runOutcome.run : undefined;
  if (
    input.runOutcome?.kind === 'runNotFound' ||
    input.streamsOutcome?.kind === 'runNotFound' ||
    input.factsOutcome?.kind === 'runNotFound' ||
    (run && run.matchId !== input.routeMatchId)
  ) {
    return { state: 'notFound' };
  }

  if (!input.hasTeamRole || isRefused(input.failure)) return { state: 'notPermitted' };

  const streamMissing =
    input.nodeKey !== undefined &&
    input.streamsOutcome?.kind === 'found' &&
    !input.streamsOutcome.streams.some((stream) => stream.nodeKey === input.nodeKey);
  if (input.factsOutcome?.kind === 'capabilityNotInRun' || streamMissing) return { state: 'capabilityNotInRun' };

  if (!input.rangeValid) return { state: 'invalidRange' };

  if (input.failure) return { state: 'error' };

  if (input.pending && !input.page) return { state: 'loading' };

  const runState = run?.state ?? input.page?.run.state;
  const inProgress = isRunInProgress(runState);
  const complete = coverageComplete(input);

  if (input.page && input.page.items.length > 0) {
    return { state: 'facts', incomplete: inProgress || !complete };
  }
  if (input.rangeSet) return { state: 'noFactsInRange' };
  if (inProgress) return { state: 'inProgress' };
  if (!complete) return { state: 'incomplete' };
  return { state: 'completeWithoutFacts' };
}
