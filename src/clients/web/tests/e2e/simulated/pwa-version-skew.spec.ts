import type { Page } from '@playwright/test';
import { expect, test } from '../support/fixtures';
import { ReleaseServer } from '../support/releaseServer';
import { releaseVersions } from '../support/releases';

const server = new ReleaseServer();
const nav = (page: Page) => page.getByRole('navigation', { name: 'Main navigation' });

test.beforeAll(() => server.start('a'));
test.afterAll(() => server.close());
test.beforeEach(() => {
  server.use('a');
  server.setUnavailable(false);
});

async function startRelease(page: Page, api: { signInAs(role: 'Coach'): void }) {
  api.signInAs('Coach');
  await page.goto(server.origin);
  await expect(nav(page)).toBeVisible();
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
  await page.reload();
  await expect(nav(page)).toBeVisible();
}

const runningCache = (page: Page) =>
  page.evaluate(async () => (await caches.keys()).filter((name) => name.startsWith('socalytics-shell-'))).catch(() => [] as string[]);

test('a minimum of B stops requests from A, shows the notice, and B runs within 30 seconds', async ({ page, api }) => {
  await startRelease(page, api);
  server.use('b');
  api.minimumWebVersion = releaseVersions.b;
  await nav(page).getByRole('link', { name: 'Teams' }).click();
  await expect(page.getByText(/updating to the version this server requires/)).toBeVisible();
  const sent = api.requests.length;
  await page.waitForTimeout(1000);
  expect(api.requests.length).toBe(sent);

  await expect.poll(() => runningCache(page), { timeout: 30_000 }).toEqual([`socalytics-shell-${releaseVersions.b}`]);
  api.minimumWebVersion = null;
  await expect(nav(page)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(/updating to the version this server requires/)).toHaveCount(0);
});

test('without release B the reload-required notice appears and requests stay blocked', async ({ page, api }) => {
  test.setTimeout(90_000);
  await startRelease(page, api);
  api.minimumWebVersion = releaseVersions.b;
  server.setUnavailable(true);
  await nav(page).getByRole('link', { name: 'Teams' }).click();
  await expect(page.getByText('Reload SocAlytics to continue. This server requires a newer version.')).toBeVisible({ timeout: 40_000 });
  await expect(page.getByRole('button', { name: 'Reload' })).toBeVisible();
  const sent = api.requests.length;
  await page.waitForTimeout(1000);
  expect(api.requests.length).toBe(sent);
});
