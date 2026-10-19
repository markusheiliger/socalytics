import { request as playwrightRequest, type APIRequestContext } from '@playwright/test';
import { expect, test as base } from '../support/fixtures';
import { extractConfirmationLink, waitForMessageText } from '../support/mailpit';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const FORGOTTEN_PASSWORD = 'E2e-Forgotten-Password-0007!';
const NEW_PASSWORD = 'E2e-Recovered-Password-0008!';
const TWO_MINUTES = 2 * 60 * 1000;

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_CHANGED_PASSWORD, FORGOTTEN_PASSWORD, NEW_PASSWORD]);
  },
});

type Session = { headers: { Cookie: string; 'X-CSRF-Token': string } };

async function signIn(api: APIRequestContext, accountName: string, password: string): Promise<Session> {
  const response = await api.post('/api/v1/session', { data: { accountName, password } });
  expect(response.ok(), `Sign-in of ${accountName} answered ${response.status()}`).toBe(true);
  const { antiforgeryToken } = (await response.json()) as { antiforgeryToken: string };
  // The session cookie is Secure, which the request context's cookie jar does not keep over http.
  const cookie = response
    .headersArray()
    .filter((h) => h.name.toLowerCase() === 'set-cookie')
    .map((h) => h.value.split(';')[0])
    .find((c) => c.startsWith('__Host-socalytics-session='));
  expect(cookie, 'the sign-in sets a session cookie').toBeTruthy();
  return { headers: { Cookie: cookie!, 'X-CSRF-Token': antiforgeryToken } };
}

/** Test setup: creates a member who sets their own password and verifies an address through Mailpit. */
async function createMemberWithVerifiedAddress(
  baseURL: string | undefined,
  accountName: string,
  address: string,
  secrets: string[],
) {
  const api = await playwrightRequest.newContext({ baseURL });
  try {
    const admin = await signIn(api, ADMIN_ACCOUNT, ADMIN_CHANGED_PASSWORD);
    const created = await api.post('/api/v1/members', { data: { accountName }, headers: admin.headers });
    expect(created.ok(), `Creating the member answered ${created.status()}`).toBe(true);
    const { setPasswordCredential } = (await created.json()) as { setPasswordCredential: { credential: string } };
    secrets.push(setPasswordCredential.credential);
    await api.delete('/api/v1/session', { headers: admin.headers });

    const redeemed = await api.post('/api/v1/credentials/redeem', {
      data: { accountName, credential: setPasswordCredential.credential, newPassword: FORGOTTEN_PASSWORD },
    });
    expect(redeemed.ok(), `Setting the member's password answered ${redeemed.status()}`).toBe(true);

    const member = await signIn(api, accountName, FORGOTTEN_PASSWORD);
    const set = await api.put('/api/v1/me/email', { data: { address }, headers: member.headers });
    expect(set.ok(), `Setting the address answered ${set.status()}`).toBe(true);

    const link = extractConfirmationLink(await waitForMessageText(address, 90_000, 'Confirm your email address'));
    const token = new URLSearchParams(link.hash.slice(1)).get('token');
    expect(token, 'the confirmation link carries a token').toBeTruthy();
    secrets.push(token!);
    const confirmed = await api.post('/api/v1/email-confirmations', { data: { token } });
    expect(confirmed.ok(), `Confirming the address answered ${confirmed.status()}`).toBe(true);
    await api.delete('/api/v1/session', { headers: member.headers });
  } finally {
    await api.dispose();
  }
}

test('recovers a forgotten password through the link in Mailpit and signs in with the new one within 2 minutes (SC-001)', async ({
  page,
  baseURL,
  secrets,
}) => {
  test.setTimeout(5 * 60 * 1000);
  const run = Date.now().toString(36);
  const accountName = `e2e-recovery-${run}`;
  const address = `recovery-${run}@example.org`;
  await createMemberWithVerifiedAddress(baseURL, accountName, address, secrets);

  // SC-001 measures the steps from requesting the recovery to signing in with the new password.
  const started = Date.now();
  await page.goto('/sign-in');
  await page.getByRole('link', { name: 'Forgot your password?' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Reset your password' })).toBeVisible();
  await page.getByLabel('Email address').fill(address);
  await page.getByRole('button', { name: 'Send reset link' }).click();
  await expect(page.getByRole('status')).toContainText('a message with a link is on its way');

  const text = await waitForMessageText(address, 90_000, 'Reset your SocAlytics password');
  const match = /https?:\/\/[^\s<>"]+\/password-recovery\/complete#token=[^\s<>"]+/.exec(text);
  expect(match, 'the message contains a recovery link').toBeTruthy();
  const link = new URL(match![0]);
  const token = new URLSearchParams(link.hash.slice(1)).get('token');
  expect(token, 'the link carries a token').toBeTruthy();
  secrets.push(token!);

  // The link points at the Web UI's own origin; the browser opens it on the origin the platform serves.
  await page.goto(`${link.pathname}${link.hash}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Choose a new password' })).toBeVisible();
  await expect(page.getByText(`Choose a new password for the account ${accountName}.`)).toBeVisible();
  expect(page.url()).not.toContain(token!);

  await page.getByLabel(/^New password/).fill(NEW_PASSWORD);
  await page.getByLabel(/^Confirm new password/).fill(NEW_PASSWORD);
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.getByRole('status')).toContainText('Your password has been changed');

  await page.getByRole('link', { name: 'Go to sign in' }).click();
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(accountName);
  await page.getByLabel('Password').fill(NEW_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  expect(Date.now() - started, `from requesting the recovery to signing in at ${baseURL}`).toBeLessThan(TWO_MINUTES);
});
