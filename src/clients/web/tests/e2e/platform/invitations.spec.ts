import { request as playwrightRequest } from '@playwright/test';
import { expect, test as base } from '../support/fixtures';
import { extractInvitationLink, waitForMessageText } from '../support/mailpit';
import { archiveActiveSeasons } from '../support/platformState';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const INVITEE_PASSWORD = 'E2e-Invitee-Password-0006!';
const TWO_MINUTES = 2 * 60 * 1000;

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD, INVITEE_PASSWORD]);
  },
});

const run = Date.now().toString(36);
const accountName = `e2e-invitee-${run}`;
const address = `${accountName}@example.org`;
const teamName = `E2E invitations team ${run}`;

// Creates the team the invited member gets a role in through the API, so the test only exercises the invitation flow.
test.beforeAll(async ({ baseURL }) => {
  await archiveActiveSeasons(baseURL);
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

    const season = await api.post('/api/v1/seasons', { data: { name: `E2E invitations ${run}` }, headers });
    expect(season.ok(), `Creating the season answered ${season.status()}`).toBe(true);
    const { id } = (await season.json()) as { id: string };
    const team = await api.post(`/api/v1/seasons/${id}/teams`, { data: { name: teamName }, headers });
    expect(team.ok(), `Creating the team answered ${team.status()}`).toBe(true);
    await api.delete('/api/v1/session', { headers });
  } finally {
    await api.dispose();
  }
});

// The club's single active season is free again for the next spec, even when the test fails.
test.afterAll(async ({ baseURL }) => {
  await archiveActiveSeasons(baseURL);
});

test('invites an address with a team role, accepts the link from Mailpit, and the new member sees the team (SC-001)', async ({
  page,
  browser,
  baseURL,
  secrets,
}) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await nav.getByRole('link', { name: 'Members' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Members' })).toBeVisible();

  // SC-001 measures the steps from the invitation to the new member's first sign-in.
  const started = Date.now();
  await page.getByRole('button', { name: 'Invite member' }).click();
  const dialog = page.getByRole('dialog', { name: 'Invite a member' });
  await dialog.getByLabel(/^Email address/).fill(address);
  await dialog.getByLabel(/^Sign-in name/).fill(accountName);
  await dialog.getByRole('button', { name: 'Add team role' }).click();
  await dialog.getByLabel('Team', { exact: true }).selectOption({ label: teamName });
  await dialog.getByLabel('Role', { exact: true }).selectOption({ label: 'Viewer' });
  await dialog.getByRole('button', { name: 'Send invitation' }).click();
  await expect(page.getByText(`The invitation was sent to ${address}.`)).toBeVisible();

  const link = extractInvitationLink(await waitForMessageText(address));
  const token = new URLSearchParams(link.hash.slice(1)).get('token');
  expect(token, 'the link carries a token').toBeTruthy();
  secrets.push(token!);

  // The invitee opens the link in their own browser context, as another person on another device would.
  const inviteeContext = await browser.newContext({ baseURL });
  try {
    const invitee = await inviteeContext.newPage();
    await invitee.goto(`${link.pathname}${link.hash}`);
    await expect(invitee.getByRole('heading', { level: 1, name: 'Accept your invitation' })).toBeVisible();
    await expect(invitee.getByLabel(/^Sign-in name/)).toHaveValue(accountName);
    expect(invitee.url()).not.toContain(token!);

    await invitee.getByLabel(/^Password/).fill(INVITEE_PASSWORD);
    await invitee.getByLabel(/^Confirm password/).fill(INVITEE_PASSWORD);
    await invitee.getByRole('button', { name: 'Create account' }).click();
    await expect(invitee.getByRole('status')).toContainText('Your account is ready');

    await invitee.getByRole('link', { name: 'Go to sign in' }).click();
    await expect(invitee).toHaveURL(/\/sign-in$/);
    await invitee.getByLabel('Sign-in name').fill(accountName);
    await invitee.getByLabel('Password').fill(INVITEE_PASSWORD);
    await invitee.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(invitee.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
    expect(Date.now() - started, `from the invitation to the first sign-in at ${baseURL}`).toBeLessThan(TWO_MINUTES);

    const inviteeNav = invitee.getByRole('navigation', { name: 'Main navigation' });
    await expect(inviteeNav.getByRole('link')).toHaveText(['Home', 'Teams', 'Account']);
    await inviteeNav.getByRole('link', { name: 'Teams' }).click();
    await expect(invitee.getByText(teamName)).toBeVisible();
  } finally {
    await inviteeContext.close();
  }
});
