import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { SIMULATED_PASSWORD } from '../support/simulatedApi';

const TOKEN = 'simulated-recovery-token';

test.beforeEach(({ api }) => {
  api.endSession();
  api.recoveryTokens.set(TOKEN, 'jdoe');
});

test('requests a recovery from the sign-in page and shows the generic confirmation (FR-001)', async ({ page, api }) => {
  await page.goto('/sign-in');
  await page.getByRole('link', { name: 'Forgot your password?' }).click();
  await expect(page).toHaveURL(/\/password-recovery$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Reset your password' })).toBeVisible();
  await expectNoAxeViolations(page);

  await page.getByLabel('Email address').fill('jdoe@example.org');
  await page.getByRole('button', { name: 'Send reset link' }).click();
  await expect(page.getByRole('status')).toContainText('a message with a link is on its way');
  expect(api.recoveryRequests).toEqual(['jdoe@example.org']);
  await expectNoAxeViolations(page);

  await page.getByRole('link', { name: 'Back to sign in' }).click();
  await expect(page).toHaveURL(/\/sign-in$/);
});

test('opens the recovery link, completes it, and lands on sign-in (FR-013, FR-022)', async ({ page, api }) => {
  await page.goto(`/password-recovery/complete#token=${TOKEN}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Choose a new password' })).toBeVisible();
  await expect(page.getByText('Choose a new password for the account jdoe.')).toBeVisible();
  await expect(page).toHaveURL(/\/password-recovery\/complete$/);
  expect(await page.evaluate(() => window.location.hash)).toBe('');
  await expectNoAxeViolations(page);

  await page.getByLabel(/^New password/).fill(SIMULATED_PASSWORD);
  await page.getByLabel(/^Confirm new password/).fill(SIMULATED_PASSWORD);
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.getByRole('status')).toContainText('Your password has been changed');
  expect(api.recoveryCompletions).toEqual([{ accountName: 'jdoe', newPassword: SIMULATED_PASSWORD }]);
  await expectNoAxeViolations(page);

  await page.getByRole('link', { name: 'Go to sign in' }).click();
  await expect(page).toHaveURL(/\/sign-in$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
});

test('shows the neutral message for an invalid token (FR-013)', async ({ page }) => {
  await page.goto('/password-recovery/complete#token=unknown-token');
  await expect(page.getByRole('alert')).toContainText('This link cannot be used');
  await expect(page).toHaveURL(/\/password-recovery\/complete$/);
  await expectNoAxeViolations(page);

  await page.getByRole('link', { name: 'Request a new link' }).click();
  await expect(page).toHaveURL(/\/password-recovery$/);
});
