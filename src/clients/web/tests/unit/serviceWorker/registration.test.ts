import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  applyUpdate,
  getUpdateState,
  isUpdateBlocked,
  registerServiceWorker,
  requireUpdate,
  requiredReloadDelayMs,
  resetUpdateState,
} from '../../../src/serviceWorker/registration';

describe('service worker registration', () => {
  it('registers /sw.js at the root scope without caching the script', async () => {
    const register = vi.fn(async () => ({}));
    const createScriptUrl = vi.fn((url: string) => url);
    expect(await registerServiceWorker({ serviceWorker: { register } }, createScriptUrl)).toBe(true);
    expect(createScriptUrl).toHaveBeenCalledWith('/sw.js');
    expect(register).toHaveBeenCalledWith('/sw.js', { scope: '/', updateViaCache: 'none' });
  });

  it('is a no-op without service worker support', async () => {
    expect(await registerServiceWorker({})).toBe(false);
    expect(await registerServiceWorker(undefined)).toBe(false);
  });

  it('logs failures without user data', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const register = vi.fn(async () => {
      throw new TypeError('secret detail');
    });
    expect(await registerServiceWorker({ serviceWorker: { register } }, (url) => url)).toBe(false);
    expect(warn).toHaveBeenCalledWith('Service worker registration failed.', 'TypeError');
    warn.mockRestore();
  });

  it('refuses any script URL other than /sw.js through the default policy', async () => {
    const { serviceWorkerScriptUrl } = await import('../../../src/trustedTypes');
    expect(serviceWorkerScriptUrl('/sw.js')).toBe('/sw.js');
    expect(() => serviceWorkerScriptUrl('/other.js')).toThrow(TypeError);
    expect(() => serviceWorkerScriptUrl('https://evil.test/sw.js')).toThrow(TypeError);
  });
});

describe('update states', () => {
  afterEach(() => {
    resetUpdateState();
    vi.useRealTimers();
    vi.unstubAllEnvs();
  });

  function fakeHost(waiting?: { postMessage: ReturnType<typeof vi.fn> }) {
    const listeners: Record<string, () => void> = {};
    const update = vi.fn(async () => undefined);
    const registration = { waiting, update, addEventListener: vi.fn() };
    const host = {
      serviceWorker: {
        controller: {},
        register: vi.fn(async () => registration),
        addEventListener: (type: string, listener: () => void) => {
          listeners[type] = listener;
        },
      },
    };
    return { host, update, listeners };
  }

  const answering = (version: string) =>
    vi.fn((message: { type: string }, transfer?: Transferable[]) => {
      if (message.type === 'VERSION') (transfer?.[0] as MessagePort).postMessage({ version });
    });

  it('checks for updates at start, when the page becomes visible, and every 15 minutes', async () => {
    vi.useFakeTimers();
    const { host, update } = fakeHost();
    await registerServiceWorker(host, (url) => url);
    expect(update).toHaveBeenCalledTimes(1);
    document.dispatchEvent(new Event('visibilitychange'));
    expect(update).toHaveBeenCalledTimes(2);
    vi.advanceTimersByTime(15 * 60 * 1000);
    expect(update).toHaveBeenCalledTimes(3);
  });

  it('is current without a waiting worker and available with a newer one', async () => {
    vi.stubEnv('VITE_SOCALYTICS_WEB_VERSION', '2026.1015.100000');
    expect(getUpdateState()).toBe('current');
    const waiting = { postMessage: answering('2026.1015.110000') };
    await registerServiceWorker(fakeHost(waiting).host, (url) => url);
    await vi.waitFor(() => expect(getUpdateState()).toBe('available'));
  });

  it('never offers a worker of an older or the same release', async () => {
    vi.stubEnv('VITE_SOCALYTICS_WEB_VERSION', '2026.1015.110000');
    const waiting = { postMessage: answering('2026.1015.100000') };
    await registerServiceWorker(fakeHost(waiting).host, (url) => url);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(getUpdateState()).toBe('current');
  });

  it('posts SKIP_WAITING on update and reloads on controllerchange', async () => {
    vi.stubEnv('VITE_SOCALYTICS_WEB_VERSION', '2026.1015.100000');
    const waiting = { postMessage: answering('2026.1015.110000') };
    const { host, listeners } = fakeHost(waiting);
    await registerServiceWorker(host, (url) => url);
    await vi.waitFor(() => expect(getUpdateState()).toBe('available'));
    applyUpdate();
    expect(waiting.postMessage).toHaveBeenCalledWith({ type: 'SKIP_WAITING' });
    const reload = vi.fn();
    vi.stubGlobal('location', { ...window.location, reload });
    listeners.controllerchange();
    expect(reload).toHaveBeenCalled();
    vi.unstubAllGlobals();
  });
});

describe('required update states', () => {
  afterEach(() => {
    vi.useRealTimers();
    resetUpdateState();
    vi.unstubAllEnvs();
  });

  it('enters required, blocks, and becomes reload-required after 30 seconds without a newer worker', () => {
    vi.useFakeTimers();
    requireUpdate('2026.1015.110000');
    expect(getUpdateState()).toBe('required');
    expect(isUpdateBlocked()).toBe(true);
    vi.advanceTimersByTime(requiredReloadDelayMs);
    expect(getUpdateState()).toBe('reload-required');
    expect(isUpdateBlocked()).toBe(true);
  });

  it('activates a waiting worker that satisfies the minimum', async () => {
    vi.stubEnv('VITE_SOCALYTICS_WEB_VERSION', '2026.1015.100000');
    const postMessage = vi.fn((message: { type: string }, transfer?: Transferable[]) => {
      if (message.type === 'VERSION') (transfer?.[0] as MessagePort).postMessage({ version: '2026.1015.110000' });
    });
    const registration = { waiting: { postMessage }, update: vi.fn(async () => undefined), addEventListener: vi.fn() };
    await registerServiceWorker({ serviceWorker: { controller: undefined, register: async () => registration } }, (url) => url);
    requireUpdate('2026.1015.110000');
    await vi.waitFor(() => expect(postMessage).toHaveBeenCalledWith({ type: 'SKIP_WAITING' }));
    expect(registration.update).toHaveBeenCalled();
  });
});
