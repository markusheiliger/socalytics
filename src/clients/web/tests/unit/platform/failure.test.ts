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
