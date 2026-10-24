import type { Page, Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { expectAllReachableByKeyboard } from '../support/keyboard';
import type { RegistryImage, RegistryProfile, RegistryWorkflow, SimulatedApi } from '../support/simulatedApi';

const hex = (c: string) => `sha-256:${c.repeat(64)}`;
const IMAGE = hex('a');
const MODEL = hex('c');
const NOT_PERMITTED = 'You are not permitted to open this page.';
const CONNECTION_REQUIRED = /A connection is required/;

const ROUTES = [
  '/registry',
  '/registry/images',
  '/registry/images/register',
  `/registry/images/${encodeURIComponent(IMAGE)}`,
  '/registry/profiles',
  '/registry/profiles/new',
  '/registry/profiles/tracker/versions/1',
  '/registry/workflows',
  '/registry/workflows/new',
  '/registry/workflows/match-analysis/versions/1.0.0',
];

function seed(api: SimulatedApi) {
  const image: RegistryImage = {
    imageDigest: IMAGE,
    repository: 'registry.example/analysts/ball-tracking',
    manifestDigest: hex('e'),
    capability: { id: 'ball-tracking', version: '1.0.0', tier: 'low-level', executionScope: 'segment' },
    modelDigest: MODEL,
    manifest: { manifestVersion: '1.0.0', videoAccess: 'required', output: { schemaVersion: '1.0.0' } },
    source: `registry.example/analysts/ball-tracking@${IMAGE}`,
    registeredAt: new Date('2026-10-10T12:00:00Z'),
    registeredBy: null,
  };
  const profile = (version: number, state: RegistryProfile['state']): RegistryProfile => ({
    profileId: 'tracker',
    version,
    profileDigest: hex(String(version + 1)),
    imageDigest: IMAGE,
    state,
    content: {
      capabilityId: 'ball-tracking',
      capabilityVersion: '1.0.0',
      modelDigest: MODEL,
      runtimeConstraints: ['linux/amd64'],
      accelerators: [],
      resources: { cpu: '2' },
      maxAttempts: 3,
      leaseSeconds: 120,
      staleTimeoutSeconds: 240,
      executionTimeoutSeconds: 900,
      videoPolicy: 'required',
      inputSchemaVersions: {},
      outputSchemaVersion: '1.0.0',
    },
    createdAt: new Date('2026-10-11T12:00:00Z'),
    createdBy: '00000000-0000-4000-8000-000000000021',
    rowVersion: 1,
  });
  const workflow = (version: string): RegistryWorkflow => ({
    workflowId: 'match-analysis',
    workflowVersion: version,
    definitionDigest: hex('9'),
    definition: {
      requestedCapabilities: ['ball-tracking'],
      completionPolicy: 'all-requested-capabilities',
      segmentationPolicy: { version: 1, durationSeconds: 300, digest: hex('8') },
      nodes: [{ nodeKey: 'track', profileId: 'tracker', profileVersion: 1, profileDigest: hex('2') }],
      edges: [],
    },
    createdAt: new Date('2026-10-12T12:00:00Z'),
    createdBy: null,
  });
  api.seedImage(image);
  api.seedProfile(profile(1, 'approved'));
  api.seedProfile(profile(2, 'draft'));
  api.seedWorkflow(workflow('1.0.0'));
  api.seedWorkflow(workflow('1.1.0'));
  api.seedDefault({ workflowId: 'match-analysis', workflowVersion: '1.0.0' });
}

async function open(page: Page, path: string) {
  await page.goto(path);
  await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible();
  await expect(page.getByText(/^Loading/)).toHaveCount(0);
}

test.describe('Access', () => {
  for (const role of ['Coach', 'Viewer'] as const) {
    test(`a ${role} sees no Registry entry and the not-permitted view on every Registry route (acceptance 4, FR-001)`, async ({ page, api }) => {
      api.signInAs(role);
      seed(api);
      await page.goto('/');
      await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
      await expect(
        page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Analyst Registry' }),
      ).toHaveCount(0);
      for (const path of ROUTES) {
        await page.goto(path);
        await expect(page.getByRole('alert')).toHaveText(NOT_PERMITTED);
        await expect(page.getByRole('heading', { level: 2 })).toHaveCount(0);
      }
      await expectNoAxeViolations(page);
    });
  }

  test('a Club Admin whose role is removed gets the not-permitted view at the next request (acceptance 4)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api);
    await open(page, '/registry');
    api.signInAs('Coach');
    await page.getByRole('link', { name: 'Analyst images' }).click();
    await expect(page.getByRole('alert')).toHaveText(NOT_PERMITTED);
    await expectNoAxeViolations(page);
  });
});

