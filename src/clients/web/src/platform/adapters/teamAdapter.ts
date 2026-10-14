import {
  createTeamDtoFromDiscriminatorValue,
  createTeamPageDtoFromDiscriminatorValue,
  type TeamDto,
  type TeamPageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory, RequestInformation } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type { SeasonState, Team } from '../models/club';
import { sendFor, withIfMatch, withJson, type PlatformApi } from './kiota';

export interface TeamAdapter {
  /** Teams the signed-in member may see: all for a Club Admin, otherwise those of their team roles. */
  listMine(): Promise<Team[]>;
  listBySeason(seasonId: string): Promise<Team[]>;
  get(id: string): Promise<Team>;
  create(seasonId: string, input: { name: string }): Promise<Team>;
  update(id: string, input: { name: string }, version: number): Promise<Team>;
}

const STATES: readonly SeasonState[] = ['draft', 'active', 'archived'];

export function toTeam(dto: TeamDto | undefined): Team {
  const state = STATES.find((s) => s === (dto?.seasonState ?? '').toLowerCase());
  if (!dto || !dto.id || !dto.seasonId || typeof dto.name !== 'string' || !state || !dto.createdAt || typeof dto.version !== 'number') {
    throw new PlatformFailure('incompatible');
  }
  return {
    id: dto.id.toString(),
    seasonId: dto.seasonId.toString(),
    seasonState: state,
    name: dto.name,
    createdAt: dto.createdAt,
    version: dto.version,
  };
}

export function createTeamAdapter(api: PlatformApi): TeamAdapter {
  const factory = createTeamDtoFromDiscriminatorValue;
  const pageFactory = createTeamPageDtoFromDiscriminatorValue as ParsableFactory<TeamPageDto>;
  const item = (id: string) => api.client.api.v1.teams.byTeamId(id as Guid);
  const collect = async (info: (token?: string) => RequestInformation) => {
    const result: Team[] = [];
    let token: string | undefined;
    do {
      const page = await sendFor<TeamPageDto>(api, info(token), pageFactory);
      result.push(...(page.items ?? []).map(toTeam));
      token = page.continuationToken ?? undefined;
    } while (token);
    return result;
  };
  return {
    listMine: () => {
      const teams = api.client.api.v1.teams;
      return collect((token) => teams.toGetRequestInformation(token ? { queryParameters: { continuationToken: token } } : undefined));
    },
    listBySeason: (seasonId) => {
      const teams = api.client.api.v1.seasons.bySeasonId(seasonId as Guid).teams;
      return collect((token) => teams.toGetRequestInformation(token ? { queryParameters: { continuationToken: token } } : undefined));
    },
    get: async (id) => toTeam(await sendFor(api, item(id).toGetRequestInformation(), factory)),
    create: async (seasonId, input) =>
      toTeam(
        await sendFor(
          api,
          withJson(api.client.api.v1.seasons.bySeasonId(seasonId as Guid).teams.toPostRequestInformation(), { name: input.name }),
          factory,
        ),
      ),
    update: async (id, input, version) =>
      toTeam(await sendFor(api, withIfMatch(withJson(item(id).toPutRequestInformation(), { name: input.name }), version), factory)),
  };
}
