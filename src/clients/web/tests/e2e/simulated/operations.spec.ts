import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { expectAllReachableByKeyboard } from '../support/keyboard';
import { operationalSummaryFixture, problem } from '../support/simulatedApi';

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

const messageId = (n: number) => `00000000-0000-4000-8000-${String(7000 + n).padStart(12, '0')}`;
const rows = (page: Page) => page.getByRole('list', { name: 'Outgoing messages' }).getByRole('listitem');
const openLinks = (page: Page) => page.getByRole('list', { name: 'Outgoing messages' }).getByRole('link');
const listedIds = async (page: Page) =>
  (await openLinks(page).evaluateAll((links) => links.map((link) => link.getAttribute('aria-label') ?? ''))).map((label) =>
    label.replace('Open message ', ''),
  );
const ids = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => messageId(from + i));

test.describe('Outgoing messages', () => {
  test('filters by state, by type, and both, with the filters in the address (US2, FR-008, FR-009)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations/outbox-messages');
    await expect(page.getByRole('heading', { level: 1, name: 'Outgoing messages' })).toBeVisible();
    await expect(rows(page)).toHaveCount(5);

    await page.getByLabel('State').selectOption('failed');
    await expect(page).toHaveURL(/\?state=failed$/);
    await expect(rows(page)).toHaveCount(3);
    expect(await listedIds(page)).toEqual(ids(7, 9));

    await page.getByRole('button', { name: 'Clear filters' }).first().click();
    await expect(page).not.toHaveURL(/state=/);
    await page.getByLabel('Message type').selectOption('analysis.ready-work');
    await expect(page).toHaveURL(/\?messageType=analysis\.ready-work$/);
    await expect(rows(page)).toHaveCount(4);
    expect(await listedIds(page)).toEqual([2, 5, 8, 11].map(messageId));

    await page.getByLabel('State').selectOption('failed');
    await expect(page).toHaveURL(/state=failed/);
    await expect(page).toHaveURL(/messageType=analysis\.ready-work/);
    await expect(rows(page)).toHaveCount(1);
    expect(await listedIds(page)).toEqual([messageId(8)]);
    await expectNoAxeViolations(page);

    await page.goto('/operations/outbox-messages?state=failed&messageType=analysis.ready-work');
    await expect(page.getByLabel('State')).toHaveValue('failed');
    await expect(page.getByLabel('Message type')).toHaveValue('analysis.ready-work');
    await expect(rows(page)).toHaveCount(1);
  });

  test('an unknown address value is ignored (FR-008)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations/outbox-messages?state=bogus&messageType=nonsense');
    await expect(rows(page)).toHaveCount(5);
    await expect(page.getByLabel('State')).toHaveValue('');
    await expect(page.getByLabel('Message type')).toHaveValue('');
    await expect(page.getByRole('alert')).toHaveCount(0);
  });

  test('pages forward and back over two fixture pages without skips or repeats, and refresh returns to the first page (FR-009, SC-002)', async ({
    page,
    api,
  }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations/outbox-messages');
    const nextPage = page.getByRole('button', { name: 'Next page' });
    const previousPage = page.getByRole('button', { name: 'Previous page' });
    await expect(rows(page)).toHaveCount(5);
    expect(await listedIds(page)).toEqual(ids(1, 5));
    await expect(previousPage).toBeDisabled();

    await nextPage.click();
    await expect(page.getByRole('link', { name: `Open message ${messageId(6)}` })).toBeVisible();
    expect(await listedIds(page)).toEqual(ids(6, 10));
    await expect(previousPage).toBeEnabled();

    await nextPage.click();
    await expect(page.getByRole('link', { name: `Open message ${messageId(11)}` })).toBeVisible();
    expect(await listedIds(page)).toEqual(ids(11, 12));
    await expect(nextPage).toBeDisabled();

    await previousPage.click();
    await expect(page.getByRole('link', { name: `Open message ${messageId(6)}` })).toBeVisible();
    expect(await listedIds(page)).toEqual(ids(6, 10));
    await previousPage.click();
    await expect(page.getByRole('link', { name: `Open message ${messageId(1)}` })).toBeVisible();
    expect(await listedIds(page)).toEqual(ids(1, 5));

    await nextPage.click();
    await expect(page.getByRole('link', { name: `Open message ${messageId(6)}` })).toBeVisible();
    await page.getByRole('button', { name: 'Refresh' }).click();
    await expect(page.getByRole('link', { name: `Open message ${messageId(1)}` })).toBeVisible();
    expect(await listedIds(page)).toEqual(ids(1, 5));
    await expect(previousPage).toBeDisabled();
  });

  test('a rejected continuation token shows the expired-position explanation and the first page (FR-009)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations/outbox-messages');
    await expect(rows(page)).toHaveCount(5);
    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(page.getByRole('link', { name: `Open message ${messageId(6)}` })).toBeVisible();

    api.operationsSwitches.set('listOutboxMessages', 'invalid-token');
    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(page.getByText('The list position expired; showing the first page again')).toBeVisible();
    await expect(page.getByRole('link', { name: `Open message ${messageId(1)}` })).toBeVisible();
    expect(await listedIds(page)).toEqual(ids(1, 5));
  });

  test('a message opens with every field, the markup subject as text, and the unknown failure category raw (FR-010, FR-011, FR-022)', async ({
    page,
    api,
  }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations/outbox-messages?state=failed');
    await page.getByRole('link', { name: `Open message ${messageId(7)}` }).click();
    await expect(page).toHaveURL(new RegExp(`/operations/outbox-messages/${messageId(7)}$`));
    await expect(page.getByRole('heading', { level: 1, name: 'Outgoing message' })).toBeVisible();

    await expect(value(page, 'Message ID')).toContainText(messageId(7));
    await expect(value(page, 'Message type')).toHaveText('Recordings finalized');
    await expect(value(page, 'Contract version')).toHaveText('1');
    await expect(value(page, 'Subject')).toHaveText('<img src=x onerror="alert(1)">analysis.markup');
    await expect(page.locator('img[src="x"]')).toHaveCount(0);
    await expect(value(page, 'State')).toHaveText('Failed');
    await expect(value(page, 'Attempt count')).toHaveText('4');
    await expect(value(page, 'Publication round')).toHaveText('1');
    await expect(value(page, 'Last attempt')).not.toHaveText('absent');
    await expect(value(page, 'Next attempt')).not.toHaveText('absent');
    await expect(value(page, 'Failure category')).toHaveText('transport-unavailable');
    await expect(value(page, 'Created')).not.toHaveText('absent');
    await expect(value(page, 'Published')).toHaveText('absent');
    await expect(value(page, 'Age')).not.toHaveText('absent');
    await expect(value(page, 'Publication lag')).toHaveText('absent');
    await expect(value(page, 'Run ID')).toHaveText('absent');
    await expect(value(page, 'Logical job ID')).toHaveText('absent');
    await expect(value(page, 'Capability ID')).toHaveText('absent');
    await expectNoAxeViolations(page);

    await page.goto(`/operations/outbox-messages/${messageId(8)}`);
    await expect(value(page, 'Failure category')).toHaveText('quantum-flux');
    await expect(value(page, 'Message type')).toHaveText('Ready work');
    await expect(value(page, 'Run ID')).toContainText('00000000-0000-4000-8000-000000003001');
    await expect(value(page, 'Capability ID')).toContainText('player-detection');
  });

  test('an unknown and a malformed identifier show the not-found view (FR-010)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto(`/operations/outbox-messages/${messageId(99)}`);
    await expect(page.getByText('This page does not exist.')).toBeVisible();
    await expect(page.getByRole('heading', { level: 1, name: 'Outgoing message' })).toHaveCount(0);

    await page.goto('/operations/outbox-messages/not-an-identifier');
    await expect(page.getByText('This page does not exist.')).toBeVisible();
    await expect(page.getByRole('heading', { level: 1, name: 'Outgoing message' })).toHaveCount(0);
  });
});

