import { PlatformFailure } from '../../platform/failure';
import {
  isRunTerminal,
  type AnalysisRunStatus,
  type FactStreamSummary,
} from '../../platform/models/analysis';
import type { RecordingSetVersion } from '../../platform/models/recordings';

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export type InvalidSelectionReason =
  | 'malformedId'
  | 'duplicateParameter'
  | 'unsupportedParameter'
  | 'rightWithoutLeft'
  | 'identicalIds';

export type ComparisonSelection =
  | { kind: 'empty' }
  | { kind: 'left'; leftRunId: string }
  | { kind: 'pair'; leftRunId: string; rightRunId: string }
  | { kind: 'invalid'; reason: InvalidSelectionReason };

function invalid(reason: InvalidSelectionReason): ComparisonSelection {
  return { kind: 'invalid', reason };
}

/** Parses only the `left` and `right` run identifiers; no other query state is supported. */
export function parseComparisonSelection(search: URLSearchParams | string): ComparisonSelection {
  const params = typeof search === 'string' ? new URLSearchParams(search) : search;
  const keys = new Set(params.keys());
  for (const key of keys) {
    if (key !== 'left' && key !== 'right') return invalid('unsupportedParameter');
  }
  const lefts = params.getAll('left');
  const rights = params.getAll('right');
  if (lefts.length > 1 || rights.length > 1) return invalid('duplicateParameter');
  const left = lefts[0];
  const right = rights[0];
  if ((left !== undefined && !UUID_PATTERN.test(left)) || (right !== undefined && !UUID_PATTERN.test(right))) {
    return invalid('malformedId');
  }
  if (right !== undefined && left === undefined) return invalid('rightWithoutLeft');
  if (left === undefined) return { kind: 'empty' };
  if (right === undefined) return { kind: 'left', leftRunId: left };
  if (left.toLowerCase() === right.toLowerCase()) return invalid('identicalIds');
  return { kind: 'pair', leftRunId: left, rightRunId: right };
}

export type PairEligibility =
  | { kind: 'eligible' }
  | { kind: 'ineligible'; side: 'left' | 'right'; reason: 'notTerminal' | 'unknownState' };

export interface RunPairInput {
  matchId: string;
  /** Team of the freshly read match. */
  teamId: string;
  leftRunId: string;
  rightRunId: string;
  left: AnalysisRunStatus;
  right: AnalysisRunStatus;
}

const IN_PROGRESS_STATES = ['requested', 'running'];

function incompatible(): PlatformFailure {
  return new PlatformFailure('incompatible');
}

function sameId(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}

/**
 * Both statuses must answer the requested identities within the selected match and team (otherwise the response
 * is incompatible); then both runs must be known terminal. Current or superseded status plays no part.
 */
export function validateRunPair(input: RunPairInput): PairEligibility {
  if (input.leftRunId.toLowerCase() === input.rightRunId.toLowerCase()) throw incompatible();
  const sides = [
    { side: 'left' as const, requested: input.leftRunId, run: input.left },
    { side: 'right' as const, requested: input.rightRunId, run: input.right },
  ];
  for (const { requested, run } of sides) {
    if (
      !sameId(run.runId, requested) ||
      !sameId(run.matchId, input.matchId) ||
      !sameId(run.teamId, input.teamId)
    ) {
      throw incompatible();
    }
  }
  for (const { side, run } of sides) {
    if (isRunTerminal(run.state)) continue;
    const reason = IN_PROGRESS_STATES.includes(run.state.toLowerCase()) ? 'notTerminal' : 'unknownState';
    return { kind: 'ineligible', side, reason };
  }
  return { kind: 'eligible' };
}

export interface ComparisonSideInput {
  matchId: string;
  teamId: string;
  run: AnalysisRunStatus;
  streams: FactStreamSummary[];
  recordingSet: RecordingSetVersion;
}

export interface ComparisonSide {
  run: AnalysisRunStatus;
  streams: FactStreamSummary[];
  recordingSet: RecordingSetVersion;
}

function present(value: unknown): value is string {
  return typeof value === 'string' && value.trim() !== '';
}

function validateRecordingSet(input: ComparisonSideInput): void {
  const set = input.recordingSet;
  if (
    !sameId(set.id, input.run.recordingSetVersionId) ||
    !sameId(set.matchId, input.matchId) ||
    !sameId(set.teamId, input.teamId) ||
    !(set.finalizedAt instanceof Date) ||
    Number.isNaN(set.finalizedAt.getTime()) ||
    !Array.isArray(set.members)
  ) {
    throw incompatible();
  }
  let previous = Number.NEGATIVE_INFINITY;
  for (const member of set.members) {
    if (
      !Number.isSafeInteger(member.position) ||
      member.position <= previous ||
      !present(member.recordingVersionId) ||
      !present(member.recordingContentDigest) ||
      !present(member.timelineMappingId) ||
      !present(member.timelineMappingDigest)
    ) {
      throw incompatible();
    }
    previous = member.position;
  }
}

function validateStreams(run: AnalysisRunStatus, streams: FactStreamSummary[]): void {
  const nodes = new Map(run.nodes.map((node) => [node.nodeKey, node]));
  const seen = new Set<string>();
  for (const stream of streams) {
    if (seen.has(stream.nodeKey)) throw incompatible();
    seen.add(stream.nodeKey);
    const node = nodes.get(stream.nodeKey);
    if (
      !node ||
      node.capabilityId !== stream.capabilityId ||
      node.capabilityVersion !== stream.capabilityVersion ||
      node.executionScope !== stream.executionScope ||
      stream.provenance.workflow.id !== run.workflowId ||
      stream.provenance.workflow.version !== run.workflowVersion
    ) {
      throw incompatible();
    }
  }
}

/** Validates one side's exact recording set and streams against its run; any inconsistency is incompatible. */
export function validateComparisonSide(input: ComparisonSideInput): ComparisonSide {
  validateRecordingSet(input);
  validateStreams(input.run, input.streams);
  return { run: input.run, streams: input.streams, recordingSet: input.recordingSet };
}
