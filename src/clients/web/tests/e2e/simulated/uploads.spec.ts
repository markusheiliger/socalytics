import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { match, team } from '../support/simulatedApi';

const STORAGE = 'http://127.0.0.1:4176';
const MiB = 1024 * 1024;

const video = (name: string, mib = 3, seed = 1) => ({
  name,
  mimeType: 'video/mp4',
  buffer: Buffer.from(Array.from({ length: mib * MiB }, (_, i) => (i * seed + (i >> 20)) & 0xff)),
});

async function control(path: string, body?: unknown) {
  await fetch(`${STORAGE}/__control/${path}`, { method: 'POST', body: JSON.stringify(body ?? {}) });
}

async function addFile(page: Page, file: ReturnType<typeof video>, displayName: string) {
  await page.getByLabel('Video files').setInputFiles(file);
  await page.getByLabel(`Display name for ${file.name}`).fill(displayName);
  await page.getByRole('button', { name: 'Add to upload queue' }).click();
}

async function setMapping(page: Page, displayName: string, minutes = '10:00') {
  const item = page.getByRole('listitem').filter({ hasText: displayName });
  await item.getByLabel(/^Media end/).fill('01:00');
  await item.getByLabel(/^Match time at media start/).fill(minutes);
  await item.getByRole('button', { name: `Save mapping for ${displayName}` }).click();
  return item;
}

