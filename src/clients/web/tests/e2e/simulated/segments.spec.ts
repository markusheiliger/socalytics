import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard, expectAllReachableByKeyboard } from '../support/keyboard';
import { match, segmentFixtures, team } from '../support/simulatedApi';

const matchId = match.id as string;
const listPath = `/matches/${matchId}/materialized-segments`;

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
});

const area = (page: Page) => page.getByRole('region', { name: 'Video segments' });
const rows = (page: Page) => area(page).getByRole('row').filter({ has: page.getByRole('cell') });

test.describe('Access', () => {
  test('a Club Admin sees the segments area and one list request is sent', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto(`/matches/${match.id}`);
    await expect(area(page)).toBeVisible();
    await expect(rows(page)).toHaveCount(5);
    expect(api.segmentRequests.listMaterializedSegments).toBe(1);
  });

  for (const role of ['Coach', 'Viewer', 'Registrar'] as const) {
    test(`a ${role} sees no segments area and no segment request is sent`, async ({ page, api }) => {
      api.signInAs(role);
      await page.goto(`/matches/${match.id}`);
      await expect(page.getByRole('heading', { name: 'Triage conversations' }).or(page.getByRole('alert')).first()).toBeVisible();
      await expect(page.getByRole('heading', { name: 'Video segments' })).toHaveCount(0);
      await expect(area(page)).toHaveCount(0);
      expect(api.segmentRequests.listMaterializedSegments).toBe(0);
      expect(api.segmentRequests.purgeMaterializedSegments).toBe(0);
    });
  }
});

test.describe('Listing and summary', () => {
  test.beforeEach(({ api }) => api.signInAs('ClubAdmin'));

  test('lists every segment in platform order with state, size, reason, availability, and use as text', async ({ page }) => {
    await page.goto(`/matches/${match.id}`);
    const table = area(page).getByRole('table', { name: 'Materialized video segments of this match' });
    await expect(table).toBeVisible();
    for (const header of ['Recording version', 'Segment number', 'State', 'Size', 'Failure reason', 'Available since', 'Use']) {
      await expect(table.getByRole('columnheader', { name: header })).toBeVisible();
    }
    await expect(area(page).getByRole('columnheader', { name: 'Interval' })).toHaveCount(0);

    const list = rows(page);
    await expect(list).toHaveCount(5);
    const numbers = await list.evaluateAll((els) => els.map((el) => el.querySelectorAll('td')[1]?.textContent));
    expect(numbers).toEqual(['0', '1', '2', '3', '4']);

    await expect(list.nth(0)).toContainText('Available');
    await expect(list.nth(0)).toContainText(/1\.6\s?MB/);
    await expect(list.nth(0)).toContainText('Not in use');
    await expect(list.nth(1)).toContainText('In use');
    await expect(list.nth(1)).toContainText(/2\s?MB/);
    await expect(list.nth(2)).toContainText('Failed');
    await expect(list.nth(2)).toContainText('segment-media-undecodable');
    await expect(list.nth(3)).toContainText('Materializing');
    await expect(list.nth(4)).toContainText('Absent');
  });

  test('absent values read "Not reported", never zero or empty text', async ({ page }) => {
    await page.goto(`/matches/${match.id}`);
    const absent = rows(page).nth(4);
    await expect(absent.getByRole('cell').filter({ hasText: 'Not reported' })).toHaveCount(3);
    const failed = rows(page).nth(2);
    await expect(failed.getByRole('cell').filter({ hasText: 'Not reported' })).toHaveCount(2);
    await expect(absent).not.toContainText(/\b0\s?(B|byte)/);
  });

  test('the full recording version and the digests are available in a disclosure', async ({ page }) => {
    await page.goto(`/matches/${match.id}`);
    const first = rows(page).first();
    await first.locator('summary').click();
    await expect(first).toContainText('00000000-0000-4000-8000-000000000a01');
    await expect(first).toContainText('Timeline mapping digest');
    await expect(first).toContainText('Segmentation policy digest');
  });

  test('the summary counts the segments per state and in use', async ({ page }) => {
    await page.goto(`/matches/${match.id}`);
    await expect(area(page).getByRole('status').filter({ hasText: '5 segments' })).toHaveText(
      '5 segments, 1 absent, 1 materializing, 2 available, 1 failed, 2 in use',
    );
  });
});

test.describe('Empty state', () => {
  test('a match without segments says so plainly and offers no purge', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    api.segments.set(matchId, []);
    await page.goto(`/matches/${match.id}`);
    await expect(area(page).getByText('This match has no video segments yet. Segments are created on demand when an analysis needs them.')).toBeVisible();
    await expect(area(page).getByRole('table')).toHaveCount(0);
    await expect(area(page).getByRole('button', { name: /purge/i })).toHaveCount(0);
    await expectNoAxeViolations(page);
  });
});

test.describe('Refresh', () => {
  test('refresh reads the list again, keeps the previous list visible, and never polls', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto(`/matches/${match.id}`);
    await expect(rows(page)).toHaveCount(5);
    expect(api.segmentRequests.listMaterializedSegments).toBe(1);

    await page.waitForTimeout(1500);
    expect(api.segmentRequests.listMaterializedSegments).toBe(1);

    const changed = segmentFixtures();
    changed[0] = { ...changed[0], state: 'absent', mediaSizeBytes: null, availableAt: null };
    api.segments.set(matchId, changed);
    api.on('GET', listPath, async (route) => {
      await new Promise((resolve) => setTimeout(resolve, 800));
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ matchId, segments: changed }) });
    });

    await area(page).getByRole('button', { name: 'Refresh' }).click();
    await expect(area(page).getByRole('button', { name: 'Refreshing…' })).toBeDisabled();
    await expect(rows(page)).toHaveCount(5);
    await expect(rows(page).nth(0)).toContainText('Available');

    await expect(area(page).getByRole('button', { name: 'Refresh' })).toBeEnabled();
    await expect(rows(page).nth(0)).toContainText('Absent');
    await expect(area(page).getByRole('status').filter({ hasText: '5 segments' })).toContainText('2 absent, 1 materializing, 1 available');
  });
});

test.describe('Accessibility and keyboard', () => {
  test.beforeEach(({ api }) => api.signInAs('ClubAdmin'));

  test('the loading and list states have no WCAG 2.2 AA violations', async ({ page, api }) => {
    api.on('GET', listPath, async (route) => {
      await new Promise((resolve) => setTimeout(resolve, 1000));
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ matchId, segments: segmentFixtures() }) });
    });
    await page.goto(`/matches/${match.id}`);
    await expect(area(page).getByText('Loading the video segments…')).toBeVisible();
    await expectNoAxeViolations(page);
    await expect(rows(page)).toHaveCount(5);
    await expectNoAxeViolations(page);
  });

  test('the area is operable with the keyboard and shows visible focus', async ({ page }) => {
    test.setTimeout(60_000);
    await page.goto(`/matches/${match.id}`);
    await expect(rows(page)).toHaveCount(5);
    for (const summary of await area(page).locator('summary').all()) await activateWithKeyboard(summary);
    await expectAllReachableByKeyboard(page, area(page));

    const before = await rows(page).count();
    await activateWithKeyboard(area(page).getByRole('button', { name: 'Refresh' }));
    await expect(area(page).getByRole('button', { name: 'Refresh' })).toBeEnabled();
    await expect(rows(page)).toHaveCount(before);
  });
});
