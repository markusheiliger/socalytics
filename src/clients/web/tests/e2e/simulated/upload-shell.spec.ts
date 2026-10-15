import { expect, test } from '../support/fixtures';
import { match, team } from '../support/simulatedApi';
import { GiB, MiB, writeTestVideo } from '../support/testFiles';

const STORAGE = 'http://127.0.0.1:4175';

async function control(path: string, body?: unknown) {
  await fetch(`${STORAGE}/__control/${path}`, { method: 'POST', body: JSON.stringify(body ?? {}) });
}

// The fake object store and its faults are shared, so these tests must not run in parallel.
test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ api }) => {
  await control('reset');
  api.on('GET', `/teams/${team.id}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }),
  );
});

async function addFile(page: import('@playwright/test').Page, file: { path: string; name: string }, displayName: string) {
  await page.getByLabel('Video files').setInputFiles(file.path);
  await page.getByLabel(`Display name for ${file.name}`).fill(displayName);
  await page.getByRole('button', { name: 'Add to upload queue' }).click();
}

test('keeps the upload visible in the indicator on other views and asks before leaving (scenario 9)', async ({ page, api }, testInfo) => {
  api.signInAs('Coach');
  await control('faults', { delayMs: 1500 });
  const file = await writeTestVideo(testInfo.outputPath('files'), 12 * MiB);
  await page.goto(`/matches/${match.id}`);
  await addFile(page, file, 'Main camera');

  const toggle = page.getByRole('button', { name: /^Uploads: \d+ active/ });
  await expect(toggle).toBeVisible();

  await page.getByRole('link', { name: 'Home' }).first().click();
  await expect(page).toHaveURL(/\/$/);
  await expect(toggle).toBeVisible();
  await toggle.click();
  const panel = page.getByRole('list', { name: 'Uploads in progress' });
  await expect(panel.getByRole('listitem')).toHaveCount(1);
  await expect(panel).toContainText('Main camera');

  await expect.poll(() => page.evaluate(() => {
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  }), { timeout: 20_000 }).toBe(true);
});

test('keeps the page responsive while a 1 GiB file is prepared and transferred (SC-006)', async ({ page, api }, testInfo) => {
  test.setTimeout(10 * 60_000);
  api.signInAs('Coach');
  await page.addInitScript(() => {
    const w = window as unknown as { __longTasks: number[] };
    w.__longTasks = [];
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) w.__longTasks.push(entry.duration);
    }).observe({ type: 'longtask', buffered: true });
  });
  const file = await writeTestVideo(testInfo.outputPath('files'), GiB);
  await page.goto(`/matches/${match.id}`);
  await addFile(page, file, 'Full match');

  const item = page.getByRole('listitem').filter({ hasText: 'Full match' });
  await expect(item.getByRole('status')).toHaveText(/Transferring/, { timeout: 5 * 60_000 });

  await page.getByRole('link', { name: 'Home' }).first().click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Uploads: \d+ active/ })).toBeVisible();
  await page.goBack();
  await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();

  const longTasks = await page.evaluate(() => (window as unknown as { __longTasks: number[] }).__longTasks);
  expect(longTasks.filter((duration) => duration > 200)).toEqual([]);
});
