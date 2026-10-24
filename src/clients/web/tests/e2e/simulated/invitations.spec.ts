import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { SIMULATED_PASSWORD, team } from '../support/simulatedApi';

test('invites a member from the members area with a team role (FR-001, FR-023)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  await page.goto('/members');
  await page.getByRole('button', { name: 'Invite member' }).click();
  const dialog = page.getByRole('dialog', { name: 'Invite a member' });
  await expect(dialog).toBeVisible();
  await expectNoAxeViolations(page);

  await dialog.getByLabel(/^Email address/).fill('jdoe@example.org');
  await dialog.getByLabel(/^Sign-in name/).fill('jdoe');
  await dialog.getByRole('button', { name: 'Add team role' }).click();
  await dialog.getByLabel('Role', { exact: true }).selectOption('Viewer');
  await dialog.getByRole('button', { name: 'Send invitation' }).click();

  await expect(page.getByText('The invitation was sent to jdoe@example.org.')).toBeVisible();
  expect(api.invitationsCreated).toMatchObject([
    { address: 'jdoe@example.org', accountName: 'jdoe', teamRoles: [{ teamId: team.id, role: 'viewer' }] },
  ]);
  await expectNoAxeViolations(page);
});

test('shows a conflict for an address that is already invited (FR-001)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.invitationsCreated.push({ token: 'other', address: 'jdoe@example.org', accountName: 'other', teamRoles: [] });
  await page.goto('/members');
  await page.getByRole('button', { name: 'Invite member' }).click();
  const dialog = page.getByRole('dialog', { name: 'Invite a member' });
  await dialog.getByLabel(/^Email address/).fill('jdoe@example.org');
  await dialog.getByLabel(/^Sign-in name/).fill('jdoe');
  await dialog.getByRole('button', { name: 'Send invitation' }).click();
  await expect(dialog.getByText('This email address is already in use')).toBeVisible();
});

test('opens the invitation link, accepts it, and lands on sign-in (FR-008, FR-023)', async ({ page, api }) => {
  api.endSession();
  api.invitationTokens.set('simulated-invitation-token', { clubName: 'Simulated Club', accountName: 'jdoe' });
  await page.goto('/invitations/accept#token=simulated-invitation-token');
  await expect(page.getByRole('heading', { level: 1, name: 'Accept your invitation' })).toBeVisible();
  await expect(page.getByText('You are invited to join Simulated Club.')).toBeVisible();
  await expect(page.getByLabel(/^Sign-in name/)).toHaveValue('jdoe');
  await expect(page).toHaveURL(/\/invitations\/accept$/);
  expect(await page.evaluate(() => window.location.hash)).toBe('');
  await expectNoAxeViolations(page);

  await page.getByLabel(/^Password/).fill(SIMULATED_PASSWORD);
  await page.getByLabel(/^Confirm password/).fill(SIMULATED_PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('status')).toContainText('Your account is ready');
  expect(api.invitationAcceptances).toEqual([{ accountName: 'jdoe', password: SIMULATED_PASSWORD }]);
  await expectNoAxeViolations(page);

  await page.getByRole('link', { name: 'Go to sign in' }).click();
  await expect(page).toHaveURL(/\/sign-in$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
});

test('shows the neutral message for an invalid token (FR-008)', async ({ page, api }) => {
  api.endSession();
  await page.goto('/invitations/accept#token=unknown-token');
  await expect(page.getByRole('alert')).toContainText('This invitation cannot be used');
  await expect(page).toHaveURL(/\/invitations\/accept$/);
  await expectNoAxeViolations(page);
});
