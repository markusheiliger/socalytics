import {
  createAnalystManagerRegistrationDtoFromDiscriminatorValue,
  createAnalystManagerRegistrationPageDtoFromDiscriminatorValue,
  type AnalystManagerRegistrationDto,
  type AnalystManagerRegistrationPageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type {
  ApplicationProfile,
  Connectivity,
  KeyProtection,
  OperatingState,
  Registration,
  RegistrationPage,
  RegistrationState,
} from '../models/registrations';
import { sendFor, withJson, type PlatformApi } from './kiota';

export interface RegistrationAdapter {
  submitPairingCode(code: string, fingerprint: string): Promise<Registration>;
  list(input?: { state?: RegistrationState; continuationToken?: string }): Promise<RegistrationPage>;
  listAll(input?: { state?: RegistrationState }): Promise<Registration[]>;
  get(id: string): Promise<Registration>;
  approve(id: string): Promise<Registration>;
  reject(id: string, reason: string): Promise<Registration>;
  revoke(id: string, reason: string): Promise<Registration>;
}

const STATE_WIRE: Record<Exclude<RegistrationState, 'unknown'>, string> = {
  pairing: 'pairing',
  pendingApproval: 'pending_approval',
  approved: 'approved',
  active: 'active',
  unregistering: 'unregistering',
  unregistered: 'unregistered',
  rejected: 'rejected',
  expired: 'expired',
  revoked: 'revoked',
};
const KEY_PROTECTIONS: readonly KeyProtection[] = ['tpm', 'secure-enclave', 'pkcs11-token', 'software'];
const APPLICATION_PROFILES: readonly ApplicationProfile[] = ['tray', 'headless-server'];
const OPERATING_WIRE: Record<string, OperatingState> = {
  running: 'running',
  paused: 'paused',
  'runtime-unavailable': 'runtimeUnavailable',
  draining: 'draining',
};
const CONNECTIVITY_WIRE: Record<string, Connectivity> = {
  online: 'online',
  offline: 'offline',
  'not-applicable': 'notApplicable',
};

function lookup<T>(table: Record<string, T>, value: string | null | undefined): T | undefined {
  return value && Object.hasOwn(table, value) ? table[value] : undefined;
}

const text = (value: string | null | undefined) => value ?? undefined;
const ident = (value: Guid | string | null | undefined) => (value ? value.toString() : undefined);
const date = (value: Date | null | undefined) => value ?? undefined;

function toState(wire: string | null | undefined): RegistrationState {
  const entry = Object.entries(STATE_WIRE).find(([, w]) => w === wire);
  return (entry?.[0] as RegistrationState | undefined) ?? 'unknown';
}

export function toRegistration(dto: AnalystManagerRegistrationDto | undefined | null): Registration {
  if (!dto || !dto.registrationId || typeof dto.deviceFingerprint !== 'string' || typeof dto.version !== 'number') {
    throw new PlatformFailure('incompatible');
  }
  const meta = dto.deviceMetadata;
  const status = dto.status;
  const origin = dto.pairingOrigin;
  return {
    registrationId: dto.registrationId.toString(),
    managerId: ident(dto.managerId),
    version: dto.version,
    state: toState(dto.state),
    deviceFingerprint: dto.deviceFingerprint,
    deviceMetadata: {
      os: text(meta?.os),
      osVersion: text(meta?.osVersion),
      architecture: text(meta?.architecture),
      managerVersion: text(meta?.managerVersion),
      claimedKeyProtection: KEY_PROTECTIONS.find((k) => k === meta?.claimedKeyProtection) ?? 'unknown',
      claimedApplicationProfile: APPLICATION_PROFILES.find((p) => p === meta?.claimedApplicationProfile),
      hostLabel: text(meta?.hostLabel),
    },
    pairingOrigin: origin ? { sourceAddress: text(origin.sourceAddress), requestedAt: date(origin.requestedAt) } : undefined,
    approvalNotice: text(dto.approvalNotice),
    submittedBy: ident(dto.submittedBy),
    submittedAt: date(dto.submittedAt),
    decidedBy: ident(dto.decidedBy),
    decidedAt: date(dto.decidedAt),
    decisionReason: text(dto.decisionReason),
    revokedBy: ident(dto.revokedBy),
    endedAt: date(dto.endedAt),
    endReason: text(dto.endReason),
    activatedAt: date(dto.activatedAt),
    createdAt: date(dto.createdAt),
    pairingExpiresAt: date(dto.pairingExpiresAt),
    approvalExpiresAt: date(dto.approvalExpiresAt),
    activationExpiresAt: date(dto.activationExpiresAt),
    status: status
      ? {
          operatingState: lookup(OPERATING_WIRE, status.operatingState) ?? 'unknown',
          pausedUntil: date(status.pausedUntil),
          runtimeUnavailableReason: text(status.runtimeUnavailableReason),
          activeAnalysts: status.activeAnalysts ?? undefined,
          maxAnalysts: status.maxAnalysts ?? undefined,
          managerVersion: text(status.managerVersion),
          reportedAt: date(status.reportedAt),
        }
      : undefined,
    connectivity: dto.connectivity ? (lookup(CONNECTIVITY_WIRE, dto.connectivity) ?? 'unknown') : undefined,
  };
}

export function createRegistrationAdapter(api: PlatformApi): RegistrationAdapter {
  const registrations = api.client.api.v1.analystManagerRegistrations;
  const item = (registrationId: string) => registrations.byRegistrationId(registrationId as Guid);
  const factory = createAnalystManagerRegistrationDtoFromDiscriminatorValue;
  const pageFactory =
    createAnalystManagerRegistrationPageDtoFromDiscriminatorValue as ParsableFactory<AnalystManagerRegistrationPageDto>;
  const one = async (info: Parameters<typeof sendFor>[1]) => toRegistration(await sendFor(api, info, factory));

  const list: RegistrationAdapter['list'] = async (input = {}) => {
    const queryParameters: { state?: string; continuationToken?: string } = {};
    if (input.state) {
      const wire = input.state === 'unknown' ? undefined : STATE_WIRE[input.state];
      if (!wire) throw new PlatformFailure('programmingError');
      queryParameters.state = wire;
    }
    if (input.continuationToken) queryParameters.continuationToken = input.continuationToken;
    const page = await sendFor<AnalystManagerRegistrationPageDto>(
      api,
      registrations.toGetRequestInformation({ queryParameters }),
      pageFactory,
    );
    return { items: (page.items ?? []).map(toRegistration), continuationToken: page.continuationToken ?? undefined };
  };

  return {
    submitPairingCode: async (code, fingerprint) => {
      try {
        return await one(withJson(registrations.toPostRequestInformation({}), { pairingCode: code, deviceFingerprint: fingerprint }));
      } catch (error) {
        if (error instanceof PlatformFailure && error.status === 404) {
          throw new PlatformFailure('pairingRefused', { status: 404, code: error.code, correlationId: error.correlationId });
        }
        throw error;
      }
    },
    list,
    listAll: async (input = {}) => {
      const result: Registration[] = [];
      let token: string | undefined;
      do {
        const page = await list({ ...input, continuationToken: token });
        result.push(...page.items);
        token = page.continuationToken;
      } while (token);
      return result;
    },
    get: (id) => one(item(id).toGetRequestInformation()),
    approve: (id) => one(item(id).approval.toPostRequestInformation()),
    reject: (id, reason) => one(withJson(item(id).rejection.toPostRequestInformation({}), { reason })),
    revoke: (id, reason) => one(withJson(item(id).revocation.toPostRequestInformation({}), { reason })),
  };
}
