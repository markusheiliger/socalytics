import type { Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';

const MEMBER = '00000000-0000-4000-8000-000000000021';
const provider = { id: 'club-entra', displayName: 'Club account' };
const fulfillJson = (route: Route, status: number, body: unknown) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? 'application/problem+json' : 'application/json',
    body: JSON.stringify(body),
  });
const dto = (externalIdentities: unknown[]) => ({
  id: MEMBER,
  accountName: 'newbie',
  membershipStatus: 'active',
  passwordSet: false,
  lockedOut: false,
  lockoutEndsAt: null,
  twoFactorEnabled: false,
  clubRoles: [],
  teamRoles: [],
  externalIdentities,
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
});
const linked = { provider: 'club-entra', displayName: 'Club account', linkedAt: '2026-10-13T12:00:00Z', providerConfigured: true };

test('redeems a link code on the public page without putting it in the address', async ({ page, api }) => {
  api.endSession();
  api.providers = [provider];
  api.linkCodes.add('LINK-4711');
  await api.simulateProviderRedirect('success');
  await page.goto('/link-account');
  await expect(page.getByRole('heading', { level: 1, name: 'Link your account' })).toBeVisible();
  await expectNoAxeViolations(page);
  await page.getByLabel('Sign-in name').fill('newbie');
  await page.getByLabel('Link code').fill('LINK-4711');
  expect(page.url()).not.toContain('LINK-4711');
  await page.getByRole('button', { name: 'Continue to provider' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  expect(api.externalStarts).toEqual([{ provider: 'club-entra', accountName: 'newbie', linkCredential: 'LINK-4711' }]);
});

test('refuses an unknown link code generically', async ({ page, api }) => {
  api.endSession();
  api.providers = [provider];
  await page.goto('/link-account');
  await page.getByLabel('Sign-in name').fill('newbie');
  await page.getByLabel('Link code').fill('WRONG');
  await page.getByRole('button', { name: 'Continue to provider' }).click();
  await expect(page.getByRole('alert')).toContainText('The account was not linked');
  await expectNoAxeViolations(page);
});

test('a Club Admin issues a link code and unlinks after confirmation', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.providers = [provider];
  let identities: unknown[] = [];
  api.on('GET', `/members/${MEMBER}`, (route) => fulfillJson(route, 200, dto(identities)));
  api.on('POST', `/members/${MEMBER}/credentials`, (route) => {
    identities = [linked];
    return fulfillJson(route, 201, { memberId: MEMBER, purpose: 'identity-link', credential: 'ONCE-LINK-4711', expiresAt: '2026-10-13T12:00:00Z' });
  });
  api.on('DELETE', `/members/${MEMBER}/external-identities/club-entra`, (route) => {
    identities = [];
    return route.fulfill({ status: 204 });
  });
  await page.goto(`/members/${MEMBER}`);
  await page.getByRole('button', { name: 'Issue link code for Club account' }).click();
  await expect(page.getByText('ONCE-LINK-4711')).toBeVisible();
  await expectNoAxeViolations(page);
  expect(page.url()).not.toContain('ONCE-LINK');
  await page.getByRole('button', { name: 'I have handed over the code' }).click();
  await expect(page.getByText('ONCE-LINK-4711')).toHaveCount(0);
  await page.reload();
  await expect(page.getByText(/Club account, linked/)).toBeVisible();
  await page.getByRole('button', { name: 'Unlink Club account' }).click();
  await expectNoAxeViolations(page);
  await page.getByRole('button', { name: 'Unlink', exact: true }).click();
  await expect(page.getByText('No provider account is linked.')).toBeVisible();
});
