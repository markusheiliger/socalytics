import { PlatformFailure } from '../../platform/failure';
import {
  isRunTerminal,
  type AnalysisRunStatus,
  type FactStreamSummary,
  type NodeInputCoverage,
  type NodeStatus,
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

export type StreamField =
  | 'nodeKey'
  | 'capabilityVersion'
  | 'schemaVersion'
  | 'outputType'
  | 'profileId'
  | 'profileDigest'
  | 'imageDigest'
  | 'modelDigest';

export interface FieldDifference {
  field: StreamField;
  left: string | undefined;
  right: string | undefined;
}

export type InputCoverageField =
  'coverageMode' | 'minimumAcceptedFraction' | 'accepted' | 'missing' | 'failed' | 'blocked' | 'skipped';

export type DependencyComparison =
  | { upstreamNodeKey: string; kind: 'leftOnly'; left: NodeInputCoverage }
  | { upstreamNodeKey: string; kind: 'rightOnly'; right: NodeInputCoverage }
  | {
      upstreamNodeKey: string;
      kind: 'same' | 'changed';
      left: NodeInputCoverage;
      right: NodeInputCoverage;
      changedFields: InputCoverageField[];
    };

export interface InputCoverageComparison {
  /** Undefined means "not reported" (distinct from a reported empty list). */
  left: NodeInputCoverage[] | undefined;
  right: NodeInputCoverage[] | undefined;
  /** Dependencies in left report order followed by right-only keys; never merged across keys. */
  dependencies: DependencyComparison[];
  /** Matching reports never prove equal full input snapshots. */
  establishesEqualInputs: false;
}

export type AvailabilityKind =
  | 'noExpectedUnits'
  | 'noAcceptedResults'
  | 'incompleteCoverage'
  | 'completeZeroFacts'
  | 'completeFacts'
  | 'incompatible';

export interface StreamAvailability {
  kind: AvailabilityKind;
  zeroFacts: boolean;
  /** Every expected unit was skipped. */
  allSkipped: boolean;
}

export type UnmatchedReason = 'noCounterpart' | 'ambiguous';

export type StreamComparisonEntry =
  | {
      kind: 'aligned';
      left: FactStreamSummary;
      right: FactStreamSummary;
      differences: FieldDifference[];
      inputCoverage: InputCoverageComparison;
      leftAvailability: StreamAvailability;
      rightAvailability: StreamAvailability;
    }
  | {
      kind: 'unmatched';
      side: 'left' | 'right';
      stream: FactStreamSummary;
      reason: UnmatchedReason;
      availability: StreamAvailability;
    };

const COUNT_KEYS = ['accepted', 'missing', 'failed', 'blocked', 'skipped'] as const;

function validCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

/** Derives the availability branch of one stream from its fact count and output coverage counts. */
export function describeStreamAvailability(stream: FactStreamSummary): StreamAvailability {
  const { coverage, factCount } = stream;
  const zeroFacts = factCount === 0;
  const incompatible: StreamAvailability = {
    kind: 'incompatible',
    zeroFacts,
    allSkipped: false,
  };
  if (!validCount(factCount) || COUNT_KEYS.some((key) => !validCount(coverage[key]))) return incompatible;
  const expected = COUNT_KEYS.reduce((sum, key) => sum + coverage[key], 0);
  if (!Number.isSafeInteger(expected)) return incompatible;
  if (factCount > 0 && coverage.accepted === 0) return incompatible;
  const allSkipped = expected > 0 && coverage.skipped === expected;
  if (expected === 0) return { kind: 'noExpectedUnits', zeroFacts, allSkipped: false };
  if (coverage.accepted === 0) return { kind: 'noAcceptedResults', zeroFacts, allSkipped };
  if (coverage.accepted < expected) return { kind: 'incompleteCoverage', zeroFacts, allSkipped: false };
  return {
    kind: zeroFacts ? 'completeZeroFacts' : 'completeFacts',
    zeroFacts,
    allSkipped: false,
  };
}

function tupleKey(stream: FactStreamSummary): string {
  return JSON.stringify([stream.capabilityId, stream.executionScope]);
}

function groupByTuple(streams: FactStreamSummary[]): Map<string, FactStreamSummary[]> {
  const groups = new Map<string, FactStreamSummary[]>();
  for (const stream of streams) {
    const key = tupleKey(stream);
    const group = groups.get(key);
    if (group) group.push(stream);
    else groups.set(key, [stream]);
  }
  return groups;
}

function streamDifferences(left: FactStreamSummary, right: FactStreamSummary): FieldDifference[] {
  const fields: [StreamField, string | undefined, string | undefined][] = [
    ['nodeKey', left.nodeKey, right.nodeKey],
    ['capabilityVersion', left.capabilityVersion, right.capabilityVersion],
    ['schemaVersion', left.schemaVersion, right.schemaVersion],
    ['outputType', left.outputType, right.outputType],
    ['profileId', left.provenance.analystProfile.id, right.provenance.analystProfile.id],
    ['profileDigest', left.provenance.analystProfile.digest, right.provenance.analystProfile.digest],
    ['imageDigest', left.provenance.imageDigest, right.provenance.imageDigest],
    ['modelDigest', left.provenance.modelDigest, right.provenance.modelDigest],
  ];
  return fields.filter(([, l, r]) => l !== r).map(([field, l, r]) => ({ field, left: l, right: r }));
}

function indexDependencies(entries: NodeInputCoverage[]): Map<string, NodeInputCoverage> {
  const index = new Map<string, NodeInputCoverage>();
  for (const entry of entries) {
    if (index.has(entry.upstreamNodeKey)) throw incompatible();
    index.set(entry.upstreamNodeKey, entry);
  }
  return index;
}

function compareInputCoverage(
  left: NodeInputCoverage[] | undefined,
  right: NodeInputCoverage[] | undefined,
): InputCoverageComparison {
  const leftIndex = indexDependencies(left ?? []);
  const rightIndex = indexDependencies(right ?? []);
  const dependencies: DependencyComparison[] = [];
  for (const [key, l] of leftIndex) {
    const r = rightIndex.get(key);
    if (!r) {
      dependencies.push({ upstreamNodeKey: key, kind: 'leftOnly', left: l });
      continue;
    }
    const changedFields = (['coverageMode', 'minimumAcceptedFraction', ...COUNT_KEYS] as InputCoverageField[]).filter(
      (field) => l[field] !== r[field],
    );
    dependencies.push({
      upstreamNodeKey: key,
      kind: changedFields.length === 0 ? 'same' : 'changed',
      left: l,
      right: r,
      changedFields,
    });
  }
  for (const [key, r] of rightIndex) {
    if (!leftIndex.has(key)) dependencies.push({ upstreamNodeKey: key, kind: 'rightOnly', right: r });
  }
  return { left, right, dependencies, establishesEqualInputs: false };
}

function indexSide(side: ComparisonSide): Map<string, NodeStatus> {
  const nodes = new Map<string, NodeStatus>();
  for (const node of side.run.nodes) {
    if (nodes.has(node.nodeKey)) throw incompatible();
    nodes.set(node.nodeKey, node);
  }
  const seen = new Set<string>();
  for (const stream of side.streams) {
    if (seen.has(stream.nodeKey) || !nodes.has(stream.nodeKey)) throw incompatible();
    seen.add(stream.nodeKey);
  }
  return nodes;
}

/**
 * Accounts for every stream exactly once on its own side. Only a capability/scope group with exactly one stream on
 * each side is aligned; ambiguous groups are emitted as independent unmatched streams, never zipped or subset.
 * Order: left streams in their order (aligned entries at the left position), then right-only streams.
 */
export function compareStreams(left: ComparisonSide, right: ComparisonSide): StreamComparisonEntry[] {
  const leftNodes = indexSide(left);
  const rightNodes = indexSide(right);
  const leftGroups = groupByTuple(left.streams);
  const rightGroups = groupByTuple(right.streams);
  const entries: StreamComparisonEntry[] = [];
  const alignedRight = new Set<FactStreamSummary>();

  for (const stream of left.streams) {
    const key = tupleKey(stream);
    const leftGroup = leftGroups.get(key) ?? [];
    const rightGroup = rightGroups.get(key) ?? [];
    if (leftGroup.length === 1 && rightGroup.length === 1) {
      const counterpart = rightGroup[0];
      alignedRight.add(counterpart);
      entries.push({
        kind: 'aligned',
        left: stream,
        right: counterpart,
        differences: streamDifferences(stream, counterpart),
        inputCoverage: compareInputCoverage(
          leftNodes.get(stream.nodeKey)?.inputCoverage,
          rightNodes.get(counterpart.nodeKey)?.inputCoverage,
        ),
        leftAvailability: describeStreamAvailability(stream),
        rightAvailability: describeStreamAvailability(counterpart),
      });
    } else {
      entries.push({
        kind: 'unmatched',
        side: 'left',
        stream,
        reason: leftGroup.length > 1 || rightGroup.length > 1 ? 'ambiguous' : 'noCounterpart',
        availability: describeStreamAvailability(stream),
      });
    }
  }

  for (const stream of right.streams) {
    if (alignedRight.has(stream)) continue;
    const key = tupleKey(stream);
    const ambiguous = (leftGroups.get(key)?.length ?? 0) > 1 || (rightGroups.get(key)?.length ?? 0) > 1;
    entries.push({
      kind: 'unmatched',
      side: 'right',
      stream,
      reason: ambiguous ? 'ambiguous' : 'noCounterpart',
      availability: describeStreamAvailability(stream),
    });
  }
  return entries;
}
