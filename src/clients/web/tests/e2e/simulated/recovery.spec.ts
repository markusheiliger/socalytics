import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { match, team } from '../support/simulatedApi';
import { MiB, writeTestVideo } from '../support/testFiles';

const STORAGE = 'http://127.0.0.1:4175';

async function control(path: string, body?: unknown) {
  await fetch(`${STORAGE}/__control/${path}`, { method: 'POST', body: JSON.stringify(body ?? {}) });
}

async function storageState(): Promise<{ parts: { partNumber: number }[]; requests: { partNumber: number }[] }> {
  return (await fetch(`${STORAGE}/__control/state`)).json();
}

// The fake object store and its faults are shared, so these tests must not run in parallel.
test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ api }) => {
  await control('reset');
  api.on('GET', `/teams/${team.id}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }),
  );
});

/** Starts a 40 MiB upload whose last part never succeeds, so parts 1 and 2 are stored, then reloads the match. */
async function startInterrupted(page: Page, api: { signInAs(role: 'Coach'): void }, dir: string) {
  api.signInAs('Coach');
  await control('faults', { failPart: { partNumber: 3, times: 'always' } });
  const file = await writeTestVideo(dir, 40 * MiB);
  await page.goto(`/matches/${match.id}`);
  await page.getByLabel('Video files').setInputFiles(file.path);
  await page.getByLabel(`Display name for ${file.name}`).fill('Main camera');
  await page.getByRole('button', { name: 'Add to upload queue' }).click();
  await expect.poll(async () => (await storageState()).parts.length, { timeout: 30_000 }).toBe(2);
  await page.waitForTimeout(500);
  await control('reset');
  await page.reload();
  return file;
}

const pending = (page: Page) => page.getByRole('list', { name: 'Pending uploads of this match' }).getByRole('listitem');

test('lists a pending upload after a reload and resumes only the missing parts (scenarios 1, 2; SC-004)', async ({ page, api }, testInfo) => {
  const file = await startInterrupted(page, api, testInfo.outputPath('files'));
  const item = pending(page).filter({ hasText: 'Main camera' });
  await expect(item).toHaveCount(1);
  await expect(item.getByRole('status')).toContainText('Choose the file again');
  await expect(item).toContainText('upload session expires on');
  await expectNoAxeViolations(page);

  await item.locator('input[type=file]').setInputFiles(file.path);
  const upload = page.getByRole('list', { name: 'Upload queue' }).getByRole('listitem').filter({ hasText: 'Main camera' });
  await upload.getByLabel(/^Media end/).fill('01:00');
  await upload.getByLabel(/^Match time at media start/).fill('10:00');
  await upload.getByRole('button', { name: 'Save mapping for Main camera' }).click();
  await expect(upload.getByRole('status')).toHaveText(/Completed/, { timeout: 30_000 });
  const sent = (await storageState()).requests.map((r) => r.partNumber);
  expect(sent).toEqual([3]);
});

test('refuses a different file and changes nothing (scenario 2)', async ({ page, api }, testInfo) => {
  await startInterrupted(page, api, testInfo.outputPath('files'));
  const other = await writeTestVideo(testInfo.outputPath('other'), 5 * MiB, 'other.mp4');
  const item = pending(page).filter({ hasText: 'Main camera' });
  await item.locator('input[type=file]').setInputFiles(other.path);
  await expect(item.getByRole('status')).toContainText('not the file that was selected');
  expect((await storageState()).requests).toHaveLength(0);
});

test('abandons a pending upload with the expiry explanation (scenario 5)', async ({ page, api }, testInfo) => {
  await startInterrupted(page, api, testInfo.outputPath('files'));
  const item = pending(page).filter({ hasText: 'Main camera' });
  await expect(item).toContainText('platform discards the stored parts when the upload expires');
  await item.getByRole('button', { name: 'Abandon Main camera' }).click();
  await expect(pending(page)).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Recordings', exact: true })).toBeVisible();
  await expect(pending(page)).toHaveCount(0);
});

test('shows an expired upload and offers to start again (scenario 6)', async ({ page, api }, testInfo) => {
  await startInterrupted(page, api, testInfo.outputPath('files'));
  (api.uploadSessions[0] as { state: string }).state = 'expired';
  await page.reload();
  const item = pending(page).filter({ hasText: 'recording.mp4' });
  await expect(item.getByRole('status')).toContainText('Expired');
  await expect(item.getByText('Start again with recording.mp4')).toBeVisible();
  await item.getByRole('button', { name: 'Dismiss recording.mp4' }).click();
  await expect(pending(page)).toHaveCount(0);
});

test('deletes the remembered uploads on explicit sign-out only (scenario 7; different member edge case)', async ({ page, api }, testInfo) => {
  await startInterrupted(page, api, testInfo.outputPath('files'));
  await expect(pending(page)).toHaveCount(1);
  await page.goto('/account');
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/sign-in/);
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await expect(page.getByRole('heading', { name: 'Recordings', exact: true })).toBeVisible();
  await expect(pending(page)).toHaveCount(0);
});

test('keeps remembered uploads when the platform ends the session (scenario 3)', async ({ page, api }, testInfo) => {
  await startInterrupted(page, api, testInfo.outputPath('files'));
  await expect(pending(page)).toHaveCount(1);
  await page.route('**/api/v1/session', (route) =>
    route.fulfill({ status: 401, contentType: 'application/problem+json', body: JSON.stringify({ status: 401 }) }),
  );
  await page.reload();
  await expect(page).toHaveURL(/sign-in/);
  await page.unroute('**/api/v1/session');
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await expect(pending(page).filter({ hasText: 'Main camera' })).toHaveCount(1);
});
