import {
  createSeasonDtoFromDiscriminatorValue,
  createSeasonPageDtoFromDiscriminatorValue,
  type SeasonDto,
  type SeasonPageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type { Season, SeasonState } from '../models/club';
import { sendFor, withJson, type PlatformApi } from './kiota';

export interface SeasonAdapter {
  list(): Promise<Season[]>;
  get(id: string): Promise<Season>;
  create(input: { name: string }): Promise<Season>;
  /** The platform decides on the stored state, so activation and archiving carry no version. */
  activate(id: string): Promise<Season>;
  archive(id: string): Promise<Season>;
}

const STATES: readonly SeasonState[] = ['draft', 'active', 'archived'];

export function toSeason(dto: SeasonDto | undefined): Season {
  const state = STATES.find((s) => s === (dto?.state ?? '').toLowerCase());
  if (!dto || !dto.id || typeof dto.name !== 'string' || !state || !dto.createdAt || typeof dto.version !== 'number') {
    throw new PlatformFailure('incompatible');
  }
  return {
    id: dto.id.toString(),
    name: dto.name,
    state,
    createdAt: dto.createdAt,
    activatedAt: dto.activatedAt ?? undefined,
    archivedAt: dto.archivedAt ?? undefined,
    version: dto.version,
  };
}

export function createSeasonAdapter(api: PlatformApi): SeasonAdapter {
  const seasons = api.client.api.v1.seasons;
  const item = (id: string) => seasons.bySeasonId(id as Guid);
  return {
    list: async () => {
      const result: Season[] = [];
      let token: string | undefined;
      do {
        const info = seasons.toGetRequestInformation(
          token ? { queryParameters: { continuationToken: token } } : undefined,
        );
        const page = await sendFor<SeasonPageDto>(api, info, createSeasonPageDtoFromDiscriminatorValue as ParsableFactory<SeasonPageDto>);
        result.push(...(page.items ?? []).map(toSeason));
        token = page.continuationToken ?? undefined;
      } while (token);
      return result;
    },
    get: async (id) => toSeason(await sendFor(api, item(id).toGetRequestInformation(), createSeasonDtoFromDiscriminatorValue)),
    create: async (input) =>
      toSeason(await sendFor(api, withJson(seasons.toPostRequestInformation(), { name: input.name }), createSeasonDtoFromDiscriminatorValue)),
    activate: async (id) =>
      toSeason(await sendFor(api, item(id).activate.toPostRequestInformation(), createSeasonDtoFromDiscriminatorValue)),
    archive: async (id) =>
      toSeason(await sendFor(api, item(id).archive.toPostRequestInformation(), createSeasonDtoFromDiscriminatorValue)),
  };
}
