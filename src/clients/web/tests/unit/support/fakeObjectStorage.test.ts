// @vitest-environment node
import { createHash } from 'node:crypto';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
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
  (await (await control('state')).json()) as { parts: { partNumber: number; size: number }[]; requests: unknown[] };

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

  describe('seeded objects', () => {
    const bytes = Buffer.from('0123456789');
    const seed = (key = 'bucket/clip.mp4') =>
      control('objects', { key, contentBase64: bytes.toString('base64'), contentType: 'video/mp4' });
    const get = (query = '', headers: Record<string, string> = {}, method = 'GET') =>
      fetch(`${store.origin}/bucket/clip.mp4${query}`, { method, headers: { origin: ORIGIN, ...headers } });

    it('serves a seeded object with its type and Accept-Ranges', async () => {
      await seed();
      const response = await get();
      expect(response.status).toBe(200);
      expect(response.headers.get('content-type')).toBe('video/mp4');
      expect(response.headers.get('accept-ranges')).toBe('bytes');
      expect(response.headers.get('access-control-allow-origin')).toBe(ORIGIN);
      expect(Buffer.from(await response.arrayBuffer())).toEqual(bytes);
    });

    it('answers a range with 206 and Content-Range', async () => {
      await seed();
      const response = await get('', { range: 'bytes=2-5' });
      expect(response.status).toBe(206);
      expect(response.headers.get('content-range')).toBe('bytes 2-5/10');
      expect(await response.text()).toBe('2345');
      const open = await get('', { range: 'bytes=7-' });
      expect(open.headers.get('content-range')).toBe('bytes 7-9/10');
      const suffix = await get('', { range: 'bytes=-3' });
      expect(await suffix.text()).toBe('789');
    });

    it('refuses an unsatisfiable range with 416', async () => {
      await seed();
      const response = await get('', { range: 'bytes=20-30' });
      expect(response.status).toBe(416);
      expect(response.headers.get('content-range')).toBe('bytes */10');
    });

    it('answers HEAD with the headers and no body', async () => {
      await seed();
      const response = await get('', { range: 'bytes=0-3' }, 'HEAD');
      expect(response.status).toBe(206);
      expect(response.headers.get('content-length')).toBe('4');
      expect(response.headers.get('content-range')).toBe('bytes 0-3/10');
      expect(await response.text()).toBe('');
    });

    it('refuses with 403 after the query expiry and serves before it', async () => {
      await seed();
      const past = Math.floor(Date.now() / 1000) - 60;
      const future = Math.floor(Date.now() / 1000) + 600;
      const expired = await get(`?Expires=${past}`);
      expect(expired.status).toBe(403);
      expect(await expired.text()).toContain('AccessDenied');
      expect((await get(`?Expires=${future}`)).status).toBe(200);
      const stamp = new Date(Date.now() - 120_000).toISOString().replace(/[-:]|\.\d{3}/g, '');
      expect((await get(`?X-Amz-Date=${stamp}&X-Amz-Expires=60`)).status).toBe(403);
      expect((await get(`?X-Amz-Date=${stamp}&X-Amz-Expires=3600`)).status).toBe(200);
    });

    it('answers 404 for an unknown key', async () => {
      expect((await get()).status).toBe(404);
    });

    it('adds CORS headers only for the configured origin', async () => {
      await seed();
      const other = await get('', { origin: 'http://evil.example' });
      expect(other.status).toBe(200);
      expect(other.headers.get('access-control-allow-origin')).toBeNull();
      const none = await fetch(`${store.origin}/bucket/clip.mp4`);
      expect(none.headers.get('access-control-allow-origin')).toBeNull();
    });

    it('answers a Range preflight for the allowed origin only', async () => {
      const preflight = (origin: string) =>
        fetch(`${store.origin}/bucket/clip.mp4`, {
          method: 'OPTIONS',
          headers: { origin, 'access-control-request-method': 'GET', 'access-control-request-headers': 'range' },
        });
      const allowed = await preflight(ORIGIN);
      expect(allowed.status).toBe(204);
      expect(allowed.headers.get('access-control-allow-methods')).toBe('GET, HEAD');
      expect((await preflight('http://evil.example')).status).toBe(403);
    });

    it('seeds in process and reset removes the objects', async () => {
      store.seedObject('bucket/clip.mp4', bytes, 'video/mp4');
      expect((await get()).status).toBe(200);
      await control('reset');
      expect((await get()).status).toBe(404);
    });
  });

  it('delays responses', async () => {
    await control('faults', { delayMs: 150 });
    const started = Date.now();
    await put(1, Buffer.from('one'));
    expect(Date.now() - started).toBeGreaterThanOrEqual(140);
  });
});
