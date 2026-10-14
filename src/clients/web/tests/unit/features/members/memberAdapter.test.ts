import { createPlatformApi } from '../../../../src/platform/client';
import { createMemberAdapter } from '../../../../src/platform/adapters/memberAdapter';

const MEMBER = '00000000-0000-4000-8000-000000000021';
const TEAM = '00000000-0000-4000-8000-000000000003';
const member = (over: Record<string, unknown> = {}) => ({
  id: MEMBER,
  accountName: 'ada',
  membershipStatus: 'active',
  passwordSet: false,
  lockedOut: false,
  lockoutEndsAt: null,
  twoFactorEnabled: false,
  clubRoles: ['club-admin'],
  teamRoles: [{ teamId: TEAM, role: 'coach' }],
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
  ...over,
});
const credential = { memberId: MEMBER, purpose: 'set-password', credential: 'CODE-1234', expiresAt: '2026-10-13T12:00:00Z' };

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });

function setup(respond: (url: string, init: RequestInit) => Response) {
  const calls: { url: string; init: RequestInit }[] = [];
  const api = createPlatformApi({
    getAntiforgeryToken: () => 'csrf-1',
    onSessionOutcome: () => {},
    onNetworkFailure: () => {},
    fetch: async (url, init) => {
      calls.push({ url, init });
      return respond(url, init);
    },
  });
  return { adapter: createMemberAdapter(api), calls };
}

const route = (c: { url: string; init: RequestInit }) => `${c.init.method} ${new URL(c.url).pathname}`;
const body = (c: { init: RequestInit }) => new TextDecoder().decode(c.init.body as ArrayBuffer);

describe('memberAdapter', () => {
  it('lists across pages and maps wire values', async () => {
    const { adapter, calls } = setup((url) =>
      url.includes('continuationToken=t2')
        ? json(200, { items: [member({ id: '00000000-0000-4000-8000-000000000022', membershipStatus: 'deactivated' })], continuationToken: null })
        : json(200, { items: [member()], continuationToken: 't2' }),
    );
    const list = await adapter.list();
    expect(list.map((m) => m.status)).toEqual(['active', 'deactivated']);
    expect(list[0].clubRoles).toEqual(['ClubAdmin']);
    expect(list[0].teamRoles).toEqual([{ teamId: TEAM, role: 'Coach' }]);
    expect(calls).toHaveLength(2);
  });

  it('creates a member with its first credential', async () => {
    const { adapter, calls } = setup(() => json(201, { member: member(), setPasswordCredential: credential }));
    const result = await adapter.create({ accountName: 'ada' });
    expect(result.credential.code).toBe('CODE-1234');
    expect(route(calls[0])).toBe('POST /api/v1/members');
    expect(body(calls[0])).toBe(JSON.stringify({ accountName: 'ada' }));
  });

  it('issues a set-password credential', async () => {
    const { adapter, calls } = setup(() => json(201, credential));
    expect((await adapter.issueCredential(MEMBER)).code).toBe('CODE-1234');
    expect(route(calls[0])).toBe(`POST /api/v1/members/${MEMBER}/credentials`);
    expect(body(calls[0])).toBe(JSON.stringify({ purpose: 'set-password' }));
  });

  it('calls the lifecycle and role operations', async () => {
    const { adapter, calls } = setup((_url, init) => (init.method === 'DELETE' && _url.endsWith('/sessions') ? new Response(null, { status: 204 }) : json(200, member())));
    await adapter.deactivate(MEMBER);
    await adapter.reactivate(MEMBER);
    await adapter.unlock(MEMBER);
    await adapter.endSessions(MEMBER);
    await adapter.assignClubRole(MEMBER, 'ClubAdmin');
    await adapter.removeClubRole(MEMBER, 'Registrar');
    await adapter.assignTeamRole(MEMBER, TEAM, 'Viewer');
    await adapter.removeTeamRole(MEMBER, TEAM);
    const base = `/api/v1/members/${MEMBER}`;
    expect(calls.map(route)).toEqual([
      `POST ${base}/deactivate`,
      `POST ${base}/reactivate`,
      `POST ${base}/unlock`,
      `DELETE ${base}/sessions`,
      `PUT ${base}/club-roles/club-admin`,
      `DELETE ${base}/club-roles/registrar`,
      `PUT ${base}/team-roles/${TEAM}`,
      `DELETE ${base}/team-roles/${TEAM}`,
    ]);
    expect(body(calls[6])).toBe(JSON.stringify({ role: 'viewer' }));
  });

  it('maps refusals and unknown shapes', async () => {
    const refused = setup(() => json(409, { status: 409, code: 'last-club-admin' }));
    await expect(refused.adapter.removeClubRole(MEMBER, 'ClubAdmin')).rejects.toMatchObject({ kind: 'conflict', code: 'last-club-admin' });
    const odd = setup(() => json(200, { id: MEMBER }));
    await expect(odd.adapter.get(MEMBER)).rejects.toMatchObject({ kind: 'incompatible' });
  });
});
