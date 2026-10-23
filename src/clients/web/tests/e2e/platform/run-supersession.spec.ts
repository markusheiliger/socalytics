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
const seasonName = `E2E supersession ${run}`;
const teamName = `E2E supersession team ${run}`;
const opponent = `E2E supersession opponent ${run}`;
const displayName = `E2E supersession camera ${run}`;
const reason = `E2E supersession ${run}`;

// Frees the club's single active season for the next spec, even when the test fails.
test.afterEach(async () => {
  await archiveActiveSeasons();
});

test('marks the earlier run as superseded with its member cancellation and the new run as current (FR-019, FR-020, FR-022)', async ({ page }, testInfo) => {
  test.setTimeout(6 * 60 * 1000);
  // The upload flow's season is archived by now and cannot take a second recording set, so this spec uses its own match.
  const file = await writeTestVideo(testInfo.outputPath('files'), FORTY_MIB);

  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
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
  await expect(page.getByRole('list', { name: 'Recording versions' }).getByRole('listitem')).toHaveCount(1, { timeout: 30_000 });

  const finalize = async (again: boolean) => {
    await page.getByRole('link', { name: 'Finalize recordings' }).click();
    await page.getByLabel(`Include ${displayName}`).check();
    await page.getByRole('button', { name: 'Review and finalize' }).click();
    await page.getByRole('button', { name: again ? 'Finalize and start another analysis' : 'Finalize', exact: true }).click();
    await expect(page).toHaveURL(/\/matches\/[0-9a-f-]+$/);
  };

  // The first recording set and its run, which a member cancels.
  await finalize(false);
  const sets = page.getByRole('list', { name: 'Finalized recording sets' });
  await expect(sets.getByRole('listitem').first()).toContainText('1 recording(s)', { timeout: 30_000 });
  const runItems = sets.getByRole('list', { name: 'Analysis runs' }).getByRole('listitem');
  const cancelButton = runItems.first().getByRole('button', { name: 'Cancel run' });
  await expect(async () => {
    await page.reload();
    await expect(cancelButton).toBeVisible({ timeout: 5_000 });
  }).toPass({ timeout: 90_000, intervals: [2_000, 5_000] });
  await cancelButton.click();
  const dialog = page.getByRole('dialog', { name: 'Cancel this analysis run?' });
  await dialog.getByLabel('Reason').fill(reason);
  await dialog.getByRole('button', { name: 'Cancel run' }).click();
  await expect(dialog).toBeHidden({ timeout: 15_000 });
  await expect(runItems.first().getByRole('status')).toContainText(`Cancelled · by ${ADMIN_ACCOUNT}`);

  // A second recording set; its run supersedes the first run.
  await finalize(true);
  const statuses = sets.getByRole('list', { name: 'Analysis runs' }).getByRole('status');
  const superseded = statuses.filter({ hasText: 'Superseded by the run of the recording set finalized' });
  const current = statuses.filter({ hasText: 'Current run' });
  await expect(async () => {
    await page.reload();
    await expect(superseded).toHaveCount(1, { timeout: 5_000 });
    await expect(current).toHaveCount(1, { timeout: 5_000 });
  }).toPass({ timeout: 90_000, intervals: [2_000, 5_000] });

  await expect(superseded).toContainText(`Cancelled · by ${ADMIN_ACCOUNT}`);
  await expect(superseded).toContainText(reason);
  await expect(current).not.toContainText('Superseded by');
  await expect(statuses).toHaveCount(2);
});
