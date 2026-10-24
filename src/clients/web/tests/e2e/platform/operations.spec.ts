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

// Runs after the upload flow, so the outgoing messages list normally has entries; the detail is opened only then.
test('reads the operational summary, lists, a message detail, and runs a reconciliation (FR-023)', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Operations', exact: true }).click();
  await expect(page).toHaveURL(/\/operations$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Operations' })).toBeVisible();
  await expect(page.locator('dt:text-is("Rejected notifications")')).toBeVisible();
  await expect(page.locator('dt:text-is("Outgoing messages pending")')).toBeVisible();
  await expect(page.locator('dt:text-is("Outgoing messages failed")')).toBeVisible();
  const observed = page.getByRole('status').filter({ hasText: 'Observed at' });
  await expect(observed).toBeVisible();

  await page.goto('/operations/outbox-messages');
  await expect(page.getByRole('heading', { level: 1, name: 'Outgoing messages' })).toBeVisible();
  const messageLinks = page.getByRole('list', { name: 'Outgoing messages' }).getByRole('link');
  if ((await messageLinks.count()) > 0) {
    await messageLinks.first().click();
    await expect(page).toHaveURL(/\/operations\/outbox-messages\/[0-9a-f-]+$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Outgoing message' })).toBeVisible();
    await expect(page.locator('dt:text-is("Message ID")')).toBeVisible();
    await expect(page.locator('dt:text-is("State")')).toBeVisible();
  }

  await page.goto('/operations/notification-receipts');
  await expect(page.getByRole('heading', { level: 1, name: 'Notification receipts' })).toBeVisible();

  await page.goto('/operations');
  await page.getByRole('button', { name: 'Run reconciliation' }).click();
  const dialog = page.getByRole('dialog', { name: 'Run reconciliation' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Run reconciliation' }).click();
  await expect(dialog).toContainText('Reconciliation result');
  await expect(dialog).toContainText(/\d+ ready jobs and \d+ notifications were queued again|Nothing needed to be queued again/);
  await expect(dialog).toContainText('Started');
  await expect(dialog).toContainText('Completed');
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toHaveCount(0);
  await expect(observed).toBeVisible();
});