const receiptId = (n: number) => `00000000-0000-4000-8000-${String(8000 + n).padStart(12, '0')}`;
const receiptRows = (page: Page) => page.getByRole('list', { name: 'Notification receipts' }).getByRole('listitem');
const receiptIds = async (page: Page) =>
  (await receiptRows(page).evaluateAll((items) => items.map((item) => item.querySelector('dd')?.textContent ?? ''))).map((text) =>
    text.trim().slice(0, 36),
  );

test.describe('Notification receipts', () => {
  test('each outcome filter shows exactly the matching receipts with their fields, and the filter is in the address (US3, FR-012)', async ({
    page,
    api,
  }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations/notification-receipts');
    await expect(page.getByRole('heading', { level: 1, name: 'Notification receipts' })).toBeVisible();
    await expect(receiptRows(page)).toHaveCount(5);

    await page.getByLabel('Outcome').selectOption('run-created');
    await expect(page).toHaveURL(/\?outcome=run-created$/);
    await expect(receiptRows(page)).toHaveCount(4);
    expect(await receiptIds(page)).toEqual([1, 4, 7, 10].map(receiptId));
    await expect(receiptRows(page).first()).toContainText('Run created');
    await expect(receiptRows(page).first()).toContainText('Recordings finalized');
    await expect(receiptRows(page).first()).toContainText('00000000-0000-4000-8000-000000003001');
    await expect(receiptRows(page).first().getByText('Rejection category')).toHaveCount(0);

    await page.getByLabel('Outcome').selectOption('run-exists');
    await expect(page).toHaveURL(/\?outcome=run-exists$/);
    await expect(receiptRows(page)).toHaveCount(4);
    expect(await receiptIds(page)).toEqual([2, 5, 8, 11].map(receiptId));
    await expect(receiptRows(page).first()).toContainText('Run already exists');

    await page.getByLabel('Outcome').selectOption('rejected');
    await expect(page).toHaveURL(/\?outcome=rejected$/);
    await expect(receiptRows(page)).toHaveCount(4);
    expect(await receiptIds(page)).toEqual([3, 6, 9, 12].map(receiptId));
    await expect(receiptRows(page).first()).toContainText('Rejected');
    await expect(receiptRows(page).first()).toContainText('unrecognized-reason');
    await expect(receiptRows(page).nth(1)).toContainText('recording-set-not-found');
    await expect(receiptRows(page).first()).toContainText('absent');
    await expectNoAxeViolations(page);

    await page.goto('/operations/notification-receipts?outcome=bogus');
    await expect(page.getByLabel('Outcome')).toHaveValue('');
    await expect(receiptRows(page)).toHaveCount(5);
  });

  test('pages forward and back over two fixture pages without skips or repeats (FR-013, SC-002)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations/notification-receipts');
    const nextPage = page.getByRole('button', { name: 'Next page' });
    const previousPage = page.getByRole('button', { name: 'Previous page' });
    await expect(receiptRows(page)).toHaveCount(5);
    expect(await receiptIds(page)).toEqual([1, 2, 3, 4, 5].map(receiptId));
    await expect(previousPage).toBeDisabled();

    await nextPage.click();
    await expect(receiptRows(page).first()).toContainText(receiptId(6));
    expect(await receiptIds(page)).toEqual([6, 7, 8, 9, 10].map(receiptId));
    await expect(previousPage).toBeEnabled();

    await nextPage.click();
    await expect(receiptRows(page).first()).toContainText(receiptId(11));
    expect(await receiptIds(page)).toEqual([11, 12].map(receiptId));
    await expect(nextPage).toBeDisabled();

    await previousPage.click();
    await expect(receiptRows(page).first()).toContainText(receiptId(6));
    await previousPage.click();
    await expect(receiptRows(page).first()).toContainText(receiptId(1));
    expect(await receiptIds(page)).toEqual([1, 2, 3, 4, 5].map(receiptId));
  });

  test('the rejected count on the summary links to the rejected receipts (FR-012)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations');
    await page.getByRole('link', { name: 'Show rejected notifications' }).click();
    await expect(page).toHaveURL(/\/operations\/notification-receipts\?outcome=rejected$/);
    await expect(page.getByLabel('Outcome')).toHaveValue('rejected');
    await expect(receiptRows(page)).toHaveCount(4);
  });

  test('an empty result states that no receipt matches, and clearing the filter restores the list (FR-012)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    api.notificationReceipts = api.notificationReceipts.filter((receipt) => receipt.outcome !== 'rejected');
    await page.goto('/operations/notification-receipts?outcome=rejected');
    await expect(page.getByText('No notification receipt matches')).toBeVisible();
    await expect(receiptRows(page)).toHaveCount(0);

    await page.getByRole('button', { name: 'Clear filters' }).first().click();
    await expect(page).not.toHaveURL(/outcome=/);
    await expect(receiptRows(page)).toHaveCount(5);
  });
});

