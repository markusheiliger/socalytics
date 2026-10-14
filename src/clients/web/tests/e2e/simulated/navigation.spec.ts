import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard, expectAllReachableByKeyboard } from '../support/keyboard';
import type { SimulatedRole } from '../support/simulatedApi';

const NAVIGATION: Record<Exclude<SimulatedRole, 'Registrar'>, string[]> = {
  ClubAdmin: ['Home', 'Club', 'Seasons', 'Teams', 'Members', 'Pair a Manager', 'Account'],
  Coach: ['Home', 'Teams', 'Account'],
  Viewer: ['Home', 'Teams', 'Account'],
};

for (const role of Object.keys(NAVIGATION) as (keyof typeof NAVIGATION)[]) {
  test(`navigation of a ${role} offers exactly the allowed routes (scenarios 1, 2)`, async ({ page, api }) => {
    api.signInAs(role);
    await page.goto('/');
    const nav = page.getByRole('navigation', { name: 'Main navigation' });
    await expect(nav.getByRole('link')).toHaveText(NAVIGATION[role]);
    await expectNoAxeViolations(page);
    for (const label of NAVIGATION[role].slice(1)) {
      await nav.getByRole('link', { name: label, exact: true }).click();
      await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
      await expectNoAxeViolations(page);
    }
  });
}

for (const role of Object.keys(NAVIGATION) as (keyof typeof NAVIGATION)[]) {
  test(`shell navigation and home view of a ${role} are operable by keyboard`, async ({ page, api }) => {
    api.signInAs(role);
    await page.goto('/');
    await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
    await expectAllReachableByKeyboard(page);
    const nav = page.getByRole('navigation', { name: 'Main navigation' });
    const last = NAVIGATION[role][NAVIGATION[role].length - 1];
    await activateWithKeyboard(nav.getByRole('link', { name: last, exact: true }));
    await expect(page).toHaveURL(new RegExp(`/${last.toLowerCase()}$`));
    await expectAllReachableByKeyboard(page);
  });
}

test('shows a not-permitted view without loading data (scenario 3)', async ({ page, api }) => {
  api.signInAs('Viewer');
  await page.goto('/members');
  await expect(page.getByRole('alert')).toContainText('not permitted');
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page);
  expect(api.requests.some((r) => r.path === '/members')).toBe(false);
});

test('shows a not-found view for an unknown address', async ({ page, api }) => {
  api.signInAs('Coach');
  await page.goto('/nowhere');
  await expect(page.getByRole('heading', { level: 2, name: 'This page does not exist.' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page);
});

test('shows the connectivity state, disables actions, and recovers (scenario 4)', async ({ page, api, context }) => {
  api.signInAs('ClubAdmin');
  await page.goto('/account');
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeEnabled();
  await context.setOffline(true);
  await expect(page.getByText(/The platform is not reachable/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeDisabled();
  await expectNoAxeViolations(page);
  await context.setOffline(false);
  await expect(page.getByText(/The platform is not reachable/)).toHaveCount(0, { timeout: 15_000 });
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeEnabled();
});

test('follows changed roles on the next action (scenario 5)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  await page.goto('/account');
  await expect(page.getByRole('link', { name: 'Members' })).toBeVisible();
  api.signInAs('Viewer');
  await page.getByLabel(/^Current password/).fill('Simulated-Password-1!');
  await page.getByLabel(/^New password/).fill('Another-Password-2!');
  await page.getByLabel(/^Repeat the new password/).fill('Another-Password-2!');
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByRole('link', { name: 'Members' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Teams' })).toBeVisible();
});
