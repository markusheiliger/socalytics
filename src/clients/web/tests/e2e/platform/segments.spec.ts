import { execFile } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { request as playwrightRequest, type Locator, type Page } from '@playwright/test';
import { expect, test as base } from '../support/fixtures';
import { archiveActiveSeasons } from '../support/platformState';
import { MiB, writeTestVideo } from '../support/testFiles';

const run = promisify(execFile);

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const MEMBER_PASSWORD = 'E2e-Member-Password-0005!';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD, MEMBER_PASSWORD]);
  },
});

const stamp = Date.now().toString(36);
const seasonName = `E2E segments ${stamp}`;
const teamName = `E2E segments team ${stamp}`;
const opponent = `E2E segments opponent ${stamp}`;
const brokenName = `E2E undecodable ${stamp}`;
const validName = `E2E decodable ${stamp}`;
const viewerAccount = `e2e-segments-viewer-${stamp}`;

// Frees the club's single active season for the next spec, even when the test fails.
test.afterEach(async () => {
  await archiveActiveSeasons();
});

interface Row {
  state: string;
  failureReason: string;
  use: string;
}

// The columns are: recording version, segment number, state, size, failure reason, available since, use.
async function readRows(area: Locator): Promise<Row[]> {
  const texts = await area.locator('tbody tr').allInnerTexts();
  return texts.map((text) => {
    const cells = text.split('\t').map((cell) => cell.trim());
    return { state: cells[2] ?? '', failureReason: cells[4] ?? '', use: cells[6] ?? '' };
  });
}

const isPurgeCandidate = (row: Row) => row.state === 'Available' || row.state === 'Failed';

// The platform marks a segment of a job that is waiting, ready, or claimed as in use.
function expectedOutcome(rows: Row[]) {
  const candidates = rows.filter(isPurgeCandidate);
  const kept = candidates.filter((row) => row.use === 'In use').length;
  return `${candidates.length - kept} segments purged, ${kept} kept because they are in use.`;
}

// The area never refreshes by itself, so every wait clicks the refresh action.
async function refreshUntil(page: Page, area: Locator, predicate: (rows: Row[]) => boolean, timeout = 120_000) {
  await expect(async () => {
    await area.getByRole('button', { name: 'Refresh' }).click();
    await expect(area.getByRole('button', { name: 'Refresh' })).toBeEnabled({ timeout: 10_000 });
    expect(predicate(await readRows(area))).toBe(true);
  }).toPass({ timeout, intervals: [2_000, 3_000, 5_000] });
}

