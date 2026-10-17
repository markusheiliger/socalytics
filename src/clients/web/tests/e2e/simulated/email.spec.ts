import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard, expectAllReachableByKeyboard } from '../support/keyboard';
import { problem } from '../support/simulatedApi';

const TOKEN = 'TOKEN-abc123-xyz';

test.beforeEach(({ secrets }) => {
  secrets.push(TOKEN);
});

test('sets an address in the account view and shows it unconfirmed (scenario 1)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  let stored: { address: string; state: string; verifiedAt: null; version: number } | null = null;
  api.on('GET', '/me/email', async (route) => {
    if (!stored) return route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify(problem(404, 'Not found')) });
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(stored) });
  });
  api.on('PUT', '/me/email', async (route, body) => {
    stored = { address: (body as { address: string }).address, state: 'unverified', verifiedAt: null, version: 1 };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(stored) });
  });
  api.on('DELETE', '/me/email', async (route) => {
    stored = null;
    await route.fulfill({ status: 204 });
  });
  api.on('POST', '/me/email/confirmation-requests', (route) => route.fulfill({ status: 202 }));

  await page.goto('/account');
  await expect(page.getByText('You have no email address yet.')).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await page.getByRole('textbox', { name: /email address/i }).fill('ada@example.org');
  await activateWithKeyboard(page.getByRole('button', { name: 'Set address' }));
  await expect(page.getByText(/we sent a confirmation link/i)).toBeVisible();
  await expect(page.getByText(/ada@example.org/)).toBeVisible();
  await expect(page.getByText(/not confirmed yet/i)).toBeVisible();
  await expectNoAxeViolations(page);

  await page.getByRole('button', { name: 'Send a new link' }).click();
  await expect(page.getByText('A new confirmation link was sent.')).toBeVisible();
  expect(api.requests.find((r) => r.method === 'PUT' && r.path === '/me/email')?.headers['x-csrf-token']).toBeTruthy();

  await page.getByRole('button', { name: 'Remove address' }).click();
  await expect(page.getByText('Address removed.')).toBeVisible();
  await expect(page.getByText('You have no email address yet.')).toBeVisible();
});

test('shows the wait time when resending is limited', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.on('GET', '/me/email', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ address: 'ada@example.org', state: 'unverified', verifiedAt: null, version: 1 }) }),
  );
  api.on('POST', '/me/email/confirmation-requests', (route) =>
    route.fulfill({
      status: 429,
      contentType: 'application/problem+json',
      headers: { 'Retry-After': '120' },
      body: JSON.stringify(problem(429, 'Too many', { code: 'email-confirmation-rate-limited' })),
    }),
  );
  await page.goto('/account');
  await page.getByRole('button', { name: 'Send a new link' }).click();
  await expect(page.getByRole('alert')).toContainText('120 seconds');
  await expectNoAxeViolations(page);
});

test('confirms on the click only and keeps the token out of address, history, and storage (scenarios 2, 3)', async ({ page, api }) => {
  api.endSession();
  let confirmed: unknown = null;
  api.on('POST', '/email-confirmations', (route, body) => {
    confirmed = body;
    return route.fulfill({ status: 204 });
  });

  await page.goto(`/email/confirm#token=${TOKEN}`);
  const button = page.getByRole('button', { name: 'Confirm this address' });
  await expect(button).toBeVisible();
  expect(page.url()).not.toContain(TOKEN);
  expect(await page.evaluate(() => window.location.hash)).toBe('');
  expect(await page.evaluate(() => JSON.stringify(window.history.state))).not.toContain('TOKEN');
  expect(api.requests.some((r) => r.path === '/email-confirmations')).toBe(false);
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await activateWithKeyboard(button);
  await expect(page.getByText('Your address is confirmed.')).toBeVisible();
  expect(confirmed).toEqual({ token: TOKEN });
  expect(page.url()).not.toContain(TOKEN);
  await expectNoAxeViolations(page);
});

test('shows one generic message for an invalid or expired link (scenario 5)', async ({ page, api }) => {
  api.signInAs('Coach');
  api.on('POST', '/email-confirmations', (route) =>
    route.fulfill({ status: 400, contentType: 'application/problem+json', body: JSON.stringify(problem(400, 'Bad', { code: 'email-confirmation-invalid' })) }),
  );
  await page.goto(`/email/confirm#token=${TOKEN}`);
  await page.getByRole('button', { name: 'Confirm this address' }).click();
  await expect(page.getByRole('alert')).toContainText(/invalid, expired, or already used/i);
  await expect(page.getByRole('alert')).toContainText('Send a new link from your account.');
  await expectNoAxeViolations(page);
});

test('shows the wait time when confirmation attempts are limited (scenario 7)', async ({ page, api }) => {
  api.endSession();
  api.on('POST', '/email-confirmations', (route) =>
    route.fulfill({ status: 429, contentType: 'application/problem+json', headers: { 'Retry-After': '45' }, body: JSON.stringify(problem(429, 'Too many')) }),
  );
  await page.goto(`/email/confirm#token=${TOKEN}`);
  await page.getByRole('button', { name: 'Confirm this address' }).click();
  await expect(page.getByRole('alert')).toContainText('45 seconds');
  await expectNoAxeViolations(page);
});
