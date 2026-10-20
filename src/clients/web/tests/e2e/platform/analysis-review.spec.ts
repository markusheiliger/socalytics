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

test('reviews the run the upload flow created: run and node status, and the not-found view (FR-022)', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  // The upload flow archived its season afterwards, so the team is reached through the season chooser.
  await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Teams' }).click();
  const seasonSelect = page.getByLabel('Season', { exact: true });
  const seasonOptions = seasonSelect.locator('option', { hasText: /^E2E upload [a-z0-9]+ \(/ });
  await expect(seasonOptions.first()).toBeAttached({ timeout: 15_000 });
  const seasonValue = await seasonOptions.last().getAttribute('value');
  await seasonSelect.selectOption(seasonValue ?? '');
  await page.getByRole('link', { name: /^E2E upload team / }).click({ timeout: 15_000 });
  await page.getByRole('list', { name: 'Matches' }).getByRole('link', { name: /^E2E upload opponent / }).click({ timeout: 15_000 });
  await expect(page.getByRole('heading', { level: 2, name: /^E2E upload opponent / })).toBeVisible();
  const matchUrl = page.url();
  const matchId = /\/matches\/([0-9a-f-]+)$/.exec(matchUrl)?.[1];
  expect(matchId, 'match identifier in the URL').toBeTruthy();

  const runs = page.getByRole('list', { name: 'Analysis runs' });
  await expect(runs.getByRole('link', { name: 'Open analysis review' }).first()).toBeVisible({ timeout: 30_000 });
  await runs.getByRole('link', { name: 'Open analysis review' }).first().click();

  await expect(page).toHaveURL(new RegExp(`/matches/${matchId}/analysis-runs/[0-9a-f-]+$`));
  await expect(page.getByRole('heading', { level: 2, name: 'Analysis review' })).toBeVisible();
  const details = page.getByRole('region', { name: 'Run details' });
  await expect(details).toContainText(
    /State\s*(Requested|Running|Partially completed|Completed|Failed|Cancelled)/,
    { timeout: 15_000 },
  );
  await expect(details.getByText('Workflow', { exact: true })).toBeVisible();

  const nodes = page.getByRole('list', { name: 'Capability nodes of the run' });
  await expect(nodes).toBeVisible();
  const first = nodes.getByRole('listitem').first();
  await expect(first).toContainText('Node key');
  await expect(first).toContainText(/(Waiting|Active|Completed|Failed|Blocked)/);
  await expect(first.getByRole('list', { name: 'Jobs' })).toContainText('Waiting:');

  // No Manager executes jobs in this project, so the first stream has no facts and says why.
  const streams = page.getByRole('list', { name: 'Fact streams of the run' });
  await expect(streams).toBeVisible({ timeout: 15_000 });
  await streams.getByRole('button', { name: /^Show facts of / }).first().click();
  await expect(
    page.getByText(/The analysis is still in progress; no facts yet\.|The analysis is incomplete\. Some windows are not accepted\./),
  ).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('list', { name: 'Facts of the selected stream' })).toHaveCount(0);

  await page.goto(`/matches/${matchId}/analysis-runs/00000000-0000-4000-8000-000000000000`);
  await expect(page.getByText('This page does not exist.')).toBeVisible({ timeout: 15_000 });
});
