import type { Guid, RequestInformation } from '@microsoft/kiota-abstractions';
import { RetryHandlerOptions } from '@microsoft/kiota-http-fetchlibrary';
import {
  createInvitationCheckDtoFromDiscriminatorValue,
  createInvitationDtoFromDiscriminatorValue,
  type InvitationCheckDto,
  type InvitationDto,
} from '../../generated/platform-api/models';
import { PlatformFailure, toPlatformFailure, type FieldError } from '../failure';
import type { Invitation, InvitationCheck, InvitationDetail, InvitationTeamRole } from '../models/invitation';
import type { TeamRoleName } from '../models/session';
import { errorMappings, withIfMatch, withJson, type PlatformApi } from './kiota';

/** A refusal the dialog and the public page show to the person; every other failure stays a `PlatformFailure`. */
export type InvitationRefusal =
  | { kind: 'invalid' }
  | { kind: 'emailAddressInUse' }
  | { kind: 'accountNameTaken' }
  | { kind: 'notOpen' }
  | { kind: 'stale' }
  | { kind: 'fieldErrors'; fieldErrors: FieldError[] }
  | { kind: 'rateLimited'; retryAfterSeconds?: number };

export type InvitationResult<T = void> = { ok: true; value: T } | ({ ok: false } & InvitationRefusal);

export interface InvitationAdapter {
  /** Club Admins only. */
  createInvitation(address: string, accountName: string, teamRoles: InvitationTeamRole[]): Promise<InvitationResult<Invitation>>;
  /** Anonymous; returns the club and account name of a usable invitation. */
  checkInvitation(token: string): Promise<InvitationResult<InvitationCheck>>;
  /** Anonymous; never establishes a session. */
  acceptInvitation(token: string, password: string): Promise<InvitationResult>;
  /** Club Admins only; newest first, optionally of one state. */
  listInvitations(state?: string): Promise<InvitationDetail[]>;
  /** Issues a new link; the invitation version is sent as `If-Match`. */
  resendInvitation(id: string, version: number): Promise<InvitationResult<InvitationDetail>>;
  revokeInvitation(id: string, version: number): Promise<InvitationResult<InvitationDetail>>;
}

const INVALID_CODE = 'invitation-invalid';
const TEAM_ROLES: readonly TeamRoleName[] = ['Coach', 'Viewer'];

// The platform sends kebab-case wire values (`coach`).
const toWire = (role: string) => role.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();
const toRole = (value: string | null | undefined) => TEAM_ROLES.find((r) => r.toLowerCase() === (value ?? '').replace(/-/g, '').toLowerCase());

function refusalOf(failure: PlatformFailure): InvitationRefusal | undefined {
  if (failure.status === 429) return { kind: 'rateLimited', retryAfterSeconds: failure.retryAfterSeconds };
  if (failure.status === 409) {
    if (failure.code === 'email-address-in-use') return { kind: 'emailAddressInUse' };
    if (failure.code === 'account-name-taken') return { kind: 'accountNameTaken' };
    if (failure.code === 'invitation-not-open') return { kind: 'notOpen' };
  }
  if (failure.status === 412) return { kind: 'stale' };
  if (failure.status === 400) {
    if (failure.code === INVALID_CODE) return { kind: 'invalid' };
    if (failure.fieldErrors.length > 0) return { kind: 'fieldErrors', fieldErrors: failure.fieldErrors };
  }
  return undefined;
}

export function toInvitation(dto: InvitationDto | undefined | null): Invitation {
  if (!dto || !dto.id || typeof dto.accountName !== 'string' || typeof dto.address !== 'string' || !dto.state || typeof dto.version !== 'number') {
    throw new PlatformFailure('incompatible');
  }
  return {
    id: dto.id.toString(),
    state: dto.state,
    address: dto.address,
    accountName: dto.accountName,
    teamRoles: (dto.teamRoles ?? []).flatMap((r) => {
      const role = toRole(r.role);
      return r.teamId && role ? [{ teamId: r.teamId.toString(), role }] : [];
    }),
    version: dto.version,
  };
}