test.describe('Accessibility', () => {
  test('every view with data has no axe violations and is reachable by keyboard (acceptance 1, SC-004)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api);
    for (const path of ROUTES) {
      await open(page, path);
      await expectNoAxeViolations(page);
      await expectAllReachableByKeyboard(page, page.locator('main'));
    }
  });

  test('empty lists and an empty overview have no axe violations and are reachable by keyboard (acceptance 1)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    const empty: [string, string][] = [
      ['/registry', 'No default workflow is set; analysis runs cannot be created until one is.'],
      ['/registry/images', 'No Analyst images are registered.'],
      ['/registry/profiles', 'No Analyst profiles exist.'],
      ['/registry/workflows', 'No workflows are defined.'],
    ];
    for (const [path, text] of empty) {
      await page.goto(path);
      await expect(page.getByText(text)).toBeVisible();
      await expectNoAxeViolations(page);
      await expectAllReachableByKeyboard(page, page.locator('main'));
    }
  });

  test('the loading and error states have no axe violations (acceptance 1)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api);
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    api.on('GET', '/registry/analyst-images', async (route: Route) => {
      await gate;
      await route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ type: 'about:blank', title: 'Error', status: 500 }) });
    });
    await page.goto('/registry/images');
    await expect(page.getByText(/^Loading/)).toBeVisible();
    await expectNoAxeViolations(page);
    release();
    await expect(page.getByRole('alert')).toContainText('Something went wrong', { timeout: 30_000 });
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, page.locator('main'));
  });

  test('the refusal states have no axe violations and are reachable by keyboard (acceptance 1)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api);
    const reference = `registry.example/analysts/ball-tracking@sha256:${'1'.repeat(64)}`;
    api.imageRefusals.set(reference, { code: 'image-not-found', errors: [{ field: 'reference', code: 'image-not-found' }] });
    await page.goto('/registry/images/register');
    await page.getByLabel('Image reference').fill('registry.example/analysts/ball-tracking:latest');
    await page.getByRole('button', { name: 'Register image' }).click();
    await expect(page.getByText('The reference must contain @sha256:')).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, page.locator('main'));

    await page.getByLabel('Image reference').fill(reference);
    await page.getByRole('button', { name: 'Register image' }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'The platform refused the' })).toContainText('The image was not found in the registry.');
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, page.locator('main'));
  });

  test('every confirmation dialog, with its result, has no axe violations and is reachable by keyboard (acceptance 1)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api);
    const dialogs: [string, string, string, string][] = [
      ['/registry/profiles/tracker/versions/2', 'Approve', 'Approve profile version', 'Approve version'],
      ['/registry/profiles/tracker/versions/1', 'Retire', 'Retire profile version', 'Retire version'],
      ['/registry/workflows/match-analysis/versions/1.1.0', 'Make default', 'Make workflow version the default', 'Make default'],
    ];
    for (const [path, opener, name, confirm] of dialogs) {
      await open(page, path);
      await page.getByRole('button', { name: opener, exact: true }).click();
      const dialog = page.getByRole('dialog', { name });
      await expect(dialog).toBeVisible();
      await expectNoAxeViolations(page);
      await expectAllReachableByKeyboard(page, dialog);
      await dialog.getByRole('button', { name: confirm }).click();
      await expect(dialog.getByRole('button', { name: 'Close' })).toBeVisible();
      await expectNoAxeViolations(page);
      await expectAllReachableByKeyboard(page, dialog);
      await dialog.getByRole('button', { name: 'Close' }).click();
      await expect(dialog).toBeHidden();
    }
  });
});

async function startOnce(page: Page, api: SimulatedApi) {
  api.signInAs('ClubAdmin');
  seed(api);
  await page.goto('/registry');
  await expect(page.getByRole('heading', { name: 'Analyst Registry' })).toBeVisible();
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Analyst Registry' })).toBeVisible();
}

test.describe('Online only', () => {
  test('an offline browser shows the connection-required state and queues nothing (acceptance 2, FR-020)', async ({ page, api }) => {
    await startOnce(page, api);
    const attempted: string[] = [];
    await page.context().setOffline(true);
    await page.route('**/api/v1/**', (route) => {
      attempted.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
      return route.abort('internetdisconnected');
    });

    for (const path of ['/registry', '/registry/images', '/registry/images/register', '/registry/profiles/new', '/registry/workflows/new']) {
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1, name: 'SocAlytics' })).toBeVisible();
      await expect(page.getByText(CONNECTION_REQUIRED)).toBeVisible();
      await expect(page.getByRole('heading', { level: 2 })).toHaveCount(0);
      await expect(page.getByText(/ball-tracking|match-analysis|registry\.example/)).toHaveCount(0);
    }
    await expectNoAxeViolations(page);
    expect(attempted.every((request) => request.startsWith('GET '))).toBe(true);
  });

  test('after a session no Registry response is in Cache Storage, IndexedDB, local storage, or session storage (acceptance 3, SC-007)', async ({ page, api }) => {
    await startOnce(page, api);
    for (const path of ROUTES) await open(page, path);

    const stored = await page.evaluate(async () => {
      const entries: string[] = [];
      for (const name of await caches.keys()) {
        const cache = await caches.open(name);
        for (const request of await cache.keys()) {
          entries.push(`cache ${request.url}`);
          const response = await cache.match(request);
          entries.push(`cache-body ${(await response?.text()) ?? ''}`);
        }
      }
      for (const storage of [localStorage, sessionStorage]) {
        for (let i = 0; i < storage.length; i++) {
          const key = storage.key(i) ?? '';
          entries.push(`storage ${key}=${storage.getItem(key) ?? ''}`);
        }
      }
      for (const info of (await indexedDB.databases?.()) ?? []) {
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
          entries.push(`indexeddb ${info.name}/${storeName}=${JSON.stringify(rows)}`);
        }
        db.close();
      }
      return entries;
    });

    expect(stored.filter((entry) => entry.startsWith('cache ') && new URL(entry.slice(6)).pathname.startsWith('/api/'))).toEqual([]);
    expect(stored.filter((entry) => /\/registry\//.test(entry.split('=')[0] ?? '') && entry.startsWith('cache '))).toEqual([]);
    const registryData = new RegExp(`registry\\.example/analysts|${IMAGE}|${MODEL}|${hex('9')}`);
    expect(stored.filter((entry) => registryData.test(entry))).toEqual([]);
  });
});
