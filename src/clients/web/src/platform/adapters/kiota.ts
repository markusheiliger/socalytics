import {
  type Parsable,
  type ParsableFactory,
  type RequestAdapter,
  type RequestInformation,
} from '@microsoft/kiota-abstractions';
import { createProblemDetailsFromDiscriminatorValue } from '../../generated/platform-api/models';
import type { PlatformApiClient } from '../../generated/platform-api/platformApiClient';
import { toPlatformFailure } from '../failure';

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

export async function sendNoContentFor(api: PlatformApi, info: RequestInformation): Promise<void> {
  try {
    await api.adapter.sendNoResponseContent(info, errorMappings);
  } catch (error) {
    throw toPlatformFailure(error);
  }
}
