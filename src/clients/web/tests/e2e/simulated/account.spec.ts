import { expectNoAxeViolations } from '../support/axe';
import { expectAllReachableByKeyboard } from '../support/keyboard';
import { expect, test } from '../support/fixtures';
import { SIMULATED_PASSWORD } from '../support/simulatedApi';

const CODE = 'ABCD-EFGH-2345';

async function fillSetPassword(page: import('@playwright/test').Page, password = 'Chosen-Password-3!') {
  await page.getByLabel('Sign-in name').fill('newbie');
  await page.getByLabel('Code').fill(CODE);
  await page.getByLabel(/^New password/).fill(password);
  await page.getByLabel(/^Repeat the new password/).fill(password);
}

test.beforeEach(({ secrets }) => {
  secrets.push(CODE);
});

test('sets a password from a code without the code entering an address (scenario 4)', async ({ page, api }) => {
  api.endSession();
  await page.goto('/set-password');
  await expect(page.getByRole('heading', { level: 1, name: 'Set your password' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await fillSetPassword(page);
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.getByText('Your password is set. You can sign in now.')).toBeVisible();
  await expect(page).toHaveURL(/\/set-password$/);
  await expect(page.getByRole('link', { name: 'Go to sign-in' })).toBeVisible();
  await expectNoAxeViolations(page);

  const redeem = api.requests.find((r) => r.method === 'POST' && r.path === '/credentials/redeem');
  expect(redeem).toBeTruthy();
  expect(page.url()).not.toContain(CODE);
});

test('shows a clear message for an expired, used, or revoked code (scenario 4)', async ({ page, api }) => {
  api.endSession();
  api.on('POST', '/credentials/redeem', async (route) => {
    await route.fulfill({
      status: 400,
      contentType: 'application/problem+json',
      body: JSON.stringify({ type: 'about:blank', title: 'Bad Request', status: 400, code: 'credential-invalid' }),
    });
  });
  await page.goto('/set-password');
  await fillSetPassword(page);
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.getByRole('alert')).toContainText(/Contact a Club Admin/);
  await expectNoAxeViolations(page);
});

test('asks to sign out first when a member is signed in (edge case)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  await page.goto('/set-password');
  await expect(page.getByText(/Sign out before you set a password/)).toBeVisible();
  await expect(page.getByLabel('Code')).toHaveCount(0);
  await expectNoAxeViolations(page);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByLabel('Code')).toBeVisible();
  expect(api.requests.some((r) => r.path === '/credentials/redeem')).toBe(false);
});

test('changes the own password from the account view (scenario 5)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  await page.goto('/account');
  await expect(page.getByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await page.getByLabel(/^Current password/).fill(SIMULATED_PASSWORD);
  await page.getByLabel(/^New password/).fill('Another-Password-2!');
  await page.getByLabel(/^Repeat the new password/).fill('Another-Password-2!');
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText('Your password was changed.')).toBeVisible();
  const change = api.requests.find((r) => r.method === 'POST' && r.path === '/me/password');
  expect(change?.headers['x-csrf-token']).toBeTruthy();
});

test('shows the platform refusal of a weak password (scenario 5)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.on('POST', '/me/password', async (route) => {
    await route.fulfill({
      status: 400,
      contentType: 'application/problem+json',
      body: JSON.stringify({
        type: 'about:blank',
        title: 'Bad Request',
        status: 400,
        code: 'validation-failed',
        errors: [{ field: 'newPassword', code: 'password-policy' }],
      }),
    });
  });
  await page.goto('/account');
  await page.getByLabel(/^Current password/).fill(SIMULATED_PASSWORD);
  await page.getByLabel(/^New password/).fill('weak');
  await page.getByLabel(/^Repeat the new password/).fill('weak');
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByRole('alert')).toContainText(/password policy/i);
  await expectNoAxeViolations(page);
});
