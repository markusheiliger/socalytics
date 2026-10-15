import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import type { SimulatedApi } from '../support/simulatedApi';

const A = '00000000-0000-4000-8000-000000000081';
const B = '00000000-0000-4000-8000-000000000082';
const C = '00000000-0000-4000-8000-000000000083';
const CONFIRMATION = 'Last 6 characters of the fingerprint';

function add(api: SimulatedApi, id: string, over: Record<string, unknown> = {}) {
  api.addRegistration({
    registrationId: id,
    version: 1,
    state: 'pending_approval',
    deviceFingerprint: 'THNN-PKJR',
    deviceMetadata: { os: 'linux', osVersion: '6.1', architecture: 'x64', managerVersion: '1.2.3', claimedKeyProtection: 'tpm', hostLabel: `host-${id.slice(-2)}` },
    pairingOrigin: { sourceAddress: '10.0.0.7', requestedAt: new Date('2026-10-12T09:00:00Z') },
    approvalNotice: 'Approve only machines you know.',
    submittedBy: '00000000-0000-4000-8000-000000000014',
    submittedAt: new Date('2026-10-12T10:00:00Z'),
    approvalExpiresAt: new Date(Date.now() + 3_600_000),
    ...over,
  } as never);
}

test('a pending request shows its details and the physical-identification warning (scenario 1)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, A);
  await page.goto('/analyst-managers');
  await expectNoAxeViolations(page);
  await page.getByRole('link', { name: 'host-81 – Pending approval' }).click();
  await expect(page.getByText('Device fingerprint: THNN-PKJR')).toBeVisible();
  await expect(page.getByText('Operating system: linux 6.1')).toBeVisible();
  await expect(page.getByText('Manager version: 1.2.3')).toBeVisible();
  await expect(page.getByText('Pairing request origin: 10.0.0.7')).toBeVisible();
  await expect(page.getByText('Approve only machines you know.')).toBeVisible();
  await expect(page.getByText('Approve only Managers you can physically identify.')).toBeVisible();
  await expect(page.getByText(/not verified by the platform/)).toBeVisible();
  await expect(page.getByText(/claims a software-backed key/)).toHaveCount(0);
  await expectNoAxeViolations(page);
});

test('a software-backed key adds a warning (scenario 2)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, A, { deviceMetadata: { os: 'linux', claimedKeyProtection: 'software', hostLabel: 'soft' } });
  await page.goto(`/analyst-managers/${A}`);
  await expect(page.getByText(/claims a software-backed key/)).toBeVisible();
});

test('approval needs the matching last 6 characters, ignoring separators and case (scenario 3, 6)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, A, { submittedBy: '00000000-0000-4000-8000-000000000011' });
  await page.goto(`/analyst-managers/${A}`);
  await page.getByRole('button', { name: 'Approve…' }).click();
  const dialog = page.getByRole('dialog');
  const confirm = dialog.getByRole('button', { name: 'Approve', exact: true });
  await expect(confirm).toBeDisabled();
  await dialog.getByLabel(CONFIRMATION).fill('abcdef');
  await expect(dialog.getByText('This does not match the fingerprint of this request.')).toBeVisible();
  await expect(confirm).toBeDisabled();
  await expectNoAxeViolations(page);
  await dialog.getByLabel(CONFIRMATION).fill('nn-pk jr');
  await expect(confirm).toBeEnabled();
  await confirm.click();
  await expect(page.getByText('State: Approved')).toBeVisible();
  await expect(page.getByText(/waits for the Manager to activate/)).toBeVisible();
});

test('rejection requires a reason (scenario 4)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, A);
  await page.goto(`/analyst-managers/${A}`);
  await page.getByRole('button', { name: 'Reject…' }).click();
  const dialog = page.getByRole('dialog');
  const confirm = dialog.getByRole('button', { name: 'Reject', exact: true });
  await expect(confirm).toBeDisabled();
  await dialog.getByLabel(/Reason/).fill('Unknown machine');
  await confirm.click();
  await expect(page.getByText('State: Rejected')).toBeVisible();
  await expect(page.getByText('Reason: Unknown machine')).toBeVisible();
});

test('a request decided meanwhile shows the refusal and the current state (scenario 5, concurrent decision)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, A);
  await page.goto(`/analyst-managers/${A}`);
  await expect(page.getByText('State: Pending approval')).toBeVisible();
  Object.assign(api.registrations[0], { state: 'rejected', version: 2, decisionReason: 'Someone else' });
  await page.getByRole('button', { name: 'Approve…' }).click();
  await page.getByRole('dialog').getByLabel(CONFIRMATION).fill('NNPKJR');
  await page.getByRole('dialog').getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'can no longer be decided' })).toBeVisible();
  await expect(page.getByText('State: Rejected')).toBeVisible();
  await expect(page.getByText('Reason: Someone else')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Approve…' })).toHaveCount(0);
});

test('an expired approval window is refused (edge case)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, A, { approvalExpiresAt: new Date(Date.now() - 60_000) });
  await page.goto(`/analyst-managers/${A}`);
  await page.getByRole('button', { name: 'Approve…' }).click();
  await page.getByRole('dialog').getByLabel(CONFIRMATION).fill('NNPKJR');
  await page.getByRole('dialog').getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'can no longer be decided' })).toBeVisible();
});

test('the navigation shows the pending count and hides it at zero (scenario 7)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, A);
  add(api, B);
  add(api, C, { state: 'active' });
  await page.goto('/');
  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await expect(nav.getByRole('link', { name: 'Analyst Managers (2 pending)' })).toBeVisible();
  await nav.getByRole('link', { name: 'Analyst Managers (2 pending)' }).click();
  await page.getByRole('link', { name: 'host-81 – Pending approval' }).click();
  await page.getByRole('button', { name: 'Reject…' }).click();
  await page.getByRole('dialog').getByLabel(/Reason/).fill('No');
  await page.getByRole('dialog').getByRole('button', { name: 'Reject', exact: true }).click();
  await expect(nav.getByRole('link', { name: 'Analyst Managers (1 pending)' })).toBeVisible();
  await page.goto(`/analyst-managers/${B}`);
  await page.getByRole('button', { name: 'Reject…' }).click();
  await page.getByRole('dialog').getByLabel(/Reason/).fill('No');
  await page.getByRole('dialog').getByRole('button', { name: 'Reject', exact: true }).click();
  await expect(nav.getByRole('link', { name: 'Analyst Managers', exact: true })).toBeVisible();
});
