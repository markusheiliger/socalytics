import { PlatformFailure, toPlatformFailure, type PlatformFailureKind } from '../../../src/platform/failure';

function apiError(status: number, additionalData: Record<string, unknown> = {}) {
  return Object.assign(new Error('api'), { responseStatusCode: status, additionalData });
}

describe('toPlatformFailure', () => {
  const rows: [string, unknown, PlatformFailureKind][] = [
    ['401', apiError(401, { code: 'unauthenticated' }), 'sessionEnded'],
    ['403 password change required', apiError(403, { code: 'password-change-required' }), 'passwordChangeRequired'],
    ['403 other', apiError(403, { code: 'forbidden' }), 'notPermitted'],
    ['403 anti-forgery refusal', apiError(403, { code: 'antiforgery-failed' }), 'sessionProblem'],
    ['400', apiError(400, { code: 'validation-failed' }), 'validation'],
    ['409', apiError(409, { code: 'conflict' }), 'conflict'],
    ['412', apiError(412, { code: 'version-mismatch' }), 'conflict'],
    ['428', apiError(428, { code: 'version-required' }), 'programmingError'],
    ['502', apiError(502), 'unreachable'],
    ['503', apiError(503), 'unreachable'],
    ['504', apiError(504), 'unreachable'],
    ['network failure', new TypeError('Failed to fetch'), 'unreachable'],
    ['500', apiError(500), 'unexpected'],
    ['501', apiError(501), 'unexpected'],
    ['unknown error shape', new Error('x'), 'incompatible'],
    ['non-error value', 'oops', 'incompatible'],
    ['unmapped status', apiError(418), 'incompatible'],
    ['409 season-archived', apiError(409, { code: 'season-archived' }), 'seasonArchived'],
    ['400 upload-declaration-invalid', apiError(400, { code: 'upload-declaration-invalid' }), 'validation'],
    ['413', apiError(413), 'tooLarge'],
    ['415', apiError(415), 'unsupportedType'],
    ['409 upload-session-expired', apiError(409, { code: 'upload-session-expired' }), 'uploadExpired'],
    ['409 upload-session-completed', apiError(409, { code: 'upload-session-completed' }), 'uploadCompleted'],
    ['409 upload-parts-incomplete', apiError(409, { code: 'upload-parts-incomplete' }), 'uploadPartsRefused'],
    ['409 upload-part-mismatch', apiError(409, { code: 'upload-part-mismatch' }), 'uploadPartsRefused'],
    ['409 upload-object-mismatch', apiError(409, { code: 'upload-object-mismatch' }), 'uploadIntegrityFailed'],
    ['409 integrity-evidence-unavailable', apiError(409, { code: 'integrity-evidence-unavailable' }), 'uploadIntegrityFailed'],
    ['400 idempotency-key-missing', apiError(400, { code: 'idempotency-key-missing' }), 'programmingError'],
    ['409 idempotency-key-reused', apiError(409, { code: 'idempotency-key-reused' }), 'programmingError'],
  ];

  it.each(rows)('maps %s', (_name, error, kind) => {
    expect(toPlatformFailure(error).kind).toBe(kind);
  });

  it('keeps field errors, code, status, and correlation reference', () => {
    const failure = toPlatformFailure(
      apiError(400, {
        code: 'validation-failed',
        correlationId: 'abc',
        errors: [{ field: 'name', code: 'required' }, { nope: true }],
      }),
    );
    expect(failure.status).toBe(400);
    expect(failure.code).toBe('validation-failed');
    expect(failure.correlationId).toBe('abc');
    expect(failure.fieldErrors).toEqual([{ field: 'name', code: 'required' }]);
  });

  it('keeps the correlation reference of an unexpected failure', () => {
    expect(toPlatformFailure(apiError(500, { correlationId: 'c-1' })).correlationId).toBe('c-1');
  });

  it('returns an existing PlatformFailure unchanged', () => {
    const failure = new PlatformFailure('conflict');
    expect(toPlatformFailure(failure)).toBe(failure);
  });
});
