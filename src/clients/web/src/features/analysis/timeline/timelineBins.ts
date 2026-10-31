import type { CoverageWindow } from '../../../platform/models/analysis';

// Development values.
export const MAX_FACTS_PER_STREAM = 5000;
export const MAX_FACTS_TOTAL = 20000;
export const BINS_PER_VIEW = 30;

export type ZoomId = '10s' | '30s' | '1m' | '5m';

export interface ZoomLevel {
  id: ZoomId;
  binMs: number;
  bins: number;
}

export const ZOOM_LEVELS: readonly ZoomLevel[] = [
  { id: '10s', binMs: 10_000, bins: BINS_PER_VIEW },
  { id: '30s', binMs: 30_000, bins: BINS_PER_VIEW },
  { id: '1m', binMs: 60_000, bins: BINS_PER_VIEW },
  { id: '5m', binMs: 300_000, bins: BINS_PER_VIEW },
];

const FINEST_BIN_MS = ZOOM_LEVELS[0].binMs;

export interface TimelineFact {
  startMs: number;
  endMs: number;
}

export interface ExtentSource {
  windows: readonly CoverageWindow[];
  facts: readonly TimelineFact[];
}

export interface Extent {
  startMs: number;
  endMs: number;
}

export type BinCoverage = 'accepted' | 'notAnalysed' | 'partial' | 'none';
export type BinState = 'facts' | 'analysedNoFacts' | 'notLoaded' | 'partial' | 'notAnalysed' | 'noCoverage';

export interface DensityBin {
  startMs: number;
  endMs: number;
  count: number;
  coverage: BinCoverage;
  state: BinState;
  /** Window states of other than `accepted` that this module does not know, shown raw and treated as not analysed. */
  rawStates: string[];
}

const KNOWN_WINDOW_STATES = new Set(['accepted', 'missing', 'failed', 'blocked', 'skipped']);

export function zoomLevel(id: ZoomId): ZoomLevel {
  return ZOOM_LEVELS.find((level) => level.id === id) ?? ZOOM_LEVELS[0];
}

/** The union of all windows and facts of all lanes, rounded out to the finest bin size; undefined when there are none. */
export function overallExtent(lanes: readonly ExtentSource[]): Extent | undefined {
  let min = Number.POSITIVE_INFINITY;
  let max = Number.NEGATIVE_INFINITY;
  for (const lane of lanes) {
    for (const window of lane.windows) {
      min = Math.min(min, window.startMs);
      max = Math.max(max, window.endMs);
    }
    for (const fact of lane.facts) {
      min = Math.min(min, fact.startMs);
      // A zero-length fact still needs a bin that contains it.
      max = Math.max(max, fact.endMs > fact.startMs ? fact.endMs : fact.startMs + 1);
    }
  }
  if (!Number.isFinite(min) || !Number.isFinite(max)) return undefined;
  const startMs = Math.floor(min / FINEST_BIN_MS) * FINEST_BIN_MS;
  let endMs = Math.ceil(max / FINEST_BIN_MS) * FINEST_BIN_MS;
  if (endMs <= startMs) endMs = startMs + FINEST_BIN_MS;
  return { startMs, endMs };
}

/** The finest level whose bins cover the extent, else the widest. */
export function defaultZoom(extent: Extent | undefined): ZoomId {
  const widest = ZOOM_LEVELS[ZOOM_LEVELS.length - 1];
  if (!extent) return widest.id;
  const length = extent.endMs - extent.startMs;
  return (ZOOM_LEVELS.find((level) => level.bins * level.binMs >= length) ?? widest).id;
}

/** Floors to a multiple of `binMs`, then clamps to `[floor(extentStart), extentEnd - bins * binMs]`. */
export function clampExtentStart(startMs: number, zoom: ZoomLevel, extent: Extent): number {
  const lower = Math.floor(extent.startMs);
  const upper = Math.max(lower, extent.endMs - zoom.bins * zoom.binMs);
  const floored = Math.floor(startMs / zoom.binMs) * zoom.binMs;
  return Math.min(upper, Math.max(lower, floored));
}

/** Moves the visible extent by half its width, earlier (-1) or later (+1). */
export function moveExtentStart(startMs: number, direction: -1 | 1, zoom: ZoomLevel, extent: Extent): number {
  const half = (zoom.bins * zoom.binMs) / 2;
  return clampExtentStart(startMs + direction * half, zoom, extent);
}

interface Interval {
  start: number;
  end: number;
}

function unionLength(intervals: Interval[]): number {
  const sorted = [...intervals].sort((a, b) => a.start - b.start);
  let total = 0;
  let cursor = Number.NEGATIVE_INFINITY;
  for (const interval of sorted) {
    const start = Math.max(interval.start, cursor);
    if (interval.end > start) {
      total += interval.end - start;
      cursor = interval.end;
    }
  }
  return total;
}

export interface DeriveBinsInput {
  windows: readonly CoverageWindow[];
  facts: readonly TimelineFact[];
  partial: boolean;
  extentStartMs: number;
  zoom: ZoomLevel;
}

/** Derives the density bins of the visible extent of one lane (research R4). */
export function deriveBins({ windows, facts, partial, extentStartMs, zoom }: DeriveBinsInput): DensityBin[] {
  const { binMs, bins: binCount } = zoom;
  const counts = new Array<number>(binCount).fill(0);
  let horizon = Number.NEGATIVE_INFINITY;
  for (const fact of facts) {
    horizon = Math.max(horizon, fact.startMs);
    const first = Math.floor((fact.startMs - extentStartMs) / binMs);
    const last = fact.endMs > fact.startMs ? Math.ceil((fact.endMs - extentStartMs) / binMs) - 1 : first;
    for (let index = Math.max(first, 0); index <= Math.min(last, binCount - 1); index++) counts[index]++;
  }

  const result: DensityBin[] = [];
  for (let index = 0; index < binCount; index++) {
    const startMs = extentStartMs + index * binMs;
    const endMs = startMs + binMs;
    const accepted: Interval[] = [];
    const all: Interval[] = [];
    const raw = new Set<string>();
    for (const window of windows) {
      if (window.startMs >= endMs || window.endMs <= startMs) continue;
      const interval = { start: Math.max(window.startMs, startMs), end: Math.min(window.endMs, endMs) };
      all.push(interval);
      const state = window.state.toLowerCase();
      if (state === 'accepted') accepted.push(interval);
      else if (!KNOWN_WINDOW_STATES.has(state)) raw.add(window.state);
    }

    let coverage: BinCoverage;
    if (all.length === 0) coverage = 'none';
    else if (unionLength(accepted) >= binMs) coverage = 'accepted';
    else if (accepted.length === 0 && unionLength(all) >= binMs) coverage = 'notAnalysed';
    else coverage = 'partial';

    const count = counts[index];
    let state: BinState;
    if (count > 0) state = 'facts';
    else if (coverage === 'accepted') state = partial && startMs > horizon ? 'notLoaded' : 'analysedNoFacts';
    else if (coverage === 'notAnalysed') state = 'notAnalysed';
    else if (coverage === 'partial') state = 'partial';
    else state = 'noCoverage';

    result.push({ startMs, endMs, count, coverage, state, rawStates: [...raw] });
  }
  return result;
}
