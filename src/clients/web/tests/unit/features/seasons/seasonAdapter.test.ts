import { createSeasonAdapter } from '../../../../src/platform/adapters/seasonAdapter';
import { createPlatformApi } from '../../../../src/platform/client';

const season = (over: Record<string, unknown> = {}) => ({
  id: '00000000-0000-4000-8000-000000000002',
  name: '2026/27',
  state: 'draft',
  createdAt: '2026-10-12T12:00:00Z',
  activatedAt: null,
  archivedAt: null,
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
  return { adapter: createSeasonAdapter(api), calls };
}

describe('seasonAdapter', () => {
  it('follows continuation tokens and maps wire states', async () => {
    const { adapter, calls } = setup((url) =>
      url.includes('continuationToken=t2')
        ? json(200, { items: [season({ id: '00000000-0000-4000-8000-000000000003', state: 'archived' })], continuationToken: null })
        : json(200, { items: [season()], continuationToken: 't2' }),
    );
    const seasons = await adapter.list();
    expect(seasons.map((s) => s.state)).toEqual(['draft', 'archived']);
    expect(calls).toHaveLength(2);
  });

  it('creates, activates, and archives through the platform routes', async () => {
    const { adapter, calls } = setup((url) => json(url.endsWith('/seasons') ? 201 : 200, season({ state: 'active', version: 2 })));
    await adapter.create({ name: '2026/27' });
    await adapter.activate('00000000-0000-4000-8000-000000000002');
    await adapter.archive('00000000-0000-4000-8000-000000000002');
    expect(calls.map((c) => `${c.init.method} ${new URL(c.url).pathname}`)).toEqual([
      'POST /api/v1/seasons',
      'POST /api/v1/seasons/00000000-0000-4000-8000-000000000002/activate',
      'POST /api/v1/seasons/00000000-0000-4000-8000-000000000002/archive',
    ]);
  });

  it('maps 409 to conflict and validation field errors', async () => {
    const conflict = setup(() => json(409, { status: 409, code: 'season-already-active' }));
    await expect(conflict.adapter.activate('00000000-0000-4000-8000-000000000002')).rejects.toMatchObject({ kind: 'conflict', code: 'season-already-active' });
    const invalid = setup(() => json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'name', code: 'required' }] }));
    await expect(invalid.adapter.create({ name: '' })).rejects.toMatchObject({ kind: 'validation' });
  });
});
