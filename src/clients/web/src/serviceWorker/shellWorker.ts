// Pure routing and cache logic of the service worker (contracts/web-ui-pwa.md § 3). `sw.ts` wires it to the worker
// scope; keeping it free of worker globals makes it testable and keeps `dist/sw.js` a single self-contained file.
export const shellCachePrefix = 'socalytics-shell-';

export interface ShellManifestFile {
  url: string;
  integrity: string;
}

export interface ShellManifestDocument {
  version: string;
  files: ShellManifestFile[];
}

export interface ShellCaches {
  open(name: string): Promise<{ addAll(requests: Request[]): Promise<void> }>;
  keys(): Promise<string[]>;
  delete(name: string): Promise<boolean>;
  match(request: string | Request, options?: { cacheName?: string; ignoreVary?: boolean }): Promise<Response | undefined>;
}

export type ShellFetch = (input: string | Request, init?: RequestInit) => Promise<Response>;

export type RequestKind = 'navigation' | 'asset';

export function shellCacheName(version: string): string {
  return `${shellCachePrefix}${version}`;
}

function parseManifest(value: unknown): ShellManifestDocument {
  const candidate = value as { version?: unknown; files?: unknown } | null;
  if (!candidate || typeof candidate.version !== 'string' || !Array.isArray(candidate.files)) {
    throw new Error('The shell manifest is malformed.');
  }
  const files = candidate.files.map((file: unknown) => {
    const entry = file as { url?: unknown; integrity?: unknown } | null;
    if (!entry || typeof entry.url !== 'string' || !entry.url.startsWith('/') || typeof entry.integrity !== 'string' || !entry.integrity.startsWith('sha384-')) {
      throw new Error('The shell manifest is malformed.');
    }
    return { url: entry.url, integrity: entry.integrity };
  });
  return { version: candidate.version, files };
}

// Caches every listed file under its integrity value; any failure fails the install and leaves no partial cache.
export async function installShell(version: string, caches: ShellCaches, fetcher: ShellFetch, base: string = globalThis.location.href): Promise<void> {
  const response = await fetcher('/shell-manifest.json', { cache: 'no-store' });
  if (!response.ok) {
    throw new Error(`The shell manifest could not be loaded (${response.status}).`);
  }
  const manifest = parseManifest(await response.json());
  if (manifest.version !== version) {
    throw new Error('The shell manifest belongs to a different release.');
  }
  const name = shellCacheName(version);
  const cache = await caches.open(name);
  try {
    await cache.addAll(manifest.files.map((file) => new Request(new URL(file.url, base).href, { integrity: file.integrity, cache: 'no-cache' })));
  } catch (error) {
    await caches.delete(name);
    throw error;
  }
}

export async function activateShell(version: string, caches: ShellCaches): Promise<void> {
  const current = shellCacheName(version);
  const names = await caches.keys();
  await Promise.all(names.filter((name) => name.startsWith(shellCachePrefix) && name !== current).map((name) => caches.delete(name)));
}

const unhandledPrefixes = ['/api/', '/alive', '/health', '/openapi'];

// `undefined` means the worker does not handle the request: other origins, non-GET, and the platform endpoints.
export function classifyRequest(request: { method: string; mode: string; url: string }, origin: string): RequestKind | undefined {
  if (request.method !== 'GET') {
    return undefined;
  }
  const url = new URL(request.url);
  if (url.origin !== origin) {
    return undefined;
  }
  if (unhandledPrefixes.some((prefix) => url.pathname === prefix.replace(/\/$/, '') || url.pathname.startsWith(prefix.endsWith('/') ? prefix : `${prefix}/`))) {
    return undefined;
  }
  if (request.mode === 'navigate') {
    return 'navigation';
  }
  return 'asset';
}

// Navigations get the cached `/index.html`; listed shell URLs come from the cache; everything else goes to the network.
export async function respondFromShell(request: Request, kind: RequestKind, version: string, caches: ShellCaches, fetcher: ShellFetch): Promise<Response> {
  const cacheName = shellCacheName(version);
  const cached = await caches.match(kind === 'navigation' ? '/index.html' : request, { cacheName, ignoreVary: true });
  return cached ?? fetcher(request);
}
