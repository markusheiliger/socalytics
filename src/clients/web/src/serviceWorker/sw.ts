import { activateShell, classifyRequest, installShell, respondFromShell, type ShellCaches } from './shellWorker';

// Built as the separate entry `dist/sw.js`; the build stamps the release version over this placeholder.
const version = '__SOCALYTICS_WEB_VERSION__';

interface WorkerEvent {
  waitUntil(promise: Promise<unknown>): void;
}

interface FetchWorkerEvent extends WorkerEvent {
  request: Request;
  respondWith(response: Promise<Response>): void;
}

interface WorkerScope {
  location: { origin: string };
  addEventListener(type: 'install' | 'activate', listener: (event: WorkerEvent) => void): void;
  addEventListener(type: 'fetch', listener: (event: FetchWorkerEvent) => void): void;
  addEventListener(type: 'message', listener: (event: { data?: unknown; ports?: readonly { postMessage(message: unknown): void }[] }) => void): void;
  skipWaiting(): Promise<void>;
}

const scope = globalThis as unknown as WorkerScope;
const shellCaches = caches as unknown as ShellCaches;

scope.addEventListener('install', (event) => {
  event.waitUntil(installShell(version, shellCaches, (input, init) => fetch(input, init)));
});

scope.addEventListener('activate', (event) => {
  event.waitUntil(activateShell(version, shellCaches));
});

scope.addEventListener('fetch', (event) => {
  const kind = classifyRequest(event.request, scope.location.origin);
  if (kind) {
    event.respondWith(respondFromShell(event.request, kind, version, shellCaches, (input, init) => fetch(input, init)));
  }
});

scope.addEventListener('message', (event) => {
  if ((event.data as { type?: unknown } | null)?.type === 'SKIP_WAITING') {
    void scope.skipWaiting();
  } else if ((event.data as { type?: unknown } | null)?.type === 'VERSION') {
    event.ports?.[0]?.postMessage({ version });
  }
});
