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

  it('delays responses', async () => {
    await control('faults', { delayMs: 150 });
    const started = Date.now();
    await put(1, Buffer.from('one'));
    expect(Date.now() - started).toBeGreaterThanOrEqual(140);
  });
});
