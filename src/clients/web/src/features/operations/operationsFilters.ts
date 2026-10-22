import type { OutboxMessageType, OutboxState, ReceiptOutcome } from '../../platform/models/operations';

export const OUTBOX_STATES: readonly OutboxState[] = ['pending', 'published', 'failed', 'discarded'];

export const OUTBOX_MESSAGE_TYPES: readonly OutboxMessageType[] = [
  'matches.recordings-finalized',
  'analysis.ready-work',
  'analysis.run-state-changed',
];

export const RECEIPT_OUTCOMES: readonly ReceiptOutcome[] = ['run-created', 'run-exists', 'rejected'];

export interface OutboxFilters {
  state?: OutboxState;
  messageType?: OutboxMessageType;
}

export interface ReceiptFilters {
  outcome?: ReceiptOutcome;
}

function known<T extends string>(values: readonly T[], value: string | null): T | undefined {
  return value !== null && (values as readonly string[]).includes(value) ? (value as T) : undefined;
}

/** Unknown address values are ignored so a stale link never sends a value the platform refuses. */
export function parseOutboxFilters(params: URLSearchParams): OutboxFilters {
  const filters: OutboxFilters = {};
  const state = known(OUTBOX_STATES, params.get('state'));
  const messageType = known(OUTBOX_MESSAGE_TYPES, params.get('messageType'));
  if (state) filters.state = state;
  if (messageType) filters.messageType = messageType;
  return filters;
}

export function parseReceiptFilters(params: URLSearchParams): ReceiptFilters {
  const outcome = known(RECEIPT_OUTCOMES, params.get('outcome'));
  return outcome ? { outcome } : {};
}

function write(params: URLSearchParams, values: Record<string, string | undefined>): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const [key, value] of Object.entries(values)) {
    if (value === undefined) next.delete(key);
    else next.set(key, value);
  }
  return next;
}

/** Writes the filters back; cleared and unknown `state` and `messageType` values leave the address. */
export function writeOutboxFilters(params: URLSearchParams, filters: OutboxFilters): URLSearchParams {
  return write(params, { state: filters.state, messageType: filters.messageType });
}

export function writeReceiptFilters(params: URLSearchParams, filters: ReceiptFilters): URLSearchParams {
  return write(params, { outcome: filters.outcome });
}

/** Stable key for a filter set, used for query keys and page stacks. */
export function filterKey(filters: OutboxFilters | ReceiptFilters): string {
  return JSON.stringify(
    Object.entries(filters)
      .filter(([, v]) => v !== undefined)
      .sort(([a], [b]) => a.localeCompare(b)),
  );
}
