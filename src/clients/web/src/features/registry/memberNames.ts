import { createMemberAdapter } from '../../platform/adapters/memberAdapter';
import type { PlatformApi } from '../../platform/adapters/kiota';
import type { Member } from '../../platform/models/member';
import type { MemberRef } from '../../platform/models/registry';

/** An empty id is the development seed, an id without a member is unknown (and keeps its id). */
export function memberRefFor(id: string | undefined, members: readonly Pick<Member, 'id' | 'accountName'>[]): MemberRef {
  if (!id) return { id: '', kind: 'seed' };
  const member = members.find((m) => m.id.toLowerCase() === id.toLowerCase());
  return member ? { id, displayName: member.accountName, kind: 'member' } : { id, kind: 'unknown' };
}

export function resolveMemberRef(ref: MemberRef, members: readonly Pick<Member, 'id' | 'accountName'>[]): MemberRef {
  return memberRefFor(ref.id, members);
}

export async function loadMemberResolver(platform: PlatformApi): Promise<(id: string | undefined) => MemberRef> {
  const members = await createMemberAdapter(platform).list();
  return (id) => memberRefFor(id, members);
}
