import type { Page } from '@playwright/test';
import { expect, test } from '../support/fixtures';
import { SimulatedApi } from '../support/simulatedApi';
import { ReleaseServer } from '../support/releaseServer';
import { releaseCheckPeriodMs, releaseVersions } from '../support/releases';

const server = new ReleaseServer();
const noticeText = 'A new version of SocAlytics is available.';

test.beforeAll(() => server.start('a'));
test.afterAll(() => server.close());
test.beforeEach(() => server.use('a'));

async function startRelease(page: Page, api: { signInAs(role: 'Coach'): void }) {
  api.signInAs('Coach');
  await page.goto(server.origin);
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
  // The first load of a release is not controlled yet; a reload makes the worker control the page.
  await page.reload();
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
}

// A reload while polling destroys the execution context; the poll then simply tries again.
const shellCaches = (page: Page) =>
  page.evaluate(async () => (await caches.keys()).filter((name) => name.startsWith('socalytics-shell-'))).catch(() => [] as string[]);

test('offers release B within the check period, "Update" runs B in every open window and removes A', async ({ page, context, api }) => {
  await startRelease(page, api);
  const second = await context.newPage();
  const secondApi = new SimulatedApi(second);
  await secondApi.install();
  secondApi.signInAs('Coach');
  await second.goto(server.origin);
  await expect(second.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
  await expect(page.getByText(noticeText)).toHaveCount(0);

  server.use('b');
  await expect(page.getByText(noticeText)).toBeVisible({ timeout: releaseCheckPeriodMs + 10_000 });
  await expect(second.getByText(noticeText)).toBeVisible({ timeout: releaseCheckPeriodMs + 10_000 });

  await page.getByRole('button', { name: 'Update' }).click();
  await expect.poll(() => shellCaches(page)).toEqual([`socalytics-shell-${releaseVersions.b}`]);
  for (const target of [page, second]) {
    await expect(target.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
    await expect(target.getByText(noticeText)).toHaveCount(0);
    await expect
      .poll(() =>
        target.evaluate(async () => {
          const registration = await navigator.serviceWorker.getRegistration();
          return registration?.active?.scriptURL ?? '';
        }),
      )
      .toContain('/sw.js');
    expect(await shellCaches(target)).toEqual([
      `socalytics-shell-${releaseVersions.b}`,
    ]);
  }
});

test('ignoring the notice keeps it available after a reload and A keeps running', async ({ page, api }) => {
  await startRelease(page, api);
  server.use('b');
  await expect(page.getByText(noticeText)).toBeVisible({ timeout: releaseCheckPeriodMs + 10_000 });
  await page.reload();
  await expect(page.getByText(noticeText)).toBeVisible({ timeout: releaseCheckPeriodMs + 10_000 });
  expect(await shellCaches(page)).toContain(`socalytics-shell-${releaseVersions.a}`);
});

test('never activates a release older than the running one', async ({ page, api }) => {
  server.use('b');
  await startRelease(page, api);
  server.use('a');
  await page.waitForTimeout(releaseCheckPeriodMs * 2 + 2000);
  await expect(page.getByText(noticeText)).toHaveCount(0);
  // The older worker may install, but it stays waiting and never replaces B.
  expect(await shellCaches(page)).toContain(`socalytics-shell-${releaseVersions.b}`);
  expect(await page.evaluate(async () => (await navigator.serviceWorker.getRegistration())?.active?.state)).toBe('activated');
});
