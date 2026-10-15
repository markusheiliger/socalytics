import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import type { SimulatedApi } from '../support/simulatedApi';

const REGISTRAR = '00000000-0000-4000-8000-000000000014';
const OTHER = '00000000-0000-4000-8000-000000000099';

function add(api: SimulatedApi, id: string, over: Record<string, unknown>) {
  api.addRegistration({
    registrationId: id,
    version: 1,
    state: 'pending_approval',
    deviceFingerprint: `FP-${id.slice(-2)}`,
    deviceMetadata: { os: 'linux', claimedKeyProtection: 'software', hostLabel: `host-${id.slice(-2)}` },
    submittedBy: REGISTRAR,
    submittedAt: new Date('2026-10-12T10:00:00Z'),
    status: { operatingState: 'running' },
    connectivity: 'online',
    ...over,
  } as never);
}

const A = '00000000-0000-4000-8000-000000000061';
const B = '00000000-0000-4000-8000-000000000062';
const C = '00000000-0000-4000-8000-000000000063';

test('a Registrar sees exactly their requests without operating state or connectivity (scenario 1)', async ({ page, api }) => {
  api.signInAs('Registrar');
  add(api, A, { approvalExpiresAt: new Date(Date.now() + 3_600_000) });
  add(api, B, { state: 'rejected', decidedAt: new Date('2026-10-12T11:00:00Z'), decisionReason: 'Not ours' });
  add(api, C, { submittedBy: OTHER });
  await page.goto('/analyst-managers/requests');
  await expect(page.getByRole('link', { name: 'host-61 – Pending approval' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'host-62 – Rejected' })).toBeVisible();
  await expect(page.getByText('Reason: Not ours')).toBeVisible();
  await expect(page.getByText(/Can be approved until/)).toBeVisible();
  await expect(page.getByText('host-63')).toHaveCount(0);
  await expect(page.getByText(/online|offline|running/i)).toHaveCount(0);
  await expectNoAxeViolations(page);
  await page.getByRole('link', { name: 'host-62 – Rejected' }).click();
  await expect(page.getByText('Reason: Not ours')).toBeVisible();
  await expectNoAxeViolations(page);
});

test("another member's request is not found (scenario 2)", async ({ page, api }) => {
  api.signInAs('Registrar');
  add(api, C, { submittedBy: OTHER });
  await page.goto(`/analyst-managers/requests/${C}`);
  await expect(page.getByRole('alert')).toHaveText('This registration request was not found.');
  await expectNoAxeViolations(page);
});

test('a Registrar without requests sees none listed (scenario 3)', async ({ page, api }) => {
  api.signInAs('Registrar');
  add(api, C, { submittedBy: OTHER });
  await page.goto('/');
  await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'My registration requests' }).click();
  await expect(page.getByText('You have not submitted any registration requests.')).toBeVisible();
  await expectNoAxeViolations(page);
});
