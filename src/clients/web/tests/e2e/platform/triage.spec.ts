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
const seasonName = `E2E triage ${run}`;
const teamName = `E2E triage team ${run}`;
const opponent = `E2E triage opponent ${run}`;

// Frees the club's single active season for the next spec, even when the test fails.
test.afterEach(async () => {
  await archiveActiveSeasons();
});

// The platform's local composition produces no completed analysis run, so only the empty list, the no-eligible-run
// message, and the not-found view are reachable here; the complete flow is proven against the simulated API (FR-024).
test('shows the empty triage section and the not-found view for an unknown conversation (FR-024)', async ({ page }) => {
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
  await expect(page.getByRole('heading', { level: 2, name: teamName })).toBeVisible();

  await page.getByRole('link', { name: 'New match' }).click();
  await page.getByLabel(/^Opponent/).fill(opponent);
  await page.getByLabel(/^Kickoff time/).fill('2031-05-17T15:30');
  await page.getByRole('button', { name: 'Create match' }).click();
  await expect(page.getByRole('heading', { level: 2, name: opponent })).toBeVisible();

  const triage = page.getByRole('region', { name: 'Triage conversations' });
  await expect(triage.getByText('You have no active triage conversations for this match.')).toBeVisible({ timeout: 15_000 });
  await expect(triage.getByText('Triage needs a finished analysis of this match first.')).toBeVisible({ timeout: 15_000 });
  await expect(triage.getByRole('button', { name: 'Start triage conversation' })).toHaveCount(0);

  const matchPath = new URL(page.url()).pathname;
  await page.goto(`${matchPath}/triage/00000000-0000-4000-8000-000000000000`);
  await expect(page.getByRole('heading', { level: 2, name: 'This page does not exist.' })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('link', { name: 'Back to match' })).toBeVisible();
});
