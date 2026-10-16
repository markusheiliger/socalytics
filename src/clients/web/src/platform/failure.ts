export type PlatformFailureKind =
  | 'sessionEnded'
  | 'passwordChangeRequired'
  | 'notPermitted'
  | 'validation'
  | 'conflict'
  | 'seasonArchived'
  | 'tooLarge'
  | 'unsupportedType'
  | 'uploadExpired'
  | 'uploadCompleted'
  | 'uploadPartsRefused'
  | 'uploadIntegrityFailed'
  | 'storageUnavailable'
  | 'pairingRefused'
  | 'rateLimited'
  | 'decisionConflict'
  | 'programmingError'
  | 'sessionProblem'
  | 'unreachable'
  | 'unexpected'
  | 'incompatible'
  | 'updateRequired';

export interface FieldError {
  field: string;
  code: string;
}

export interface FailureDetails {
  status?: number;
  code?: string;
  correlationId?: string;
  fieldErrors?: FieldError[];
  retryAfterSeconds?: number;
}

export class PlatformFailure extends Error {
  readonly kind: PlatformFailureKind;
  readonly status?: number;
  readonly code?: string;
  readonly correlationId?: string;
  readonly fieldErrors: FieldError[];
  readonly retryAfterSeconds?: number;

  constructor(kind: PlatformFailureKind, details: FailureDetails = {}) {
    super(`Platform request failed: ${kind}`);
    this.name = 'PlatformFailure';
    this.kind = kind;
    this.status = details.status;
    this.code = details.code;
    this.correlationId = details.correlationId;
    this.fieldErrors = details.fieldErrors ?? [];
    this.retryAfterSeconds = details.retryAfterSeconds;
  }
}

export interface ProblemBody {
  code?: string;
  correlationId?: string;
  errors?: FieldError[];
}

export function parseProblem(value: unknown): ProblemBody {
  if (typeof value !== 'object' || value === null) return {};
  const record = value as Record<string, unknown>;
  const errors = Array.isArray(record.errors)
    ? record.errors.flatMap((entry): FieldError[] => {
        const e = entry as Record<string, unknown> | null;
        return e && typeof e.field === 'string' && typeof e.code === 'string'
          ? [{ field: e.field, code: e.code }]
          : [];
      })
    : undefined;
  return {
    code: typeof record.code === 'string' ? record.code : undefined,
    correlationId: typeof record.correlationId === 'string' ? record.correlationId : undefined,
    errors,
  };
}

// Kiota's ApiError carries the status; the problem fields are in `additionalData`.
function retryAfterOf(headers: unknown): number | undefined {
  if (typeof headers !== 'object' || headers === null) return undefined;
  const entry = Object.entries(headers).find(([name]) => name.toLowerCase() === 'retry-after');
  const raw = Array.isArray(entry?.[1]) ? entry[1][0] : entry?.[1];
  const seconds = typeof raw === 'string' && /^\d+$/.test(raw.trim()) ? Number(raw.trim()) : undefined;
  return seconds;
}

const CONFLICT_KINDS: Record<string, PlatformFailureKind> = {
  'season-archived': 'seasonArchived',
  'upload-session-expired': 'uploadExpired',
  'upload-session-completed': 'uploadCompleted',
  'upload-parts-incomplete': 'uploadPartsRefused',
  'upload-part-mismatch': 'uploadPartsRefused',
  'upload-object-mismatch': 'uploadIntegrityFailed',
  'integrity-evidence-unavailable': 'uploadIntegrityFailed',
  'idempotency-key-reused': 'programmingError',
};

function problemOf(error: unknown): { status?: number; problem: ProblemBody; retryAfterSeconds?: number } {
  const e = error as { responseStatusCode?: unknown; additionalData?: unknown; responseHeaders?: unknown };
  const status = typeof e.responseStatusCode === 'number' ? e.responseStatusCode : undefined;
  const data = (e.additionalData ?? {}) as Record<string, unknown>;
  return { status, problem: parseProblem(data), retryAfterSeconds: retryAfterOf(e.responseHeaders) };
}

const DECISION_CONFLICT_CODES = ['invalid-state-transition', 'analyst-manager-registration-expired'];
const QUERY_FIELDS = ['state', 'continuationToken'];

export function mapProblem(status: number | undefined, problem: ProblemBody, retryAfterSeconds?: number): PlatformFailure {
  const details: FailureDetails = {
    status,
    code: problem.code,
    correlationId: problem.correlationId,
    fieldErrors: problem.errors,
    retryAfterSeconds,
  };
  if (status === undefined) return new PlatformFailure('incompatible', details);
  if (status === 401) return new PlatformFailure('sessionEnded', details);
  if (status === 403) {
    if (problem.code === 'password-change-required') return new PlatformFailure('passwordChangeRequired', details);
    if (problem.code === 'antiforgery-failed') return new PlatformFailure('sessionProblem', details);
    return new PlatformFailure('notPermitted', details);
  }
  if (status === 400) {
    if (problem.code === 'idempotency-key-missing') return new PlatformFailure('programmingError', details);
    if (problem.code === 'analyst-manager-pairing-code-invalid') return new PlatformFailure('pairingRefused', details);
    if (problem.errors?.some((e) => QUERY_FIELDS.includes(e.field))) return new PlatformFailure('programmingError', details);
    return new PlatformFailure('validation', details);
  }
  if (status === 429 && problem.code === 'analyst-manager-rate-limited') return new PlatformFailure('rateLimited', details);
  if (status === 409) {
    if (problem.code && DECISION_CONFLICT_CODES.includes(problem.code)) return new PlatformFailure('decisionConflict', details);
    const kind = CONFLICT_KINDS[problem.code ?? ''];
    return new PlatformFailure(kind ?? 'conflict', details);
  }
  if (status === 412) return new PlatformFailure('conflict', details);
  if (status === 413) return new PlatformFailure('tooLarge', details);
  if (status === 415) return new PlatformFailure('unsupportedType', details);
  if (status === 428) return new PlatformFailure('programmingError', details);
  if (status >= 502 && status <= 504) return new PlatformFailure('unreachable', details);
  if (status >= 500) return new PlatformFailure('unexpected', details);
  return new PlatformFailure('incompatible', details);
}

export function toPlatformFailure(error: unknown): PlatformFailure {
  if (error instanceof PlatformFailure) return error;
  if (error instanceof TypeError) return new PlatformFailure('unreachable');
  if (typeof error === 'object' && error !== null && 'responseStatusCode' in error) {
    const { status, problem, retryAfterSeconds } = problemOf(error);
    return mapProblem(status, problem, retryAfterSeconds);
  }
  return new PlatformFailure('incompatible');
}