export function toInvitationDetail(dto: InvitationDto | undefined | null): InvitationDetail {
  const base = toInvitation(dto);
  if (!dto || !dto.createdAt || !dto.invitedBy?.id || typeof dto.invitedBy.accountName !== 'string') throw new PlatformFailure('incompatible');
  return {
    id: base.id,
    state: base.state,
    address: base.address,
    accountName: base.accountName,
    teamRoles: (dto.teamRoles ?? []).flatMap((r) => {
      const role = toRole(r.role);
      return r.teamId && role ? [{ teamId: r.teamId.toString(), teamName: r.teamName ?? '', role }] : [];
    }),
    invitedBy: { id: dto.invitedBy.id.toString(), accountName: dto.invitedBy.accountName },
    createdAt: dto.createdAt,
    linkExpiresAt: dto.linkExpiresAt ?? undefined,
    deliveryState: dto.deliveryState ?? undefined,
    resendCount: dto.resendCount ?? 0,
    revokedAt: dto.revokedAt ?? undefined,
    acceptedAt: dto.acceptedAt ?? undefined,
    acceptedMemberId: dto.acceptedMemberId?.toString(),
    version: base.version,
  };
}

export function createInvitationAdapter(api: PlatformApi): InvitationAdapter {
  // A 429 must reach the person with its wait time instead of being retried silently.
  const noRetry = (info: RequestInformation) => {
    info.addRequestOptions([new RetryHandlerOptions({ maxRetries: 0 })]);
    return info;
  };

  const attempt = async <T>(send: () => Promise<T>): Promise<InvitationResult<T>> => {
    try {
      return { ok: true, value: await send() };
    } catch (error) {
      const failure = toPlatformFailure(error);
      const refusal = refusalOf(failure);
      if (refusal) return { ok: false, ...refusal };
      throw failure;
    }
  };

  const invitations = api.client.api.v1.invitations;

  const operate = (id: string, version: number, operation: 'resend' | 'revoke') =>
    attempt(async () => {
      try {
        const item = invitations.byInvitationId(id as Guid);
        const info = operation === 'resend' ? item.resend.toPostRequestInformation() : item.revoke.toPostRequestInformation();
        return toInvitationDetail(await api.adapter.send(noRetry(withIfMatch(info, version)), createInvitationDtoFromDiscriminatorValue, errorMappings));
      } catch (error) {
        throw toPlatformFailure(error);
      }
    });

  return {
    listInvitations: async (state) => {
      try {
        const info = invitations.toGetRequestInformation(state ? { queryParameters: { state } } : undefined);
        const items = await api.adapter.sendCollection(info, createInvitationDtoFromDiscriminatorValue, errorMappings);
        return (items ?? []).map(toInvitationDetail);
      } catch (error) {
        throw toPlatformFailure(error);
      }
    },
    resendInvitation: (id, version) => operate(id, version, 'resend'),
    revokeInvitation: (id, version) => operate(id, version, 'revoke'),
    createInvitation: (address, accountName, teamRoles) =>
      attempt(async () => {
        try {
          const body = { address, accountName, teamRoles: teamRoles.map((r) => ({ teamId: r.teamId, role: toWire(r.role) })) };
          const dto = await api.adapter.send(
            noRetry(withJson(invitations.toPostRequestInformation(), body)),
            createInvitationDtoFromDiscriminatorValue,
            errorMappings,
          );
          return toInvitation(dto);
        } catch (error) {
          throw toPlatformFailure(error);
        }
      }),
    checkInvitation: (token) =>
      attempt(async () => {
        try {
          const dto = (await api.adapter.send(
            noRetry(withJson(invitations.checks.toPostRequestInformation(), { token })),
            createInvitationCheckDtoFromDiscriminatorValue,
            errorMappings,
          )) as InvitationCheckDto | undefined;
          if (!dto?.clubName || !dto.accountName) throw new PlatformFailure('incompatible');
          return { clubName: dto.clubName, accountName: dto.accountName };
        } catch (error) {
          throw toPlatformFailure(error);
        }
      }),
    acceptInvitation: (token, password) =>
      attempt(async (): Promise<void> => {
        try {
          await api.adapter.sendNoResponseContent(
            noRetry(withJson(invitations.acceptances.toPostRequestInformation(), { token, password })),
            errorMappings,
          );
        } catch (error) {
          throw toPlatformFailure(error);
        }
      }),
  };
}