test('shows real failed and available segments and purges the unused ones (FR-019)', async ({ page, browser, baseURL }, testInfo) => {
  test.setTimeout(10 * 60 * 1000);

  const files = testInfo.outputPath('files');
  await mkdir(files, { recursive: true });
  const broken = await writeTestVideo(files, MiB, 'undecodable.mp4');
  const validPath = path.join(files, 'decodable.mp4');
  await run(process.env.SOCALYTICS_FFMPEG_PATH ?? 'ffmpeg', [
    '-y', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc=size=160x120:rate=10',
    '-t', '70', '-c:v', 'mpeg4', '-q:v', '12', '-an', validPath,
  ]);

  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await nav.getByRole('link', { name: 'Seasons' }).click();
  await page.getByLabel(/^Season name/).fill(seasonName);
  await page.getByRole('button', { name: 'Create season' }).click();
  const seasonItem = page.getByRole('listitem').filter({ hasText: seasonName });
  await expect(seasonItem).toContainText('Draft');
  await seasonItem.getByRole('link', { name: seasonName }).click();
  await page.getByRole('button', { name: 'Activate' }).click();
  await expect(page.getByText('State: Active')).toBeVisible();

  await nav.getByRole('link', { name: 'Teams' }).click();
  await page.getByLabel('Season', { exact: true }).selectOption({ label: `${seasonName} (Active)` });
  await page.getByLabel(/^Team name/).fill(teamName);
  await page.getByRole('button', { name: 'Create team' }).click();
  await expect(page.getByText('The team was created.')).toBeVisible();
  await page.getByRole('link', { name: teamName }).click({ timeout: 15_000 });
  await expect(page.getByRole('heading', { level: 2, name: teamName })).toBeVisible();
  const teamId = new URL(page.url()).pathname.split('/').pop()!;

  await page.getByRole('link', { name: 'New match' }).click();
  await page.getByLabel(/^Opponent/).fill(opponent);
  await page.getByLabel(/^Kickoff time/).fill('2031-05-17T15:30');
  await page.getByRole('button', { name: 'Create match' }).click();
  await expect(page.getByRole('heading', { level: 2, name: opponent })).toBeVisible();
  await expect(page.getByText('No recordings have been uploaded yet.')).toBeVisible({ timeout: 15_000 });
  const matchPath = new URL(page.url()).pathname;

  // Two recordings that do not overlap in match time, so each one requests its own segment.
  await page.getByLabel('Video files').setInputFiles([broken.path, validPath]);
  await page.getByLabel('Display name for undecodable.mp4').fill(brokenName);
  await page.getByLabel('Display name for decodable.mp4').fill(validName);
  await page.getByRole('button', { name: 'Add to upload queue' }).click();

  const queue = page.getByRole('list', { name: 'Upload queue' });
  for (const [name, matchStart] of [[brokenName, '10:00'], [validName, '20:00']]) {
    const item = queue.getByRole('listitem').filter({ hasText: name });
    await expect(item).toBeVisible();
    await item.getByLabel(/^Media end/).fill('01:00');
    await item.getByLabel(/^Match time at media start/).fill(matchStart);
    await item.getByRole('button', { name: `Save mapping for ${name}` }).click();
  }
  for (const name of [brokenName, validName]) {
    await expect(queue.getByRole('listitem').filter({ hasText: name }).getByRole('status')).toHaveText(/Completed/, {
      timeout: 4 * 60 * 1000,
    });
  }
  await expect(page.getByRole('list', { name: 'Recording versions' }).getByRole('listitem')).toHaveCount(2, { timeout: 30_000 });

  await page.getByRole('link', { name: 'Finalize recordings' }).click();
  await page.getByLabel(`Include ${brokenName}`).check();
  await page.getByLabel(`Include ${validName}`).check();
  await page.getByRole('button', { name: 'Review and finalize' }).click();
  await page.getByRole('button', { name: 'Finalize', exact: true }).click();
  await expect(page).toHaveURL(/\/matches\/[0-9a-f-]+$/);

  const area = page.getByRole('region', { name: 'Video segments' });
  const table = area.getByRole('table', { name: 'Materialized video segments of this match' });
  const runs = page.getByRole('list', { name: 'Analysis runs' });

  // The platform requests the segments asynchronously and the worker retries a failing one, so refresh with a bound.
  await expect(area).toBeVisible({ timeout: 30_000 });
  await expect(async () => {
    await page.reload();
    await expect(table).toBeVisible({ timeout: 5_000 });
  }).toPass({ timeout: 90_000, intervals: [2_000, 5_000] });
  await refreshUntil(page, area, (rows) => rows.some((r) => r.state === 'Failed') && rows.some((r) => r.state === 'Available'));

  const failed = table.locator('tbody tr').filter({ hasText: 'Failed' });
  await expect(failed).toHaveCount(1);
  const failedRow = (await readRows(area)).find((r) => r.state === 'Failed')!;
  expect(failedRow.failureReason).not.toBe('');
  expect(failedRow.failureReason).not.toBe('Not reported');

  // The available segment is pinned into the waiting job of the run, which no Manager completes: it is in use.
  const availableRow = table.locator('tbody tr').filter({ hasText: 'Available' });
  await expect(availableRow).toHaveCount(1);
  await expect(availableRow).toContainText('In use');
  await expect(availableRow).not.toContainText('Not in use');

  // The first purge keeps the in-use segment and reports it.
  const purgeButton = area.getByRole('button', { name: 'Purge unused segments' });
  const beforeFirst = await readRows(area);
  const firstOutcome = expectedOutcome(beforeFirst);
  if (beforeFirst.some((r) => isPurgeCandidate(r) && r.use !== 'In use')) {
    await purgeButton.click();
    const dialog = page.getByRole('dialog', { name: 'Purge the unused segments of this match?' });
    await dialog.getByRole('button', { name: 'Purge', exact: true }).click();
    await expect(dialog.getByRole('status')).toContainText(firstOutcome, { timeout: 30_000 });
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toBeHidden();
    await expect(purgeButton).toBeFocused();
  } else {
    await expect(area.getByText('Nothing to purge: every segment is in use or already absent.')).toBeVisible();
    expect(firstOutcome).toContain('0 segments purged, ');
  }
  const kept = (await readRows(area)).filter((r) => r.state === 'Available' && r.use === 'In use');
  expect(kept, 'the in-use available segment is kept by the purge').toHaveLength(1);

  // Cancelling the run makes its job terminal, so the segment is no longer in use.
  const runItem = runs.getByRole('listitem').first();
  await runItem.getByRole('button', { name: 'Cancel run' }).click();
  const cancel = page.getByRole('dialog', { name: 'Cancel this analysis run?' });
  await cancel.getByLabel('Reason').fill(`E2E segments ${stamp}`);
  await cancel.getByRole('button', { name: 'Cancel run' }).click();
  await expect(cancel).toBeHidden({ timeout: 15_000 });
  await expect(runItem.getByRole('status')).toContainText('Cancelled', { timeout: 15_000 });

  await refreshUntil(page, area, (rows) => rows.some((r) => r.state === 'Available' && r.use === 'Not in use'), 60_000);

  // A second, stale view of the same list lets the repeat purge be confirmed after the segments are gone.
  const stale = await page.context().newPage();
  await stale.goto(matchPath);
  const staleArea = stale.getByRole('region', { name: 'Video segments' });
  await expect(staleArea.getByRole('button', { name: 'Purge unused segments' })).toBeVisible({ timeout: 30_000 });

  const beforeSecond = await readRows(area);
  const secondOutcome = expectedOutcome(beforeSecond);
  expect(secondOutcome).not.toMatch(/^0 segments purged/);
  await purgeButton.click();
  const dialog = page.getByRole('dialog', { name: 'Purge the unused segments of this match?' });
  await dialog.getByRole('button', { name: 'Purge', exact: true }).click();
  await expect(dialog.getByRole('status')).toContainText(secondOutcome, { timeout: 30_000 });
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();

  await expect(async () => {
    const rows = await readRows(area);
    expect(rows.length).toBeGreaterThan(0);
    expect(rows.every((r) => r.state === 'Absent')).toBe(true);
  }).toPass({ timeout: 30_000 });
  await expect(area.getByText('Nothing to purge: every segment is in use or already absent.')).toBeVisible();
  await expect(purgeButton).toHaveCount(0);

  await staleArea.getByRole('button', { name: 'Purge unused segments' }).click();
  const staleDialog = stale.getByRole('dialog', { name: 'Purge the unused segments of this match?' });
  await staleDialog.getByRole('button', { name: 'Purge', exact: true }).click();
  await expect(staleDialog.getByRole('status')).toContainText('0 segments purged, 0 kept because they are in use.', { timeout: 30_000 });
  await staleDialog.getByRole('button', { name: 'Close' }).click();
  await stale.close();

  // A member without the Club Admin role sees no area at all.
  const api = await playwrightRequest.newContext({ baseURL });
  try {
    const signIn = await api.post('/api/v1/session', { data: { accountName: ADMIN_ACCOUNT, password: ADMIN_CHANGED_PASSWORD } });
    expect(signIn.ok(), `Club Admin sign-in answered ${signIn.status()}`).toBe(true);
    const { antiforgeryToken } = (await signIn.json()) as { antiforgeryToken: string };
    const cookie = signIn
      .headersArray()
      .filter((h) => h.name.toLowerCase() === 'set-cookie')
      .map((h) => h.value.split(';')[0])
      .find((c) => c.startsWith('__Host-socalytics-session='))!;
    const headers = { Cookie: cookie, 'X-CSRF-Token': antiforgeryToken };
    const created = await api.post('/api/v1/members', { data: { accountName: viewerAccount }, headers });
    expect(created.ok(), `Creating the member answered ${created.status()}`).toBe(true);
    const member = (await created.json()) as { member: { id: string }; setPasswordCredential: { credential: string } };
    const redeemContext = await playwrightRequest.newContext({ baseURL });
    const redeemed = await redeemContext.post('/api/v1/credentials/redeem', {
      data: { accountName: viewerAccount, credential: member.setPasswordCredential.credential, newPassword: MEMBER_PASSWORD },
    });
    await redeemContext.dispose();
    expect(redeemed.ok(), 'the member sets the password with the credential code').toBe(true);
    const role = await api.put(`/api/v1/members/${member.member.id}/team-roles/${teamId}`, { data: { role: 'viewer' }, headers });
    expect(role.ok(), `Assigning the role answered ${role.status()}`).toBe(true);
    await api.delete('/api/v1/session', { headers });
  } finally {
    await api.dispose();
  }

  const viewerContext = await browser.newContext({ baseURL });
  try {
    const viewer = await viewerContext.newPage();
    const segmentRequests: string[] = [];
    viewer.on('request', (request) => {
      if (request.url().includes('materialized-segments')) segmentRequests.push(request.url());
    });
    await viewer.goto('/');
    await viewer.getByLabel('Sign-in name').fill(viewerAccount);
    await viewer.getByLabel('Password').fill(MEMBER_PASSWORD);
    await viewer.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(viewer.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
    await viewer.goto(matchPath);
    await expect(viewer.getByRole('heading', { level: 2, name: opponent })).toBeVisible({ timeout: 15_000 });
    await expect(viewer.getByRole('heading', { name: 'Video segments' })).toHaveCount(0);
    expect(segmentRequests).toHaveLength(0);
  } finally {
    await viewerContext.close();
  }
});
