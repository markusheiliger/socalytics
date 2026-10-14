import {
  createClubDtoFromDiscriminatorValue,
  type ClubDto,
} from '../../generated/platform-api/models';
import { PlatformFailure } from '../failure';
import type { Club } from '../models/club';
import { sendFor, withIfMatch, withJson, type PlatformApi } from './kiota';

export interface ClubAdapter {
  get(): Promise<Club>;
  update(details: { displayName: string }, version: number): Promise<Club>;
}

export function toClub(dto: ClubDto | undefined): Club {
  if (!dto || !dto.id || typeof dto.displayName !== 'string' || typeof dto.version !== 'number') {
    throw new PlatformFailure('incompatible');
  }
  return { id: dto.id.toString(), displayName: dto.displayName, version: dto.version };
}

export function createClubAdapter(api: PlatformApi): ClubAdapter {
  const { client } = api;
  return {
    get: async () => toClub(await sendFor(api, client.api.v1.club.toGetRequestInformation(), createClubDtoFromDiscriminatorValue)),
    update: async (details, version) =>
      toClub(
        await sendFor(
          api,
          withIfMatch(withJson(client.api.v1.club.toPutRequestInformation(), { displayName: details.displayName }), version),
          createClubDtoFromDiscriminatorValue,
        ),
      ),
  };
}
