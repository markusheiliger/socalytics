import { createClubAdapter } from '../../../../src/platform/adapters/clubAdapter';
import { createPlatformApi } from '../../../../src/platform/client';
import { PlatformFailure } from '../../../../src/platform/failure';

const clubBody = { id: '00000000-0000-4000-8000-000000000001', displayName: 'Club', version: 3 };

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
  return { adapter: createClubAdapter(api), calls };
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });

describe('clubAdapter', () => {
  it('maps the club and sends the loaded version as If-Match on update', async () => {
    const { adapter, calls } = setup(() => json(200, { ...clubBody, displayName: 'New', version: 4 }));
    const club = await adapter.update({ displayName: 'New' }, 3);
    expect(club).toEqual({ id: clubBody.id, displayName: 'New', version: 4 });
    expect(calls[0].url).toContain('/api/v1/club');
    expect(calls[0].init.method).toBe('PUT');
    expect(new Headers(calls[0].init.headers as HeadersInit).get('If-Match')).toBe('"3"');
    expect(new TextDecoder().decode(calls[0].init.body as ArrayBuffer)).toBe(JSON.stringify({ displayName: 'New' }));
  });

  it('classifies 412 as conflict and 400 as validation with field errors', async () => {
    const conflict = setup(() => json(412, { status: 412, code: 'version-conflict' }));
    await expect(conflict.adapter.update({ displayName: 'x' }, 1)).rejects.toMatchObject({ kind: 'conflict' });
    const invalid = setup(() =>
      json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'displayName', code: 'required' }] }),
    );
    await expect(invalid.adapter.update({ displayName: '' }, 1)).rejects.toMatchObject({
      kind: 'validation',
      fieldErrors: [{ field: 'displayName', code: 'required' }],
    });
  });

  it('refuses an unknown response shape as incompatible', async () => {
    const { adapter } = setup(() => json(200, { id: clubBody.id }));
    await expect(adapter.get()).rejects.toBeInstanceOf(PlatformFailure);
    await expect(adapter.get()).rejects.toMatchObject({ kind: 'incompatible' });
  });
});
