// Fake S3-compatible object store for the `simulated` browser tests (research.md § R14,
// contracts/development-storage.md § Simulated tests). It is a real HTTP server because requests from dedicated workers
// are not reliably routed by `page.route`.
//
// Part uploads:   PUT /<anything>/<n>?partNumber=<n>   (the part number is the query value or the last path segment)
// Reads:          GET|HEAD /<key> for seeded objects, with `Range` (206), and a 403 once the query's expiry has passed
//                 (`Expires` in epoch seconds, or `X-Amz-Date` plus `X-Amz-Expires`)
// Control:        GET /__control/health | GET /__control/state | POST /__control/reset | POST /__control/faults
//                 | POST /__control/objects {key, contentBase64, contentType}
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { pathToFileURL } from 'node:url';

const CONTROL = '/__control/';
const ALLOWED_REQUEST_HEADERS = new Set(['x-amz-checksum-sha256', 'content-type', 'content-length']);

const READ_METHODS = new Set(['GET', 'HEAD']);
const READ_REQUEST_HEADERS = new Set(['range', 'if-range']);

function expiryOf(url) {
  const expires = url.searchParams.get('Expires');
  if (expires !== null) return Number(expires) * 1000;
  const date = /^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z$/.exec(url.searchParams.get('X-Amz-Date') ?? '');
  const lifetime = url.searchParams.get('X-Amz-Expires');
  if (!date || lifetime === null) return null;
  return Date.UTC(+date[1], +date[2] - 1, +date[3], +date[4], +date[5], +date[6]) + Number(lifetime) * 1000;
}

// Returns {start, end} (inclusive), 'unsatisfiable', or null when the header is absent or not a single byte range.
function parseRange(header, size) {
  const match = /^bytes=(\d*)-(\d*)$/.exec(String(header ?? '').trim());
  if (!match || (match[1] === '' && match[2] === '')) return null;
  let start;
  let end;
  if (match[1] === '') {
    const suffix = Number(match[2]);
    if (suffix === 0) return 'unsatisfiable';
    start = Math.max(size - suffix, 0);
    end = size - 1;
  } else {
    start = Number(match[1]);
    end = match[2] === '' ? size - 1 : Math.min(Number(match[2]), size - 1);
  }
  return start >= size || start > end ? 'unsatisfiable' : { start, end };
}

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
  /** @type {Map<string, { bytes: Buffer, contentType: string }>} */
  let objects = new Map();
  let faults = emptyFaults();

  const allowedOrigins = new Set(allowedOrigin.split(',').map((o) => o.trim()));
  const cors = (origin) =>
    origin && allowedOrigins.has(origin)
      ? {
          'access-control-allow-origin': origin,
          'access-control-expose-headers': 'ETag, Content-Range, Content-Length, Accept-Ranges',
          vary: 'Origin',
        }
      : {};

  const seedObject = (key, bytes, contentType = 'application/octet-stream') =>
    objects.set(key.replace(/^\/+/, ''), { bytes: Buffer.from(bytes), contentType });

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
    if (req.method === 'GET' && action === 'state') {
      return sendJson(res, 200, {
        parts,
        requests,
        faults,
        objects: [...objects].map(([key, o]) => ({ key, size: o.bytes.length, contentType: o.contentType })),
      });
    }
    if (req.method === 'POST' && action === 'reset') {
      parts = [];
      requests = [];
      objects = new Map();
      faults = emptyFaults();
      return sendJson(res, 200, { status: 'reset' });
    }
    if (req.method === 'POST' && action === 'objects') {
      const body = JSON.parse((await readBody(req)).toString('utf8') || '{}');
      if (typeof body.key !== 'string' || typeof body.contentBase64 !== 'string') {
        return sendJson(res, 400, { error: 'key and contentBase64 are required' });
      }
      seedObject(body.key, Buffer.from(body.contentBase64, 'base64'), body.contentType);
      return sendJson(res, 200, { status: 'seeded', key: body.key });
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

  async function read(req, res, url) {
    const origin = req.headers.origin;
    const key = decodeURIComponent(url.pathname).replace(/^\/+/, '');
    const range = req.headers.range ?? null;
    requests.push({ method: req.method, path: url.pathname, range, origin: origin ?? null });

    const expiry = expiryOf(url);
    if (expiry !== null && Date.now() > expiry) {
      return send(res, 403, { ...cors(origin), 'content-type': 'application/xml' }, xmlError('AccessDenied', 'Request has expired'));
    }
    const object = objects.get(key);
    if (!object) {
      return send(res, 404, { ...cors(origin), 'content-type': 'application/xml' }, xmlError('NoSuchKey', 'The object does not exist'));
    }

    const size = object.bytes.length;
    const headers = {
      ...cors(origin),
      'accept-ranges': 'bytes',
      'content-type': object.contentType,
      etag: `"${createHash('sha256').update(object.bytes).digest('hex')}"`,
    };
    const requested = parseRange(range, size);
    if (requested === 'unsatisfiable') {
      return send(res, 416, { ...headers, 'content-type': 'application/xml', 'content-range': `bytes */${size}` }, xmlError('InvalidRange', 'The requested range is not satisfiable'));
    }
    const slice = requested ? object.bytes.subarray(requested.start, requested.end + 1) : object.bytes;
    if (requested) headers['content-range'] = `bytes ${requested.start}-${requested.end}/${size}`;
    headers['content-length'] = String(slice.length);
    res.writeHead(requested ? 206 : 200, headers);
    res.end(req.method === 'HEAD' ? undefined : slice);
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
      const isRead = READ_METHODS.has(method);
      const allowedHeaders = isRead ? READ_REQUEST_HEADERS : ALLOWED_REQUEST_HEADERS;
      if ((method !== 'PUT' && !isRead) || requested.some((h) => !allowedHeaders.has(h))) {
        return send(res, 403, {}, xmlError('AccessForbidden', 'CORS request refused'));
      }
      return send(res, 204, {
        ...cors(origin),
        'access-control-allow-methods': isRead ? 'GET, HEAD' : 'PUT',
        'access-control-allow-headers': requested.join(', '),
        'access-control-max-age': '600',
      });
    }

    if (READ_METHODS.has(req.method)) return read(req, res, url);

    if (req.method !== 'PUT') return send(res, 405, cors(origin), xmlError('MethodNotAllowed', 'Only PUT is accepted'));

    const body = await readBody(req);
    const segments = url.pathname.split('/').filter(Boolean);
    const partNumber = Number(url.searchParams.get('partNumber') ?? segments.at(-1));
    const claimed = req.headers['x-amz-checksum-sha256'];
    requests.push({ method: 'PUT', path: url.pathname, partNumber, size: body.length, checksum: claimed ?? null });

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
    seedObject,
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
