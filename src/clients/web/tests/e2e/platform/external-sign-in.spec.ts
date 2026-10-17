import type { BrowserContext } from '@playwright/test';
import { expect, test as base } from '../support/fixtures';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const PROVIDER_NAME = 'Test provider';
// The AppHost in e2e mode runs the test provider on this fixed port (OidcProviderE2ePort in its Program.cs).
const PROVIDER_ORIGIN = 'http://127.0.0.1:4190';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_CHANGED_PASSWORD]);
  },
});

test.describe.configure({ mode: 'serial' });

const memberName = `e2e-external-${Date.now().toString(36)}`;

// The platform registers its own HTTPS origin as the redirect URI, which does not carry the browser binding cookie of the
// Web UI origin; pointing the provider at the Web UI origin makes the callback reach the API through the proxy.
async function routeCallbackThroughWebOrigin(context: BrowserContext, webOrigin: string) {
  await context.route(`${PROVIDER_ORIGIN}/test/authorize*`, (route) => {
    const target = new URL(route.request().url());
    target.searchParams.set('redirect_uri', `${webOrigin}/api/v1/session/external/callback`);
    return route.continue({ url: target.toString() });
  });
}

test('links a member through the provider, signs in with it, and is refused after the Club Admin unlinks (SC-001, SC-004)', async ({
  page,
  browser,
  baseURL,
}) => {
  const webOrigin = new URL(baseURL!).origin;
  await routeCallbackThroughWebOrigin(page.context(), webOrigin);

  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await nav.getByRole('link', { name: 'Members' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Members' })).toBeVisible();
  await page.getByLabel(/^Sign-in name/).fill(memberName);
  await page.getByLabel('First credential').selectOption({ label: `Link code for ${PROVIDER_NAME}` });
  await page.getByRole('button', { name: 'Create member' }).click();
  const code = (await page.getByLabel('Code', { exact: true }).textContent())?.trim();
  expect(code, 'the link code is shown once').toBeTruthy();
  expect(page.url()).not.toContain(code!);
  await page.getByRole('button', { name: 'I have handed over the code' }).click();

  const memberContext = await browser.newContext({ baseURL });
  try {
    await routeCallbackThroughWebOrigin(memberContext, webOrigin);
    const memberPage = await memberContext.newPage();

    await memberPage.goto('/link-account');
    await memberPage.getByLabel('Sign-in name').fill(memberName);
    await memberPage.getByLabel('Link code').fill(code!);
    await memberPage.getByRole('button', { name: 'Continue to provider' }).click();
    await expect(memberPage.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
    expect(memberPage.url()).not.toContain(code!);

    await memberPage.goto('/account');
    await memberPage.getByRole('button', { name: 'Sign out' }).click();
    await expect(memberPage).toHaveURL(/\/sign-in$/);

    await memberPage.getByRole('button', { name: `Sign in with ${PROVIDER_NAME}` }).click();
    await expect(memberPage.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
    expect(memberPage.url()).toMatch(/^http:\/\/127\.0\.0\.1:4174\/$/);
    expect(memberPage.url()).not.toContain(PROVIDER_ORIGIN);

    await page.getByRole('link', { name: `${memberName} (Active)` }).click();
    await expect(page.getByRole('heading', { level: 2, name: memberName })).toBeVisible();
    await expect(page.getByText(/Test provider, linked/)).toBeVisible();
    await page.getByRole('button', { name: `Unlink ${PROVIDER_NAME}` }).click();
    await page.getByRole('button', { name: 'Unlink', exact: true }).click();
    await expect(page.getByText('The provider account was unlinked')).toBeVisible();

    // The unlink ended the member's sessions.
    await memberPage.reload();
    await expect(memberPage).toHaveURL(/\/sign-in$/);

    await memberPage.getByRole('button', { name: `Sign in with ${PROVIDER_NAME}` }).click();
    await expect(memberPage).toHaveURL(/\/sign-in\?external=failed$/);
    await expect(memberPage.getByText(/Sign-in was not possible/)).toBeVisible();
    await expect(memberPage.getByRole('heading', { level: 2, name: 'Home' })).toHaveCount(0);
  } finally {
    await memberContext.close();
  }
});
