// @vitest-environment node
import { createHash } from 'node:crypto';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { startFakeObjectStorage, type FakeObjectStorage } from '../../e2e/support/fakeObjectStorage.mjs';

const ORIGIN = 'http://127.0.0.1:4173';
const sha256 = (body: Buffer) => createHash('sha256').update(body).digest('base64');

let store: FakeObjectStorage;

const put = (part: number, body: Buffer, checksum = sha256(body)) =>
  fetch(`${store.origin}/bucket/recording/${part}?partNumber=${part}`, {
    method: 'PUT',
    headers: { 'x-amz-checksum-sha256': checksum, origin: ORIGIN },
    body: new Uint8Array(body),
  });
const control = (action: string, body?: unknown) =>
  fetch(`${store.origin}/__control/${action}`, {
    method: body === undefined && action !== 'reset' ? 'GET' : 'POST',
    body: body === undefined ? undefined : JSON.stringify(body),
  });
const state = async () =>
  (await (await control('state')).json()) as {
    parts: { partNumber: number; size: number }[];
    requests: unknown[];
    inFlight: number;
  };
const inFlight = async () => (await state()).inFlight;
const untilInFlight = (check: (count: number) => void) =>
  vi.waitFor(async () => check(await inFlight()), { timeout: 2000, interval: 20 });

beforeAll(async () => {
  store = await startFakeObjectStorage({ allowedOrigin: ORIGIN });
});
afterAll(() => store.close());
beforeEach(async () => {
  await control('reset');
});

describe('fake object storage', () => {
  it('stores a part whose checksum matches the body', async () => {
    const response = await put(1, Buffer.from('part one'));
    expect(response.status).toBe(200);
    expect(response.headers.get('access-control-allow-origin')).toBe(ORIGIN);
    expect((await state()).parts).toEqual([expect.objectContaining({ partNumber: 1, size: 8 })]);
  });

  it('refuses a checksum mismatch with 400 BadDigest and stores nothing', async () => {
    const response = await put(1, Buffer.from('part one'), sha256(Buffer.from('other')));
    expect(response.status).toBe(400);
    expect(await response.text()).toContain('BadDigest');
    expect((await state()).parts).toEqual([]);
  });

  it('answers a preflight for the allowed origin only', async () => {
    const preflight = (origin: string) =>
      fetch(`${store.origin}/bucket/recording/1`, {
        method: 'OPTIONS',
        headers: {
          origin,
          'access-control-request-method': 'PUT',
          'access-control-request-headers': 'x-amz-checksum-sha256',
        },
      });
    const allowed = await preflight(ORIGIN);
    expect(allowed.status).toBe(204);
    expect(allowed.headers.get('access-control-allow-origin')).toBe(ORIGIN);
    expect(allowed.headers.get('access-control-allow-methods')).toBe('PUT');
    const refused = await preflight('http://evil.example');
    expect(refused.status).toBe(403);
    expect(refused.headers.get('access-control-allow-origin')).toBeNull();
  });

  it('fails a part once and then accepts it', async () => {
    await control('faults', { failPart: { partNumber: 2, times: 1 } });
    expect((await put(2, Buffer.from('two'))).status).toBe(500);
    expect((await put(2, Buffer.from('two'))).status).toBe(200);
    expect((await put(3, Buffer.from('three'))).status).toBe(200);
  });

  it('fails a part always', async () => {
    await control('faults', { failPart: { partNumber: 2, times: 'always', status: 503 } });
    expect((await put(2, Buffer.from('two'))).status).toBe(503);
    expect((await put(2, Buffer.from('two'))).status).toBe(503);
  });

  it('refuses with 403 when grants are expired', async () => {
    await control('faults', { expireGrants: true });
    const response = await put(1, Buffer.from('one'));
    expect(response.status).toBe(403);
    expect(await response.text()).toContain('AccessDenied');
  });

  it('delays responses', async () => {
    await control('faults', { delayMs: 150 });
    const started = Date.now();
    await put(1, Buffer.from('one'));
    expect(Date.now() - started).toBeGreaterThanOrEqual(140);
  });

  describe('in-flight counter', () => {
    it('is 0 when idle and after a response', async () => {
      expect(await inFlight()).toBe(0);
      await put(1, Buffer.from('one'));
      expect(await inFlight()).toBe(0);
    });

    it('is greater than 0 while a delayed part request is handled and 0 after its response', async () => {
      await control('faults', { delayMs: 400 });
      const pending = put(1, Buffer.from('one'));
      await untilInFlight((count) => expect(count).toBeGreaterThan(0));
      expect((await pending).status).toBe(200);
      expect(await inFlight()).toBe(0);
    });

    it('returns to 0 after a request aborted by the client', async () => {
      await control('faults', { delayMs: 1000 });
      const controller = new AbortController();
      const pending = fetch(`${store.origin}/bucket/recording/1?partNumber=1`, {
        method: 'PUT',
        headers: { origin: ORIGIN },
        body: new Uint8Array(Buffer.from('one')),
        signal: controller.signal,
      }).catch((error: unknown) => error);
      await untilInFlight((count) => expect(count).toBeGreaterThan(0));
      controller.abort();
      await pending;
      await untilInFlight((count) => expect(count).toBe(0));
    });

    it('is not changed by reset', async () => {
      await control('faults', { delayMs: 400 });
      const pending = put(1, Buffer.from('one'));
      await untilInFlight((count) => expect(count).toBeGreaterThan(0));
      const before = await inFlight();
      await control('reset');
      expect(await inFlight()).toBe(before);
      await pending;
      expect(await inFlight()).toBe(0);
    });

    it('does not count preflights', async () => {
      await control('faults', { delayMs: 400 });
      const preflight = await fetch(`${store.origin}/bucket/recording/1`, {
        method: 'OPTIONS',
        headers: {
          origin: ORIGIN,
          'access-control-request-method': 'PUT',
          'access-control-request-headers': 'x-amz-checksum-sha256',
        },
      });
      expect(preflight.status).toBe(204);
      expect(await inFlight()).toBe(0);
    });
  });
});
