import { createPlatformApi } from '../../../../src/platform/client';
import { createTeamAdapter } from '../../../../src/platform/adapters/teamAdapter';

const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const team = (over: Record<string, unknown> = {}) => ({
  id: TEAM,
  seasonId: SEASON,
  seasonState: 'Active',
  name: 'First Team',
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
  ...over,
});

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });

function setup(respond: (url: string, init: RequestInit) => Response) {
  const calls: { url: string; init: RequestInit }[] = [];
  const api = createPlatformApi({
    getAntiforgeryToken: () => 'csrf-1',
    onSessionOutcome: () => {},
    onNetworkFailure: () => {},
    fetch: async (url, init) => {
      calls.push({ url, init });
      return respond(url, init);
    },
  });
  return { adapter: createTeamAdapter(api), calls };
}

const route = (c: { url: string; init: RequestInit }) => `${c.init.method} ${new URL(c.url).pathname}`;

describe('teamAdapter', () => {
  it('lists the member’s teams and the teams of a season, following continuation tokens', async () => {
    const { adapter, calls } = setup((url) =>
      url.includes('continuationToken=t2')
        ? json(200, { items: [team({ id: '00000000-0000-4000-8000-000000000004', seasonState: 'Archived' })], continuationToken: null })
        : json(200, { items: [team()], continuationToken: url.includes('/seasons/') ? null : 't2' }),
    );
    const mine = await adapter.listMine();
    expect(mine.map((t) => t.seasonState)).toEqual(['active', 'archived']);
    await adapter.listBySeason(SEASON);
    expect(calls.map(route)).toEqual(['GET /api/v1/teams', 'GET /api/v1/teams', `GET /api/v1/seasons/${SEASON}/teams`]);
  });

  it('creates in a season and updates with the loaded version as If-Match', async () => {
    const { adapter, calls } = setup((url, init) => json(init.method === 'POST' ? 201 : 200, team({ version: url.endsWith(TEAM) ? 4 : 1 })));
    await adapter.create(SEASON, { name: 'Second Team' });
    const updated = await adapter.update(TEAM, { name: 'Renamed' }, 3);
    expect(updated.version).toBe(4);
    expect(calls.map(route)).toEqual([`POST /api/v1/seasons/${SEASON}/teams`, `PUT /api/v1/teams/${TEAM}`]);
    expect(new TextDecoder().decode(calls[1].init.body as ArrayBuffer)).toBe(JSON.stringify({ name: 'Renamed' }));
    expect(new Headers(calls[1].init.headers as HeadersInit).get('If-Match')).toBe('"3"');
  });

  it('maps conflicts, validation errors, and unknown shapes', async () => {
    const stale = setup(() => json(412, { status: 412 }));
    await expect(stale.adapter.update(TEAM, { name: 'x' }, 1)).rejects.toMatchObject({ kind: 'conflict' });
    const invalid = setup(() => json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'name', code: 'required' }] }));
    await expect(invalid.adapter.create(SEASON, { name: '' })).rejects.toMatchObject({ kind: 'validation' });
    const odd = setup(() => json(200, { id: TEAM }));
    await expect(odd.adapter.get(TEAM)).rejects.toMatchObject({ kind: 'incompatible' });
  });
});
