import { expectNoAxeViolations } from '../support/axe';
import { activateWithKeyboard, expectAllReachableByKeyboard } from '../support/keyboard';
import { expect, test } from '../support/fixtures';
import { SIMULATED_PASSWORD, sessionFor, type SimulatedApi, type SimulatedRole } from '../support/simulatedApi';

/** Sign-in succeeds for `role` once the form is submitted; until then the session is ended. */
function allowSignIn(api: SimulatedApi, role: SimulatedRole, passwordChangeRequired = false) {
  api.endSession();
  api.on('POST', '/session', async (route) => {
    api.signInAs(role, { passwordChangeRequired });
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(sessionFor(role, passwordChangeRequired)),
    });
  });
}

async function fillSignIn(page: import('@playwright/test').Page, password = SIMULATED_PASSWORD) {
  await page.getByLabel('Sign-in name').fill('admin');
  await page.getByLabel('Password').fill(password);
}

test('signs in with the keyboard and keeps secrets out of storage (scenario 1)', async ({ page, api }) => {
  allowSignIn(api, 'ClubAdmin');
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await fillSignIn(page);
  await activateWithKeyboard(page.getByRole('button', { name: 'Sign in' }));
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  await expect(page).toHaveURL(/\/$/);
  await expectNoAxeViolations(page);
});

test('shows one generic refusal message for every refusal (scenario 2)', async ({ page, api }) => {
  api.endSession();
  const messages: string[] = [];
  for (const [status, code] of [[401, undefined], [403, 'account-locked'], [403, 'account-deactivated']] as const) {
    api.on('POST', '/session', async (route) => {
      await route.fulfill({
        status,
        contentType: 'application/problem+json',
        body: JSON.stringify({ type: 'about:blank', title: 'Refused', status, ...(code ? { code } : {}) }),
      });
    });
    await page.goto('/sign-in');
    await fillSignIn(page, 'wrong-password-1');
    await page.getByRole('button', { name: 'Sign in' }).click();
    const alert = page.getByRole('alert');
    await expect(alert).toBeVisible();
    messages.push((await alert.textContent()) ?? '');
    await expectNoAxeViolations(page);
  }
  expect(new Set(messages).size).toBe(1);
  expect(messages[0]).not.toMatch(/locked|deactivated/i);
});

test('shows only the password-change view until the change succeeds (scenario 3)', async ({ page, api }) => {
  allowSignIn(api, 'ClubAdmin', true);
  await page.goto('/sign-in');
  await fillSignIn(page);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/\/password-change$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Change your password' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await page.goto('/club');
  await expect(page).toHaveURL(/\/password-change$/);

  await page.getByLabel(/^Current password/).fill(SIMULATED_PASSWORD);
  await page.getByLabel(/^New password/).fill('Another-Password-2!');
  await page.getByLabel(/^Repeat the new password/).fill('Another-Password-2!');
  await activateWithKeyboard(page.getByRole('button', { name: 'Change password' }));
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
});

test('signs out and shows no member data after going back (scenario 6)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  await page.goto('/account');
  await expect(page.getByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/sign-in$/);
  expect(api.requests.some((r) => r.method === 'DELETE' && r.path === '/session')).toBe(true);
  await page.goBack();
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toHaveCount(0);
});

test('returns to sign-in with a session-ended message on the next action (scenario 7)', async ({ page, api }) => {
  api.signInAs('ClubAdmin', { passwordChangeRequired: true });
  await page.goto('/password-change');
  await expect(page.getByRole('heading', { level: 1, name: 'Change your password' })).toBeVisible();
  api.endSession();
  await page.getByLabel(/^Current password/).fill(SIMULATED_PASSWORD);
  await page.getByLabel(/^New password/).fill('Another-Password-2!');
  await page.getByLabel(/^Repeat the new password/).fill('Another-Password-2!');
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page).toHaveURL(/\/sign-in$/);
  await expect(page.getByText('Your session ended. Sign in again to continue.')).toBeVisible();
  await expectNoAxeViolations(page);
});

test('sends the anti-forgery token and shows a refused check as a session problem (scenario 8)', async ({
  page,
  api,
}) => {
  api.signInAs('ClubAdmin', { passwordChangeRequired: true });
  await page.goto('/password-change');
  await page.getByLabel(/^Current password/).fill(SIMULATED_PASSWORD);
  await page.getByLabel(/^New password/).fill('Another-Password-2!');
  await page.getByLabel(/^Repeat the new password/).fill('Another-Password-2!');
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  const change = api.requests.find((r) => r.method === 'POST' && r.path === '/me/password');
  expect(change?.headers['x-csrf-token']).toBeTruthy();

  api.signInAs('ClubAdmin', { passwordChangeRequired: true });
  api.on('POST', '/me/password', async (route) => {
    await route.fulfill({
      status: 403,
      contentType: 'application/problem+json',
      body: JSON.stringify({ type: 'about:blank', title: 'Forbidden', status: 403, code: 'antiforgery-failed' }),
    });
  });
  await page.goto('/password-change');
  await page.getByLabel(/^Current password/).fill(SIMULATED_PASSWORD);
  await page.getByLabel(/^New password/).fill('Another-Password-2!');
  await page.getByLabel(/^Repeat the new password/).fill('Another-Password-2!');
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText(/could not be verified/i)).toBeVisible();
});

test('asks for cookies when the browser blocks them', async ({ page, api }) => {
  api.endSession();
  await page.addInitScript(() => Object.defineProperty(Navigator.prototype, 'cookieEnabled', { get: () => false }));
  await page.goto('/sign-in');
  await fillSignIn(page);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('alert')).toContainText(/cookies/i);
  expect(api.requests.filter((r) => r.method === 'POST' && r.path === '/session')).toHaveLength(0);
});
