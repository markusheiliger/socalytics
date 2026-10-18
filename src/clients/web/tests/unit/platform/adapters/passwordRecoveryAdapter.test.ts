import { describe, expect, it } from 'vitest';
import { createPasswordRecoveryAdapter } from '../../../../src/platform/adapters/passwordRecoveryAdapter';
import { PlatformFailure } from '../../../../src/platform/failure';
import { bodyOf, ok, problem, setup } from './support';

describe('passwordRecoveryAdapter', () => {
  it('requests a recovery for an address', async () => {
    const { api, calls } = setup(() => ok(202));
    const result = await createPasswordRecoveryAdapter(api).requestRecovery('member@example.org');
    expect(result.ok).toBe(true);
    expect(calls[0].url).toContain('/api/v1/password-recovery/requests');
    expect(calls[0].init.method).toBe('POST');
    expect(bodyOf(calls[0])).toEqual({ address: 'member@example.org' });
  });

  it('maps address field violations', async () => {
    const { api } = setup(() => problem(400, 'validation-failed', [{ field: 'address', code: 'invalid' }]));
    const result = await createPasswordRecoveryAdapter(api).requestRecovery('nope');
    expect(result).toEqual({ ok: false, kind: 'fieldErrors', fieldErrors: [{ field: 'address', code: 'invalid' }] });
  });

  it('returns the account name of a usable recovery', async () => {
    const { api, calls } = setup(() => ok(200, { accountName: 'jdoe' }));
    const result = await createPasswordRecoveryAdapter(api).checkRecovery('tok');
    expect(result).toEqual({ ok: true, value: 'jdoe' });
    expect(calls[0].url).toContain('/api/v1/password-recovery/checks');
    expect(bodyOf(calls[0])).toEqual({ token: 'tok' });
  });

  it('treats a check response without an account name as incompatible', async () => {
    const { api } = setup(() => ok(200, {}));
    await expect(createPasswordRecoveryAdapter(api).checkRecovery('tok')).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('maps password-recovery-invalid for check and completion', async () => {
    const { api } = setup(() => problem(400, 'password-recovery-invalid'));
    const adapter = createPasswordRecoveryAdapter(api);
    expect(await adapter.checkRecovery('tok')).toEqual({ ok: false, kind: 'invalid' });
    expect(await adapter.completeRecovery('tok', 'Secret-123')).toEqual({ ok: false, kind: 'invalid' });
  });

  it('completes a recovery and maps password policy violations', async () => {
    const first = setup(() => ok(204));
    const done = await createPasswordRecoveryAdapter(first.api).completeRecovery('tok', 'Secret-123');
    expect(done.ok).toBe(true);
    expect(first.calls[0].url).toContain('/api/v1/password-recovery/completions');
    expect(bodyOf(first.calls[0])).toEqual({ token: 'tok', newPassword: 'Secret-123' });

    const second = setup(() => problem(400, 'validation-failed', [{ field: 'newPassword', code: 'password-policy' }]));
    expect(await createPasswordRecoveryAdapter(second.api).completeRecovery('tok', 'x')).toEqual({
      ok: false,
      kind: 'fieldErrors',
      fieldErrors: [{ field: 'newPassword', code: 'password-policy' }],
    });
  });

  it('maps 429 with its wait time and does not retry it', async () => {
    const { api, calls } = setup(
      () =>
        new Response(JSON.stringify({ status: 429, code: 'password-recovery-rate-limited' }), {
          status: 429,
          headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '42' },
        }),
    );
    const result = await createPasswordRecoveryAdapter(api).requestRecovery('member@example.org');
    expect(result).toEqual({ ok: false, kind: 'rateLimited', retryAfterSeconds: 42 });
    expect(calls).toHaveLength(1);
  });

  it('rethrows other failures', async () => {
    const { api } = setup(() => problem(500, 'unexpected'));
    await expect(createPasswordRecoveryAdapter(api).requestRecovery('a@b.c')).rejects.toBeInstanceOf(PlatformFailure);
  });
});
