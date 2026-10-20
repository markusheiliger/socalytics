import {
  type Parsable,
  type ParsableFactory,
  type RequestAdapter,
  type RequestInformation,
} from '@microsoft/kiota-abstractions';
import { createProblemDetailsFromDiscriminatorValue } from '../../generated/platform-api/models';
import type { PlatformApiClient } from '../../generated/platform-api/platformApiClient';
import { toPlatformFailure } from '../failure';
import { ResponseMetadataOption } from '../middleware/responseMetadata';

export interface PlatformApi {
  adapter: RequestAdapter;
  client: PlatformApiClient;
}

// Kiota's `XXX` maps every error status; `toPlatformFailure` classifies by status and problem code.
export const errorMappings = {
  XXX: createProblemDetailsFromDiscriminatorValue as ParsableFactory<Parsable>,
};

export function withJson(info: RequestInformation, body: unknown): RequestInformation {
  info.setStreamContent(new TextEncoder().encode(JSON.stringify(body)).buffer as ArrayBuffer, 'application/json');
  return info;
}

/** Every edit sends the loaded version as `If-Match` (research.md § R8). */
export function withIfMatch(info: RequestInformation, version: number): RequestInformation {
  info.headers.add('If-Match', `"${version}"`);
  return info;
}

/** The caller owns the key so a retry of the same intent reuses it (research.md § R8); adapters never generate one. */
export function withIdempotencyKey(info: RequestInformation, key: string): RequestInformation {
  info.headers.add('Idempotency-Key', key);
  return info;
}

export async function sendFor<T extends Parsable>(
  api: PlatformApi,
  info: RequestInformation,
  factory: ParsableFactory<T>,
): Promise<T> {
  try {
    const result = await api.adapter.send(info, factory, errorMappings);
    if (result === undefined) throw toPlatformFailure(undefined);
    return result as T;
  } catch (error) {
    throw toPlatformFailure(error);
  }
}

export interface ResponseWithMetadata<T> {
  body: T;
  status: number | undefined;
  etag: string | undefined;
}

/** Like `sendFor`, and also returns the response status and `ETag` header (research.md § R3). */
export async function sendWithMetadataFor<T extends Parsable>(
  api: PlatformApi,
  info: RequestInformation,
  factory: ParsableFactory<T>,
): Promise<ResponseWithMetadata<T>> {
  const option = new ResponseMetadataOption();
  info.addRequestOptions([option]);
  const body = await sendFor(api, info, factory);
  return { body, status: option.status, etag: option.etag };
}

export async function sendNoContentFor(api: PlatformApi, info: RequestInformation): Promise<void> {
  try {
    await api.adapter.sendNoResponseContent(info, errorMappings);
  } catch (error) {
    throw toPlatformFailure(error);
  }
}
