import { serviceWorkerScriptUrl } from '../trustedTypes';

export interface ServiceWorkerHost {
  serviceWorker?: { register(url: string, options: { scope: string; updateViaCache: 'none' }): Promise<unknown> };
}

// Registers `/sw.js`; a no-op without service worker support. Failures are logged without user data.
export async function registerServiceWorker(host: ServiceWorkerHost | undefined = globalThis.navigator, createScriptUrl: (url: string) => string = serviceWorkerScriptUrl): Promise<boolean> {
  if (!host?.serviceWorker) {
    return false;
  }
  try {
    await host.serviceWorker.register(createScriptUrl('/sw.js'), { scope: '/', updateViaCache: 'none' });
    return true;
  } catch (error) {
    console.warn('Service worker registration failed.', error instanceof Error ? error.name : 'unknown');
    return false;
  }
}
