import { createPlatformApi } from '../../../src/platform/client';
import { createSessionAdapter } from '../../../src/platform/adapters/sessionAdapter';

const sessionBody = {
  sessionId: '00000000-0000-4000-8000-000000000004',
  idleExpiresAt: '2030-01-01T00:00:00Z',
  absoluteExpiresAt: '2030-01-02T00:00:00Z',
  passwordChangeRequired: false,
  antiforgeryToken: 'csrf-9',
  member: {
    id: '00000000-0000-4000-8000-000000000001',
    accountName: 'ada',
    membershipStatus: 'Active',
    passwordChangeRequired: false,
    clubRoles: ['ClubAdmin', 'Unknown'],
    teamRoles: [{ teamId: '00000000-0000-4000-8000-0000000000a1', teamName: 'U12', seasonId: '00000000-0000-4000-8000-0000000000b1', role: 'Coach' }],
  },
};

function setup(respond: (url: string, init: RequestInit) => Response, token?: string) {
  const calls: { url: string; init: RequestInit }[] = [];
  const api = createPlatformApi({
    getAntiforgeryToken: () => token,
    onSessionOutcome: () => {},
    onNetworkFailure: () => {},
    fetch: async (url, init) => {
      calls.push({ url, init });
      return respond(url, init);
    },
  });
  return { adapter: createSessionAdapter(api), calls };
}

const ok = (status: number, body?: unknown) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const problem = (status: number, code: string) =>
  new Response(JSON.stringify({ status, code }), { status, headers: { 'Content-Type': 'application/problem+json' } });

describe('sessionAdapter', () => {
  it('signs in with a JSON body and maps the session to a plain model', async () => {
    const { adapter, calls } = setup(() => ok(200, sessionBody));
    const session = await adapter.signIn('ada', 'pw');
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/session`);
    expect(calls[0].init.method).toBe('POST');
    expect(JSON.parse(new TextDecoder().decode(calls[0].init.body as ArrayBuffer))).toEqual({ accountName: 'ada', password: 'pw' });
    expect(session.antiforgeryToken).toBe('csrf-9');
    expect(session.member.clubRoles).toEqual(['ClubAdmin']);
    expect(session.member.teamRoles[0]).toEqual({ teamId: '00000000-0000-4000-8000-0000000000a1', teamName: 'U12', seasonId: '00000000-0000-4000-8000-0000000000b1', role: 'Coach' });
    expect(session.idleExpiresAt).toBeInstanceOf(Date);
  });

  it('restores the session', async () => {
    const { adapter, calls } = setup(() => ok(200, sessionBody));
    await adapter.restore();
    expect(calls[0].init.method).toBe('GET');
  });

  it('maps failures to PlatformFailure kinds', async () => {
    await expect(setup(() => problem(401, 'sign-in-failed')).adapter.signIn('a', 'b')).rejects.toMatchObject({ kind: 'sessionEnded' });
    await expect(setup(() => problem(401, 'unauthenticated')).adapter.restore()).rejects.toMatchObject({ kind: 'sessionEnded' });
    await expect(setup(() => problem(403, 'password-change-required')).adapter.signOut()).rejects.toMatchObject({ kind: 'passwordChangeRequired' });
    await expect(setup(() => problem(403, 'antiforgery-failed')).adapter.signOut()).rejects.toMatchObject({ kind: 'sessionProblem' });
  });

  it('rejects an incomplete session as incompatible', async () => {
    const { adapter } = setup(() => ok(200, { sessionId: sessionBody.sessionId }));
    await expect(adapter.restore()).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('signs out with the anti-forgery token', async () => {
    const { adapter, calls } = setup(() => ok(204), 'csrf-1');
    await adapter.signOut();
    expect(calls[0].init.method).toBe('DELETE');
    expect((calls[0].init.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-1');
  });

  it('changes the own password and maps validation errors', async () => {
    const ok204 = setup(() => ok(204), 'csrf-1');
    await ok204.adapter.changeOwnPassword('old', 'new');
    expect(ok204.calls[0].url).toBe(`${window.location.origin}/api/v1/me/password`);
    expect(JSON.parse(new TextDecoder().decode(ok204.calls[0].init.body as ArrayBuffer))).toEqual({ currentPassword: 'old', newPassword: 'new' });
    await expect(setup(() => problem(400, 'validation-failed')).adapter.changeOwnPassword('a', 'b')).rejects.toMatchObject({ kind: 'validation' });
  });

  it('redeems a credential without a session token', async () => {
    const { adapter, calls } = setup(() => ok(204));
    await adapter.redeemCredential('ada', 'code-1', 'new');
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/credentials/redeem`);
    expect(JSON.parse(new TextDecoder().decode(calls[0].init.body as ArrayBuffer))).toEqual({ accountName: 'ada', credential: 'code-1', newPassword: 'new' });
  });
});
