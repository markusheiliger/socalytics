import { expect, test as base } from '../support/fixtures';
import { extractConfirmationLink, waitForMessageText } from '../support/mailpit';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const TWO_MINUTES = 2 * 60 * 1000;

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD]);
  },
});

const address = `club-admin-${Date.now().toString(36)}@example.org`;

test('sets an address, reads the link from Mailpit, confirms it, and sees it verified within 2 minutes (SC-001)', async ({
  page,
  baseURL,
  secrets,
}) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  await page.goto('/account');
  await expect(page.getByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
  await expect(page.getByText(/no email address yet|Your address is/)).toBeVisible();

  // SC-001 measures the steps from setting the address to seeing it verified.
  const started = Date.now();
  await page.getByRole('textbox', { name: /email address/i }).fill(address);
  await page.getByRole('button', { name: /^(Set|Change) address$/ }).click();
  await expect(page.getByText(/we sent a confirmation link/i)).toBeVisible();
  await expect(page.getByText(/not confirmed yet/i)).toBeVisible();

  const link = extractConfirmationLink(await waitForMessageText(address));
  const token = new URLSearchParams(link.hash.slice(1)).get('token');
  expect(token, 'the link carries a token').toBeTruthy();
  secrets.push(token!);

  // The link points at the Web UI's own origin; the browser opens it on the origin of the signed-in session.
  await page.goto(`${link.pathname}${link.hash}`);
  expect(page.url()).not.toContain(token!);
  await expect(page.getByRole('heading', { level: 1, name: 'Confirm your email address' })).toBeVisible();
  await page.getByRole('button', { name: 'Confirm this address' }).click();
  await expect(page.getByText('Your address is confirmed.')).toBeVisible();

  await page.getByRole('link', { name: 'Go to your account' }).click();
  await expect(page.getByText(`Your address is ${address}. Confirmed.`)).toBeVisible();

  expect(Date.now() - started, `from setting the address to seeing it verified at ${baseURL}`).toBeLessThan(TWO_MINUTES);
});
