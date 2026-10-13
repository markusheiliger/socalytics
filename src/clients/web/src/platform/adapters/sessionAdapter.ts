import {
  type Parsable,
  type ParsableFactory,
  type RequestAdapter,
  type RequestInformation,
} from '@microsoft/kiota-abstractions';
import {
  createProblemDetailsFromDiscriminatorValue,
  createSessionInfoDtoFromDiscriminatorValue,
  type SessionInfoDto,
} from '../../generated/platform-api/models';
import type { PlatformApiClient } from '../../generated/platform-api/platformApiClient';
import { PlatformFailure, toPlatformFailure } from '../failure';
import type { ClubRole, Session, TeamRole, TeamRoleName } from '../models/session';

export interface SessionAdapter {
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

const CLUB_ROLES: readonly string[] = ['ClubAdmin', 'Registrar'];
const TEAM_ROLES: readonly string[] = ['Coach', 'Viewer'];

export function toSession(dto: SessionInfoDto | undefined): Session {
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
  const teamRoles = (member.teamRoles ?? []).flatMap((r): TeamRole[] =>
    r.teamId && r.seasonId && r.teamName !== undefined && r.teamName !== null && TEAM_ROLES.includes(r.role ?? '')
      ? [{ teamId: r.teamId.toString(), teamName: r.teamName, seasonId: r.seasonId.toString(), role: r.role as TeamRoleName }]
      : [],
  );
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
      clubRoles: (member.clubRoles ?? []).filter((r): r is ClubRole => CLUB_ROLES.includes(r)),
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

  const sendSession = async (info: RequestInformation) => {
    try {
      return toSession(await adapter.send(info, createSessionInfoDtoFromDiscriminatorValue, errorMappings));
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
