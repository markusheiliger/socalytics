import { request as playwrightRequest, type APIRequestContext } from '@playwright/test';
import { startPairing } from '../support/analystManager';
import { expect, test as base } from '../support/fixtures';
import { extractConfirmationLink, waitForMessageText } from '../support/mailpit';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const REGISTRAR_PASSWORD = 'E2e-Registrar-Password-0005!';
const TWO_MINUTES = 2 * 60 * 1000;

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD, REGISTRAR_PASSWORD]);
  },
});

const address = `club-admin-${Date.now().toString(36)}@example.org`;

test('sets an address, reads the link from Mailpit, confirms it, and sees it verified within 2 minutes (SC-001)', async ({
  page,
  baseURL,
  secrets,
}) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  await page.goto('/account');
  await expect(page.getByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
  await expect(page.getByText(/no email address yet|Your address is/)).toBeVisible();

  // SC-001 measures the steps from setting the address to seeing it verified.
  const started = Date.now();
  await page.getByRole('textbox', { name: /email address/i }).fill(address);
  await page.getByRole('button', { name: /^(Set|Change) address$/ }).click();
  await expect(page.getByText(/we sent a confirmation link/i)).toBeVisible();
  await expect(page.getByText(/not confirmed yet/i)).toBeVisible();

  const link = extractConfirmationLink(await waitForMessageText(address));
  const token = new URLSearchParams(link.hash.slice(1)).get('token');
  expect(token, 'the link carries a token').toBeTruthy();
  secrets.push(token!);

  // The link points at the Web UI's own origin; the browser opens it on the origin of the signed-in session.
  await page.goto(`${link.pathname}${link.hash}`);
  expect(page.url()).not.toContain(token!);
  await expect(page.getByRole('heading', { level: 1, name: 'Confirm your email address' })).toBeVisible();
  await page.getByRole('button', { name: 'Confirm this address' }).click();
  await expect(page.getByText('Your address is confirmed.')).toBeVisible();

  await page.getByRole('link', { name: 'Go to your account' }).click();
  await expect(page.getByText(`Your address is ${address}. Confirmed.`)).toBeVisible();

  expect(Date.now() - started, `from setting the address to seeing it verified at ${baseURL}`).toBeLessThan(TWO_MINUTES);
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

test('notifies the Club Admin with only the event and the fleet link when a Registrar submits a pairing code (SC-006)', async ({
  baseURL,
  secrets,
}) => {
  const registrarName = `e2e-registrar-${Date.now().toString(36)}`;
  const api = await playwrightRequest.newContext({ baseURL });
  try {
    // Test setup: the Club Admin creates a Registrar who sets their own password.
    const admin = await signIn(api, ADMIN_ACCOUNT, ADMIN_CHANGED_PASSWORD);
    const created = await api.post('/api/v1/members', { data: { accountName: registrarName }, headers: admin.headers });
    expect(created.ok(), `Creating the Registrar answered ${created.status()}`).toBe(true);
    const { member, setPasswordCredential } = (await created.json()) as {
      member: { id: string };
      setPasswordCredential: { credential: string };
    };
    secrets.push(setPasswordCredential.credential);
    const redeemed = await api.post('/api/v1/credentials/redeem', {
      data: { accountName: registrarName, credential: setPasswordCredential.credential, newPassword: REGISTRAR_PASSWORD },
    });
    expect(redeemed.ok(), `Setting the Registrar's password answered ${redeemed.status()}`).toBe(true);
    const assigned = await api.put(`/api/v1/members/${member.id}/club-roles/registrar`, { headers: admin.headers });
    expect(assigned.ok(), `Assigning Registrar answered ${assigned.status()}`).toBe(true);
    await api.delete('/api/v1/session', { headers: admin.headers });

    const manager = await startPairing(baseURL);
    secrets.push(manager.pairingCode);

    const registrar = await signIn(api, registrarName, REGISTRAR_PASSWORD);
    const submitted = await api.post('/api/v1/analyst-manager-registrations', {
      data: { pairingCode: manager.pairingCode, deviceFingerprint: manager.deviceFingerprint },
      headers: registrar.headers,
    });
    expect(submitted.status(), `Submitting the pairing code answered ${submitted.status()}`).toBe(201);
    await api.delete('/api/v1/session', { headers: registrar.headers });

    const text = await waitForMessageText(address, 90_000, 'An Analyst Manager registration request waits for approval');
    expect(text).toContain('registration requests wait');
    expect(text).toMatch(/https?:\/\/\S+\/analyst-managers/);
    for (const hidden of [manager.pairingCode, manager.deviceFingerprint, manager.hostLabel, registrarName]) {
      expect(text, 'the message carries no registration details').not.toContain(hidden);
    }
  } finally {
    await api.dispose();
  }
});
