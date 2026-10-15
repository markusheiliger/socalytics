import { describe, expect, it, vi } from 'vitest';
import { registerServiceWorker } from '../../../src/serviceWorker/registration';

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
