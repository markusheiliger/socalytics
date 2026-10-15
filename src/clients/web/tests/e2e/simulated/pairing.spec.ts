import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { SIMULATED_PASSWORD, sessionFor, type SimulatedApi } from '../support/simulatedApi';

const CODE = 'PAIR-4711-CODE';
const FINGERPRINT = 'ABCD-EF12-3456-7890';
const REGISTRATION = '00000000-0000-4000-8000-000000000041';
const REFUSAL = 'The pairing code or device fingerprint was not accepted. Check both and try again.';

function addPairing(api: SimulatedApi, over: Record<string, unknown> = {}) {
  api.addRegistration(
    {
      registrationId: REGISTRATION,
      version: 1,
      state: 'pairing',
      deviceFingerprint: FINGERPRINT,
      deviceMetadata: { os: 'linux', claimedKeyProtection: 'software', hostLabel: 'lab-pc' },
      approvalExpiresAt: new Date(Date.now() + 3_600_000),
      createdAt: new Date(),
      ...over,
    } as never,
    CODE,
  );
}

async function submit(page: Page, code: string, fingerprint: string) {
  await page.getByLabel('Pairing code').fill(code);
  await page.getByLabel('Device fingerprint').fill(fingerprint);
  await page.getByRole('button', { name: 'Submit' }).click();
}

for (const role of ['Registrar', 'ClubAdmin'] as const) {
  test(`${role} submits a valid code and sees the pending request (scenarios 1, 6)`, async ({ page, api }) => {
    api.signInAs(role);
    addPairing(api);
    await page.goto('/analyst-managers/pair');
    await expectNoAxeViolations(page);
    await submit(page, CODE, FINGERPRINT);
    await expect(page.getByText('State: Pending approval')).toBeVisible();
    await expect(page.getByText('Host label: lab-pc')).toBeVisible();
    await expect(page.getByText(/Claimed key protection: Software/)).toBeVisible();
    await expect(page.getByText('Operating system: linux')).toBeVisible();
    await expect(page.getByText(`Device fingerprint: ${FINGERPRINT}`)).toBeVisible();
    await expect(page.getByText(/Open for approval until/)).toBeVisible();
    await expect(page.getByLabel('Pairing code')).toHaveValue('');
    await expect(page.getByLabel('Device fingerprint')).toHaveValue('');
    await expectNoAxeViolations(page);
  });
}

test('refuses wrong, expired, and repeated codes with one identical message (scenario 2, SC-004)', async ({ page, api }) => {
  api.signInAs('Registrar');
  addPairing(api);
  await page.goto('/analyst-managers/pair');
  const refusals: string[] = [];
  const refused = async (code: string, fingerprint: string) => {
    await submit(page, code, fingerprint);
    const alert = page.getByRole('alert');
    await expect(alert).toBeVisible();
    refusals.push((await alert.innerText()).trim());
    await expectNoAxeViolations(page);
  };
  await refused('NO-SUCH-CODE', FINGERPRINT);
  await refused(CODE, 'FFFF-FFFF-FFFF');

  // The code is used by the first successful submission, so the repeat is refused like any other.
  await submit(page, CODE, FINGERPRINT);
  await expect(page.getByText('State: Pending approval')).toBeVisible();
  await refused(CODE, FINGERPRINT);

  // An expired pairing is no longer in the pairing state.
  api.registrations[0].state = 'expired';
  api.pairingCodes.set('EXPIRED-CODE', REGISTRATION);
  await refused('EXPIRED-CODE', FINGERPRINT);

  expect(new Set(refusals)).toEqual(new Set([REFUSAL]));
  await expect(page.getByLabel('Pairing code')).toHaveValue('');
});

test('signed-out visitors sign in and return to the pairing page (scenario 3)', async ({ page, api }) => {
  api.endSession();
  api.on('POST', '/session', async (route) => {
    api.signInAs('Registrar');
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(sessionFor('Registrar')) });
  });
  await page.goto('/analyst-managers/pair');
  await expect(page).toHaveURL(/\/sign-in\?returnTo=%2Fanalyst-managers%2Fpair$/);
  await page.getByLabel('Sign-in name').fill('registrar');
  await page.getByLabel('Password').fill(SIMULATED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/\/analyst-managers\/pair$/);
  await expect(page.getByLabel('Pairing code')).toBeVisible();
});

for (const role of ['Coach', 'Viewer'] as const) {
  test(`${role} sees the not-permitted view and submits nothing (scenario 4)`, async ({ page, api }) => {
    api.signInAs(role);
    await page.goto('/analyst-managers/pair');
    await expect(page.getByRole('alert')).toContainText('not permitted');
    await expect(page.getByLabel('Pairing code')).toHaveCount(0);
    await expectNoAxeViolations(page);
    expect(api.requests.filter((r) => r.method === 'POST' && r.path.startsWith('/analyst-manager-registrations'))).toEqual([]);
  });
}

test('shows the rate limit with the wait (scenario 5)', async ({ page, api }) => {
  api.signInAs('Registrar');
  addPairing(api);
  api.rateLimitSubmissionsFor = 42;
  await page.goto('/analyst-managers/pair');
  await submit(page, CODE, FINGERPRINT);
  await expect(page.getByRole('alert')).toHaveText('Too many attempts. Try again in 42 seconds.');
  await expectNoAxeViolations(page);
});

test('keeps the code and fingerprint out of addresses, history, and storage (SC-007)', async ({ page, api, secrets }) => {
  secrets.push(CODE, FINGERPRINT);
  api.signInAs('Registrar');
  addPairing(api);
  await page.goto('/analyst-managers/pair');
  const urls: string[] = [];
  page.on('request', (request) => {
    if (request.method() === 'GET') urls.push(request.url());
  });
  page.on('framenavigated', (frame) => urls.push(frame.url()));
  await submit(page, CODE, FINGERPRINT);
  await expect(page.getByText('State: Pending approval')).toBeVisible();
  const forbidden = [CODE, FINGERPRINT, encodeURIComponent(CODE), encodeURIComponent(FINGERPRINT)];
  for (const url of [...urls, page.url()]) {
    for (const value of forbidden) expect(url).not.toContain(value);
  }
  const history = await page.evaluate(() => JSON.stringify(window.history.state));
  for (const value of forbidden) expect(history).not.toContain(value);
  const typed = await page.evaluate(() => Array.from(document.querySelectorAll('input')).map((i) => i.value));
  expect(typed).toEqual(['', '']);
});