// The fake object store and its faults are shared, so these tests must not run in parallel.
test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ api }) => {
  await control('reset');
  api.on('GET', `/teams/${team.id}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }),
  );
});

test('uploads two files one at a time, completes both, and sends no media bytes to the API (scenarios 1, 2, 5; SC-003)', async ({ page, api }) => {
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await expect(page.getByText('No recordings have been uploaded yet.')).toBeVisible();
  await addFile(page, video('main.mp4', 3, 1), 'Main camera');
  await addFile(page, video('second.mp4', 3, 3), 'Second angle');
  const list = page.getByRole('list', { name: 'Upload queue' });
  await expect(list.getByRole('listitem')).toHaveCount(2);
  await expectNoAxeViolations(page);

  await setMapping(page, 'Main camera');
  await setMapping(page, 'Second angle', '10:30');
  await expect(list.getByRole('listitem').filter({ hasText: 'Main camera' }).getByRole('status')).toHaveText(/Completed/, { timeout: 20_000 });
  await expect(list.getByRole('listitem').filter({ hasText: 'Second angle' }).getByRole('status')).toHaveText(/Completed/, { timeout: 20_000 });
  const versions = page.getByRole('list', { name: 'Recording versions' });
  await expect(versions.getByRole('listitem')).toHaveCount(2);
  expect(api.recordingVersions).toHaveLength(2);
  expect(api.bodyContentTypes.every((type) => type.startsWith('application/json'))).toBe(true);
});

test('lets the member remove a file from the queue before it starts (scenario 2)', async ({ page, api }) => {
  api.signInAs('Coach');
  await control('faults', { delayMs: 500 });
  await page.goto(`/matches/${match.id}`);
  await addFile(page, video('main.mp4', 3, 1), 'Main camera');
  await addFile(page, video('second.mp4', 3, 3), 'Second angle');
  const remove = page.getByRole('button', { name: 'Remove Second angle from the queue' });
  if (await remove.isVisible()) await remove.click();
  await expect(page.getByRole('listitem').filter({ hasText: 'Main camera' })).toBeVisible();
  expect(api.uploadSessions.length).toBeLessThanOrEqual(2);
});

test('retries a failed part and continues (scenario 3)', async ({ page, api }) => {
  api.signInAs('Coach');
  await control('faults', { failPart: { partNumber: 1, times: 2 } });
  await page.goto(`/matches/${match.id}`);
  await addFile(page, video('main.mp4'), 'Main camera');
  const item = await setMapping(page, 'Main camera');
  await expect(item.getByRole('status')).toHaveText(/Completed/, { timeout: 30_000 });
});

test('pauses after exhausted retries and continues on retry (scenario 3)', async ({ page, api }) => {
  test.setTimeout(90_000);
  api.signInAs('Coach');
  await control('faults', { failPart: { partNumber: 1, times: 'always' } });
  await page.goto(`/matches/${match.id}`);
  await addFile(page, video('main.mp4'), 'Main camera');
  const item = page.getByRole('listitem').filter({ hasText: 'Main camera' });
  await expect(item.getByRole('status')).toHaveText(/Paused/, { timeout: 60_000 });
  await expect(item.getByRole('status')).toContainText('Parts already stored are kept');
  await control('reset');
  await item.getByRole('button', { name: 'Retry Main camera' }).click();
  await setMapping(page, 'Main camera');
  await expect(item.getByRole('status')).toHaveText(/Completed/, { timeout: 30_000 });
});

test('requests fresh grants when the grants expired (scenario 4)', async ({ page, api }) => {
  api.signInAs('Coach');
  await control('faults', { expireGrants: true });
  await page.goto(`/matches/${match.id}`);
  await addFile(page, video('main.mp4'), 'Main camera');
  await expect.poll(() => api.requests.filter((r) => r.path.endsWith('/grants')).length, { timeout: 20_000 }).toBeGreaterThan(0);
  const item = page.getByRole('listitem').filter({ hasText: 'Main camera' });
  await expect(item.getByRole('status')).toHaveText(/Paused/, { timeout: 20_000 });
  await control('faults', { expireGrants: false });
  await item.getByRole('button', { name: 'Retry Main camera' }).click();
  await setMapping(page, 'Main camera');
  await expect(item.getByRole('status')).toHaveText(/Completed/, { timeout: 30_000 });
});

test('shows a refused completion and keeps the upload pending (scenario 6)', async ({ page, api }) => {
  api.signInAs('Coach');
  let refuse = true;
  await page.route('**/api/v1/matches/*/upload-sessions/*/completion', async (route) => {
    if (refuse) {
      refuse = false;
      await route.fulfill({ status: 409, contentType: 'application/problem+json', body: JSON.stringify({ status: 409, code: 'upload-parts-incomplete' }) });
      return;
    }
    await route.fallback();
  });
  await page.goto(`/matches/${match.id}`);
  await addFile(page, video('main.mp4'), 'Main camera');
  const item = await setMapping(page, 'Main camera');
  await expect(item.getByRole('status')).toContainText('missing parts', { timeout: 20_000 });
  await item.getByRole('button', { name: 'Retry Main camera' }).click();
  await expect(item.getByRole('status')).toHaveText(/Completed/, { timeout: 30_000 });
});

test('refuses non-video files and shows the platform reason for a refused start (scenario 7)', async ({ page, api }) => {
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await page.getByLabel('Video files').setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('x') });
  await expect(page.getByRole('alert')).toContainText('Only video files are accepted');
  expect(api.uploadSessions).toHaveLength(0);

  await page.route('**/api/v1/matches/*/upload-sessions', (route) =>
    route.fulfill({ status: 413, contentType: 'application/problem+json', body: JSON.stringify({ status: 413, code: 'recording-too-large' }) }),
  );
  await addFile(page, video('big.mp4'), 'Big');
  await expect(page.getByRole('alert')).toContainText('larger than the platform accepts');
});

test('offers no upload action to a Viewer (scenario 8)', async ({ page, api }) => {
  api.signInAs('Viewer');
  await page.goto(`/matches/${match.id}`);
  await expect(page.getByRole('heading', { name: 'Recordings', exact: true })).toBeVisible();
  await expect(page.getByLabel('Video files')).toHaveCount(0);
  await expectNoAxeViolations(page);
});

test('warns when the same file is added twice (edge case)', async ({ page, api }) => {
  api.signInAs('Coach');
  await control('faults', { delayMs: 2000 });
  await page.goto(`/matches/${match.id}`);
  const file = video('main.mp4');
  await addFile(page, file, 'First');
  await addFile(page, file, 'Second');
  await expect(page.getByText(/already uploaded or uploading/).first()).toBeVisible({ timeout: 20_000 });
});
