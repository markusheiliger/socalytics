import type { RequestInformation } from '@microsoft/kiota-abstractions';
import { RetryHandlerOptions } from '@microsoft/kiota-http-fetchlibrary';
import {
  createPasswordRecoveryCheckDtoFromDiscriminatorValue,
  type PasswordRecoveryCheckDto,
} from '../../generated/platform-api/models';
import { PlatformFailure, toPlatformFailure, type FieldError } from '../failure';
import { errorMappings, withJson, type PlatformApi } from './kiota';

/** A refusal the pages show to the person; every other failure stays a `PlatformFailure`. */
export type PasswordRecoveryRefusal =
  | { kind: 'invalid' }
  | { kind: 'fieldErrors'; fieldErrors: FieldError[] }
  | { kind: 'rateLimited'; retryAfterSeconds?: number };

export type PasswordRecoveryResult<T = void> = { ok: true; value: T } | ({ ok: false } & PasswordRecoveryRefusal);

export interface PasswordRecoveryAdapter {
  /** Anonymous; `ok` is the same for every well-formed address. */
  requestRecovery(address: string): Promise<PasswordRecoveryResult>;
  /** Anonymous; returns the account name the recovery belongs to. */
  checkRecovery(token: string): Promise<PasswordRecoveryResult<string>>;
  /** Anonymous; never establishes a session. */
  completeRecovery(token: string, newPassword: string): Promise<PasswordRecoveryResult>;
}

const INVALID_CODE = 'password-recovery-invalid';

function refusalOf(failure: PlatformFailure): PasswordRecoveryRefusal | undefined {
  if (failure.status === 429) return { kind: 'rateLimited', retryAfterSeconds: failure.retryAfterSeconds };
  if (failure.status === 400) {
    if (failure.code === INVALID_CODE) return { kind: 'invalid' };
    if (failure.fieldErrors.length > 0) return { kind: 'fieldErrors', fieldErrors: failure.fieldErrors };
  }
  return undefined;
}

export function createPasswordRecoveryAdapter(api: PlatformApi): PasswordRecoveryAdapter {
  // A 429 must reach the person with its wait time instead of being retried silently.
  const noRetry = (info: RequestInformation) => {
    info.addRequestOptions([new RetryHandlerOptions({ maxRetries: 0 })]);
    return info;
  };

  const attempt = async <T>(send: () => Promise<T>): Promise<PasswordRecoveryResult<T>> => {
    try {
      return { ok: true, value: await send() };
    } catch (error) {
      const failure = toPlatformFailure(error);
      const refusal = refusalOf(failure);
      if (refusal) return { ok: false, ...refusal };
      throw failure;
    }
  };

  const sendNoContent = (info: RequestInformation) => async (): Promise<void> => {
    try {
      await api.adapter.sendNoResponseContent(noRetry(info), errorMappings);
    } catch (error) {
      throw toPlatformFailure(error);
    }
  };

  const recovery = api.client.api.v1.passwordRecovery;

  return {
    requestRecovery: (address) =>
      attempt(sendNoContent(withJson(recovery.requests.toPostRequestInformation(), { address }))),
    checkRecovery: (token) =>
      attempt(async () => {
        try {
          const dto = (await api.adapter.send(
            noRetry(withJson(recovery.checks.toPostRequestInformation(), { token })),
            createPasswordRecoveryCheckDtoFromDiscriminatorValue,
            errorMappings,
          )) as PasswordRecoveryCheckDto | undefined;
          if (!dto?.accountName) throw new PlatformFailure('incompatible');
          return dto.accountName;
        } catch (error) {
          throw toPlatformFailure(error);
        }
      }),
    completeRecovery: (token, newPassword) =>
      attempt(sendNoContent(withJson(recovery.completions.toPostRequestInformation(), { token, newPassword }))),
  };
}
