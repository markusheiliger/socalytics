import type { ParsableFactory, Parsable, RequestInformation } from '@microsoft/kiota-abstractions';
import { RetryHandlerOptions } from '@microsoft/kiota-http-fetchlibrary';
import {
  createEmailAddressDtoFromDiscriminatorValue,
  createNotificationPreferenceDtoFromDiscriminatorValue,
  createNotificationPreferenceListDtoFromDiscriminatorValue,
  type NotificationPreferenceDto,
  type NotificationPreferenceListDto,
  type EmailAddressDto,
} from '../../generated/platform-api/models';
import { PlatformFailure, toPlatformFailure } from '../failure';
import type { EmailAddress, NotificationPreference } from '../models/email';
import { errorMappings, withIfMatch, withJson, type PlatformApi } from './kiota';

export interface EmailAdapter {
  /** `null` when the member has no address. */
  get(): Promise<EmailAddress | null>;
  set(address: string, version?: number): Promise<EmailAddress>;
  remove(version: number): Promise<void>;
  resend(): Promise<void>;
  /** Anonymous; the token is sent only here, never stored. */
  confirm(token: string): Promise<void>;
  /** The notification types that apply to the member's roles. */
  preferences(): Promise<NotificationPreference[]>;
  setPreference(type: string, enabled: boolean): Promise<NotificationPreference>;
}

export function toNotificationPreference(dto: NotificationPreferenceDto | undefined | null): NotificationPreference {
  if (!dto || typeof dto.type !== 'string' || typeof dto.enabled !== 'boolean' || typeof dto.accountMessage !== 'boolean') {
    throw new PlatformFailure('incompatible');
  }
  return { type: dto.type, enabled: dto.enabled, accountMessage: dto.accountMessage };
}

/** The platform refused for rate-limit reasons (`429`); `retryAfterSeconds` is its `Retry-After`. */
export class RateLimitedFailure extends PlatformFailure {
  readonly retryAfterSeconds?: number;

  constructor(retryAfterSeconds: number | undefined, base: PlatformFailure) {
    super('incompatible', { status: 429, code: base.code, correlationId: base.correlationId });
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

function retryAfterOf(error: unknown): number | undefined {
  const headers = (error as { responseHeaders?: Record<string, string[]> }).responseHeaders ?? {};
  const key = Object.keys(headers).find((name) => name.toLowerCase() === 'retry-after');
  const seconds = key ? Number(headers[key]?.[0]) : NaN;
  return Number.isFinite(seconds) && seconds >= 0 ? seconds : undefined;
}

export function toEmailAddress(dto: EmailAddressDto | undefined | null): EmailAddress {
  if (
    !dto ||
    typeof dto.address !== 'string' ||
    (dto.state !== 'unverified' && dto.state !== 'verified') ||
    typeof dto.version !== 'number'
  ) {
    throw new PlatformFailure('incompatible');
  }
  return { address: dto.address, state: dto.state, verifiedAt: dto.verifiedAt ?? undefined, version: dto.version };
}

export function createEmailAdapter(api: PlatformApi): EmailAdapter {
  const email = api.client.api.v1.me.email;
  const failed = (error: unknown): PlatformFailure => {
    const failure = toPlatformFailure(error);
    return failure.status === 429 ? new RateLimitedFailure(retryAfterOf(error), failure) : failure;
  };
  // A 429 must reach the caller with its wait time instead of being retried silently.
  const noRetry = (info: RequestInformation) => {
    info.addRequestOptions([new RetryHandlerOptions({ maxRetries: 0 })]);
    return info;
  };
  const send = async <T extends Parsable>(info: RequestInformation, factory: ParsableFactory<T>): Promise<T> => {
    try {
      const result = await api.adapter.send(noRetry(info), factory, errorMappings);
      if (result === undefined) throw new PlatformFailure('incompatible');
      return result as T;
    } catch (error) {
      throw failed(error);
    }
  };
  const sendNoContent = async (info: RequestInformation): Promise<void> => {
    try {
      await api.adapter.sendNoResponseContent(noRetry(info), errorMappings);
    } catch (error) {
      throw failed(error);
    }
  };
  return {
    get: async () => {
      try {
        return toEmailAddress(await send(email.toGetRequestInformation(), createEmailAddressDtoFromDiscriminatorValue));
      } catch (error) {
        if (error instanceof PlatformFailure && error.status === 404) return null;
        throw error;
      }
    },
    set: async (address, version) => {
      const info = withJson(email.toPutRequestInformation(), { address });
      return toEmailAddress(
        await send(version === undefined ? info : withIfMatch(info, version), createEmailAddressDtoFromDiscriminatorValue),
      );
    },
    remove: (version) => sendNoContent(withIfMatch(email.toDeleteRequestInformation(), version)),
    resend: () => sendNoContent(email.confirmationRequests.toPostRequestInformation()),
    confirm: (token) => sendNoContent(withJson(api.client.api.v1.emailConfirmations.toPostRequestInformation(), { token })),
    preferences: async () => {
      const list = await send<NotificationPreferenceListDto>(
        api.client.api.v1.me.notificationPreferences.toGetRequestInformation(),
        createNotificationPreferenceListDtoFromDiscriminatorValue,
      );
      if (!Array.isArray(list.items)) throw new PlatformFailure('incompatible');
      return list.items.map(toNotificationPreference);
    },
    setPreference: async (type, enabled) =>
      toNotificationPreference(
        await send(
          withJson(api.client.api.v1.me.notificationPreferences.byType(type).toPutRequestInformation(), { enabled }),
          createNotificationPreferenceDtoFromDiscriminatorValue,
        ),
      ),
  };
}
