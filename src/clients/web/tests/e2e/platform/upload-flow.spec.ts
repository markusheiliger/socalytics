import { expect, test as base } from '../support/fixtures';
import { archiveActiveSeasons } from '../support/platformState';
import { FORTY_MIB, writeTestVideo } from '../support/testFiles';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD]);
  },
});

const run = Date.now().toString(36);
const seasonName = `E2E upload ${run}`;
const teamName = `E2E upload team ${run}`;
const opponent = `E2E upload opponent ${run}`;
const displayName = `E2E main camera ${run}`;

// Frees the club's single active season for the next spec, even when the test fails.
test.afterEach(async () => {
  await archiveActiveSeasons();
});

test('uploads a 40 MiB recording directly to object storage and shows the accepted version (SC-002, SC-003)', async ({ page }, testInfo) => {
  test.setTimeout(5 * 60 * 1000);
  const file = await writeTestVideo(testInfo.outputPath('files'), FORTY_MIB);

  // Recording bytes must go to object storage, never to the platform API (SC-003).
  const apiBodyTypes: string[] = [];
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/v1') && request.postDataBuffer()) {
      apiBodyTypes.push(request.headers()['content-type'] ?? '');
    }
  });
  const storagePuts: string[] = [];
  page.on('request', (request) => {
    if (request.method() === 'PUT' && new URL(request.url()).origin !== new URL(page.url()).origin) {
      storagePuts.push(request.url());
    }
  });

  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  const nav = page.getByRole('navigation', { name: 'Main navigation' });

  await nav.getByRole('link', { name: 'Seasons' }).click();
  await page.getByLabel(/^Season name/).fill(seasonName);
  await page.getByRole('button', { name: 'Create season' }).click();
  const seasonItem = page.getByRole('listitem').filter({ hasText: seasonName });
  await expect(seasonItem).toContainText('Draft');
  await seasonItem.getByRole('link', { name: seasonName }).click();
  await page.getByRole('button', { name: 'Activate' }).click();
  await expect(page.getByText('State: Active')).toBeVisible();

  await nav.getByRole('link', { name: 'Teams' }).click();
  await page.getByLabel('Season', { exact: true }).selectOption({ label: `${seasonName} (Active)` });
  await page.getByLabel(/^Team name/).fill(teamName);
  await page.getByRole('button', { name: 'Create team' }).click();
  await expect(page.getByText('The team was created.')).toBeVisible();
  await page.getByRole('link', { name: teamName }).click({ timeout: 15_000 });
  await expect(page.getByRole('heading', { level: 2, name: teamName })).toBeVisible();

  await page.getByRole('link', { name: 'New match' }).click();
  await page.getByLabel(/^Opponent/).fill(opponent);
  await page.getByLabel(/^Kickoff time/).fill('2031-05-17T15:30');
  await page.getByRole('button', { name: 'Create match' }).click();
  await expect(page.getByRole('heading', { level: 2, name: opponent })).toBeVisible();
  await expect(page.getByText('No recordings have been uploaded yet.')).toBeVisible({ timeout: 15_000 });

  await page.getByLabel('Video files').setInputFiles(file.path);
  await page.getByLabel(`Display name for ${file.name}`).fill(displayName);
  await page.getByRole('button', { name: 'Add to upload queue' }).click();

  const item = page.getByRole('list', { name: 'Upload queue' }).getByRole('listitem').filter({ hasText: displayName });
  await expect(item).toBeVisible();
  await item.getByLabel(/^Media end/).fill('01:00');
  await item.getByLabel(/^Match time at media start/).fill('10:00');
  await item.getByRole('button', { name: `Save mapping for ${displayName}` }).click();
  await expect(item.getByRole('status')).toHaveText(/Completed/, { timeout: 4 * 60 * 1000 });

  const versions = page.getByRole('list', { name: 'Recording versions' });
  await expect(versions.getByRole('listitem')).toHaveCount(1, { timeout: 30_000 });
  await expect(versions.getByRole('listitem').first()).toContainText(displayName);
  await expect(versions.getByRole('listitem').first()).toContainText('40.0 MiB');
  await expect(versions.getByRole('listitem').first()).toContainText(/digest sha-256-parts:16777216:3:[0-9a-f]{64}/);

  expect(storagePuts.length, 'parts sent directly to object storage').toBeGreaterThan(0);
  expect(apiBodyTypes.every((type) => type.startsWith('application/json')), 'only JSON bodies to /api/v1').toBe(true);
});
