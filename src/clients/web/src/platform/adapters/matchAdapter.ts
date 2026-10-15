import {
  createMatchDtoFromDiscriminatorValue,
  createMatchPageDtoFromDiscriminatorValue,
  type MatchDto,
  type MatchPageDto,
} from '../../generated/platform-api/models';
import type { Guid, ParsableFactory } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import type { HomeAway, Match, MatchInput, MatchUpdate, Page, PageRequest } from '../models/matches';
import { sendFor, withIfMatch, withJson, type PlatformApi } from './kiota';

export interface MatchAdapter {
  listByTeam(teamId: string, page?: PageRequest): Promise<Page<Match>>;
  get(id: string): Promise<Match>;
  create(teamId: string, input: MatchInput): Promise<Match>;
  update(id: string, input: MatchUpdate, version: number): Promise<Match>;
}

const HOME_AWAY: readonly HomeAway[] = ['home', 'away', 'neutral'];

export function toMatch(dto: MatchDto | undefined): Match {
  const homeAway = HOME_AWAY.find((h) => h === (dto?.homeAway ?? '').toLowerCase());
  if (
    !dto ||
    !dto.id ||
    !dto.teamId ||
    typeof dto.opponent?.name !== 'string' ||
    !dto.kickoffAt ||
    !homeAway ||
    !dto.createdAt ||
    typeof dto.version !== 'number'
  ) {
    throw new PlatformFailure('incompatible');
  }
  return {
    id: dto.id.toString(),
    teamId: dto.teamId.toString(),
    opponentName: dto.opponent.name,
    kickoffAt: dto.kickoffAt,
    homeAway,
    competition: dto.competition ?? undefined,
    createdAt: dto.createdAt,
    version: dto.version,
  };
}

export function pageQuery(page?: PageRequest): { continuationToken?: string; pageSize?: string } {
  const query: { continuationToken?: string; pageSize?: string } = {};
  if (page?.continuationToken) query.continuationToken = page.continuationToken;
  if (page?.pageSize !== undefined) query.pageSize = String(page.pageSize);
  return query;
}

export function createMatchAdapter(api: PlatformApi): MatchAdapter {
  const factory = createMatchDtoFromDiscriminatorValue;
  const pageFactory = createMatchPageDtoFromDiscriminatorValue as ParsableFactory<MatchPageDto>;
  const item = (id: string) => api.client.api.v1.matches.byMatchId(id as Guid);
  const details = (input: MatchUpdate) => ({
    kickoffAt: input.kickoffAt.toISOString(),
    homeAway: input.homeAway,
    ...(input.competition ? { competition: input.competition } : {}),
  });
  return {
    listByTeam: async (teamId, page) => {
      const info = api.client.api.v1.teams.byTeamId(teamId as Guid).matches.toGetRequestInformation({ queryParameters: pageQuery(page) });
      const result = await sendFor<MatchPageDto>(api, info, pageFactory);
      return { items: (result.items ?? []).map(toMatch), continuationToken: result.continuationToken ?? undefined };
    },
    get: async (id) => toMatch(await sendFor(api, item(id).toGetRequestInformation(), factory)),
    create: async (teamId, input) =>
      toMatch(
        await sendFor(
          api,
          withJson(api.client.api.v1.teams.byTeamId(teamId as Guid).matches.toPostRequestInformation(), {
            opponent: { name: input.opponentName },
            ...details(input),
          }),
          factory,
        ),
      ),
    update: async (id, input, version) =>
      toMatch(await sendFor(api, withIfMatch(withJson(item(id).toPutRequestInformation(), details(input)), version), factory)),
  };
}
