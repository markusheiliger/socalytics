import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import type { SimulatedApi } from '../support/simulatedApi';

const id = (n: number) => `00000000-0000-4000-8000-0000000002${String(n).padStart(2, '0')}`;
const REPORTED = new Date('2026-10-12T10:00:00Z');
const RESUME = new Date('2026-10-13T15:30:00Z');

function add(api: SimulatedApi, n: number, state: string, over: Record<string, unknown> = {}) {
  api.addRegistration({
    registrationId: id(n),
    version: 1,
    state,
    deviceFingerprint: 'THNN-PKJR',
    deviceMetadata: { os: 'linux', managerVersion: '1.2.3', claimedKeyProtection: 'tpm', hostLabel: `host-${n}` },
    createdAt: new Date(`2026-10-0${n}T09:00:00Z`),
    submittedBy: '00000000-0000-4000-8000-000000000014',
    ...over,
  } as never);
}

const online = {
  connectivity: 'online',
  status: { operatingState: 'running', activeAnalysts: 2, maxAnalysts: 4, managerVersion: '2.0.0', reportedAt: REPORTED },
};

test('the fleet lists the current registrations with every field, pending first (scenario 1, 6)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, 1, 'active', online);
  add(api, 2, 'active', { connectivity: 'offline', status: { operatingState: 'paused', pausedUntil: RESUME, reportedAt: REPORTED } });
  add(api, 3, 'pending_approval');
  add(api, 4, 'revoked');
  add(api, 5, 'active', { connectivity: 'not-applicable' });
  add(api, 6, 'active', {
    connectivity: 'online',
    status: { operatingState: 'runtime-unavailable', runtimeUnavailableReason: 'Docker stopped', reportedAt: REPORTED },
  });
  await page.goto('/analyst-managers');
  const list = page.getByRole('list', { name: 'Analyst Managers' });
  await expect(list.getByRole('link').first()).toHaveText('host-3 – Pending approval');
  await expect(list.getByRole('link', { name: 'host-4 – Revoked' })).toHaveCount(0);
  await expect(list.getByText('Analysts: 2 of 4')).toBeVisible();
  await expect(list.getByText('Version: 2.0.0')).toBeVisible();
  await expect(list.getByText(`Connectivity: Online (last report: ${REPORTED.toLocaleString()})`).first()).toBeVisible();
  await expect(list.getByText(`Operating state: Paused until ${RESUME.toLocaleString()}`)).toBeVisible();
  await expect(list.getByText('Operating state: Runtime unavailable: Docker stopped')).toBeVisible();
  await expect(list.getByText('Connectivity: Not applicable (no report yet)')).toBeVisible();
  await expect(list.getByText('Submitted by you')).toHaveCount(0);
  await expectNoAxeViolations(page);
});

test('filters show exactly the matching registrations (scenario 2)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, 1, 'active', online);
  add(api, 3, 'pending_approval');
  add(api, 4, 'revoked');
  add(api, 5, 'rejected');
  await page.goto('/analyst-managers');
  const links = page.getByRole('list', { name: 'Analyst Managers' }).getByRole('link');
  await expect(links).toHaveCount(2);
  await page.getByRole('combobox', { name: 'Show' }).click();
  await page.getByRole('option', { name: 'Ended' }).click();
  await expect(links).toHaveText(['host-4 – Revoked', 'host-5 – Rejected']);
  await page.getByRole('combobox', { name: 'Show' }).click();
  await page.getByRole('option', { name: 'All' }).click();
  await expect(links).toHaveCount(4);
  await page.getByRole('combobox', { name: 'Show' }).click();
  await page.getByRole('option', { name: 'Active', exact: true }).click();
  await expect(links).toHaveText(['host-1 – Active']);
});

test('a change is shown at the next refresh without a reload (scenario 3, SC-006)', async ({ page, api }) => {
  await page.clock.install();
  api.signInAs('ClubAdmin');
  add(api, 1, 'active', online);
  await page.goto('/analyst-managers');
  await expect(page.getByText('Operating state: Running')).toBeVisible();
  Object.assign(api.registrations[0], { connectivity: 'offline', status: { operatingState: 'draining', reportedAt: REPORTED } });
  await page.clock.fastForward(31_000);
  await expect(page.getByText('Operating state: Draining')).toBeVisible();
  await expect(page.getByText(/^Connectivity: Offline/)).toBeVisible();
});