test.describe('Reconciliation', () => {
  const openDialog = async (page: Page) => {
    await page.goto('/operations');
    await page.getByRole('button', { name: 'Run reconciliation' }).click();
    const dialog = page.getByRole('dialog', { name: 'Run reconciliation' });
    await expect(dialog).toBeVisible();
    return dialog;
  };

  test('cancelling sends nothing (US4, FR-014, FR-015)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    const dialog = await openDialog(page);
    await expect(dialog).toContainText('never authorizes work');
    await expect(dialog).toContainText('recorded in the audit trail');
    await expectNoAxeViolations(page);

    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toHaveCount(0);
    expect(api.reconciliationRequests).toBe(0);
  });

  test('confirming with a double click sends one request, shows the outcome, and reads the summary again (FR-015, FR-016, SC-004)', async ({
    page,
    api,
  }) => {
    api.signInAs('ClubAdmin');
    const observed = page.getByRole('status').filter({ hasText: 'Observed at' });
    await page.goto('/operations');
    await expect(observed).toBeVisible();
    const first = await observed.innerText();

    api.operationalSummary.observedAt = new Date(api.operationalSummary.observedAt.getTime() + 300_000);
    await page.getByRole('button', { name: 'Run reconciliation' }).click();
    const dialog = page.getByRole('dialog', { name: 'Run reconciliation' });
    await dialog.getByRole('button', { name: 'Run reconciliation' }).dblclick();

    await expect(dialog).toContainText('2 ready jobs and 1 notifications were queued again');
    await expect(dialog).toContainText('Started');
    await expect(dialog).toContainText('Completed');
    expect(api.reconciliationRequests).toBe(1);

    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toHaveCount(0);
    await expect(observed).not.toHaveText(first);
    expect(api.reconciliationRequests).toBe(1);
  });

  test('an outcome without queued work says nothing needed to be queued again (FR-016)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    api.reconciliationResult = { ...api.reconciliationResult, readyJobsRepublished: 0, notificationsRepublished: 0 };
    const dialog = await openDialog(page);
    await dialog.getByRole('button', { name: 'Run reconciliation' }).click();
    await expect(dialog).toContainText('Nothing needed to be queued again');
    expect(api.reconciliationRequests).toBe(1);
  });

  test('a 503 shows the refusal explanation without a second request (FR-017)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    api.operationsSwitches.set('reconcileAnalysisPublication', 'unavailable');
    const dialog = await openDialog(page);
        await dialog.getByRole('button', { name: 'Run reconciliation' }).click();

    await expect(dialog.getByRole('alert')).toContainText('The reconciliation did not complete; nothing was changed by the Web UI');
    await expect(dialog.getByRole('button', { name: 'Run reconciliation' })).toHaveCount(0);
    await page.waitForTimeout(500);
    expect(api.reconciliationRequests).toBe(1);
  });

  test('a 403 shows the not-permitted view (FR-017)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    api.operationsSwitches.set('reconcileAnalysisPublication', 'forbidden');
    const dialog = await openDialog(page);
    await dialog.getByRole('button', { name: 'Run reconciliation' }).click();

    await expect(page.getByText(NOT_PERMITTED)).toBeVisible();
    expect(api.reconciliationRequests).toBe(1);
  });
});

