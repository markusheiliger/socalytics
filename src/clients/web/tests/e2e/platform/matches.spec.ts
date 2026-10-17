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
const seasonName = `E2E matches ${run}`;
const teamName = `E2E matches team ${run}`;
const opponent = `E2E opponent ${run}`;
const competition = `E2E cup ${run}`;

// Archives the club's active seasons through the API so the single active season is free for the next spec, even when
// the test fails.
test.afterEach(async () => {
  await archiveActiveSeasons();
});

test('creates a match, edits its competition, and lists it for the team (SC-002)', async ({ page }) => {
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
  await page.getByLabel(/^Team name/).fill(teamName);
  await page.getByRole('button', { name: 'Create team' }).click();
  await expect(page.getByText('The team was created.')).toBeVisible();
  await page.getByRole('link', { name: teamName }).click({ timeout: 15_000 });
  await expect(page.getByRole('heading', { level: 2, name: teamName })).toBeVisible();

  await page.getByRole('link', { name: 'New match' }).click();
  await page.getByLabel(/^Opponent/).fill(opponent);
  await page.getByLabel(/^Kickoff time/).fill('2031-05-17T15:30');
  await page.getByLabel('Venue').selectOption('away');
  await page.getByRole('button', { name: 'Create match' }).click();
  await expect(page.getByRole('heading', { level: 2, name: opponent })).toBeVisible();

  await page.getByRole('link', { name: 'Edit match' }).click();
  // The form is unmounted while the match loads; fill only once it holds the match's current values.
  await expect(page.getByLabel(/^Opponent/)).toHaveValue(opponent);
  await page.getByLabel('Competition').fill(competition);
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('The match was saved.')).toBeVisible();

  await page.getByRole('link', { name: 'Back to match' }).click();
  await expect(page.getByRole('heading', { level: 2, name: opponent })).toBeVisible();
  await page.getByRole('link', { name: 'Back to team' }).click();
  const list = page.getByRole('list', { name: 'Matches' });
  await expect(list.getByRole('link', { name: opponent })).toBeVisible({ timeout: 15_000 });
  await expect(list).toContainText(competition);
});
