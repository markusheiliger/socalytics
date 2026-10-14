import { createPlatformClient } from '../../../src/platform/client';
import { toPlatformFailure } from '../../../src/platform/failure';

function setup(respond: (url: string, init: RequestInit) => Response, token: string | undefined = 'csrf-1') {
  const calls: { url: string; init: RequestInit }[] = [];
  const outcomes: string[] = [];
  let network = 0;
  const client = createPlatformClient({
    getAntiforgeryToken: () => token,
    onSessionOutcome: (o) => outcomes.push(o),
    onNetworkFailure: () => network++,
    fetch: async (url, init) => {
      calls.push({ url, init });
      return respond(url, init);
    },
  });
  return { client, calls, outcomes, network: () => network };
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json' } });

describe('createPlatformClient', () => {
  it('requests the current origin with same-origin credentials', async () => {
    const { client, calls } = setup(() => json(401, { status: 401, code: 'unauthenticated' }));
    await expect(client.api.v1.session.get()).rejects.toBeDefined();
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/session`);
    expect(calls[0].init.credentials).toBe('same-origin');
  });

  it('sends the anti-forgery token on unsafe methods only', async () => {
    const { client, calls } = setup(() => json(401, { status: 401, code: 'unauthenticated' }));
    await client.api.v1.session.get().catch(() => undefined);
    await client.api.v1.session.delete().catch(() => undefined);
    const header = (i: number) => (calls[i].init.headers as Record<string, string> | undefined)?.['X-CSRF-Token'];
    expect(header(0)).toBeUndefined();
    expect(header(1)).toBe('csrf-1');
  });

  it('raises the session-ended event and maps the error to a PlatformFailure', async () => {
    const { client, outcomes } = setup(() => json(401, { status: 401, code: 'unauthenticated' }));
    const error = await client.api.v1.session.get().catch((e: unknown) => e);
    expect(outcomes).toEqual(['sessionEnded']);
    expect(toPlatformFailure(error).kind).toBe('sessionEnded');
  });

  it('reports network failures', async () => {
    const { client, network } = setup(() => {
      throw new TypeError('Failed to fetch');
    });
    const error = await client.api.v1.session.get().catch((e: unknown) => e);
    expect(network()).toBe(1);
    expect(toPlatformFailure(error).kind).toBe('unreachable');
  });
});
