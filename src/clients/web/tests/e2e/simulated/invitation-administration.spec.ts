import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { team, type SimulatedInvitation } from '../support/simulatedApi';

const ADMIN = '00000000-0000-4000-8000-000000000021';
const idOf = (n: number) => `00000000-0000-4000-8000-0000000000e${n}`;
const at = (hours: number) => new Date(Date.UTC(2026, 9, 12, 12) + hours * 3_600_000);

const invitation = (n: number, over: Partial<SimulatedInvitation> = {}): SimulatedInvitation => ({
  id: idOf(n),
  state: 'pending',
  address: `invitee${n}@example.org`,
  accountName: `invitee${n}`,
  teamRoles: [{ teamId: team.id, teamName: team.name, role: 'viewer' }],
  invitedBy: { id: ADMIN, accountName: 'ada' },
  createdAt: at(n),
  linkIssuedAt: at(n),
  linkExpiresAt: at(n + 72),
  deliveryState: 'sent',
  resendCount: 0,
  revokedAt: null,
  acceptedAt: null,
  acceptedMemberId: null,
  version: 1,
  ...over,
});

test('lists invitations newest first and filters them by state (FR-013, FR-023)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.invitations.push(
    invitation(1, { state: 'revoked', revokedAt: at(5) }),
    invitation(2, { state: 'expired', deliveryState: 'failed' }),
    invitation(3),
  );
  await page.goto('/members');
  const section = page.getByRole('region', { name: 'Invitations' });
  const items = section.getByRole('list', { name: 'Invitations' }).getByRole('listitem');
  await expect(items).toHaveCount(3);
  await expect(items.nth(0)).toContainText('invitee3@example.org');
  await expect(items.nth(0)).toContainText('State: Pending');
  await expect(items.nth(0)).toContainText('Delivery: Message sent');
  await expect(items.nth(1)).toContainText('invitee2@example.org');
  await expect(items.nth(1)).toContainText('State: Expired');
  await expect(items.nth(1)).toContainText('Delivery: Message could not be delivered');
  await expect(items.nth(2)).toContainText('State: Revoked');
  await expectNoAxeViolations(page);

  await section.getByLabel('Show invitations').selectOption('expired');
  await expect(items).toHaveCount(1);
  await expect(items.first()).toContainText('invitee2@example.org');
});

test('resends an invitation and refreshes the list (FR-014, FR-023)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.invitations.push(invitation(1, { state: 'expired' }));
  await page.goto('/members');
  const section = page.getByRole('region', { name: 'Invitations' });
  await expect(section).toContainText('State: Expired');

  await section.getByRole('button', { name: 'Resend invitation to invitee1@example.org' }).click();
  await expect(section.getByRole('status')).toContainText('A new invitation was sent to invitee1@example.org.');
  await expect(section).toContainText('State: Pending');
  expect(api.invitationOperations).toEqual([{ id: idOf(1), operation: 'resend', ifMatch: '"1"' }]);
  await expectNoAxeViolations(page);
});

test('revokes an invitation only after the confirmation (FR-015, FR-023)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.invitations.push(invitation(1));
  await page.goto('/members');
  const section = page.getByRole('region', { name: 'Invitations' });

  await section.getByRole('button', { name: 'Revoke invitation to invitee1@example.org' }).click();
  const dialog = page.getByRole('dialog', { name: 'Revoke this invitation?' });
  await expect(dialog).toBeVisible();
  await expectNoAxeViolations(page);
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(dialog).toBeHidden();
  expect(api.invitationOperations).toEqual([]);

  await section.getByRole('button', { name: 'Revoke invitation to invitee1@example.org' }).click();
  await page.getByRole('dialog', { name: 'Revoke this invitation?' }).getByRole('button', { name: 'Revoke invitation' }).click();
  await expect(section.getByRole('status')).toContainText('The invitation to invitee1@example.org was revoked.');
  await expect(section).toContainText('State: Revoked');
  await expect(section.getByRole('button', { name: /^Resend invitation/ })).toHaveCount(0);
  expect(api.invitationOperations).toEqual([{ id: idOf(1), operation: 'revoke', ifMatch: '"1"' }]);
  await expectNoAxeViolations(page);
});

test('refreshes the list when the invitation version changed in the meantime (FR-016)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.invitations.push(invitation(1));
  await page.goto('/members');
  const section = page.getByRole('region', { name: 'Invitations' });
  await expect(section).toContainText('State: Pending');

  api.invitations[0].version = 2;
  await section.getByRole('button', { name: 'Resend invitation to invitee1@example.org' }).click();
  await expect(section.getByRole('alert')).toContainText('This invitation was changed in the meantime. The list was refreshed.');
  await section.getByRole('button', { name: 'Resend invitation to invitee1@example.org' }).click();
  await expect(section.getByRole('status')).toContainText('A new invitation was sent to invitee1@example.org.');
  expect(api.invitationOperations).toEqual([{ id: idOf(1), operation: 'resend', ifMatch: '"2"' }]);
});

test('refreshes the list when the invitation was accepted in the meantime (FR-016)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.invitations.push(invitation(1));
  await page.goto('/members');
  const section = page.getByRole('region', { name: 'Invitations' });
  await expect(section).toContainText('State: Pending');

  api.invitations[0].state = 'accepted';
  api.invitations[0].acceptedAt = at(10);
  await section.getByRole('button', { name: 'Resend invitation to invitee1@example.org' }).click();
  await expect(section.getByRole('alert')).toContainText('This invitation was already accepted or revoked. The list was refreshed.');
  await expect(section).toContainText('State: Accepted');
  await expect(section.getByRole('button', { name: /^Resend invitation/ })).toHaveCount(0);
  await expectNoAxeViolations(page);
});
