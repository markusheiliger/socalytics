import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { expectAllReachableByKeyboard } from '../support/keyboard';
import { problem } from '../support/simulatedApi';

test.beforeEach(({ api }) => {
  api.on('GET', '/me/email', (route) =>
    route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify(problem(404, 'Not found')) }),
  );
});

test('a Club Admin switches the pending-request message off with the keyboard (scenarios 1, 2)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  let enabled = true;
  api.on('GET', '/me/notification-preferences', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        items: [
          { type: 'email-confirmation', enabled: true, accountMessage: true },
          { type: 'registration-pending', enabled, accountMessage: false },
        ],
      }),
    }),
  );
  api.on('PUT', '/me/notification-preferences/registration-pending', (route, body) => {
    enabled = (body as { enabled: boolean }).enabled;
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ type: 'registration-pending', enabled, accountMessage: false }),
    });
  });

  await page.goto('/account');
  const toggle = page.getByRole('switch', { name: /waiting for approval/i });
  await expect(toggle).toBeChecked();
  await expect(page.getByText(/email address confirmation: always sent/i)).toBeVisible();
  await expect(page.getByRole('switch')).toHaveCount(1);
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await toggle.focus();
  await page.keyboard.press('Space');
  await expect(toggle).not.toBeChecked();
  expect(api.requests.find((r) => r.method === 'PUT' && r.path === '/me/notification-preferences/registration-pending')?.headers['x-csrf-token']).toBeTruthy();
  await expectNoAxeViolations(page);
});

test('a member without a configurable type sees account messages only (scenario 3)', async ({ page, api }) => {
  api.signInAs('Coach');
  await page.goto('/account');
  await expect(page.getByText(/always sent/i)).toBeVisible();
  await expect(page.getByRole('switch')).toHaveCount(0);
  await expectNoAxeViolations(page);
});
