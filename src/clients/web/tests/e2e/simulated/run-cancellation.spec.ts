import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard, expectAllReachableByKeyboard } from '../support/keyboard';
import { analysisRun, match, team, type SimulatedApi } from '../support/simulatedApi';

const digest = (c: string) => `sha256:${c.repeat(64)}`;
const id = (n: number) => `00000000-0000-4000-8000-0000000003${String(n).padStart(2, '0')}`;

function seed(api: SimulatedApi, state: 'Requested' | 'Running' | 'Completed' = 'Running') {
  api.recordingSetVersions.push({
    id: id(90),
    matchId: match.id,
    teamId: match.teamId,
    finalizedAt: new Date(),
    members: [{ position: 0, recordingVersionId: id(1), recordingContentDigest: digest('1'), timelineMappingId: id(71), timelineMappingDigest: digest('b') }],
  } as never);
  api.analysisRuns.push({ ...analysisRun, runId: id(31), recordingSetVersionId: id(90), state, runningAt: state === 'Running' ? new Date() : null });
}

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
});

const cancelButton = (page: import('@playwright/test').Page) => page.getByRole('button', { name: 'Cancel run', exact: true });

test('lets a Coach cancel a running run with a reason and shows it without a reload (FR-017–FR-020)', async ({ page, api }) => {
  seed(api);
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await expect(page.getByRole('status').filter({ hasText: 'State: Running' })).toBeVisible();
  await cancelButton(page).click();
  const dialog = page.getByRole('dialog', { name: 'Cancel this analysis run?' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Keep the run' })).toBeFocused();
  await expectNoAxeViolations(page);
  await dialog.getByLabel('Reason (optional)').fill('Wrong recording set');
  await dialog.getByRole('button', { name: 'Cancel run', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  const status = page.getByRole('status').filter({ hasText: 'Cancelled · by coach' });
  await expect(status).toContainText('Wrong recording set');
  await expect(cancelButton(page)).toHaveCount(0);
  expect(api.analysisRuns[0].state).toBe('Cancelled');
  expect(api.analysisRuns[0].cancellationReason).toBe('Wrong recording set');
  const request = api.requests.find((r) => r.path === `/analysis/runs/${id(31)}/cancellation`);
  expect(request?.method).toBe('POST');
  expect(request?.headers['if-match']).toContain('1');
  await expectNoAxeViolations(page);
});

test('cancels a requested run without a reason', async ({ page, api }) => {
  seed(api, 'Requested');
  api.signInAs('ClubAdmin');
  await page.goto(`/matches/${match.id}`);
  await cancelButton(page).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel run', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Cancelled · by admin' })).toBeVisible();
  expect(api.analysisRuns[0].cancellationReason).toBeNull();
});

test('offers a Viewer no cancel action', async ({ page, api }) => {
  seed(api);
  api.signInAs('Viewer');
  await page.goto(`/matches/${match.id}`);
  await expect(page.getByRole('status').filter({ hasText: 'State: Running' })).toBeVisible();
  await expect(cancelButton(page)).toHaveCount(0);
});

test('offers no cancel action for a finished run', async ({ page, api }) => {
  seed(api, 'Completed');
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await expect(page.getByRole('status').filter({ hasText: 'State: Completed' })).toBeVisible();
  await expect(cancelButton(page)).toHaveCount(0);
});

test('changes nothing when the dialog is dismissed', async ({ page, api }) => {
  seed(api);
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await cancelButton(page).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Keep the run' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('status').filter({ hasText: 'State: Running' })).toBeVisible();
  await cancelButton(page).click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(api.requests.some((r) => r.path.endsWith('/cancellation'))).toBe(false);
  expect(api.analysisRuns[0].state).toBe('Running');
});

for (const [status, version] of [
  [412, 1],
  [409, 1],
] as const) {
  test(`shows the conflict message and refreshes the list after a ${status}`, async ({ page, api }) => {
    seed(api);
    api.signInAs('Coach');
    api.on('POST', `/analysis/runs/${id(31)}/cancellation`, (route) => {
      // Someone else completed the run in the meantime.
      api.analysisRuns[0].state = 'Completed';
      api.analysisRuns[0].version = version + 1;
      return route.fulfill({
        status,
        contentType: 'application/problem+json',
        body: JSON.stringify({ type: 'about:blank', title: 'Conflict', status, code: status === 409 ? 'analysis-run-terminal' : undefined }),
      });
    });
    await page.goto(`/matches/${match.id}`);
    await cancelButton(page).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel run', exact: true }).click();
    await expect(page.getByText(/This item was changed by someone else/)).toBeVisible();
    await expect(page.getByRole('status').filter({ hasText: 'State: Completed' })).toBeVisible();
    await expect(cancelButton(page)).toHaveCount(0);
  });
}

test('supports the keyboard flow and has no accessibility violations (FR-018)', async ({ page, api }) => {
  seed(api);
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  const trigger = cancelButton(page);
  await expect(trigger).toBeVisible();
  await activateWithKeyboard(trigger);
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('button', { name: 'Keep the run' })).toBeFocused();
  await expectAllReachableByKeyboard(page, dialog);
  await expectNoAxeViolations(page);
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(trigger).toBeFocused();
  await activateWithKeyboard(trigger);
  await dialog.getByLabel('Reason (optional)').fill('Stop');
  await dialog.getByRole('button', { name: 'Cancel run', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('status').filter({ hasText: 'Cancelled · by coach' })).toBeVisible();
  await expectNoAxeViolations(page);
});
