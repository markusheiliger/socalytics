import { loadMemberResolver, memberRefFor, resolveMemberRef } from '../../../../src/features/registry/memberNames';
import { createPlatformApi } from '../../../../src/platform/client';

const ADA = '00000000-0000-4000-8000-000000000021';
const members = [{ id: ADA, accountName: 'ada' }];

describe('memberRefFor', () => {
  it('resolves a known member to its name', () => {
    expect(memberRefFor(ADA, members)).toEqual({ id: ADA, displayName: 'ada', kind: 'member' });
  });

  it('treats an empty id as the development seed', () => {
    expect(memberRefFor('', members)).toEqual({ id: '', kind: 'seed' });
    expect(memberRefFor(undefined, members)).toEqual({ id: '', kind: 'seed' });
  });

  it('keeps the id of an unknown member', () => {
    expect(memberRefFor('missing', members)).toEqual({ id: 'missing', kind: 'unknown' });
  });

  it('resolves an existing reference', () => {
    expect(resolveMemberRef({ id: ADA, kind: 'member' }, members).displayName).toBe('ada');
  });
});

describe('loadMemberResolver', () => {
  it('lists the members through the member adapter', async () => {
    const urls: string[] = [];
    const api = createPlatformApi({
      getAntiforgeryToken: () => 'csrf-1',
      onSessionOutcome: () => {},
      onNetworkFailure: () => {},
      fetch: async (url) => {
        urls.push(new URL(url).pathname);
        const member = {
          id: ADA, accountName: 'ada', membershipStatus: 'active', passwordSet: true, lockedOut: false,
          twoFactorEnabled: false, clubRoles: ['club-admin'], teamRoles: [], createdAt: '2026-10-12T12:00:00Z', version: 1,
        };
        return new Response(JSON.stringify({ items: [member] }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      },
    });
    const resolve = await loadMemberResolver(api);
    expect(urls).toEqual(['/api/v1/members']);
    expect(resolve(ADA).displayName).toBe('ada');
    expect(resolve('').kind).toBe('seed');
    expect(resolve('other').kind).toBe('unknown');
  });
});
