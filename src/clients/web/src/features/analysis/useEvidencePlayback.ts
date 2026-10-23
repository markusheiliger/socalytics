import { useCallback, useEffect, useReducer, useRef } from 'react';
import type { AnalysisEvidenceAdapter } from '../../platform/adapters/analysisEvidenceAdapter';
import type { EvidenceMediaPiece, EvidenceOutcome, EvidencePart, EvidenceResolution } from '../../platform/models/analysis';
import { expiresWithinMargin, isInsideFact, isPlayable, orderParts, type PlayablePart } from './evidenceTimeline';

export const EVIDENCE_CONTEXT_MS = 5000;
export const MAX_AUTOMATIC_RESOLUTIONS = 20;
const DEFAULT_RETRY_AFTER_SECONDS = 5;

export type PlaybackStatus =
  | 'resolving'
  | 'playing'
  | 'preparing'
  | 'partUnavailable'
  | 'renewing'
  | 'notFound'
  | 'rateLimited'
  | 'offline'
  | 'error';

type Phase = 'resolving' | 'renewing' | 'ready' | 'notFound' | 'rateLimited' | 'offline' | 'error';
type RequestKind = 'initial' | 'auto' | 'renew' | 'retry';

export interface PlaybackModel {
  phase: Phase;
  inFlight: boolean;
  resolution?: EvidenceResolution;
  /** Current match-time position; undefined until the first resolution names the requested start. */
  matchMs?: number;
  autoAttempts: number;
  /** Match time of the one renewal that is used up until playback moves on from it. */
  renewedAtMs?: number;
  retryAfterSeconds?: number;
}

export type PlaybackAction =
  | { type: 'reset' }
  | { type: 'request'; kind: RequestKind }
  | { type: 'outcome'; outcome: EvidenceOutcome }
  | { type: 'position'; matchMs: number }
  | { type: 'continue' }
  | { type: 'fail' };

export const initialPlaybackModel: PlaybackModel = { phase: 'resolving', inFlight: false, autoAttempts: 0 };

export function currentPart(model: PlaybackModel): EvidencePart | undefined {
  if (model.phase !== 'ready' || !model.resolution || model.matchMs === undefined) return undefined;
  const matchMs = model.matchMs;
  return orderParts(model.resolution.parts).find((part) => part.endMs > matchMs);
}

export function playbackStatus(model: PlaybackModel): PlaybackStatus {
  if (model.phase !== 'ready') return model.phase;
  const part = currentPart(model);
  if (!part || isPlayable(part)) return 'playing';
  return part.state === 'materializing' || part.state === 'rematerializing' ? 'preparing' : 'partUnavailable';
}

export function playbackReducer(model: PlaybackModel, action: PlaybackAction): PlaybackModel {
  switch (action.type) {
    case 'reset':
      return initialPlaybackModel;
    case 'request':
      switch (action.kind) {
        case 'initial':
          return { ...initialPlaybackModel, inFlight: true };
        case 'retry':
          return { ...model, phase: model.resolution ? model.phase : 'resolving', inFlight: true, autoAttempts: 0 };
        case 'auto':
          return { ...model, inFlight: true, autoAttempts: model.autoAttempts + 1 };
        case 'renew':
          return { ...model, phase: 'renewing', inFlight: true, renewedAtMs: model.matchMs ?? 0 };
      }
      return model;
    case 'outcome': {
      const { outcome } = action;
      const base = { ...model, inFlight: false, retryAfterSeconds: undefined };
      switch (outcome.kind) {
        case 'resolution':
          return {
            ...base,
            phase: 'ready',
            resolution: outcome.resolution,
            matchMs: model.matchMs ?? outcome.resolution.requested.fromMs,
          };
        case 'notFound':
          return { ...base, phase: 'notFound' };
        case 'rateLimited':
          return { ...base, phase: 'rateLimited', retryAfterSeconds: outcome.retryAfterSeconds };
        case 'offline':
          return { ...base, phase: 'offline' };
        default:
          // Unavailable storage is a recoverable error like any other failed resolution.
          return { ...base, phase: 'error' };
      }
    }
    case 'position': {
      const progressed = model.renewedAtMs !== undefined && action.matchMs > model.renewedAtMs;
      return { ...model, matchMs: action.matchMs, renewedAtMs: progressed ? undefined : model.renewedAtMs };
    }
    case 'continue': {
      const part = currentPart(model);
      return part && !isPlayable(part) ? { ...model, matchMs: part.endMs } : model;
    }
    case 'fail':
      return { ...model, phase: 'error', inFlight: false };
  }
}

export interface EvidenceFactRef {
  acceptedResultId: string;
  ordinal: number;
}

