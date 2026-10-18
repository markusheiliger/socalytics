import { expect, test as base } from '../support/fixtures';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD]);
  },
});

const reason = `E2E cancellation ${Date.now().toString(36)}`;

test('cancels the run the upload flow created and shows it as cancelled with the reason (FR-021)', async ({ page }) => {
  test.setTimeout(3 * 60 * 1000);

  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  // The upload flow's season is archived by now; the newest one of its seasons holds the run.
  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await nav.getByRole('link', { name: 'Teams' }).click();
  const seasonSelect = page.getByLabel('Season', { exact: true });
  const uploadSeasons = seasonSelect.locator('option', { hasText: /^E2E upload \S+ \(/ });
  await expect(uploadSeasons.first()).toBeAttached({ timeout: 15_000 });
  const label = await uploadSeasons.last().textContent();
  await seasonSelect.selectOption({ label: label as string });
  await page.getByRole('link', { name: /^E2E upload team / }).first().click({ timeout: 15_000 });
  await page.getByRole('link', { name: /^E2E upload opponent / }).first().click({ timeout: 15_000 });

  const runs = page.getByRole('list', { name: 'Analysis runs' });
  const runItem = runs.getByRole('listitem').first();
  const cancelButton = runItem.getByRole('button', { name: 'Cancel run' });
  await expect(async () => {
    await page.reload();
    await expect(cancelButton).toBeVisible({ timeout: 5_000 });
  }).toPass({ timeout: 90_000, intervals: [2_000, 5_000] });

  await cancelButton.click();
  const dialog = page.getByRole('dialog', { name: 'Cancel this analysis run?' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Reason').fill(reason);
  await dialog.getByRole('button', { name: 'Cancel run' }).click();
  await expect(dialog).toBeHidden({ timeout: 15_000 });

  const status = runItem.getByRole('status');
  await expect(status).toContainText(`Cancelled · by ${ADMIN_ACCOUNT}`);
  await expect(status).toContainText(reason);
  await expect(runItem.getByRole('button', { name: 'Cancel run' })).toHaveCount(0);

  // The platform keeps the cancellation after a reload.
  await page.reload();
  await expect(runs.getByRole('listitem').first().getByRole('status')).toContainText(reason, { timeout: 15_000 });
});
