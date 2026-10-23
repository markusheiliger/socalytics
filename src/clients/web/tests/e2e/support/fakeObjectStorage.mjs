// Fake S3-compatible object store for the `simulated` browser tests (research.md § R14,
// contracts/development-storage.md § Simulated tests). It is a real HTTP server because requests from dedicated workers
// are not reliably routed by `page.route`.
//
// Part uploads:   PUT /<anything>/<n>?partNumber=<n>   (the part number is the query value or the last path segment)
// Control:        GET /__control/health | GET /__control/state | POST /__control/reset | POST /__control/faults
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { pathToFileURL } from 'node:url';

const CONTROL = '/__control/';
const ALLOWED_REQUEST_HEADERS = new Set(['x-amz-checksum-sha256', 'content-type', 'content-length']);

function emptyFaults() {
  return { failParts: [], delayMs: 0, expireGrants: false };
}

function xmlError(code, message) {
  return `<?xml version="1.0" encoding="UTF-8"?><Error><Code>${code}</Code><Message>${message}</Message></Error>`;
}

/**
 * Starts the fake store. `allowedOrigin` (one origin, or several separated by commas) is the only origin whose preflights and responses carry CORS headers.
 * Resolves to `{ server, port, origin, close }`; `port: 0` picks a free port.
 */
export async function startFakeObjectStorage({ port = 0, allowedOrigin, host = '127.0.0.1' } = {}) {
  /** @type {{ key: string, partNumber: number, size: number, sha256: string }[]} */
  let parts = [];
  let requests = [];
  let faults = emptyFaults();

  const allowedOrigins = new Set(allowedOrigin.split(',').map((o) => o.trim()));
  const cors = (origin) =>
    origin && allowedOrigins.has(origin)
      ? { 'access-control-allow-origin': origin, 'access-control-expose-headers': 'ETag', vary: 'Origin' }
      : {};

  const send = (res, status, headers = {}, body = '') => {
    res.writeHead(status, headers);
    res.end(body);
  };

  const sendJson = (res, status, value) =>
    send(res, status, { 'content-type': 'application/json' }, JSON.stringify(value));

  const readBody = (req) =>
    new Promise((resolve, reject) => {
      const chunks = [];
      req.on('data', (chunk) => chunks.push(chunk));
      req.on('end', () => resolve(Buffer.concat(chunks)));
      req.on('error', reject);
    });

  async function control(req, res, url) {
    const action = url.pathname.slice(CONTROL.length);
    if (req.method === 'GET' && action === 'health') return sendJson(res, 200, { status: 'ok' });
    if (req.method === 'GET' && action === 'state') return sendJson(res, 200, { parts, requests, faults });
    if (req.method === 'POST' && action === 'reset') {
      parts = [];
      requests = [];
      faults = emptyFaults();
      return sendJson(res, 200, { status: 'reset' });
    }
    if (req.method === 'POST' && action === 'faults') {
      const body = JSON.parse((await readBody(req)).toString('utf8') || '{}');
      // failPart: { partNumber, times: number | 'always', status? } (fail part n that many times, or on every request)
      if (body.failPart) {
        faults.failParts.push({
          partNumber: Number(body.failPart.partNumber),
          remaining: body.failPart.times === 'always' ? Infinity : Number(body.failPart.times ?? 1),
          status: Number(body.failPart.status ?? 500),
        });
      }
      if (body.delayMs !== undefined) faults.delayMs = Number(body.delayMs);
      if (body.expireGrants !== undefined) faults.expireGrants = Boolean(body.expireGrants);
      return sendJson(res, 200, { faults });
    }
    return sendJson(res, 404, { error: 'unknown control action' });
  }

  async function storage(req, res, url) {
    const origin = req.headers.origin;

    if (req.method === 'OPTIONS') {
      if (!origin || !allowedOrigins.has(origin)) return send(res, 403, {}, xmlError('AccessForbidden', 'CORS origin refused'));
      const method = req.headers['access-control-request-method'];
      const requested = String(req.headers['access-control-request-headers'] ?? '')
        .split(',')
        .map((h) => h.trim().toLowerCase())
        .filter(Boolean);
      if (method !== 'PUT' || requested.some((h) => !ALLOWED_REQUEST_HEADERS.has(h))) {
        return send(res, 403, {}, xmlError('AccessForbidden', 'CORS request refused'));
      }
      return send(res, 204, {
        ...cors(origin),
        'access-control-allow-methods': 'PUT',
        'access-control-allow-headers': requested.join(', '),
        'access-control-max-age': '600',
      });
    }

    if (req.method !== 'PUT') return send(res, 405, cors(origin), xmlError('MethodNotAllowed', 'Only PUT is accepted'));

    const body = await readBody(req);
    const segments = url.pathname.split('/').filter(Boolean);
    const partNumber = Number(url.searchParams.get('partNumber') ?? segments.at(-1));
    const claimed = req.headers['x-amz-checksum-sha256'];
    requests.push({ method: 'PUT', path: url.pathname, partNumber, size: body.length, checksum: claimed ?? null, ts: Date.now() });

    if (faults.delayMs > 0) await new Promise((resolve) => setTimeout(resolve, faults.delayMs));

    if (faults.expireGrants) {
      return send(res, 403, { ...cors(origin), 'content-type': 'application/xml' }, xmlError('AccessDenied', 'Request has expired'));
    }
    const fault = faults.failParts.find((f) => f.partNumber === partNumber && f.remaining > 0);
    if (fault) {
      fault.remaining -= 1;
      return send(res, fault.status, { ...cors(origin), 'content-type': 'application/xml' }, xmlError('InternalError', 'Injected failure'));
    }

    const actual = createHash('sha256').update(body).digest('base64');
    if (claimed !== actual) {
      return send(res, 400, { ...cors(origin), 'content-type': 'application/xml' }, xmlError('BadDigest', 'The checksum does not match the content'));
    }

    const key = segments.slice(0, -1).join('/') || url.pathname;
    parts = parts.filter((p) => !(p.key === key && p.partNumber === partNumber));
    parts.push({ key, partNumber, size: body.length, sha256: actual });
    return send(res, 200, { ...cors(origin), etag: `"${actual}"` });
  }

  const server = createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://fake-object-storage');
    const handler = url.pathname.startsWith(CONTROL) ? control : storage;
    handler(req, res, url).catch((error) => send(res, 500, {}, xmlError('InternalError', String(error?.message ?? error))));
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, host, resolve);
  });
  const actualPort = server.address().port;

  return {
    server,
    port: actualPort,
    origin: `http://${host}:${actualPort}`,
    close: () => new Promise((resolve) => (server.close(() => resolve()), server.closeAllConnections())),
  };
}

// Started by Playwright as a `webServer`: FAKE_STORAGE_PORT and FAKE_STORAGE_ALLOWED_ORIGIN come from playwright.config.ts.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { origin } = await startFakeObjectStorage({
    port: Number(process.env.FAKE_STORAGE_PORT ?? 4176),
    allowedOrigin: process.env.FAKE_STORAGE_ALLOWED_ORIGIN ?? 'http://127.0.0.1:4173',
  });
  console.log(`Fake object storage listening on ${origin}`);
}
