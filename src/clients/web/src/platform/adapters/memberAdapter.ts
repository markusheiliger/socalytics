import {
  createMemberCreatedDtoFromDiscriminatorValue,
  createMemberDtoFromDiscriminatorValue,
  createMemberPageDtoFromDiscriminatorValue,
  createIssuedCredentialDtoFromDiscriminatorValue,
  type IssuedCredentialDto,
  type MemberCreatedDto,
  type MemberDto,
  type MemberPageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type { CredentialCode, Member, MembershipStatus, MemberTeamRole } from '../models/member';
import type { ClubRole, TeamRoleName } from '../models/session';
import { sendFor, sendNoContentFor, withJson, type PlatformApi } from './kiota';

export interface MemberAdapter {
  list(): Promise<Member[]>;
  get(id: string): Promise<Member>;
  /** The platform also issues the first set-password credential, returned here to show once. */
  create(input: { accountName: string }): Promise<{ member: Member; credential: CredentialCode }>;
  issueCredential(id: string): Promise<CredentialCode>;
  deactivate(id: string): Promise<Member>;
  reactivate(id: string): Promise<Member>;
  unlock(id: string): Promise<Member>;
  endSessions(id: string): Promise<void>;
  assignClubRole(id: string, role: ClubRole): Promise<Member>;
  removeClubRole(id: string, role: ClubRole): Promise<Member>;
  assignTeamRole(id: string, teamId: string, role: TeamRoleName): Promise<Member>;
  removeTeamRole(id: string, teamId: string): Promise<Member>;
}

const STATUSES: readonly MembershipStatus[] = ['active', 'deactivated'];
const CLUB_ROLES: readonly ClubRole[] = ['ClubAdmin', 'Registrar'];
const TEAM_ROLES: readonly TeamRoleName[] = ['Coach', 'Viewer'];

// The platform sends kebab-case wire values (`club-admin`, `coach`).
const normalize = (value: string | null | undefined) => (value ?? '').replace(/-/g, '').toLowerCase();
const find = <T extends string>(all: readonly T[], value: string | null | undefined) => all.find((r) => r.toLowerCase() === normalize(value));
const toWire = (role: string) => role.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();

export function toMember(dto: MemberDto | undefined | null): Member {
  const status = find(STATUSES, dto?.membershipStatus);
  if (
    !dto ||
    !dto.id ||
    typeof dto.accountName !== 'string' ||
    !status ||
    typeof dto.passwordSet !== 'boolean' ||
    typeof dto.lockedOut !== 'boolean' ||
    !dto.createdAt ||
    typeof dto.version !== 'number'
  ) {
    throw new PlatformFailure('incompatible');
  }
  const teamRoles = (dto.teamRoles ?? []).flatMap((r): MemberTeamRole[] => {
    const role = find(TEAM_ROLES, r.role);
    return r.teamId && role ? [{ teamId: r.teamId.toString(), role }] : [];
  });
  return {
    id: dto.id.toString(),
    accountName: dto.accountName,
    status,
    passwordSet: dto.passwordSet,
    lockedOut: dto.lockedOut,
    lockoutEndsAt: dto.lockoutEndsAt ?? undefined,
    twoFactorEnabled: dto.twoFactorEnabled === true,
    clubRoles: (dto.clubRoles ?? []).flatMap((r) => find(CLUB_ROLES, r) ?? []),
    teamRoles,
    createdAt: dto.createdAt,
    version: dto.version,
  };
}

export function toCredentialCode(dto: IssuedCredentialDto | undefined | null): CredentialCode {
  if (!dto || !dto.memberId || !dto.credential || !dto.expiresAt || typeof dto.purpose !== 'string') {
    throw new PlatformFailure('incompatible');
  }
  return { memberId: dto.memberId.toString(), purpose: dto.purpose, code: dto.credential, expiresAt: dto.expiresAt };
}

export function createMemberAdapter(api: PlatformApi): MemberAdapter {
  const members = api.client.api.v1.members;
  const item = (id: string) => members.byMemberId(id as Guid);
  const memberFactory = createMemberDtoFromDiscriminatorValue;
  const pageFactory = createMemberPageDtoFromDiscriminatorValue as ParsableFactory<MemberPageDto>;
  const member = async (info: Parameters<typeof sendFor>[1]) => toMember(await sendFor(api, info, memberFactory));
  return {
    list: async () => {
      const result: Member[] = [];
      let token: string | undefined;
      do {
        const info = members.toGetRequestInformation(token ? { queryParameters: { continuationToken: token } } : undefined);
        const page = await sendFor<MemberPageDto>(api, info, pageFactory);
        result.push(...(page.items ?? []).map(toMember));
        token = page.continuationToken ?? undefined;
      } while (token);
      return result;
    },
    get: (id) => member(item(id).toGetRequestInformation()),
    create: async (input) => {
      const created = await sendFor<MemberCreatedDto>(
        api,
        withJson(members.toPostRequestInformation(), { accountName: input.accountName }),
        createMemberCreatedDtoFromDiscriminatorValue as ParsableFactory<MemberCreatedDto>,
      );
      return { member: toMember(created.member), credential: toCredentialCode(created.setPasswordCredential) };
    },
    issueCredential: async (id) =>
      toCredentialCode(
        await sendFor(
          api,
          withJson(item(id).credentials.toPostRequestInformation(), { purpose: 'set-password' }),
          createIssuedCredentialDtoFromDiscriminatorValue,
        ),
      ),
    deactivate: (id) => member(item(id).deactivate.toPostRequestInformation()),
    reactivate: (id) => member(item(id).reactivate.toPostRequestInformation()),
    unlock: (id) => member(item(id).unlock.toPostRequestInformation()),
    endSessions: (id) => sendNoContentFor(api, item(id).sessions.toDeleteRequestInformation()),
    assignClubRole: (id, role) => member(item(id).clubRoles.byClubRole(toWire(role)).toPutRequestInformation()),
    removeClubRole: (id, role) => member(item(id).clubRoles.byClubRole(toWire(role)).toDeleteRequestInformation()),
    assignTeamRole: (id, teamId, role) =>
      member(withJson(item(id).teamRoles.byTeamId(teamId as Guid).toPutRequestInformation(), { role: toWire(role) })),
    removeTeamRole: (id, teamId) => member(item(id).teamRoles.byTeamId(teamId as Guid).toDeleteRequestInformation()),
  };
}
