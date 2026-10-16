import type { Page } from '@playwright/test';
import { expect, test } from '../support/fixtures';
import { ReleaseServer } from '../support/releaseServer';
import { releaseVersions } from '../support/releases';
import { FAKE_STORAGE_ORIGIN, match, team } from '../support/simulatedApi';
import { MiB, writeTestVideo } from '../support/testFiles';

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

async function control(path: string, body?: unknown) {
  await fetch(`${FAKE_STORAGE_ORIGIN}/__control/${path}`, { method: 'POST', body: JSON.stringify(body ?? {}) });
}

async function storageState(): Promise<{ parts: { partNumber: number }[]; requests: { partNumber: number }[] }> {
  return (await fetch(`${FAKE_STORAGE_ORIGIN}/__control/state`)).json();
}

test('a minimum of B during an upload stops part requests, B runs, and the upload resumes with the missing parts', async ({ page, api }, testInfo) => {
  test.setTimeout(120_000);
  await control('reset');
  api.on('GET', `/teams/${team.id}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }),
  );
  await startRelease(page, api);

  // Parts 1 and 2 are stored in release A; part 3 never succeeds, so the upload is mid-transfer.
  await control('faults', { failPart: { partNumber: 3, times: 'always' } });
  const file = await writeTestVideo(testInfo.outputPath('files'), 40 * MiB);
  await page.goto(`${server.origin}/matches/${match.id}`);
  await page.getByLabel('Video files').setInputFiles(file.path);
  await page.getByLabel(`Display name for ${file.name}`).fill('Main camera');
  await page.getByRole('button', { name: 'Add to upload queue' }).click();
  await expect.poll(async () => (await storageState()).parts.length, { timeout: 30_000 }).toBe(2);

  server.use('b');
  api.minimumWebVersion = releaseVersions.b;
  server.setUnavailable(true);
  await nav(page).getByRole('link', { name: 'Teams' }).click();
  const required = page.getByRole('alertdialog');
  await expect(required).toContainText('uploads continue after the update');
  await page.waitForTimeout(500);
  const sent = (await storageState()).requests.length;
  await page.waitForTimeout(1500);
  expect((await storageState()).requests).toHaveLength(sent);

  // Release B becomes reachable, takes over, and the journal survives the reload.
  server.setUnavailable(false);
  await expect.poll(() => runningCache(page), { timeout: 30_000 }).toEqual([`socalytics-shell-${releaseVersions.b}`]);
  api.minimumWebVersion = null;
  await expect(nav(page)).toBeVisible({ timeout: 30_000 });
  await control('reset');
  await page.goto(`${server.origin}/matches/${match.id}`);

  const item = page.getByRole('list', { name: 'Pending uploads of this match' }).getByRole('listitem').filter({ hasText: 'Main camera' });
  await expect(item).toHaveCount(1);
  await item.locator('input[type=file]').setInputFiles(file.path);
  const upload = page.getByRole('list', { name: 'Upload queue' }).getByRole('listitem').filter({ hasText: 'Main camera' });
  await upload.getByLabel(/^Media end/).fill('01:00');
  await upload.getByLabel(/^Match time at media start/).fill('10:00');
  await upload.getByRole('button', { name: 'Save mapping for Main camera' }).click();
  await expect(upload.getByRole('status')).toHaveText(/Completed/, { timeout: 30_000 });
  expect((await storageState()).requests.map((r) => r.partNumber)).toEqual([3]);
});
