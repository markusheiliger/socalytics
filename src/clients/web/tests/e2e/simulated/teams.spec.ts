import type { Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { season, team } from '../support/simulatedApi';

const fulfillJson = (route: Route, status: number, body: unknown) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? 'application/problem+json' : 'application/json',
    body: JSON.stringify(body),
  });

test('creates and edits teams of a season (scenario 3)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  const second = { ...team, id: '00000000-0000-4000-8000-000000000031', name: 'Second Team' };
  let created = false;
  let name = second.name;
  api.on('GET', `/seasons/${season.id}/teams`, (route) =>
    fulfillJson(route, 200, { items: created ? [team, { ...second, name }] : [team], continuationToken: null }),
  );
  api.on('POST', `/seasons/${season.id}/teams`, (route) => {
    created = true;
    return fulfillJson(route, 201, second);
  });
  api.on('GET', `/teams/${second.id}`, (route) => fulfillJson(route, 200, { ...second, name }));
  api.on('PUT', `/teams/${second.id}`, (route, body) => {
    name = (body as { name: string }).name;
    return fulfillJson(route, 200, { ...second, name, version: 2 });
  });

  await page.goto('/teams');
  await expect(page.getByRole('link', { name: 'First Team' })).toBeVisible();
  await expectNoAxeViolations(page);
  await page.getByLabel(/^Team name/).fill('Second Team');
  await page.getByRole('button', { name: 'Create team' }).click();
  const link = page.getByRole('link', { name: 'Second Team' });
  await expect(link).toBeVisible();

  await link.click();
  await page.getByLabel(/^Team name/).fill('Reserves');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('The team was saved.')).toBeVisible();
  const put = api.requests.find((r) => r.method === 'PUT' && r.path === `/teams/${second.id}`);
  expect(put?.headers['if-match']).toBe('"1"');
  await expectNoAxeViolations(page);
  await page.getByRole('link', { name: 'Back to teams' }).click();
  await expect(page.getByRole('link', { name: 'Reserves' })).toBeVisible();
});

for (const role of ['Coach', 'Viewer'] as const) {
  test(`shows a ${role} their team read-only`, async ({ page, api }) => {
    api.signInAs(role);
    api.on('GET', `/teams/${team.id}`, (route) => fulfillJson(route, 200, team));
    await page.goto('/teams');
    await expect(page.getByRole('heading', { level: 2, name: 'Teams' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create team' })).toHaveCount(0);
    await expectNoAxeViolations(page);
    await page.getByRole('link', { name: 'First Team' }).click();
    await expect(page.getByText('You can view this team but not change it.')).toBeVisible();
    await expect(page.getByLabel(/^Team name/)).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Save' })).toHaveCount(0);
    await expectNoAxeViolations(page);
  });
}
