import { describe, expect, it } from 'vitest';
import { createInvitationAdapter } from '../../../../src/platform/adapters/invitationAdapter';
import { PlatformFailure } from '../../../../src/platform/failure';
import { bodyOf, ids, ok, problem, setup } from './support';

const dto = {
  id: '00000000-0000-4000-8000-0000000000e1',
  state: 'pending',
  address: 'new@example.org',
  accountName: 'jdoe',
  teamRoles: [{ teamId: ids.team, teamName: 'U17', role: 'coach' }],
  invitedBy: { id: '00000000-0000-4000-8000-0000000000e2', accountName: 'admin' },
  createdAt: '2026-10-17T20:00:00Z',
  linkIssuedAt: '2026-10-17T20:00:05Z',
  linkExpiresAt: '2026-10-24T20:00:05Z',
  deliveryState: 'queued',
  resendCount: 0,
  revokedAt: null,
  acceptedAt: null,
  acceptedMemberId: null,
  version: 1,
};

describe('invitationAdapter', () => {
  it('creates an invitation with kebab-case roles', async () => {
    const { api, calls } = setup(() => ok(201, dto));
    const result = await createInvitationAdapter(api).createInvitation('new@example.org', 'jdoe', [{ teamId: ids.team, role: 'Coach' }]);
    expect(result).toEqual({
      ok: true,
      value: { id: dto.id, state: 'pending', address: 'new@example.org', accountName: 'jdoe', teamRoles: [{ teamId: ids.team, role: 'Coach' }], version: 1 },
    });
    expect(calls[0].url).toContain('/api/v1/invitations');
    expect(calls[0].init.method).toBe('POST');
    expect(bodyOf(calls[0])).toEqual({ address: 'new@example.org', accountName: 'jdoe', teamRoles: [{ teamId: ids.team, role: 'coach' }] });
  });

  it('maps the two conflicts and field violations', async () => {
    const adapter = (r: () => Response) => createInvitationAdapter(setup(r).api);
    expect(await adapter(() => problem(409, 'email-address-in-use')).createInvitation('a@b.c', 'x', [])).toEqual({ ok: false, kind: 'emailAddressInUse' });
    expect(await adapter(() => problem(409, 'account-name-taken')).createInvitation('a@b.c', 'x', [])).toEqual({ ok: false, kind: 'accountNameTaken' });
    expect(
      await adapter(() => problem(400, 'validation-failed', [{ field: 'address', code: 'invalid' }])).createInvitation('nope', 'x', []),
    ).toEqual({ ok: false, kind: 'fieldErrors', fieldErrors: [{ field: 'address', code: 'invalid' }] });
  });

  it('rethrows other conflicts and failures', async () => {
    const { api } = setup(() => problem(409, 'something-else'));
    await expect(createInvitationAdapter(api).createInvitation('a@b.c', 'x', [])).rejects.toBeInstanceOf(PlatformFailure);
  });

  it('returns club and account name of a usable invitation', async () => {
    const { api, calls } = setup(() => ok(200, { clubName: 'FC Example', accountName: 'jdoe' }));
    const result = await createInvitationAdapter(api).checkInvitation('tok');
    expect(result).toEqual({ ok: true, value: { clubName: 'FC Example', accountName: 'jdoe' } });
    expect(calls[0].url).toContain('/api/v1/invitations/checks');
    expect(bodyOf(calls[0])).toEqual({ token: 'tok' });
  });

  it('treats an incomplete check response as incompatible', async () => {
    const { api } = setup(() => ok(200, {}));
    await expect(createInvitationAdapter(api).checkInvitation('tok')).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('maps invitation-invalid for check and acceptance', async () => {
    const adapter = createInvitationAdapter(setup(() => problem(400, 'invitation-invalid')).api);
    expect(await adapter.checkInvitation('tok')).toEqual({ ok: false, kind: 'invalid' });
    expect(await adapter.acceptInvitation('tok', 'Secret-123')).toEqual({ ok: false, kind: 'invalid' });
  });

  it('accepts and maps password policy violations', async () => {
    const first = setup(() => ok(204));
    expect((await createInvitationAdapter(first.api).acceptInvitation('tok', 'Secret-123')).ok).toBe(true);
    expect(first.calls[0].url).toContain('/api/v1/invitations/acceptances');
    expect(bodyOf(first.calls[0])).toEqual({ token: 'tok', password: 'Secret-123' });

    const second = setup(() => problem(400, 'validation-failed', [{ field: 'password', code: 'password-policy' }]));
    expect(await createInvitationAdapter(second.api).acceptInvitation('tok', 'x')).toEqual({
      ok: false,
      kind: 'fieldErrors',
      fieldErrors: [{ field: 'password', code: 'password-policy' }],
    });
  });

  it('maps 429 with its wait time and does not retry it', async () => {
    const { api, calls } = setup(
      () =>
        new Response(JSON.stringify({ status: 429, code: 'invitation-rate-limited' }), {
          status: 429,
          headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '42' },
        }),
    );
    expect(await createInvitationAdapter(api).checkInvitation('tok')).toEqual({ ok: false, kind: 'rateLimited', retryAfterSeconds: 42 });
    expect(calls).toHaveLength(1);
  });
});
