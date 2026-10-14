import type { TimelineSpan } from '../../platform/models/recordings';
import { formatTime, parseTime } from './mappingTime';

export interface SpanDraft {
  mediaStart: string;
  mediaEnd: string;
  matchStart: string;
}

export type SpanField = keyof SpanDraft;
export type SpanFieldError = 'required' | 'invalidTime' | 'endBeforeStart';

/** The default draft: one span covering the whole recording; the member enters only the match time. */
export function createDefaultDraft(durationSeconds?: number): SpanDraft[] {
  return [{ mediaStart: formatTime(0), mediaEnd: durationSeconds === undefined ? '' : formatTime(durationSeconds), matchStart: '' }];
}

/** A new span after the last one, in media and match time, for a gap such as half-time. */
export function appendSpan(spans: SpanDraft[], durationSeconds?: number): SpanDraft[] {
  const last = spans[spans.length - 1];
  const lastEnd = last ? parseTime(last.mediaEnd) : undefined;
  return [
    ...spans,
    {
      mediaStart: lastEnd === undefined ? '' : formatTime(lastEnd),
      mediaEnd: durationSeconds !== undefined && lastEnd !== undefined && durationSeconds > lastEnd ? formatTime(durationSeconds) : '',
      matchStart: '',
    },
  ];
}

export function validateSpan(span: SpanDraft): Partial<Record<SpanField, SpanFieldError>> {
  const errors: Partial<Record<SpanField, SpanFieldError>> = {};
  const parsed: Partial<Record<SpanField, number>> = {};
  for (const field of ['mediaStart', 'mediaEnd', 'matchStart'] as const) {
    const text = span[field].trim();
    if (text === '') {
      errors[field] = 'required';
      continue;
    }
    const value = parseTime(text);
    if (value === undefined) errors[field] = 'invalidTime';
    else parsed[field] = value;
  }
  if (parsed.mediaStart !== undefined && parsed.mediaEnd !== undefined && parsed.mediaEnd <= parsed.mediaStart) {
    errors.mediaEnd = 'endBeforeStart';
  }
  return errors;
}

export type DraftResult = { ok: true; spans: TimelineSpan[] } | { ok: false; errors: Partial<Record<SpanField, SpanFieldError>>[] };

/** Converts the draft to seconds; the platform's validation of overlaps and bounds stays authoritative. */
export function toTimelineSpans(draft: SpanDraft[]): DraftResult {
  const errors = draft.map(validateSpan);
  if (draft.length === 0 || errors.some((e) => Object.keys(e).length > 0)) return { ok: false, errors };
  return {
    ok: true,
    spans: draft.map((s) => ({
      mediaStartSeconds: parseTime(s.mediaStart) as number,
      mediaEndSeconds: parseTime(s.mediaEnd) as number,
      matchStartSeconds: parseTime(s.matchStart) as number,
    })),
  };
}
