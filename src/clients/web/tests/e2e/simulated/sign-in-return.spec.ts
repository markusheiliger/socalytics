import { expect, test } from '../support/fixtures';
import { SIMULATED_PASSWORD, sessionFor, type SimulatedApi } from '../support/simulatedApi';

function allowSignIn(api: SimulatedApi) {
  api.endSession();
  api.on('POST', '/session', async (route) => {
    api.signInAs('ClubAdmin');
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(sessionFor('ClubAdmin')) });
  });
}

async function signIn(page: import('@playwright/test').Page) {
  await page.getByLabel('Sign-in name').fill('admin');
  await page.getByLabel('Password').fill(SIMULATED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

test('returns to the guarded route after sign-in', async ({ page, api }) => {
  allowSignIn(api);
  await page.goto('/members');
  await expect(page).toHaveURL(/\/sign-in\?returnTo=%2Fmembers$/);
  await signIn(page);
  await expect(page).toHaveURL(/\/members$/);
  await expect(page.getByRole('heading', { level: 2, name: 'Members' })).toBeVisible();
});

for (const value of ['//evil.example/members', 'https://evil.example/', '/no/such/route']) {
  test(`goes home instead of ${value}`, async ({ page, api }) => {
    allowSignIn(api);
    await page.goto(`/sign-in?returnTo=${encodeURIComponent(value)}`);
    await signIn(page);
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  });
}