const operationsResponses = /\/api\/v1\/operations\//;

async function holdOperationsResponses(page: Page): Promise<() => void> {
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(operationsResponses, async (route) => {
    await gate;
    await route.fallback();
  });
  return release;
}

async function failOperationsResponses(page: Page): Promise<void> {
  await page.route(operationsResponses, (route) =>
    route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify(problem(500, 'Server error')) }),
  );
}

async function operationsInBrowserStorage(page: Page): Promise<{ cacheApi: string[]; text: string }> {
  return page.evaluate(async () => {
    const cacheApi: string[] = [];
    for (const name of await caches.keys()) {
      cacheApi.push(
        ...(await (await caches.open(name)).keys()).map((request) => new URL(request.url).pathname).filter((p) => p.startsWith('/api/')),
      );
    }
    const entries: string[] = [];
    for (const storage of [localStorage, sessionStorage]) {
      for (let i = 0; i < storage.length; i++) {
        const key = storage.key(i) ?? '';
        entries.push(`${key}=${storage.getItem(key) ?? ''}`);
      }
    }
    for (const info of (await indexedDB.databases?.()) ?? []) {
      if (!info.name) continue;
      const db = await new Promise<IDBDatabase>((resolve, reject) => {
        const open = indexedDB.open(info.name as string);
        open.onsuccess = () => resolve(open.result);
        open.onerror = () => reject(open.error);
      });
      for (const store of Array.from(db.objectStoreNames)) {
        const rows = await new Promise<unknown[]>((resolve, reject) => {
          const request = db.transaction(store).objectStore(store).getAll();
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        entries.push(`${info.name}/${store}=${JSON.stringify(rows)}`);
      }
      db.close();
    }
    return { cacheApi, text: entries.join('\n') };
  });
}

const views = [
  { name: 'summary', path: '/operations', operation: 'getAnalysisOperationalSummary', loading: undefined },
  { name: 'outgoing messages', path: '/operations/outbox-messages', operation: 'listOutboxMessages', loading: 'Loading…' },
  { name: 'outgoing message', path: `/operations/outbox-messages/${messageId(7)}`, operation: 'getOutboxMessage', loading: 'Loading…' },
  { name: 'notification receipts', path: '/operations/notification-receipts', operation: 'listNotificationReceipts', loading: 'Loading…' },
] as const;

test.describe('Quality', () => {
  for (const view of views) {
    test(`the ${view.name} view has no WCAG 2.2 AA violations while loading, with data, when refused, and on error (FR-020, FR-021, SC-005)`, async ({
      page,
      api,
    }) => {
      api.signInAs('ClubAdmin');
      const release = await holdOperationsResponses(page);
      await page.goto(view.path);
      await expect(page.getByText('Loading…')).toBeVisible();
      await expectNoAxeViolations(page);
      release();
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await expect(page.getByText('Loading…')).toHaveCount(0);
      await expectNoAxeViolations(page);
      await page.unroute(operationsResponses);

      await failOperationsResponses(page);
      await page.reload();
      await expect(page.getByRole('alert')).toBeVisible();
      await expectNoAxeViolations(page);
      await page.unroute(operationsResponses);

      api.operationsSwitches.set(view.operation, 'forbidden');
      await page.reload();
      await expect(page.getByText(NOT_PERMITTED)).toBeVisible();
      await expectNoAxeViolations(page);
    });
  }

  test('the lists have no violations in their empty state (FR-021, SC-005)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    api.outboxMessages = [];
    api.notificationReceipts = [];
    await page.goto('/operations/outbox-messages');
    await expect(page.getByText('No outgoing messages yet')).toBeVisible();
    await expectNoAxeViolations(page);
    await page.goto('/operations/notification-receipts');
    await expect(page.getByText('No notification receipts yet')).toBeVisible();
    await expectNoAxeViolations(page);
  });

  test('every view, filter, paging control, and copy control works with the keyboard alone and shows focus (FR-020, SC-005)', async ({
    page,
    api,
    context,
  }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    api.signInAs('ClubAdmin');
    await page.goto('/operations');
    await expect(value(page, 'Running')).toBeVisible();
    await expectAllReachableByKeyboard(page, page.locator('main'));

    await page.goto('/operations/outbox-messages');
    await expect(rows(page)).toHaveCount(5);
    await expectAllReachableByKeyboard(page, page.locator('main'));
    const state = page.getByLabel('State');
    await state.focus();
    await page.keyboard.press('ArrowDown');
    await expect(page).toHaveURL(/\?state=/);
    await expectAllReachableByKeyboard(page, page.locator('main'));
    const clear = page.getByRole('button', { name: 'Clear filters' }).first();
    await clear.focus();
    await page.keyboard.press('Enter');
    await expect(page).not.toHaveURL(/state=/);
    await expect(rows(page)).toHaveCount(5);
    const next = page.getByRole('button', { name: 'Next page' });
    await expect(next).toBeEnabled();
    await next.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('link', { name: `Open message ${messageId(6)}` })).toBeVisible();
    const previous = page.getByRole('button', { name: 'Previous page' });
    await previous.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('link', { name: `Open message ${messageId(1)}` })).toBeVisible();

    const open = page.getByRole('link', { name: `Open message ${messageId(1)}` });
    await open.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 1, name: 'Outgoing message' })).toBeVisible();
    await expectAllReachableByKeyboard(page, page.locator('main'));
    const copy = page.getByRole('button', { name: `Copy ${messageId(1)}` });
    await copy.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('status').filter({ hasText: 'Copied' })).toBeVisible();
    await expectNoAxeViolations(page);

    await page.goto('/operations/notification-receipts');
    await expect(receiptRows(page)).toHaveCount(5);
    await expectAllReachableByKeyboard(page, page.locator('main'));
    await page.getByLabel('Outcome').focus();
    await page.keyboard.press('ArrowDown');
    await expect(page).toHaveURL(/\?outcome=/);
    await expectAllReachableByKeyboard(page, page.locator('main'));
    await page.getByRole('button', { name: 'Clear filters' }).first().focus();
    await page.keyboard.press('Enter');
    await expect(receiptRows(page)).toHaveCount(5);
    await expect(page.getByRole('button', { name: 'Next page' })).toBeEnabled();
    await page.getByRole('button', { name: 'Next page' }).focus();
    await page.keyboard.press('Enter');
    await expect(receiptRows(page).first()).toContainText(receiptId(6));
    await page.getByRole('button', { name: 'Previous page' }).focus();
    await page.keyboard.press('Enter');
    await expect(receiptRows(page).first()).toContainText(receiptId(1));
  });

  test('the reconciliation dialog is keyboard operable and has no violations in confirmation, progress, outcome, and refusal (FR-020, SC-005)', async ({
    page,
    api,
  }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations');
    await expect(value(page, 'Running')).toBeVisible();
    const trigger = page.getByRole('button', { name: 'Run reconciliation' });
    await trigger.focus();
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog', { name: 'Run reconciliation' });
    await expect(dialog).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);

    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    expect(api.reconciliationRequests).toBe(0);

    const release = await holdOperationsResponses(page);
    await trigger.focus();
    await page.keyboard.press('Enter');
    await dialog.getByRole('button', { name: 'Run reconciliation' }).focus();
    await page.keyboard.press('Enter');
    await expect(dialog.getByRole('status').filter({ hasText: 'Reconciliation is running' })).toBeVisible();
    await expectNoAxeViolations(page);
    release();
    await expect(dialog).toContainText('2 ready jobs and 1 notifications were queued again');
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);
    await dialog.getByRole('button', { name: 'Close' }).focus();
    await page.keyboard.press('Enter');
    await expect(dialog).toHaveCount(0);
    await page.unroute(operationsResponses);

    api.operationsSwitches.set('reconcileAnalysisPublication', 'unavailable');
    await trigger.focus();
    await page.keyboard.press('Enter');
    await dialog.getByRole('button', { name: 'Run reconciliation' }).click();
    await expect(dialog.getByRole('alert')).toContainText('The reconciliation did not complete');
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, dialog);
  });

  test('no operations response is left in Cache Storage, localStorage, sessionStorage, or IndexedDB (FR-019, SC-006)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations');
    await expect(value(page, 'Running')).toBeVisible();
    await page.getByRole('button', { name: 'Run reconciliation' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Run reconciliation' }).click();
    await expect(page.getByRole('dialog')).toContainText('queued again');
    await page.getByRole('button', { name: 'Close' }).click();
    await page.goto('/operations/outbox-messages?state=failed');
    await expect(rows(page)).toHaveCount(3);
    await page.getByRole('link', { name: `Open message ${messageId(7)}` }).click();
    await expect(value(page, 'Failure category')).toHaveText('transport-unavailable');
    await page.goto('/operations/notification-receipts');
    await expect(receiptRows(page)).toHaveCount(5);

    const stored = await operationsInBrowserStorage(page);
    expect(stored.cacheApi).toEqual([]);
    for (const marker of [messageId(7), receiptId(1), 'transport-unavailable', 'analysis.markup', 'profile-not-approved', 'unrecognized-reason']) {
      expect(stored.text, `storage holds ${marker}`).not.toContain(marker);
    }
  });

  test('offline, /operations and the confirmation show the connection-required state and send nothing (FR-019, SC-006)', async ({
    page,
    api,
    context,
  }) => {
    api.signInAs('ClubAdmin');
    await page.goto('/operations');
    await expect(value(page, 'Running')).toBeVisible();
    await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
    });
    await page.reload();
    await expect(value(page, 'Running')).toBeVisible();

    await page.getByRole('button', { name: 'Run reconciliation' }).click();
    const dialog = page.getByRole('dialog', { name: 'Run reconciliation' });
    await expect(dialog).toBeVisible();
    const before = api.requests.length;
    await context.setOffline(true);
    await page.evaluate(() => window.dispatchEvent(new Event('offline')));
    await expect(dialog.getByText(/A connection is required/)).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Run reconciliation' })).toBeDisabled();
    await dialog.getByRole('button', { name: 'Cancel' }).click();
    expect(api.reconciliationRequests).toBe(0);
    expect(api.requests.slice(before).filter((request) => request.path.startsWith('/operations'))).toEqual([]);

    const attempted: string[] = [];
    await page.route('**/api/v1/**', (route) => {
      attempted.push(new URL(route.request().url()).pathname);
      return route.abort('internetdisconnected');
    });
    await page.goto('/operations');
    await expect(page.getByText(/A connection is required/).first()).toBeVisible();
    await expect(value(page, 'Running')).toHaveCount(0);
    expect(attempted.filter((path) => path.startsWith('/api/v1/operations'))).toEqual([]);
    expect(api.reconciliationRequests).toBe(0);
  });
});
