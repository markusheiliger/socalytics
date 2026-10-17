import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';

const provider = { id: 'club-entra', displayName: 'Club account' };

test('shows no provider buttons when none is configured', async ({ page, api }) => {
  api.endSession();
  await page.goto('/sign-in');
  await expect(page.getByLabel('Sign-in name')).toBeVisible();
  await expect(page.getByRole('button', { name: /Sign in with/ })).toHaveCount(0);
  await expectNoAxeViolations(page);
});

test('signs in through the provider and lands on home', async ({ page, api }) => {
  api.endSession();
  api.providers = [provider];
  await api.simulateProviderRedirect('success');
  await page.goto('/sign-in');
  await expect(page.getByRole('button', { name: 'Sign in with Club account' })).toBeVisible();
  await expectNoAxeViolations(page);
  await page.getByRole('button', { name: 'Sign in with Club account' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
});

test('shows the generic refusal after a failed callback', async ({ page, api }) => {
  api.endSession();
  api.providers = [provider];
  await api.simulateProviderRedirect('failed');
  await page.goto('/sign-in');
  await page.getByRole('button', { name: 'Sign in with Club account' }).click();
  await expect(page.getByRole('alert')).toContainText('Sign-in was not possible');
  await expect(page.getByLabel('Sign-in name')).toBeEnabled();
  await expectNoAxeViolations(page);
});

test('says the provider is unavailable and keeps the local form usable', async ({ page, api }) => {
  api.endSession();
  api.providers = [provider];
  api.providerUnavailable = true;
  await page.goto('/sign-in');
  await page.getByRole('button', { name: 'Sign in with Club account' }).click();
  await expect(page.getByRole('alert')).toContainText('currently unavailable');
  await expect(page.getByLabel('Sign-in name')).toBeEnabled();
  await expectNoAxeViolations(page);
});
