import type { Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expectAllReachableByKeyboard } from '../support/keyboard';
import { expect, test } from '../support/fixtures';
import { club, season } from '../support/simulatedApi';

const fulfillJson = (route: Route, status: number, body: unknown) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? 'application/problem+json' : 'application/json',
    body: JSON.stringify(body),
  });

test('edits the club details and shows them (scenario 1)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.on('PUT', '/club', async (route, body) => {
    await fulfillJson(route, 200, { ...club, ...(body as object), version: 2 });
  });
  await page.goto('/club');
  await expect(page.getByRole('heading', { level: 2, name: 'Club' })).toBeVisible();
  await expectNoAxeViolations(page);
  await expectAllReachableByKeyboard(page, page.locator('main'));

  const name = page.getByLabel(/^Club name/);
  await name.fill('Renamed Club');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('The club details were saved.')).toBeVisible();
  await expect(name).toHaveValue('Renamed Club');
  const put = api.requests.find((r) => r.method === 'PUT' && r.path === '/club');
  expect(put?.headers['if-match']).toBe('"1"');
  await expectNoAxeViolations(page);
});

test('creates a season in the draft state and activates it (scenario 2)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  const draft = { ...season, id: '00000000-0000-4000-8000-000000000021', name: '2027/28', state: 'Draft', activatedAt: null };
  let created = false;
  let state = 'draft';
  const current = () => ({ ...draft, state });
  api.on('GET', '/seasons', (route) =>
    fulfillJson(route, 200, { items: created ? [season, current()] : [season], continuationToken: null }),
  );
  api.on('POST', '/seasons', (route) => {
    created = true;
    return fulfillJson(route, 201, current());
  });
  api.on('GET', `/seasons/${draft.id}`, (route) => fulfillJson(route, 200, current()));
  api.on('POST', `/seasons/${draft.id}/activate`, (route) => {
    state = 'active';
    return fulfillJson(route, 200, { ...current(), version: 2 });
  });
  api.on('POST', `/seasons/${draft.id}/archive`, (route) => {
    state = 'archived';
    return fulfillJson(route, 200, { ...current(), version: 3 });
  });

  await page.goto('/seasons');
  await expectNoAxeViolations(page);
  await page.getByLabel(/^Season name/).fill('2027/28');
  await page.getByRole('button', { name: 'Create season' }).click();
  const link = page.getByRole('link', { name: '2027/28' });
  await expect(link).toBeVisible();
  await expect(page.getByRole('listitem').filter({ hasText: '2027/28' })).toContainText('Draft');

  await link.click();
  await expect(page.getByText('State: Draft')).toBeVisible();
  await expectNoAxeViolations(page);
  await page.getByRole('button', { name: 'Activate' }).click();
  await expect(page.getByText('The season is now active.')).toBeVisible();
  await expect(page.getByText('State: Active')).toBeVisible();

  await page.getByRole('button', { name: 'Archive' }).click();
  await expect(page.getByText('State: Archived')).toBeVisible();
  await expect(page.getByText('Its teams are read-only.').first()).toBeVisible();
  await expectNoAxeViolations(page);
});

test('refuses a concurrent edit and offers to reload (scenario 4)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  let reloaded = false;
  api.on('GET', '/club', (route) =>
    fulfillJson(route, 200, reloaded ? { ...club, displayName: 'Theirs', version: 2 } : club),
  );
  api.on('PUT', '/club', (route) => fulfillJson(route, 412, { type: 'about:blank', title: 'Precondition Failed', status: 412 }));
  await page.goto('/club');
  await page.getByLabel(/^Club name/).fill('Mine');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('alert')).toContainText('changed by someone else');
  await expectNoAxeViolations(page);
  reloaded = true;
  await page.getByRole('button', { name: 'Reload' }).click();
  await expect(page.getByLabel(/^Club name/)).toHaveValue('Theirs');
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('shows the platform reason next to the affected field (scenario 5)', async ({ page, api }) => {
  api.signInAs('ClubAdmin');
  api.on('POST', '/seasons', (route) =>
    fulfillJson(route, 400, {
      type: 'about:blank',
      title: 'Bad Request',
      status: 400,
      code: 'validation-failed',
      errors: [{ field: 'name', code: 'required' }],
    }),
  );
  await page.goto('/seasons');
  await page.getByRole('button', { name: 'Create season' }).click();
  const name = page.getByLabel(/^Season name/);
  await expect(page.getByText('This field is required.')).toBeVisible();
  await expect(name).toHaveAttribute('aria-invalid', 'true');
  await expectNoAxeViolations(page);
});
