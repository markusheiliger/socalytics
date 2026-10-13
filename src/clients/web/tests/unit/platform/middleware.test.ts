import type { Middleware } from '@microsoft/kiota-http-fetchlibrary';
import { AntiforgeryHeaderHandler } from '../../../src/platform/middleware/antiforgery';
import { ConnectivityHandler } from '../../../src/platform/middleware/connectivity';
import { SessionOutcomeHandler } from '../../../src/platform/middleware/sessionOutcome';

function fakeNext(respond: () => Response | Promise<Response>) {
  const seen: RequestInit[] = [];
  const next: Middleware = {
    next: undefined,
    execute: async (_url, init) => {
      seen.push(init);
      return respond();
    },
  };
  return { next, seen };
}

function problem(status: number, code: string) {
  return new Response(JSON.stringify({ status, code }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

describe('AntiforgeryHeaderHandler', () => {
  it.each(['POST', 'PUT', 'PATCH', 'DELETE'])('adds the token on %s', async (method) => {
    const { next, seen } = fakeNext(() => new Response(null, { status: 204 }));
    const handler = new AntiforgeryHeaderHandler(() => 'token-1');
    handler.next = next;
    await handler.execute('/x', { method });
    expect((seen[0].headers as Record<string, string>)['X-CSRF-Token']).toBe('token-1');
  });

  it.each(['GET', 'HEAD', undefined])('does not add the token on %s', async (method) => {
    const { next, seen } = fakeNext(() => new Response(null, { status: 200 }));
    const handler = new AntiforgeryHeaderHandler(() => 'token-1');
    handler.next = next;
    await handler.execute('/x', { method });
    expect(seen[0].headers).toBeUndefined();
  });

  it('adds nothing without a session token', async () => {
    const { next, seen } = fakeNext(() => new Response(null, { status: 204 }));
    const handler = new AntiforgeryHeaderHandler(() => undefined);
    handler.next = next;
    await handler.execute('/x', { method: 'POST' });
    expect(seen[0].headers).toBeUndefined();
  });

  it('keeps existing headers', async () => {
    const { next, seen } = fakeNext(() => new Response(null, { status: 204 }));
    const handler = new AntiforgeryHeaderHandler(() => 't');
    handler.next = next;
    await handler.execute('/x', { method: 'POST', headers: { Accept: 'application/json' } });
    expect(seen[0].headers).toEqual({ Accept: 'application/json', 'X-CSRF-Token': 't' });
  });
});

describe('SessionOutcomeHandler', () => {
  it('reports sessionEnded on 401', async () => {
    const outcomes: string[] = [];
    const { next } = fakeNext(() => problem(401, 'unauthenticated'));
    const handler = new SessionOutcomeHandler((o) => outcomes.push(o));
    handler.next = next;
    const response = await handler.execute('/x', {});
    expect(outcomes).toEqual(['sessionEnded']);
    expect(response.status).toBe(401);
  });

  it('does not report a failed sign-in as an ended session', async () => {
    const outcomes: string[] = [];
    const { next } = fakeNext(() => problem(401, 'sign-in-failed'));
    const handler = new SessionOutcomeHandler((o) => outcomes.push(o));
    handler.next = next;
    await handler.execute('/x', {});
    expect(outcomes).toEqual([]);
  });

  it('reports passwordChangeRequired on the matching 403', async () => {
    const outcomes: string[] = [];
    const { next } = fakeNext(() => problem(403, 'password-change-required'));
    const handler = new SessionOutcomeHandler((o) => outcomes.push(o));
    handler.next = next;
    await handler.execute('/x', {});
    expect(outcomes).toEqual(['passwordChangeRequired']);
  });

  it.each(['forbidden', 'antiforgery-failed'])('ignores 403 %s', async (code) => {
    const outcomes: string[] = [];
    const { next } = fakeNext(() => problem(403, code));
    const handler = new SessionOutcomeHandler((o) => outcomes.push(o));
    handler.next = next;
    await handler.execute('/x', {});
    expect(outcomes).toEqual([]);
  });

  it('ignores successes and leaves the body readable', async () => {
    const outcomes: string[] = [];
    const { next } = fakeNext(() => new Response('{"a":1}', { status: 200 }));
    const handler = new SessionOutcomeHandler((o) => outcomes.push(o));
    handler.next = next;
    const response = await handler.execute('/x', {});
    expect(outcomes).toEqual([]);
    expect(await response.json()).toEqual({ a: 1 });
  });

  it('still reports sessionEnded when the 401 body is not JSON', async () => {
    const outcomes: string[] = [];
    const { next } = fakeNext(() => new Response('nope', { status: 401 }));
    const handler = new SessionOutcomeHandler((o) => outcomes.push(o));
    handler.next = next;
    await handler.execute('/x', {});
    expect(outcomes).toEqual(['sessionEnded']);
  });
});

describe('ConnectivityHandler', () => {
  it('reports a network failure and rethrows', async () => {
    let reports = 0;
    const { next } = fakeNext(() => Promise.reject(new TypeError('Failed to fetch')));
    const handler = new ConnectivityHandler(() => reports++);
    handler.next = next;
    await expect(handler.execute('/x', {})).rejects.toBeInstanceOf(TypeError);
    expect(reports).toBe(1);
  });

  it.each([502, 503, 504])('reports status %i', async (status) => {
    let reports = 0;
    const { next } = fakeNext(() => new Response(null, { status }));
    const handler = new ConnectivityHandler(() => reports++);
    handler.next = next;
    const response = await handler.execute('/x', {});
    expect(response.status).toBe(status);
    expect(reports).toBe(1);
  });

  it.each([200, 401, 500])('does not report status %i', async (status) => {
    let reports = 0;
    const { next } = fakeNext(() => new Response(null, { status }));
    const handler = new ConnectivityHandler(() => reports++);
    handler.next = next;
    await handler.execute('/x', {});
    expect(reports).toBe(0);
  });

  it('does not report other errors', async () => {
    let reports = 0;
    const { next } = fakeNext(() => Promise.reject(new Error('boom')));
    const handler = new ConnectivityHandler(() => reports++);
    handler.next = next;
    await expect(handler.execute('/x', {})).rejects.toThrow('boom');
    expect(reports).toBe(0);
  });
});
