import { request as playwrightRequest } from '@playwright/test';
import { expect, test as base } from '../support/fixtures';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const MEMBER_PASSWORD = 'E2e-Member-Password-0004!';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD, MEMBER_PASSWORD]);
  },
});

const run = Date.now().toString(36);
const memberName = `e2e-member-${run}`;
const teamName = `E2E members team ${run}`;

// Creates the team the Coach role is assigned to through the API, so the test only exercises the member flow in the UI.
test.beforeAll(async ({ baseURL }) => {
  const api = await playwrightRequest.newContext({ baseURL });
  try {
    const signIn = await api.post('/api/v1/session', {
      data: { accountName: ADMIN_ACCOUNT, password: ADMIN_CHANGED_PASSWORD },
    });
    expect(signIn.ok(), `Club Admin sign-in answered ${signIn.status()}`).toBe(true);
    const { antiforgeryToken } = (await signIn.json()) as { antiforgeryToken: string };
    // The session cookie is Secure, which the request context's cookie jar does not keep over http.
    const cookie = signIn
      .headersArray()
      .filter((h) => h.name.toLowerCase() === 'set-cookie')
      .map((h) => h.value.split(';')[0])
      .find((c) => c.startsWith('__Host-socalytics-session='));
    expect(cookie, 'the sign-in sets a session cookie').toBeTruthy();
    const headers = { Cookie: cookie!, 'X-CSRF-Token': antiforgeryToken };

    const season = await api.post('/api/v1/seasons', { data: { name: `E2E members ${run}` }, headers });
    expect(season.ok(), `Creating the season answered ${season.status()}`).toBe(true);
    const { id } = (await season.json()) as { id: string };
    const team = await api.post(`/api/v1/seasons/${id}/teams`, { data: { name: teamName }, headers });
    expect(team.ok(), `Creating the team answered ${team.status()}`).toBe(true);
    await api.delete('/api/v1/session', { headers });
  } finally {
    await api.dispose();
  }
});

test('creates a member, sets their password, assigns Coach, and ends their access by deactivation (SC-001, SC-002, SC-004)', async ({
  page,
  browser,
  baseURL,
}) => {
  const nav = page.getByRole('navigation', { name: 'Main navigation' });

  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  await nav.getByRole('link', { name: 'Members' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Members' })).toBeVisible();
  await page.getByLabel(/^Sign-in name/).fill(memberName);
  await page.getByRole('button', { name: 'Create member' }).click();
  await page.getByRole('button', { name: 'I have handed over the code' }).click();

  await page.getByRole('link', { name: `${memberName} (Active)` }).click();
  await expect(page.getByRole('heading', { level: 2, name: memberName })).toBeVisible();
  await expect(page.getByText('Password: not set yet')).toBeVisible();

  await page.getByRole('button', { name: 'Issue set-password code' }).click();
  const code = (await page.getByLabel('Code', { exact: true }).textContent())?.trim();
  expect(code, 'the issued code is shown once').toBeTruthy();
  expect(page.url()).not.toContain(code!);
  await page.getByRole('button', { name: 'I have handed over the code' }).click();
  await expect(page.getByText(code!)).toHaveCount(0);

  // The member sets the password in a second browser context, as another person on another device would.
  const memberContext = await browser.newContext({ baseURL });
  try {
    const memberPage = await memberContext.newPage();
    await memberPage.goto('/set-password');
    await memberPage.getByLabel(/^Sign-in name/).fill(memberName);
    await memberPage.getByLabel(/^Code/).fill(code!);
    await memberPage.getByLabel(/^New password/).fill(MEMBER_PASSWORD);
    await memberPage.getByLabel(/^Repeat the new password/).fill(MEMBER_PASSWORD);
    await memberPage.getByRole('button', { name: 'Set password' }).click();
    await expect(memberPage.getByText('Your password is set. You can sign in now.')).toBeVisible();

    await page.reload();
    await expect(page.getByText('Password: set')).toBeVisible();

    await page.getByLabel('Team', { exact: true }).selectOption({ label: teamName });
    await page.getByLabel('Role', { exact: true }).selectOption({ label: 'Coach' });
    await page.getByRole('button', { name: 'Assign team role' }).click();
    await expect(page.getByText(`${teamName}: Coach`)).toBeVisible();

    await memberPage.getByRole('link', { name: 'Go to sign-in' }).click();
    await expect(memberPage).toHaveURL(/\/sign-in$/);
    await memberPage.getByLabel('Sign-in name').fill(memberName);
    await memberPage.getByLabel('Password').fill(MEMBER_PASSWORD);
    await memberPage.getByRole('button', { name: 'Sign in' }).click();
    const memberNav = memberPage.getByRole('navigation', { name: 'Main navigation' });
    await expect(memberPage.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
    await expect(memberNav.getByRole('link')).toHaveText(['Home', 'Teams', 'Account']);

    await page.getByRole('button', { name: 'Deactivate' }).click();
    await expect(page.getByText('Status: Deactivated')).toBeVisible();

    await memberNav.getByRole('link', { name: 'Teams' }).click();
    await expect(memberPage).toHaveURL(/\/sign-in$/);
    await expect(memberPage.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
  } finally {
    await memberContext.close();
  }
});