test('revoking needs a reason and the typed host label (scenario 4)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, 1, 'active', online);
  await page.goto(`/analyst-managers/${id(1)}`);
  await page.getByRole('button', { name: 'Revoke…' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('Host label: host-1')).toBeVisible();
  await expect(dialog.getByText('Active Analysts: 2')).toBeVisible();
  await expect(dialog.getByText(/cannot be undone.*retried by another Manager.*registered again/)).toBeVisible();
  const confirm = dialog.getByRole('button', { name: 'Revoke' });
  await expect(confirm).toBeDisabled();
  await dialog.getByLabel(/^Reason/).fill('Lost laptop');
  await dialog.getByLabel('Type the host label to confirm').fill('host-2');
  await expect(confirm).toBeDisabled();
  await expectNoAxeViolations(page);
  await dialog.getByLabel('Type the host label to confirm').fill('host-1');
  await confirm.click();
  await expect(page.getByText('State: Revoked')).toBeVisible();
  await expect(page.getByText('End reason: Lost laptop')).toBeVisible();
  await expect(page.getByText(/^Connectivity:/)).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Revoke…' })).toHaveCount(0);
});

test('a Registrar and other members do not reach the fleet (scenario 5, SC-005)', async ({ page, api }) => {
  api.signInAs('Registrar');
  await page.goto('/analyst-managers');
  await expect(page.getByRole('alert')).toContainText('not permitted');
  await expect(page.getByRole('link', { name: 'My registration requests' })).toBeVisible();
  api.signInAs('Coach');
  await page.goto('/analyst-managers');
  await expect(page.getByRole('alert')).toContainText('not permitted');
  api.signInAs('ClubAdmin');
  await page.goto('/analyst-managers');
  await expect(page.getByRole('heading', { name: 'Analyst Managers' })).toBeVisible();
});

test('the fleet shows the claimed profile, including Not reported', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, 1, 'active', { deviceMetadata: { os: 'linux', claimedKeyProtection: 'tpm', claimedApplicationProfile: 'headless-server', hostLabel: 'host-1' } });
  add(api, 2, 'active', { deviceMetadata: { os: 'linux', claimedKeyProtection: 'tpm', claimedApplicationProfile: 'tray', hostLabel: 'host-2' } });
  add(api, 3, 'active');
  await page.goto('/analyst-managers');
  await expect(page.getByText('Profile (claimed): Headless server')).toBeVisible();
  await expect(page.getByText('Profile (claimed): Tray')).toBeVisible();
  await expect(page.getByText('Profile (claimed): Not reported')).toBeVisible();
  await expectNoAxeViolations(page);
});

test('a never-reported Manager, unknown values, and a long label do not break the view', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  const long = 'workstation-'.repeat(8);
  add(api, 1, 'active', { connectivity: 'offline' });
  add(api, 2, 'active', {
    deviceMetadata: { claimedKeyProtection: 'quantum', hostLabel: long },
    connectivity: 'teleporting',
    status: { operatingState: 'levitating', reportedAt: REPORTED },
  });
  await page.goto('/analyst-managers');
  await expect(page.getByText('Connectivity: Offline (no report yet)')).toBeVisible();
  await expect(page.getByText('Operating state: Unknown')).toBeVisible();
  await expect(page.getByText('Claimed key protection: Unknown')).toBeVisible();
  const link = page.getByRole('link', { name: /…/ });
  await expect(link).toHaveAttribute('title', long);
  await expectNoAxeViolations(page);
});

test('revocation is disabled while the platform is unreachable', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, 1, 'active', online);
  await page.goto(`/analyst-managers/${id(1)}`);
  await expect(page.getByRole('button', { name: 'Revoke…' })).toBeEnabled();
  await page.context().setOffline(true);
  await page.evaluate(() => window.dispatchEvent(new Event('offline')));
  await expect(page.getByRole('button', { name: 'Revoke…' })).toBeDisabled();
});

test('a registration revoked meanwhile shows the refusal (concurrent change)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  add(api, 1, 'active', online);
  await page.goto(`/analyst-managers/${id(1)}`);
  await page.getByRole('button', { name: 'Revoke…' }).click();
  Object.assign(api.registrations[0], { state: 'revoked', version: 2 });
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/^Reason/).fill('Lost');
  await dialog.getByLabel('Type the host label to confirm').fill('host-1');
  await dialog.getByRole('button', { name: 'Revoke' }).click();
  await expect(page.getByRole('alert')).toContainText('no longer active');
  await expect(page.getByText('State: Revoked')).toBeVisible();
});
