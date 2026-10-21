import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { operationalSummaryFixture } from '../support/simulatedApi';

const NOT_PERMITTED = 'You are not permitted to open this page.';

const value = (page: Page, label: string) => page.locator(`dt:text-is("${label}") + dd`);

test.describe('Summary', () => {
  test('a Club Admin opens Operations, sees every value and the observation time, and refreshes (US1, FR-001, FR-004 to FR-007)', async ({
    page,
    api,
  }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/');
    await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Operations', exact: true }).click();

    await expect(page).toHaveURL(/\/operations$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Operations' })).toBeVisible();
    await expect(value(page, 'Reason category')).toHaveText('profile-not-approved');
    await expect(page.locator('dt:text-is("Since")')).toBeVisible();
    await expect(value(page, 'Running')).toHaveText('2');
    await expect(value(page, 'Completed')).toHaveText('7');
    await expect(value(page, 'Queued')).toHaveText('1');
    await expect(value(page, 'Quarantined')).toHaveText('1');
    await expect(value(page, 'Runs with blocked nodes')).toHaveText('1');
    await expect(value(page, 'Stale attempts')).toHaveText('2');
    await expect(value(page, 'Rejected notifications')).toContainText('4');
    await expect(value(page, 'Outgoing messages pending')).toContainText('3');
    await expect(value(page, 'Outgoing messages failed')).toContainText('3');
    await expect(value(page, 'Oldest pending message')).toHaveText('2 h 5 min');
    const observed = page.getByRole('status').filter({ hasText: 'Observed at' });
    await expect(observed).toBeVisible();
    const first = await observed.innerText();
    await expectNoAxeViolations(page);

    api.operationalSummary.observedAt = new Date(api.operationalSummary.observedAt.getTime() + 90_000);
    api.operationalSummary.staleAttempts = 5;
    await page.getByRole('button', { name: 'Refresh' }).click();
    await expect(value(page, 'Stale attempts')).toHaveText('5');
    await expect(observed).not.toHaveText(first);
    await expect(observed).toContainText('Observed at');
  });

  test('an unresolvable definition states that new runs cannot start; a resolvable one does not (FR-004)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations');
    await expect(page.getByRole('alert')).toContainText(
      'New analysis runs cannot start until the default workflow resolves',
    );

    api.operationalSummary = {
      ...operationalSummaryFixture(),
      workflowDefinition: { state: 'resolvable', category: null, since: null },
    };
    await page.reload();
    await expect(page.getByText('The default workflow definition resolves.')).toBeVisible();
    await expect(page.getByText('New analysis runs cannot start until the default workflow resolves')).toHaveCount(0);
  });

  test('the failed count links to the failed outgoing messages (FR-006)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations');
    await page.getByRole('link', { name: 'Show failed outgoing messages' }).click();
    await expect(page).toHaveURL(/\/operations\/outbox-messages\?state=failed$/);
  });

  test('a Coach has no Operations entry and /operations shows the not-permitted view without an operations request (FR-001, SC-003)', async ({
    page,
    api,
  }) => {
    api.signInAs('Coach');
    await page.goto('/');
    await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
    await expect(
      page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Operations', exact: true }),
    ).toHaveCount(0);

    await page.goto('/operations');
    await expect(page.getByRole('alert')).toHaveText(NOT_PERMITTED);
    await expect(page.getByRole('heading', { level: 1, name: 'Operations' })).toHaveCount(0);
    expect(api.requests.filter((request) => request.path.startsWith('/operations'))).toEqual([]);
  });
});
