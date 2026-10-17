import {
  type Parsable,
  type ParsableFactory,
  type RequestAdapter,
  type RequestInformation,
} from '@microsoft/kiota-abstractions';
import {
  createProblemDetailsFromDiscriminatorValue,
  createCurrentMemberDtoFromDiscriminatorValue,
  createSessionInfoDtoFromDiscriminatorValue,
  createIdentityProvidersDtoFromDiscriminatorValue,
  createExternalAuthorizationDtoFromDiscriminatorValue,
  type CurrentMemberDto,
  type ExternalAuthorizationDto,
  type IdentityProvidersDto,
  type SessionInfoDto,
} from '../../generated/platform-api/models';
import type { PlatformApiClient } from '../../generated/platform-api/platformApiClient';
import { PlatformFailure, toPlatformFailure } from '../failure';
import type { ClubRole, Session, TeamRole, TeamRoleName } from '../models/session';

export interface IdentityProvider {
  id: string;
  displayName: string;
}

export interface SessionAdapter {
  listProviders(): Promise<IdentityProvider[]>;
  /** Returns the provider's authorization URL; the browser then navigates to it. */
  startExternalSignIn(provider: string): Promise<string>;
  signIn(accountName: string, password: string): Promise<Session>;
  restore(): Promise<Session>;
  signOut(): Promise<void>;
  changeOwnPassword(currentPassword: string, newPassword: string): Promise<void>;
  redeemCredential(accountName: string, code: string, newPassword: string): Promise<void>;
}

// Kiota's `XXX` maps every error status; `toPlatformFailure` classifies by status and problem code.
const errorMappings = {
  XXX: createProblemDetailsFromDiscriminatorValue as ParsableFactory<Parsable>,
};

const CLUB_ROLES: readonly ClubRole[] = ['ClubAdmin', 'Registrar'];
const TEAM_ROLES: readonly TeamRoleName[] = ['Coach', 'Viewer'];

// The platform sends kebab-case wire values (`club-admin`, `coach`).
const normalizeRole = (value: string | null | undefined) => (value ?? '').replace(/-/g, '').toLowerCase();
const findRole = <T extends string>(roles: readonly T[], value: string | null | undefined): T | undefined =>
  roles.find((role) => role.toLowerCase() === normalizeRole(value));

export function toSession(dto: SessionInfoDto | undefined, teamRoleSource?: CurrentMemberDto): Session {
  const member = dto?.member;
  if (
    !dto ||
    !member ||
    !dto.sessionId ||
    !dto.antiforgeryToken ||
    !dto.idleExpiresAt ||
    !dto.absoluteExpiresAt ||
    !member.id ||
    !member.accountName ||
    typeof dto.passwordChangeRequired !== 'boolean'
  ) {
    throw new PlatformFailure('incompatible');
  }
  const teamRoles = (teamRoleSource?.teamRoles ?? member.teamRoles ?? []).flatMap((r): TeamRole[] => {
    const role = findRole(TEAM_ROLES, r.role);
    return r.teamId && r.seasonId && r.teamName !== undefined && r.teamName !== null && role
      ? [{ teamId: r.teamId.toString(), teamName: r.teamName, seasonId: r.seasonId.toString(), role }]
      : [];
  });
  return {
    sessionId: dto.sessionId.toString(),
    idleExpiresAt: dto.idleExpiresAt,
    absoluteExpiresAt: dto.absoluteExpiresAt,
    antiforgeryToken: dto.antiforgeryToken,
    passwordChangeRequired: dto.passwordChangeRequired,
    member: {
      id: member.id.toString(),
      accountName: member.accountName,
      membershipStatus: member.membershipStatus ?? '',
      clubRoles: (member.clubRoles ?? []).flatMap((r) => findRole(CLUB_ROLES, r) ?? []),
      teamRoles,
    },
  };
}

export function createSessionAdapter(api: { adapter: RequestAdapter; client: PlatformApiClient }): SessionAdapter {
  const { adapter, client } = api;

  const withJson = (info: RequestInformation, body: unknown) => {
    info.setStreamContent(new TextEncoder().encode(JSON.stringify(body)).buffer as ArrayBuffer, 'application/json');
    return info;
  };

  // The session response carries no team roles; they come from the member's own profile.
  const sendSession = async (info: RequestInformation) => {
    try {
      const dto = (await adapter.send(info, createSessionInfoDtoFromDiscriminatorValue, errorMappings)) as SessionInfoDto | undefined;
      const me = dto && dto.passwordChangeRequired === false
        ? ((await adapter.send(client.api.v1.me.toGetRequestInformation(), createCurrentMemberDtoFromDiscriminatorValue, errorMappings)) as CurrentMemberDto | undefined)
        : undefined;
      return toSession(dto, me);
    } catch (error) {
      throw toPlatformFailure(error);
    }
  };

  const sendNoContent = async (info: RequestInformation) => {
    try {
      await adapter.sendNoResponseContent(info, errorMappings);
    } catch (error) {
      throw toPlatformFailure(error);
    }
  };

  return {
    listProviders: async () => {
      try {
        const dto = (await adapter.send(client.api.v1.identityProviders.toGetRequestInformation(), createIdentityProvidersDtoFromDiscriminatorValue, errorMappings)) as IdentityProvidersDto | undefined;
        return (dto?.providers ?? []).flatMap((p): IdentityProvider[] => (p.id && p.displayName ? [{ id: p.id, displayName: p.displayName }] : []));
      } catch (error) {
        throw toPlatformFailure(error);
      }
    },
    startExternalSignIn: async (provider) => {
      try {
        const dto = (await adapter.send(
          withJson(client.api.v1.session.external.toPostRequestInformation(), { provider }),
          createExternalAuthorizationDtoFromDiscriminatorValue,
          errorMappings,
        )) as ExternalAuthorizationDto | undefined;
        if (!dto?.authorizationUrl) throw new PlatformFailure('incompatible');
        return dto.authorizationUrl;
      } catch (error) {
        throw toPlatformFailure(error);
      }
    },
    signIn: (accountName, password) =>
      sendSession(withJson(client.api.v1.session.toPostRequestInformation(), { accountName, password })),
    restore: () => sendSession(client.api.v1.session.toGetRequestInformation()),
    signOut: () => sendNoContent(client.api.v1.session.toDeleteRequestInformation()),
    changeOwnPassword: (currentPassword, newPassword) =>
      sendNoContent(withJson(client.api.v1.me.password.toPostRequestInformation(), { currentPassword, newPassword })),
    redeemCredential: (accountName, code, newPassword) =>
      sendNoContent(
        withJson(client.api.v1.credentials.redeem.toPostRequestInformation(), {
          accountName,
          credential: code,
          newPassword,
        }),
      ),
  };
}
