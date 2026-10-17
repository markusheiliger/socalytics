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
const seasonName = `E2E recovery ${run}`;
const teamName = `E2E recovery team ${run}`;
const opponent = `E2E recovery opponent ${run}`;
const displayName = `E2E recovery camera ${run}`;

// Frees the club's single active season for the next spec, even when the test fails.
test.afterEach(async () => {
  await archiveActiveSeasons();
});

test('resumes a 40 MiB upload after a reload and forgets it after sign-out (SC-004)', async ({ page }, testInfo) => {
  test.setTimeout(5 * 60 * 1000);
  const file = await writeTestVideo(testInfo.outputPath('files'), FORTY_MIB);

  async function signIn() {
    await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
    await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  }

  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await signIn();

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
  await page.getByLabel(/^Kickoff time/).fill('2031-05-18T15:30');
  await page.getByRole('button', { name: 'Create match' }).click();
  await expect(page.getByRole('heading', { level: 2, name: opponent })).toBeVisible();
  await expect(page.getByText('No recordings have been uploaded yet.')).toBeVisible({ timeout: 15_000 });
  const matchUrl = page.url();

  // Let only the first part reach object storage, so the upload is interrupted with one stored part.
  const pageOrigin = new URL(page.url()).origin;
  let firstPartStored = false;
  let allowedFirst = false;
  await page.route(
    (url) => url.origin !== pageOrigin,
    async (route) => {
      if (route.request().method() !== 'PUT') return route.continue();
      if (!allowedFirst) {
        allowedFirst = true;
        const response = await route.fetch();
        firstPartStored = response.ok();
        return route.fulfill({ response });
      }
      return route.abort('failed');
    },
  );

  await page.getByLabel('Video files').setInputFiles(file.path);
  await page.getByLabel(`Display name for ${file.name}`).fill(displayName);
  await page.getByRole('button', { name: 'Add to upload queue' }).click();
  await expect.poll(() => firstPartStored, { timeout: 2 * 60 * 1000 }).toBe(true);

  await page.unrouteAll({ behavior: 'ignoreErrors' });
  await page.reload();

  const pending = page
    .getByRole('list', { name: 'Pending uploads of this match' })
    .getByRole('listitem')
    .filter({ hasText: displayName });
  await expect(pending).toHaveCount(1);
  await expect(pending.getByRole('status')).toContainText('Choose the file again');

  await pending.locator('input[type=file]').setInputFiles(file.path);
  const item = page.getByRole('list', { name: 'Upload queue' }).getByRole('listitem').filter({ hasText: displayName });
  await item.getByLabel(/^Media end/).fill('01:00');
  await item.getByLabel(/^Match time at media start/).fill('10:00');
  await item.getByRole('button', { name: `Save mapping for ${displayName}` }).click();
  await expect(item.getByRole('status')).toHaveText(/Completed/, { timeout: 4 * 60 * 1000 });

  const versions = page.getByRole('list', { name: 'Recording versions' });
  await expect(versions.getByRole('listitem')).toHaveCount(1, { timeout: 30_000 });
  await expect(versions.getByRole('listitem').first()).toContainText(displayName);

  // Explicit sign-out deletes the browser's upload records; none is shown after signing in again.
  await page.goto('/account');
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/sign-in/);
  await signIn();
  await page.goto(matchUrl);
  await expect(page.getByRole('heading', { name: 'Recordings', exact: true })).toBeVisible();
  await expect(
    page.getByRole('list', { name: 'Pending uploads of this match' }).getByRole('listitem'),
  ).toHaveCount(0);
});
