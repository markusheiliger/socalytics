import type { Page, Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { expectAllReachableByKeyboard } from '../support/keyboard';
import type { SimulatedApi } from '../support/simulatedApi';

const AGENT = 'specialist-tactics';
const PROMPT = `${AGENT}-prompt`;
const NOT_PERMITTED = 'You are not permitted to open this page.';
const CONNECTION_REQUIRED = /A connection is required/;
const MARKUP = '<img src=x onerror="window.__pwned=1"> <b>bold</b> {{match.opponent}} {{ unknown_slot }}';
const FORM_MARKER = 'form-content-marker-7f3a91';

const ROUTES = [
  '/agent-catalog',
  '/agent-catalog/baseline',
  `/agent-catalog/agents/${AGENT}`,
  `/agent-catalog/agents/${AGENT}/versions/new?from=0.1.0`,
  `/agent-catalog/agents/${AGENT}/versions/0.1.0`,
  '/agent-catalog/artifacts/prompt-templates',
  `/agent-catalog/artifacts/prompt-templates/new?fromId=${PROMPT}&fromVersion=0.1.0`,
  `/agent-catalog/artifacts/prompt-templates/${PROMPT}/versions/0.1.0`,
  '/agent-catalog/artifacts/model-selections/new',
  '/agent-catalog/artifacts/tool-policies/new',
  '/agent-catalog/artifacts/safety-policies/new',
  '/agent-catalog/artifacts/modifier-policies/new',
  '/agent-catalog/artifacts/dimension-sets/new',
  '/agent-catalog/artifacts/philosophy-profiles/new',
];

async function open(page: Page, path: string) {
  await page.goto(path);
  await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible();
  await expect(page.getByText(/^Loading/)).toHaveCount(0);
}

const draftVersion = (version: string) => ({
  schemaVersion: '1.0.0',
  kind: 'agent-version',
  agentId: AGENT,
  version,
  group: 'specialist',
  displayName: 'Tactics Specialist',
  artifacts: {},
});

function seedMarkupPrompt(api: SimulatedApi) {
  api.seedCatalogArtifact({
    schemaVersion: '1.0.0',
    kind: 'prompt-template',
    id: 'markup-prompt',
    version: '1.0.0',
    group: 'specialist',
    responseContract: 'agent-response/1',
    text: MARKUP,
    slots: [],
  });
}

test.beforeEach(async ({ api }) => {
  api.signInAs('ClubAdmin');
});

test.describe('Access', () => {
  for (const role of ['Coach', 'Viewer', 'Registrar'] as const) {
    test(`a ${role} sees no Agent catalog entry and the not-permitted view on every address without a catalog request (acceptance 4, FR-001)`, async ({ page, api }) => {
      api.signInAs(role);
      const requests: string[] = [];
      page.on('request', (request) => {
        if (request.url().includes('/api/v1/agent-catalog')) requests.push(request.url());
      });
      await page.goto('/');
      await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
      await expect(
        page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Agent catalog' }),
      ).toHaveCount(0);
      for (const path of ROUTES) {
        await page.goto(path);
        await expect(page.getByRole('alert')).toHaveText(NOT_PERMITTED);
        await expect(page.getByRole('heading', { level: 2 })).toHaveCount(0);
      }
      await expectNoAxeViolations(page);
      expect(requests).toEqual([]);
    });
  }

  test('a Club Admin whose role is removed gets the not-permitted view at the next request (acceptance 4)', async ({ page, api }) => {
    await open(page, '/agent-catalog');
    api.signInAs('Coach');
    await page.getByRole('link', { name: `Show the agent ${AGENT}` }).click();
    await expect(page.getByRole('alert')).toHaveText(NOT_PERMITTED);
    await expectNoAxeViolations(page);
  });
});

test.describe('Accessibility', () => {
  test('every view and form with data has no axe violations and is reachable by keyboard (acceptance 1, SC-004)', async ({ page, api }) => {
    seedMarkupPrompt(api);
    const paths = [...ROUTES, '/agent-catalog/artifacts/prompt-templates/markup-prompt/versions/1.0.0'];
    for (const path of paths) {
      await open(page, path);
      await expectNoAxeViolations(page);
      await expectAllReachableByKeyboard(page, page.locator('main'));
    }
  });

  test('empty states have no axe violations and are reachable by keyboard (acceptance 1)', async ({ page, api }) => {
    api.catalogArtifacts.length = 0;
    for (const path of ['/agent-catalog/artifacts/prompt-templates', '/agent-catalog/agents/specialist-fitness']) {
      await open(page, path);
      await expectNoAxeViolations(page);
      await expectAllReachableByKeyboard(page, page.locator('main'));
    }
  });

  test('the loading, error, and not-found states have no axe violations (acceptance 1)', async ({ page, api }) => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    api.on('GET', '/agent-catalog/agents', async (route: Route) => {
      await gate;
      await route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ type: 'about:blank', title: 'Error', status: 500 }) });
    });
    await page.goto('/agent-catalog');
    await expect(page.getByText(/^Loading/)).toBeVisible();
    await expectNoAxeViolations(page);
    release();
    await expect(page.getByRole('alert')).toContainText('Something went wrong', { timeout: 30_000 });
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, page.locator('main'));

    await page.goto('/agent-catalog/agents/unknown-agent');
    await expect(page.getByRole('alert')).toContainText('This catalog record was not found.');
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, page.locator('main'));
  });

  test('the refusal states of a form have no axe violations and are reachable by keyboard (acceptance 1)', async ({ page, api }) => {
    await open(page, `/agent-catalog/artifacts/prompt-templates/new?fromId=${PROMPT}&fromVersion=0.1.0`);
    api.refuseCatalogNext('registerAgentCatalogArtifact', {
      status: 400,
      code: 'agent-catalog-validation-failed',
      extra: { violations: [{ code: 'schema-violation', pointer: '', detail: 'The record is not an object of this kind.' }] },
    });
    await page.getByRole('button', { name: 'Register', exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'Problems without a field' })).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, page.locator('main'));
  });

  test('every confirmation dialog, with its result and refusal, has no axe violations and is reachable by keyboard (acceptance 1)', async ({ page, api }) => {
    api.seedCatalogAgentVersion(draftVersion('0.2.0'), { state: 'draft' });
    api.seedCatalogAgentVersion(draftVersion('0.3.0'), { state: 'approved' });
    api.seedCatalogAgentVersion(draftVersion('0.4.0'), { state: 'approved' });
    const dialog = page.getByRole('dialog');
    const versionPath = (version: string) => `/agent-catalog/agents/${AGENT}/versions/${version}`;

    // Approval, then its result.
    await open(page, versionPath('0.2.0'));
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(dialog).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);
    await dialog.getByRole('button', { name: 'Approve version' }).click();
    await expect(dialog.getByRole('status')).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toHaveCount(0);

    // Retirement, then its result.
    await open(page, versionPath('0.3.0'));
    await page.getByRole('button', { name: 'Retire', exact: true }).click();
    await expect(dialog).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);
    await dialog.getByRole('button', { name: 'Retire version' }).click();
    await expect(dialog.getByRole('status')).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toHaveCount(0);

    // Enablement refused after the version changed.
    await open(page, `/agent-catalog/agents/${AGENT}`);
    await page.getByLabel('New enabled version').selectOption('0.4.0');
    await page.getByRole('button', { name: 'Change enabled version' }).click();
    await expect(dialog).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);
    api.catalogVersions.find((v) => v.agentId === AGENT && v.version === '0.4.0')!.state = 'draft';
    await dialog.getByRole('button', { name: 'Enable version' }).click();
    await expect(dialog.getByRole('alert').filter({ hasText: 'This version cannot be enabled.' })).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);
  });
});

