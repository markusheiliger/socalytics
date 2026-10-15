import { request as playwrightRequest } from '@playwright/test';
import { expect, test as base } from '../support/fixtures';
import { ScriptedManager } from '../support/scriptedManager';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const REGISTRAR_PASSWORD = 'E2e-Registrar-Password-0005!';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD, REGISTRAR_PASSWORD]);
  },
});

const run = Date.now().toString(36);
const registrarName = `e2e-registrar-${run}`;
const hostLabel = `scripted-manager-${run}`;
// The first test pairs this Manager and the second one approves and activates it, so the tests run in file order.
let manager: ScriptedManager;

// The Club Admin gives a new member the Registrar role through the API, so the test only exercises the pairing flow.
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

    const created = await api.post('/api/v1/members', { data: { accountName: registrarName }, headers });
    expect(created.ok(), `Creating the member answered ${created.status()}`).toBe(true);
    const body = (await created.json()) as { member: { id: string }; setPasswordCredential: { credential: string } };

    const redeemContext = await playwrightRequest.newContext({ baseURL });
    const redeemed = await redeemContext.post('/api/v1/credentials/redeem', {
      data: { accountName: registrarName, credential: body.setPasswordCredential.credential, newPassword: REGISTRAR_PASSWORD },
    });
    await redeemContext.dispose();
    expect(redeemed.ok(), 'the member sets the password with the credential code').toBe(true);

    const role = await api.put(`/api/v1/members/${body.member.id}/club-roles/registrar`, { headers });
    expect(role.ok(), `Assigning the Registrar role answered ${role.status()}`).toBe(true);
    await api.delete('/api/v1/session', { headers });
  } finally {
    await api.dispose();
  }
});

test('a Registrar pairs a scripted Manager through the verification address and sees the pending request (SC-001, SC-003)', async ({
  page,
  baseURL,
}) => {
  manager = new ScriptedManager({ origin: baseURL!, hostLabel });
  const started = await manager.startPairing();
  const verificationUri = new URL(started.verificationUri);
  // The AppHost names the Web UI endpoint `localhost`; the browser tests address the same server as 127.0.0.1.
  expect(verificationUri.port, 'the verification address is on the Web UI endpoint').toBe(new URL(baseURL!).port);
  expect(verificationUri.pathname).toBe('/analyst-managers/pair');

  await page.goto(verificationUri.toString());
  await expect(page).toHaveURL(/\/sign-in\?/);
  await page.getByLabel('Sign-in name').fill(registrarName);
  await page.getByLabel('Password').fill(REGISTRAR_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page).toHaveURL(/\/analyst-managers\/pair$/);
  await expect(page.getByRole('heading', { level: 2, name: 'Pair a Manager' })).toBeVisible();
  await page.getByLabel(/^Pairing code/).fill(started.pairingCode);
  await page.getByLabel(/^Device fingerprint/).fill(started.deviceFingerprint);
  await page.getByRole('button', { name: 'Submit' }).click();

  await expect(page.getByRole('heading', { name: 'Request submitted' })).toBeVisible();
  await expect(page.getByText('State: Pending approval')).toBeVisible();
  await expect(page.getByText(`Host label: ${hostLabel}`)).toBeVisible();
  await expect(page.getByText(`Device fingerprint: ${started.deviceFingerprint}`)).toBeVisible();
  expect(page.url()).not.toContain(started.pairingCode);
});

test('a Club Admin approves the pending request with the fingerprint tail and the scripted Manager activates (SC-002, SC-003)', async ({
  page,
}) => {
  const { deviceFingerprint } = manager.pairingStart;
  const tail = deviceFingerprint.replace(/[^A-Za-z0-9]/g, '').toUpperCase().slice(-6);

  await page.goto('/');
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  const navEntry = page.getByRole('navigation').getByRole('link', { name: /^Analyst Managers \(\d+ pending\)$/ });
  await expect(navEntry).toBeVisible();
  await navEntry.click();
  await expect(page.getByRole('heading', { level: 2, name: 'Analyst Managers' })).toBeVisible();

  await page.getByRole('link', { name: `${hostLabel} – Pending approval` }).click();
  await expect(page.getByText('State: Pending approval')).toBeVisible();
  await expect(page.getByText(`Device fingerprint: ${deviceFingerprint}`)).toBeVisible();

  await page.getByRole('button', { name: 'Approve…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Approve this Manager' });
  const approve = dialog.getByRole('button', { name: 'Approve', exact: true });
  await expect(approve).toBeDisabled();
  await dialog.getByLabel('Last 6 characters of the fingerprint').fill(tail);
  await expect(approve).toBeEnabled();
  await approve.click();
  await expect(page.getByText('Approved. The request now waits for the Manager to activate.')).toBeVisible();
  await expect(page.getByText('State: Approved')).toBeVisible();

  const approved = await manager.waitForApproval();
  const activated = await manager.activate(approved.challenge!);
  expect(activated.managerId).toBeTruthy();

  await page.reload();
  await expect(page.getByText('State: Active')).toBeVisible();
  await expect(page.getByText(/^Activated: /)).toBeVisible();
});

test('a Club Admin sees the reporting Manager online in the fleet, revokes it, and finds it under the ended filter (SC-003, SC-006)', async ({
  page,
}) => {
  const report = await manager.reportStatus({
    operatingState: 'running',
    activeAnalysts: 1,
    maxAnalysts: 2,
    managerVersion: '1.2.3-e2e',
  });
  expect(report.reportAccepted, 'the platform accepts the status report').toBe(true);

  await page.goto('/');
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  await page.getByRole('navigation').getByRole('link', { name: /^Analyst Managers/ }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Analyst Managers' })).toBeVisible();

  const entryLink = page.getByRole('link', { name: `${hostLabel} – Active` });
  const entry = page.getByRole('listitem').filter({ has: entryLink });
  await expect(entry.getByText(/^Connectivity: Online \(last report: /)).toBeVisible();
  await expect(entry.getByText('Operating state: Running')).toBeVisible();
  await expect(entry.getByText('Analysts: 1 of 2')).toBeVisible();
  await expect(entry.getByText('Version: 1.2.3-e2e')).toBeVisible();

  await entryLink.click();
  await page.getByRole('button', { name: 'Revoke…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Revoke this Manager' });
  const revoke = dialog.getByRole('button', { name: 'Revoke', exact: true });
  await expect(revoke).toBeDisabled();
  await dialog.getByLabel(/^Reason/).fill('End-to-end revocation');
  await dialog.getByLabel('Type the host label to confirm').fill(hostLabel);
  await expect(revoke).toBeEnabled();
  await revoke.click();
  await expect(page.getByText('The registration was revoked.')).toBeVisible();
  await expect(page.getByText('State: Revoked')).toBeVisible();

  await page.getByRole('link', { name: 'Back to Analyst Managers' }).click();
  await expect(page.getByRole('link', { name: `${hostLabel} – Revoked` })).toHaveCount(0);
  await page.getByLabel('Show').click();
  await page.getByRole('option', { name: 'Ended' }).click();
  await expect(page.getByRole('link', { name: `${hostLabel} – Revoked` })).toBeVisible();
});
