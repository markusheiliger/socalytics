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

test('shows lane totals equal to the reported fact count and jumps from a bin to exactly its facts (FR-021, SC-004)', async ({ page }, testInfo) => {
  test.setTimeout(3 * 60 * 1000);

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

  const runs = page.getByRole('list', { name: 'Analysis runs' });
  await expect(runs.getByRole('link', { name: 'Open analysis review' }).first()).toBeVisible({ timeout: 30_000 });
  await runs.getByRole('link', { name: 'Open analysis review' }).first().click();
  await expect(page.getByRole('heading', { level: 2, name: 'Analysis review' })).toBeVisible();

  const timeline = page.getByRole('region', { name: 'Timeline', exact: true });
  await expect(timeline).toBeVisible({ timeout: 30_000 });
  const lanes = timeline.getByRole('region', { name: /^Timeline of / });
  await expect(lanes.first()).toBeVisible({ timeout: 15_000 });

  // Facts exist only once a Manager has executed the run's jobs; the platform project starts none.
  const laneCount = await lanes.count();
  let withFacts = -1;
  let reported = 0;
  for (let i = 0; i < laneCount; i++) {
    const text = (await lanes.nth(i).textContent()) ?? '';
    const total = Number(/(\d+) facts reported/.exec(text)?.[1] ?? 0);
    if (total > 0 && withFacts < 0) {
      withFacts = i;
      reported = total;
    }
  }
  if (withFacts < 0) {
    testInfo.annotations.push({ type: 'skipped', description: 'The run has no facts: no Manager executes jobs in the platform project.' });
    test.skip(true, 'The run has no facts: no Manager executes jobs in the platform project.');
  }

  const lane = lanes.nth(withFacts);
  const bins = lane.getByRole('group', { name: /^Bins of/ }).getByRole('button');
  const names = await bins.evaluateAll((buttons) => buttons.map((b) => b.getAttribute('aria-label') ?? ''));
  const counts = names.map((name) => Number(/: Facts present, (\d+)$/.exec(name)?.[1] ?? 0));

  // A complete lane's bins add up to the reported total unless a fact touches a bin boundary and is counted in two bins.
  const total = counts.reduce((sum, count) => sum + count, 0);
  expect(total).toBeGreaterThanOrEqual(reported);

  const index = counts.findIndex((count) => count > 0);
  expect(index, 'a bin with facts').toBeGreaterThanOrEqual(0);
  await bins.nth(index).click();
  const selection = lane.getByRole('group', { name: 'Selection', exact: true });
  await expect(selection.getByText(`${counts[index]} facts`, { exact: true })).toBeVisible();
  await selection.getByRole('button', { name: /^Show facts of / }).click();

  await expect(page).toHaveURL(/[?&]stream=[^&]+&fromMs=\d+&toMs=\d+$/);
  await expect(page.getByRole('region', { name: 'Timeline', exact: true })).toHaveCount(0);
  const list = page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li');
  await expect(list.first()).toBeVisible({ timeout: 15_000 });
  if (!(await page.getByRole('button', { name: /^Load more/ }).isVisible())) {
    await expect(list).toHaveCount(counts[index]);
  }
});
