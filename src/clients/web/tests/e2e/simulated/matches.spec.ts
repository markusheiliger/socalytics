import type { Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard, expectAllReachableByKeyboard } from '../support/keyboard';
import { match, season, team } from '../support/simulatedApi';

const fulfillJson = (route: Route, status: number, body: unknown) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? 'application/problem+json' : 'application/json',
    body: JSON.stringify(body),
  });

const dto = (over: Record<string, unknown> = {}) => ({ ...match, ...over });

test('lists, creates, and edits matches of a team (scenarios 1-3, 5)', async ({ page, api }) => {
  api.signInAs('Coach');
  const created = dto({ id: '00000000-0000-4000-8000-000000000041', opponent: { name: 'Town SC' }, kickoffAt: '2026-12-24T18:30:00Z', competition: null });
  let items: Record<string, unknown>[] = [dto()];
  api.on('GET', `/teams/${team.id}`, (route) => fulfillJson(route, 200, team));
  api.on('GET', `/teams/${team.id}/matches`, (route) => fulfillJson(route, 200, { items, continuationToken: null }));
  api.on('POST', `/teams/${team.id}/matches`, (route) => {
    items = [...items, created];
    return fulfillJson(route, 201, created);
  });
  api.on('GET', `/matches/${created.id}`, (route) => fulfillJson(route, 200, items.find((m) => m.id === created.id)));
  api.on('PUT', `/matches/${created.id}`, (route, body) => {
    const next = { ...created, ...(body as object), version: 2 };
    items = items.map((m) => (m.id === created.id ? next : m));
    return fulfillJson(route, 200, next);
  });

  await page.goto(`/teams/${team.id}`);
  const list = page.getByRole('list', { name: 'Matches' });
  await expect(list.getByRole('link', { name: 'Rivals FC' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await activateWithKeyboard(page.getByRole('link', { name: 'New match' }));
  await page.getByLabel(/^Opponent/).fill('Town SC');
  await page.getByLabel(/^Kickoff time/).fill('2026-12-24T18:30');
  await page.getByLabel('Venue').selectOption('away');
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));
  await activateWithKeyboard(page.getByRole('button', { name: 'Create match' }));
  await expect(page.getByRole('heading', { level: 2, name: 'Town SC' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  await activateWithKeyboard(page.getByRole('link', { name: 'Edit match' }));
  await expect(page.getByLabel(/^Opponent/)).toHaveAttribute('readonly', '');
  await page.getByLabel('Competition').fill('Cup');
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));
  await activateWithKeyboard(page.getByRole('button', { name: 'Save' }));
  await expect(page.getByText('The match was saved.')).toBeVisible();
  const put = api.requests.find((r) => r.method === 'PUT' && r.path === `/matches/${created.id}`);
  expect(put?.headers['if-match']).toBe('"1"');

  await page.goto(`/teams/${team.id}`);
  await expect(list.getByRole('link', { name: 'Town SC' })).toBeVisible();
  await expect(list).toContainText('Cup');
});

test('refuses a stale save and offers to reload (scenario 5)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.on('GET', `/teams/${team.id}`, (route) => fulfillJson(route, 200, team));
  api.on('GET', `/matches/${match.id}`, (route) => fulfillJson(route, 200, match));
  api.on('PUT', `/matches/${match.id}`, (route) => fulfillJson(route, 412, { status: 412 }));
  await page.goto(`/matches/${match.id}/edit`);
  await activateWithKeyboard(page.getByRole('button', { name: 'Save' }));
  await expect(page.getByRole('alert')).toContainText(/changed by someone else/i);
  await expect(page.getByRole('button', { name: 'Reload' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));
});

test('shows a Viewer matches read-only (scenario 4)', async ({ page, api }) => {
  api.signInAs('Viewer');
  api.on('GET', `/teams/${team.id}`, (route) => fulfillJson(route, 200, team));
  api.on('GET', `/teams/${team.id}/matches`, (route) => fulfillJson(route, 200, { items: [match], continuationToken: null }));
  api.on('GET', `/matches/${match.id}`, (route) => fulfillJson(route, 200, match));
  await page.goto(`/teams/${team.id}`);
  await expect(page.getByRole('link', { name: 'New match' })).toHaveCount(0);
  await expectNoAxeViolations(page);
  await activateWithKeyboard(page.getByRole('link', { name: 'Rivals FC' }));
  await expect(page.getByText('You can view this match but not change it.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Edit match' })).toHaveCount(0);
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));
});

test('shows matches of an archived season read-only (scenario 6)', async ({ page, api }) => {
  api.signInAs('Coach');
  const archived = { ...team, seasonState: 'Archived' };
  api.on('GET', `/teams/${team.id}`, (route) => fulfillJson(route, 200, archived));
  api.on('GET', `/teams/${team.id}/matches`, (route) => fulfillJson(route, 200, { items: [match], continuationToken: null }));
  api.on('GET', `/matches/${match.id}`, (route) => fulfillJson(route, 200, match));
  await page.goto(`/teams/${team.id}`);
  await expect(page.getByRole('link', { name: 'Rivals FC' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'New match' })).toHaveCount(0);
  await page.getByRole('link', { name: 'Rivals FC' }).click();
  await expect(page.getByText(/matches are read-only/i)).toBeVisible();
  await expect(page.getByRole('link', { name: 'Edit match' })).toHaveCount(0);
  await expectNoAxeViolations(page);
  void season;
});
