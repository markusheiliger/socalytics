import { createMatchAdapter } from '../../../../src/platform/adapters/matchAdapter';
import { PlatformFailure } from '../../../../src/platform/failure';
import { bodyOf, header, ids, ok, problem, setup } from './support';

const wire = {
  id: ids.match,
  teamId: ids.team,
  opponent: { name: 'Rovers' },
  kickoffAt: '2026-10-10T14:00:00Z',
  homeAway: 'neutral',
  competition: 'Cup',
  createdAt: '2026-10-01T10:00:00Z',
  version: 3,
};

describe('matchAdapter', () => {
  it('lists the matches of a team with paging', async () => {
    const { api, calls } = setup(() => ok(200, { items: [wire], continuationToken: 'next' }));
    const page = await createMatchAdapter(api).listByTeam(ids.team, { continuationToken: 'tok', pageSize: 5 });
    expect(calls[0].url).toContain(`/api/v1/teams/${ids.team}/matches`);
    expect(calls[0].url).toContain('continuationToken=tok');
    expect(calls[0].url).toContain('pageSize=5');
    expect(page.continuationToken).toBe('next');
    expect(page.items[0]).toMatchObject({ id: ids.match, teamId: ids.team, opponentName: 'Rovers', homeAway: 'neutral', competition: 'Cup', version: 3 });
    expect(page.items[0].kickoffAt).toBeInstanceOf(Date);
  });

  it('gets a match and accepts home', async () => {
    const { api, calls } = setup(() => ok(200, { ...wire, homeAway: 'home', competition: null }));
    const match = await createMatchAdapter(api).get(ids.match);
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/matches/${ids.match}`);
    expect(match.homeAway).toBe('home');
    expect(match.competition).toBeUndefined();
  });

  it('refuses an unknown homeAway value as incompatible', async () => {
    const { api } = setup(() => ok(200, { ...wire, homeAway: 'elsewhere' }));
    await expect(createMatchAdapter(api).get(ids.match)).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('creates a match with the opponent in the body', async () => {
    const { api, calls } = setup(() => ok(201, wire));
    const match = await createMatchAdapter(api).create(ids.team, {
      opponentName: 'Rovers',
      kickoffAt: new Date('2026-10-10T14:00:00Z'),
      homeAway: 'neutral',
      competition: 'Cup',
    });
    expect(calls[0].init.method).toBe('POST');
    expect(bodyOf(calls[0])).toEqual({
      opponent: { name: 'Rovers' },
      kickoffAt: '2026-10-10T14:00:00.000Z',
      homeAway: 'neutral',
      competition: 'Cup',
    });
    expect(match.id).toBe(ids.match);
  });

  it('updates with If-Match and never sends the opponent', async () => {
    const { api, calls } = setup(() => ok(200, { ...wire, version: 4 }));
    const match = await createMatchAdapter(api).update(ids.match, { kickoffAt: new Date('2026-10-10T14:00:00Z'), homeAway: 'away' }, 3);
    expect(calls[0].init.method).toBe('PUT');
    expect(header(calls[0], 'If-Match')).toBe('"3"');
    expect(bodyOf(calls[0])).toEqual({ kickoffAt: '2026-10-10T14:00:00.000Z', homeAway: 'away' });
    expect(match.version).toBe(4);
  });

  it('maps a season-archived refusal', async () => {
    const { api } = setup(() => problem(409, 'season-archived'));
    const error = await createMatchAdapter(api).get(ids.match).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(PlatformFailure);
    expect((error as PlatformFailure).kind).toBe('seasonArchived');
  });
});
