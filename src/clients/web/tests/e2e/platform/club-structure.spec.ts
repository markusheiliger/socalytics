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

// Evidence (FR-001, SC-003): the flaky cause was not reproduced. 20 repeats alone passed, and a 1.5 s delay on every
// season and team list GET (candidates 1 to 3 of the spec's research) did not fail the unchanged test. The fixes below
// are therefore supported candidates, not a reproduced cause: (a) names are derived per attempt, so a rerun or repeat
// never meets the previous attempt's season and teams; (b) the club's active seasons are archived before the test, so a
// season left active by an earlier project cannot block the activation; (c) the absence assertion for the old team name
// runs only after the team list request that follows the rename was answered. Three probes (list not refreshed after
// creation, season not activated, save not persisting the name) each failed the unchanged assertions without hanging.
// Convergence runs: with one CPU-burning process per core (4), 5 repeats passed and 0 failed, run after the session and
// navigation specs (`--no-deps` on a fresh platform fails every repeat in `beforeEach` with a 401 sign-in because the
// initial password was not changed yet, a precondition failure and not a test result). The whole platform project
// ran 5 times: 5 passed, 0 failed, and this test passed each time, without a retry.
// Follow-up (FR-001, SC-003): the failure was still not reproduced. The baseline (`npm ci`, build, unit tests) passed and
// a whole `--project platform` run passed with this test green. The recorded failures of the implementation pull
// requests #235, #284, and #314 could not be read (`gh` has no `GH_TOKEN` in the implementing environment, so no logs or
// traces were available), so no candidate is supported by recorded evidence.
// Completed reproduction (convergence): with one AppHost kept running, the dependency projects once and then
// `--no-deps --repeat-each 20 --trace on` passed 20 of 20, 0 failed. Under one CPU-burning process per core (4), 5 runs
// of the project on a fresh platform passed, 0 failed. In a scratch copy (removed) with unchanged assertions, a 2.5 s
// delay on `POST .../teams`, a 3 s delay on every team list GET until the save was shown, and a 2.5 s delay on the seasons
// list GET each passed (the scratch test executed in every run). Five `--project platform` runs passed with this test
// green (two further runs failed with `ERR_CONNECTION_REFUSED` in the first specs because the previous AppHost was still
// shutting down; this test did not run in them and they were repeated). Probes again: team list not refreshed, season not
// activated, and save not persisting each failed the unchanged assertions in 5 to 16 s without hanging (3 of 3). A forced
// mid-test failure in a scratch copy left 0 active seasons after cleanup. `npm run build` and `npm test` passed.
// Verdicts: (1) second creation racing the first refetch: rejected, 2.5 s delayed POST passed; (2) pre-rename list GET
// matched by `waitForResponse`: rejected, 3 s delayed earlier list GET passed; (3) service worker `controllerchange`
// reload: rejected, the trace shows only the initial navigation; (4) seasons list refetch and `selectOption`: rejected,
// 2.5 s delayed seasons GET passed; (5) shared state and platform behavior: not reproduced in 20 + 5 + 5 runs, recorded
// logs untestable (#235, #284, and #314 unreadable); (6) runner load and readiness: not reproduced under CPU load.
// Cause: not reproduced, so the test and its support are unchanged and the earlier hardening above is the only protection.
// Archives the club's active seasons through the API so the single active season is free for this test and the next
// spec, even when a test fails.
test.beforeEach(async () => {
  await archiveActiveSeasons();
});

test.afterEach(async () => {
  await archiveActiveSeasons();
});

test('creates and activates a season, creates two teams, and edits one (SC-002)', async ({ page }) => {
  const run = `${Date.now().toString(36)}-${test.info().repeatEachIndex}`;
  const seasonName = `E2E structure ${run}`;
  const firstTeam = `E2E first ${run}`;
  const secondTeam = `E2E second ${run}`;
  const renamedTeam = `E2E renamed ${run}`;

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

  const isTeamList = (response: { url(): string; request(): { method(): string } }) =>
    response.request().method() === 'GET' && /\/api\/v1\/seasons\/[^/]+\/teams(\?|$)/.test(response.url());
  const refreshedList = page.waitForResponse(isTeamList);
  await page.getByRole('link', { name: 'Back to teams' }).click();
  await page.getByLabel('Season', { exact: true }).selectOption({ label: `${seasonName} (Active)` });
  await refreshedList;
  await expect(page.getByRole('link', { name: renamedTeam })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('link', { name: firstTeam })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('link', { name: secondTeam })).toHaveCount(0);
});
