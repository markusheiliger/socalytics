export type PlatformFailureKind =
  | 'sessionEnded'
  | 'passwordChangeRequired'
  | 'notPermitted'
  | 'validation'
  | 'conflict'
  | 'programmingError'
  | 'sessionProblem'
  | 'unreachable'
  | 'unexpected'
  | 'incompatible';

export interface FieldError {
  field: string;
  code: string;
}

export interface FailureDetails {
  status?: number;
  code?: string;
  correlationId?: string;
  fieldErrors?: FieldError[];
}

export class PlatformFailure extends Error {
  readonly kind: PlatformFailureKind;
  readonly status?: number;
  readonly code?: string;
  readonly correlationId?: string;
  readonly fieldErrors: FieldError[];

  constructor(kind: PlatformFailureKind, details: FailureDetails = {}) {
    super(`Platform request failed: ${kind}`);
    this.name = 'PlatformFailure';
    this.kind = kind;
    this.status = details.status;
    this.code = details.code;
    this.correlationId = details.correlationId;
    this.fieldErrors = details.fieldErrors ?? [];
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
function problemOf(error: unknown): { status?: number; problem: ProblemBody } {
  const e = error as { responseStatusCode?: unknown; additionalData?: unknown };
  const status = typeof e.responseStatusCode === 'number' ? e.responseStatusCode : undefined;
  const data = (e.additionalData ?? {}) as Record<string, unknown>;
  return { status, problem: parseProblem(data) };
}

export function mapProblem(status: number | undefined, problem: ProblemBody): PlatformFailure {
  const details: FailureDetails = {
    status,
    code: problem.code,
    correlationId: problem.correlationId,
    fieldErrors: problem.errors,
  };
  if (status === undefined) return new PlatformFailure('incompatible', details);
  if (status === 401) return new PlatformFailure('sessionEnded', details);
  if (status === 403) {
    if (problem.code === 'password-change-required') return new PlatformFailure('passwordChangeRequired', details);
    if (problem.code === 'antiforgery-failed') return new PlatformFailure('sessionProblem', details);
    return new PlatformFailure('notPermitted', details);
  }
  if (status === 400) return new PlatformFailure('validation', details);
  if (status === 409 || status === 412) return new PlatformFailure('conflict', details);
  if (status === 428) return new PlatformFailure('programmingError', details);
  if (status >= 502 && status <= 504) return new PlatformFailure('unreachable', details);
  if (status >= 500) return new PlatformFailure('unexpected', details);
  return new PlatformFailure('incompatible', details);
}

export function toPlatformFailure(error: unknown): PlatformFailure {
  if (error instanceof PlatformFailure) return error;
  if (error instanceof TypeError) return new PlatformFailure('unreachable');
  if (typeof error === 'object' && error !== null && 'responseStatusCode' in error) {
    const { status, problem } = problemOf(error);
    return mapProblem(status, problem);
  }
  return new PlatformFailure('incompatible');
}
