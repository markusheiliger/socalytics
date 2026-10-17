import type { Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { expectAllReachableByKeyboard } from '../support/keyboard';
import { problem } from '../support/simulatedApi';

const MEMBER = '00000000-0000-4000-8000-000000000021';
const fulfillJson = (route: Route, status: number, body: unknown) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? 'application/problem+json' : 'application/json',
    body: JSON.stringify(body),
  });
const member = {
  id: MEMBER,
  accountName: 'ada',
  membershipStatus: 'active',
  passwordSet: true,
  lockedOut: false,
  lockoutEndsAt: null,
  twoFactorEnabled: false,
  clubRoles: [],
  teamRoles: [],
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
};

test('a Club Admin sets, sees, and removes a member address with the keyboard (scenarios 1-3)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  let email: { address: string; state: string; verifiedAt: string | null; version: number } | undefined;
  api.on('GET', `/members/${MEMBER}`, (route) => fulfillJson(route, 200, member));
  api.on('GET', `/members/${MEMBER}/email`, (route) =>
    email ? fulfillJson(route, 200, email) : fulfillJson(route, 404, problem(404, 'Not found')),
  );
  api.on('PUT', `/members/${MEMBER}/email`, (route, body) => {
    email = { address: (body as { address: string }).address, state: 'unverified', verifiedAt: null, version: 1 };
    return fulfillJson(route, 200, email);
  });
  api.on('DELETE', `/members/${MEMBER}/email`, (route) => {
    email = undefined;
    return route.fulfill({ status: 204 });
  });

  await page.goto(`/members/${MEMBER}`);
  await expect(page.getByText('This member has no email address.')).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  const field = page.getByRole('textbox', { name: /member's email address/i });
  await field.focus();
  await page.keyboard.type('ada@example.org');
  await page.keyboard.press('Enter');
  await expect(page.getByText(/we sent a confirmation link/i)).toBeVisible();
  await expect(page.getByText(/ada@example.org/)).toBeVisible();
  await expect(page.getByText(/not confirmed yet/i)).toBeVisible();
  const put = api.requests.find((r) => r.method === 'PUT' && r.path === `/members/${MEMBER}/email`);
  expect(put?.headers['x-csrf-token']).toBeTruthy();
  await expectNoAxeViolations(page);

  const remove = page.getByRole('button', { name: 'Remove address' });
  await remove.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByText('Address removed.')).toBeVisible();
  await expect(page.getByText('This member has no email address.')).toBeVisible();
  const del = api.requests.find((r) => r.method === 'DELETE' && r.path === `/members/${MEMBER}/email`);
  expect(del?.headers['if-match']).toBe('"1"');
  await expectNoAxeViolations(page);
});