test.describe('Prompt text', () => {
  test('shows markup and placeholders verbatim as plain text (acceptance 1, FR-021)', async ({ page, api }) => {
    seedMarkupPrompt(api);
    await open(page, '/agent-catalog/artifacts/prompt-templates/markup-prompt/versions/1.0.0');
    const prompt = page.getByLabel('Prompt text', { exact: true });
    await expect(prompt).toHaveText(MARKUP);
    await expect(page.locator('main img')).toHaveCount(0);
    await expect(page.locator('main b')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined();
    await expectNoAxeViolations(page);
  });

  test('keeps markup and placeholders verbatim in the registration form (FR-021)', async ({ page, api }) => {
    seedMarkupPrompt(api);
    await open(page, '/agent-catalog/artifacts/prompt-templates/new?fromId=markup-prompt&fromVersion=1.0.0');
    await expect(page.getByRole('textbox', { name: /^Prompt text/ })).toHaveValue(MARKUP);
    await expect(page.locator('main img')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined();
  });
});

async function startOnce(page: Page) {
  await page.goto('/agent-catalog');
  await expect(page.getByRole('heading', { level: 2, name: 'Agent catalog' })).toBeVisible();
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
  await page.reload();
  await expect(page.getByRole('heading', { level: 2, name: 'Agent catalog' })).toBeVisible();
}

test.describe('Online only', () => {
  test('an offline browser shows the connection-required state and queues nothing (acceptance 2, FR-020)', async ({ page }) => {
    await startOnce(page);
    const attempted: string[] = [];
    await page.context().setOffline(true);
    await page.route('**/api/v1/**', (route) => {
      attempted.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
      return route.abort('internetdisconnected');
    });

    for (const path of ROUTES) {
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1, name: 'SocAlytics' })).toBeVisible();
      await expect(page.getByText(CONNECTION_REQUIRED)).toBeVisible();
      await expect(page.getByRole('heading', { level: 2 })).toHaveCount(0);
      await expect(page.getByText(/specialist-tactics|simulated-model|Tactics Specialist/)).toHaveCount(0);
    }
    await expectNoAxeViolations(page);
    expect(attempted.every((request) => request.startsWith('GET '))).toBe(true);
  });

  test('after a session no catalog response or form content is in Cache Storage, IndexedDB, local storage, or session storage (acceptance 3, SC-007)', async ({ page, api }) => {
    seedMarkupPrompt(api);
    await startOnce(page);
    for (const path of ROUTES) await open(page, path);
    await open(page, '/agent-catalog/artifacts/prompt-templates/markup-prompt/versions/1.0.0');
    await open(page, '/agent-catalog/artifacts/prompt-templates/new');
    await page.getByRole('textbox', { name: /^Identifier/ }).fill(FORM_MARKER);
    await page.getByRole('textbox', { name: /^Prompt text/ }).fill(FORM_MARKER);

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
          const request = indexedDB.open(info.name as string);
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
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
    expect(stored.filter((entry) => entry.startsWith('cache ') && /agent-catalog/.test(entry))).toEqual([]);
    const catalogData = new RegExp(`${FORM_MARKER}|markup-prompt|Provide decision support|match\\.opponent`);
    expect(stored.filter((entry) => catalogData.test(entry))).toEqual([]);
  });
});
