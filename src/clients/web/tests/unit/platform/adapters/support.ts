import { createPlatformApi } from '../../../../src/platform/client';

export interface Call {
  url: string;
  init: RequestInit;
}

export function setup(respond: (call: Call) => Response) {
  const calls: Call[] = [];
  const api = createPlatformApi({
    getAntiforgeryToken: () => 'csrf-1',
    onSessionOutcome: () => {},
    onNetworkFailure: () => {},
    fetch: async (url, init) => {
      const call = { url, init };
      calls.push(call);
      return respond(call);
    },
  });
  return { api, calls };
}

export const ok = (status: number, body?: unknown) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

export const problem = (status: number, code: string, errors?: { field: string; code: string }[]) =>
  new Response(JSON.stringify({ status, code, errors }), { status, headers: { 'Content-Type': 'application/problem+json' } });

export const bodyOf = (call: Call) => JSON.parse(new TextDecoder().decode(call.init.body as ArrayBuffer));

export function header(call: Call, name: string): string | undefined {
  const headers = call.init.headers as Record<string, string> | Headers;
  const value = headers instanceof Headers ? headers.get(name) : (headers[name] ?? headers[name.toLowerCase()]);
  return value ?? undefined;
}

export const ids = {
  team: '00000000-0000-4000-8000-0000000000a1',
  match: '00000000-0000-4000-8000-0000000000c1',
  session: '00000000-0000-4000-8000-0000000000d1',
  version: '00000000-0000-4000-8000-0000000000e1',
  mapping: '00000000-0000-4000-8000-0000000000f1',
  set: '00000000-0000-4000-8000-000000000101',
  run: '00000000-0000-4000-8000-000000000201',
};
