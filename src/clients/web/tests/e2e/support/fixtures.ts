import { expect, test as base } from '@playwright/test';
import { ANTIFORGERY_TOKEN, SIMULATED_PASSWORD, SimulatedApi } from './simulatedApi';

type Fixtures = {
  /** Simulated platform API, installed before the test runs. */
  api: SimulatedApi;
  /** Secret values that must never appear in browser storage; tests may add their own. */
  secrets: string[];
  /** Fails the test on any Content Security Policy violation and on secrets in browser storage. */
  guards: void;
};

const SECRET_KEY = /password|credential|passcode|csrf|antiforgery|secret/i;
const GRANT_MATERIAL = /X-Amz-Signature|x-amz-checksum-sha256|X-Amz-Credential|\/bucket\/[^"\s]*\?partNumber=|127\.0\.0\.1:4175/i;

export const test = base.extend<Fixtures>({
  secrets: async ({}, use) => {
    await use([ANTIFORGERY_TOKEN, SIMULATED_PASSWORD]);
  },

  api: async ({ page }, use) => {
    const api = new SimulatedApi(page);
    await api.install();
    await use(api);
  },

  guards: [
    async ({ page, secrets }, use) => {
      const violations: string[] = [];
      await page.exposeFunction('__reportCspViolation', (detail: string) => violations.push(detail));
      await page.addInitScript(() => {
        document.addEventListener('securitypolicyviolation', (event) => {
          (window as unknown as { __reportCspViolation: (d: string) => void }).__reportCspViolation(
            `${event.violatedDirective}: ${event.blockedURI || event.sample || 'inline'}`,
          );
        });
      });

      await use();

      expect(violations, 'Content Security Policy violations').toEqual([]);

      if (page.url() === 'about:blank') return;
      const stored = await page.evaluate(async () => {
        const entries: string[] = [];
        for (const storage of [localStorage, sessionStorage]) {
          for (let i = 0; i < storage.length; i++) {
            const key = storage.key(i) ?? '';
            entries.push(`${key}=${storage.getItem(key) ?? ''}`);
          }
        }
        entries.push(...document.cookie.split(';').map((c) => c.trim()).filter(Boolean));
        const databases = (await indexedDB.databases?.()) ?? [];
        for (const info of databases) {
          if (!info.name) continue;
          const db = await new Promise<IDBDatabase>((resolve, reject) => {
            const open = indexedDB.open(info.name as string);
            open.onsuccess = () => resolve(open.result);
            open.onerror = () => reject(open.error);
          });
          for (const storeName of Array.from(db.objectStoreNames)) {
            const rows = await new Promise<unknown[]>((resolve, reject) => {
              const request = db.transaction(storeName, 'readonly').objectStore(storeName).getAll();
              request.onsuccess = () => resolve(request.result);
              request.onerror = () => reject(request.error);
            });
            const keys = await new Promise<IDBValidKey[]>((resolve, reject) => {
              const request = db.transaction(storeName, 'readonly').objectStore(storeName).getAllKeys();
              request.onsuccess = () => resolve(request.result);
              request.onerror = () => reject(request.error);
            });
            entries.push(`${info.name}/${storeName}=${JSON.stringify(keys)}${JSON.stringify(rows)}`);
          }
          db.close();
        }
        return entries;
      });

      // The upload journal may hold digests and progress, never a grant URL, a signature, or a checksum header value (SC-007).
      const leaked = stored.filter(
        (entry) =>
          secrets.some((secret) => entry.includes(secret)) ||
          SECRET_KEY.test(entry.split('=')[0] ?? '') ||
          GRANT_MATERIAL.test(entry),
      );
      expect(leaked.map((entry) => entry.split('=')[0]), 'secrets in browser storage').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