export interface EvidencePlayback {
  status: PlaybackStatus;
  /** The playable part the player should show; present only while `status` is `playing`. */
  playing?: PlayablePart;
  /** The part that is not playable yet or at all; present for `preparing` and `partUnavailable`. */
  pending?: EvidencePart;
  nextPlayable?: PlayablePart;
  matchMs?: number;
  insideFact: boolean;
  shortened: boolean;
  finished: boolean;
  automaticAttemptsExhausted: boolean;
  retryAfterSeconds?: number;
  setPosition(matchMs: number): void;
  /** The current piece reached its end. */
  advance(): void;
  /** A media error or a stall near the link's expiry: one re-resolution, then a recoverable error. */
  mediaFailed(): void;
  continueAfterUnavailable(): void;
  tryAgain(): void;
}

interface Options {
  now?: () => number;
  enabled?: boolean;
}

/**
 * Resolves the evidence of one fact and drives its playback state (data-model.md). Resolutions and links live only
 * in this reducer state and are dropped on unmount.
 */
export function useEvidencePlayback(
  adapter: AnalysisEvidenceAdapter,
  runId: string,
  fact: EvidenceFactRef,
  { now = Date.now, enabled = true }: Options = {},
): EvidencePlayback {
  const [model, dispatch] = useReducer(playbackReducer, initialPlaybackModel);
  const modelRef = useRef(model);
  modelRef.current = model;
  const requestId = useRef(0);
  const { acceptedResultId, ordinal } = fact;

  const resolve = useCallback(
    async (kind: RequestKind, retryFailed = false) => {
      const id = ++requestId.current;
      dispatch({ type: 'request', kind });
      let outcome: EvidenceOutcome;
      try {
        outcome = await adapter.resolve(runId, {
          fact: { acceptedResultId, ordinal, contextBeforeMs: EVIDENCE_CONTEXT_MS, contextAfterMs: EVIDENCE_CONTEXT_MS },
          ...(retryFailed ? { retryFailed: true } : {}),
        });
      } catch {
        outcome = { kind: 'error' };
      }
      if (id === requestId.current) dispatch({ type: 'outcome', outcome });
    },
    [adapter, runId, acceptedResultId, ordinal],
  );

  useEffect(() => {
    if (!enabled) return undefined;
    void resolve('initial');
    return () => {
      requestId.current += 1;
    };
  }, [enabled, resolve]);

  const status = playbackStatus(model);
  const part = currentPart(model);
  const retryAfterSeconds = part?.retryAfterSeconds ?? DEFAULT_RETRY_AFTER_SECONDS;
  const partKey = part ? `${part.startMs}-${part.endMs}-${part.state}` : '';

  useEffect(() => {
    if (status !== 'preparing' || model.inFlight || model.autoAttempts >= MAX_AUTOMATIC_RESOLUTIONS) return undefined;
    const timer = setTimeout(() => void resolve('auto'), retryAfterSeconds * 1000);
    return () => clearTimeout(timer);
  }, [status, model.inFlight, model.autoAttempts, partKey, retryAfterSeconds, resolve]);

  const playing = status === 'playing' && part && isPlayable(part) ? part : undefined;
  const piece: EvidenceMediaPiece | undefined = playing?.media;
  const matchMs = model.matchMs;

  // A piece whose link expires within the margin is not started: resolve again first, once per stretch of progress.
  useEffect(() => {
    if (!piece || model.inFlight || model.renewedAtMs !== undefined) return;
    if (expiresWithinMargin(piece, now())) void resolve('renew');
  }, [piece, matchMs, model.inFlight, model.renewedAtMs, now, resolve]);

  const setPosition = useCallback((ms: number) => dispatch({ type: 'position', matchMs: ms }), []);
  const advance = useCallback(() => {
    const current = currentPart(modelRef.current);
    if (current) dispatch({ type: 'position', matchMs: current.endMs });
  }, []);
  const mediaFailed = useCallback(() => {
    const m = modelRef.current;
    if (m.inFlight || m.phase !== 'ready') return;
    if (m.renewedAtMs === undefined) void resolve('renew');
    else dispatch({ type: 'fail' });
  }, [resolve]);
  const continueAfterUnavailable = useCallback(() => dispatch({ type: 'continue' }), []);
  const tryAgain = useCallback(() => {
    const m = modelRef.current;
    const current = currentPart(m);
    void resolve('retry', current?.state === 'failed');
  }, [resolve]);

  const ordered = model.resolution ? orderParts(model.resolution.parts) : [];
  const nextPlayable = ordered.find((p): p is PlayablePart => isPlayable(p) && matchMs !== undefined && p.endMs > matchMs && p !== part);

  return {
    status,
    playing,
    pending: status === 'preparing' || status === 'partUnavailable' ? part : undefined,
    nextPlayable,
    matchMs,
    insideFact: matchMs !== undefined && isInsideFact(model.resolution?.fact, matchMs),
    shortened: model.resolution?.shortened ?? false,
    finished: model.phase === 'ready' && part === undefined,
    automaticAttemptsExhausted: status === 'preparing' && model.autoAttempts >= MAX_AUTOMATIC_RESOLUTIONS,
    retryAfterSeconds: status === 'rateLimited' ? model.retryAfterSeconds : part?.retryAfterSeconds,
    setPosition,
    advance,
    mediaFailed,
    continueAfterUnavailable,
    tryAgain,
  };
}
