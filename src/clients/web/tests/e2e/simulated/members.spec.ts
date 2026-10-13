import type { Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expectAllReachableByKeyboard } from '../support/keyboard';
import { expect, test } from '../support/fixtures';
import { problem, team } from '../support/simulatedApi';

const MEMBER = '00000000-0000-4000-8000-000000000021';
const fulfillJson = (route: Route, status: number, body: unknown) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? 'application/problem+json' : 'application/json',
    body: JSON.stringify(body),
  });
const dto = (over: Record<string, unknown> = {}) => ({
  id: MEMBER,
  accountName: 'newbie',
  membershipStatus: 'active',
  passwordSet: false,
  lockedOut: false,
  lockoutEndsAt: null,
  twoFactorEnabled: false,
  clubRoles: [],
  teamRoles: [],
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
  ...over,
});
const credential = { memberId: MEMBER, purpose: 'set-password', credential: 'ONCE-CODE-4711', expiresAt: '2026-10-13T12:00:00Z' };

test('creates a member without a usable password (scenario 1)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  let created = false;
  api.on('GET', '/members', (route) =>
    fulfillJson(route, 200, { items: created ? [dto()] : [], continuationToken: null }),
  );
  api.on('POST', '/members', (route) => {
    created = true;
    return fulfillJson(route, 201, { member: dto(), setPasswordCredential: credential });
  });
  await page.goto('/members');
  await expect(page.getByRole('heading', { level: 2, name: 'Members' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));
  await page.getByLabel(/^Sign-in name/).fill('newbie');
  await page.getByRole('button', { name: 'Create member' }).click();
  await page.getByRole('button', { name: 'I have handed over the code' }).click();
  await expect(page.getByRole('link', { name: 'newbie (Active)' })).toBeVisible();
});

test('shows a credential code once and never in an address (scenario 2)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.on('GET', `/members/${MEMBER}`, (route) => fulfillJson(route, 200, dto()));
  api.on('POST', `/members/${MEMBER}/credentials`, (route) => fulfillJson(route, 201, credential));
  await page.goto(`/members/${MEMBER}`);
  await page.getByRole('button', { name: 'Issue set-password code' }).click();
  await expect(page.getByText('ONCE-CODE-4711')).toBeVisible();
  await expectNoAxeViolations(page);
  expect(page.url()).not.toContain('ONCE-CODE');
  await page.getByRole('button', { name: 'I have handed over the code' }).click();
  await expect(page.getByText('ONCE-CODE-4711')).toHaveCount(0);
  await page.reload();
  await expect(page.getByText('ONCE-CODE-4711')).toHaveCount(0);
});

test('assigns and removes roles and shows the last Club Admin refusal (scenario 3)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  let current = dto({ clubRoles: ['club-admin'] });
  api.on('GET', `/members/${MEMBER}`, (route) => fulfillJson(route, 200, current));
  api.on('PUT', `/members/${MEMBER}/team-roles/${team.id}`, (route) => {
    current = dto({ clubRoles: ['club-admin'], teamRoles: [{ teamId: team.id, role: 'coach' }] });
    return fulfillJson(route, 200, current);
  });
  api.on('DELETE', `/members/${MEMBER}/team-roles/${team.id}`, (route) => {
    current = dto({ clubRoles: ['club-admin'] });
    return fulfillJson(route, 200, current);
  });
  api.on('DELETE', `/members/${MEMBER}/club-roles/club-admin`, (route) =>
    fulfillJson(route, 409, problem(409, 'Conflict', { code: 'last-club-admin' })),
  );
  await page.goto(`/members/${MEMBER}`);
  await page.getByRole('button', { name: 'Assign team role' }).click();
  await expect(page.getByText('First Team: Coach')).toBeVisible();
  await expectNoAxeViolations(page);
  await page.getByRole('button', { name: 'Remove Coach of First Team' }).click();
  await expect(page.getByText('No team roles.')).toBeVisible();
  await page.getByRole('button', { name: 'Remove Club Admin' }).click();
  await expect(page.getByRole('alert')).toContainText('at least one active Club Admin');
  await expectNoAxeViolations(page);
});

test('deactivates and reactivates a member (scenario 4)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  let current = dto({ passwordSet: true });
  api.on('GET', `/members/${MEMBER}`, (route) => fulfillJson(route, 200, current));
  api.on('POST', `/members/${MEMBER}/deactivate`, (route) => {
    current = dto({ passwordSet: true, membershipStatus: 'deactivated' });
    return fulfillJson(route, 200, current);
  });
  api.on('POST', `/members/${MEMBER}/reactivate`, (route) => {
    current = dto({ passwordSet: true });
    return fulfillJson(route, 200, current);
  });
  await page.goto(`/members/${MEMBER}`);
  await page.getByRole('button', { name: 'Deactivate' }).click();
  await expect(page.getByText('Status: Deactivated')).toBeVisible();
  await page.getByRole('button', { name: 'Reactivate' }).click();
  await expect(page.getByText('Status: Active')).toBeVisible();
});

test('unlocks a locked member (scenario 5)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  let current = dto({ passwordSet: true, lockedOut: true });
  api.on('GET', `/members/${MEMBER}`, (route) => fulfillJson(route, 200, current));
  api.on('POST', `/members/${MEMBER}/unlock`, (route) => {
    current = dto({ passwordSet: true });
    return fulfillJson(route, 200, current);
  });
  await page.goto(`/members/${MEMBER}`);
  await expect(page.getByText('This account is locked.')).toBeVisible();
  await page.getByRole('button', { name: 'Unlock' }).click();
  await expect(page.getByText('The member was unlocked.')).toBeVisible();
  await expect(page.getByText('This account is locked.')).toHaveCount(0);
});

test('ends a member’s sessions (scenario 6)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.on('GET', `/members/${MEMBER}`, (route) => fulfillJson(route, 200, dto({ passwordSet: true })));
  api.on('DELETE', `/members/${MEMBER}/sessions`, (route) => route.fulfill({ status: 204 }));
  await page.goto(`/members/${MEMBER}`);
  await page.getByRole('button', { name: 'End sessions' }).click();
  await expect(page.getByText('The member\'s sessions were ended.')).toBeVisible();
  expect(api.requests.some((r) => r.method === 'DELETE' && r.path === `/members/${MEMBER}/sessions`)).toBe(true);
});
