import { expect, test as base } from '../support/fixtures';
import { archiveActiveSeasons } from '../support/platformState';

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
const seasonName = `E2E structure ${run}`;
const firstTeam = `E2E first ${run}`;
const secondTeam = `E2E second ${run}`;
const renamedTeam = `E2E renamed ${run}`;

// Archives the club's active seasons through the API so the single active season is free for the next spec, even when
// the test fails.
test.afterEach(async () => {
  await archiveActiveSeasons();
});

test('creates and activates a season, creates two teams, and edits one (SC-002)', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  const nav = page.getByRole('navigation', { name: 'Main navigation' });

  await nav.getByRole('link', { name: 'Seasons' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Seasons' })).toBeVisible();
  await page.getByLabel(/^Season name/).fill(seasonName);
  await page.getByRole('button', { name: 'Create season' }).click();
  const seasonItem = page.getByRole('listitem').filter({ hasText: seasonName });
  await expect(seasonItem).toContainText('Draft');

  await seasonItem.getByRole('link', { name: seasonName }).click();
  await expect(page.getByText('State: Draft')).toBeVisible();
  await page.getByRole('button', { name: 'Activate' }).click();
  await expect(page.getByText('The season is now active.')).toBeVisible();
  await expect(page.getByText('State: Active')).toBeVisible();

  await nav.getByRole('link', { name: 'Teams' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Teams' })).toBeVisible();
  await page.getByLabel('Season', { exact: true }).selectOption({ label: `${seasonName} (Active)` });

  for (const name of [firstTeam, secondTeam]) {
    await page.getByLabel(/^Team name/).fill(name);
    await page.getByRole('button', { name: 'Create team' }).click();
    await expect(page.getByText('The team was created.')).toBeVisible();
    await expect(page.getByRole('link', { name })).toBeVisible({ timeout: 15_000 });
  }
  await expect(page.getByRole('link', { name: firstTeam })).toBeVisible({ timeout: 15_000 });

  await page.getByRole('link', { name: secondTeam }).click();
  // The form is unmounted while the team refetches; fill only once it holds the team's current name and can be saved.
  const nameField = page.getByLabel(/^Team name/);
  const save = page.getByRole('button', { name: 'Save' });
  await expect(save).toBeEnabled();
  await expect(nameField).toHaveValue(secondTeam);
  await nameField.fill(renamedTeam);
  await save.click();
  await expect(page.getByText('The team was saved.')).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: renamedTeam })).toBeVisible();
  await expect(page.getByLabel(/^Team name/)).toHaveValue(renamedTeam);

  await page.getByRole('link', { name: 'Back to teams' }).click();
  await page.getByLabel('Season', { exact: true }).selectOption({ label: `${seasonName} (Active)` });
  await expect(page.getByRole('link', { name: renamedTeam })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('link', { name: firstTeam })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('link', { name: secondTeam })).toHaveCount(0);
});
