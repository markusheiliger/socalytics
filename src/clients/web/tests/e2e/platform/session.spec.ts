import { expect, test as base } from '../support/fixtures';

const ACCOUNT_NAME = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const E2E_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
const NEW_PASSWORD = 'E2e-Changed-Password-0002!';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([E2E_PASSWORD, NEW_PASSWORD]);
  },
});

test.describe.configure({ mode: 'serial' });

async function signIn(page: import('@playwright/test').Page, password: string) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ACCOUNT_NAME);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

test('signs in as the first Club Admin, changes the initial password, signs out, and signs in again', async ({
  page,
}) => {
  await signIn(page, E2E_PASSWORD);
  await expect(page).toHaveURL(/\/password-change$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Change your password' })).toBeVisible();

  await page.goto('/club');
  await expect(page).toHaveURL(/\/password-change$/);

  await page.getByLabel(/^Current password/).fill(E2E_PASSWORD);
  await page.getByLabel(/^New password/).fill(NEW_PASSWORD);
  await page.getByLabel(/^Repeat the new password/).fill(NEW_PASSWORD);
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  await page.goto('/account');
  await expect(page.getByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/sign-in$/);

  await signIn(page, NEW_PASSWORD);
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
});

test('returns to sign-in without member data once the session has ended', async ({ page, context }) => {
  await signIn(page, NEW_PASSWORD);
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  await context.clearCookies();
  await page.reload();

  await expect(page).toHaveURL(/\/sign-in$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toHaveCount(0);
  await expect(page.getByText(ACCOUNT_NAME)).toHaveCount(0);

  const response = await page.request.get('/api/v1/session');
  expect(response.status()).toBe(401);
});
