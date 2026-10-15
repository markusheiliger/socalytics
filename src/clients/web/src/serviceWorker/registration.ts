import { serviceWorkerScriptUrl } from '../trustedTypes';
import { compareVersions, isVersion } from './version';

export type UpdateState = 'current' | 'available';

interface WorkerLike {
  state?: string;
  postMessage(message: unknown, transfer?: Transferable[]): void;
  addEventListener?(type: 'statechange', listener: () => void): void;
}

interface RegistrationLike {
  waiting?: WorkerLike | null;
  installing?: WorkerLike | null;
  update?(): Promise<unknown>;
  addEventListener?(type: 'updatefound', listener: () => void): void;
}

export interface ServiceWorkerHost {
  serviceWorker?: {
    controller?: unknown;
    register(url: string, options: { scope: string; updateViaCache: 'none' }): Promise<unknown>;
    addEventListener?(type: 'controllerchange', listener: () => void): void;
  };
}

export const defaultCheckPeriodMs = 15 * 60 * 1000;

// The check period is a non-production value; builds for tests shorten it with VITE_SOCALYTICS_UPDATE_CHECK_MS.
function configuredPeriod(): number {
  const value = Number(import.meta.env?.VITE_SOCALYTICS_UPDATE_CHECK_MS);
  return Number.isFinite(value) && value > 0 ? value : defaultCheckPeriodMs;
}

// In-memory update state (data-model.md § Update State); `required` states arrive with the version-skew work.
let state: UpdateState = 'current';
let waiting: WorkerLike | undefined;
const listeners = new Set<() => void>();

export function getUpdateState(): UpdateState {
  return state;
}

export function subscribeUpdateState(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function setUpdate(next: UpdateState, worker?: WorkerLike): void {
  waiting = worker;
  if (state !== next) {
    state = next;
    listeners.forEach((listener) => listener());
  }
}

export function resetUpdateState(): void {
  waiting = undefined;
  state = 'current';
  listeners.forEach((listener) => listener());
}

// Asks a worker for its release version; undefined when it does not answer.
function askVersion(worker: WorkerLike): Promise<string | undefined> {
  return new Promise((resolve) => {
    if (typeof MessageChannel === 'undefined') {
      resolve(undefined);
      return;
    }
    const channel = new MessageChannel();
    const timer = setTimeout(() => resolve(undefined), 2000);
    channel.port1.onmessage = (event) => {
      clearTimeout(timer);
      channel.port1.close();
      resolve(typeof event.data?.version === 'string' ? event.data.version : undefined);
    };
    worker.postMessage({ type: 'VERSION' }, [channel.port2]);
  });
}

// A waiting worker only counts as an update when it is newer than the running release, so a release is never
// replaced by an older one.
async function offerUpdate(worker: WorkerLike): Promise<void> {
  const running = import.meta.env?.VITE_SOCALYTICS_WEB_VERSION;
  const offered = await askVersion(worker);
  if (isVersion(running) && isVersion(offered) && compareVersions(offered, running) <= 0) {
    return;
  }
  setUpdate('available', worker);
}

/** Activates the waiting worker; every window reloads on `controllerchange`. */
export function applyUpdate(): void {
  waiting?.postMessage({ type: 'SKIP_WAITING' });
}

function watch(registration: RegistrationLike, hasController: () => boolean): void {
  if (registration.waiting && hasController()) {
    void offerUpdate(registration.waiting);
  }
  registration.addEventListener?.('updatefound', () => {
    const installing = registration.installing;
    installing?.addEventListener?.('statechange', () => {
      if (installing.state === 'installed' && hasController()) {
        void offerUpdate(installing);
      }
    });
  });
}

function scheduleChecks(registration: RegistrationLike): void {
  const check = () => {
    registration.update?.().catch(() => undefined);
  };
  check();
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      check();
    }
  });
  setInterval(check, configuredPeriod());
}

// Registers `/sw.js`; a no-op without service worker support. Failures are logged without user data.
export async function registerServiceWorker(host: ServiceWorkerHost | undefined = globalThis.navigator, createScriptUrl: (url: string) => string = serviceWorkerScriptUrl): Promise<boolean> {
  const container = host?.serviceWorker;
  if (!container) {
    return false;
  }
  try {
    const registration = (await container.register(createScriptUrl('/sw.js'), { scope: '/', updateViaCache: 'none' })) as RegistrationLike | undefined;
    if (registration) {
      // A first installation has no controller yet and must not reload the page when it takes control.
      const hadController = !!container.controller;
      container.addEventListener?.('controllerchange', () => {
        if (hadController) {
          globalThis.location.reload();
        }
      });
      watch(registration, () => !!container.controller);
      scheduleChecks(registration);
    }
    return true;
  } catch (error) {
    console.warn('Service worker registration failed.', error instanceof Error ? error.name : 'unknown');
    return false;
  }
}
