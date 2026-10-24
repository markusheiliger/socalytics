import { expect, test as base } from '../support/fixtures';
import { archiveActiveSeasons } from '../support/platformState';
import { getSyntheticVideo, SYNTHETIC_VIDEO_FRAME_RATE } from '../support/syntheticVideo';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD]);
  },
});

const run = Date.now().toString(36);
const seasonName = `E2E evidence ${run}`;
const teamName = `E2E evidence team ${run}`;
const opponent = `E2E evidence opponent ${run}`;
const displayName = `E2E evidence camera ${run}`;
const FRAME_SECONDS = 1 / SYNTHETIC_VIDEO_FRAME_RATE;

// Frees the club's single active season for the next spec, even when the test fails.
test.afterEach(async () => {
  await archiveActiveSeasons();
});

function clockToSeconds(clock: string): number {
  const [minutes, rest] = clock.split(':');
  return Number(minutes) * 60 + Number(rest);
}

test('plays the evidence of a fact of an uploaded synthetic recording to the fact window (SC-001, SC-005; FR-024)', async ({ page }, testInfo) => {
  test.setTimeout(6 * 60 * 1000);
  const video = getSyntheticVideo();

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

  await page.getByRole('link', { name: 'New match' }).click();
  await page.getByLabel(/^Opponent/).fill(opponent);
  await page.getByLabel(/^Kickoff time/).fill('2031-05-17T15:30');
  await page.getByRole('button', { name: 'Create match' }).click();
  await expect(page.getByRole('heading', { level: 2, name: opponent })).toBeVisible();
  await expect(page.getByText('No recordings have been uploaded yet.')).toBeVisible({ timeout: 15_000 });

  await page.getByLabel('Video files').setInputFiles({ name: video.name, mimeType: video.mimeType, buffer: video.bytes() });
  await page.getByLabel(`Display name for ${video.name}`).fill(displayName);
  await page.getByRole('button', { name: 'Add to upload queue' }).click();

  const item = page.getByRole('list', { name: 'Upload queue' }).getByRole('listitem').filter({ hasText: displayName });
  await expect(item).toBeVisible();
  await item.getByLabel(/^Media end/).fill(`00:${String(video.durationSeconds).padStart(2, '0')}`);
  await item.getByLabel(/^Match time at media start/).fill('00:00');
  await item.getByRole('button', { name: `Save mapping for ${displayName}` }).click();
  await expect(item.getByRole('status')).toHaveText(/Completed/, { timeout: 2 * 60 * 1000 });
  await expect(page.getByRole('list', { name: 'Recording versions' }).getByRole('listitem')).toHaveCount(1, { timeout: 30_000 });

  await page.getByRole('link', { name: 'Finalize recordings' }).click();
  await page.getByLabel(`Include ${displayName}`).check();
  await page.getByRole('button', { name: 'Review and finalize' }).click();
  await page.getByRole('button', { name: 'Finalize', exact: true }).click();
  await expect(page).toHaveURL(/\/matches\/[0-9a-f-]+$/);

  const runs = page.getByRole('list', { name: 'Analysis runs' });
  await expect(async () => {
    await page.reload();
    await expect(runs.getByRole('link', { name: 'Open analysis review' }).first()).toBeVisible({ timeout: 5_000 });
  }).toPass({ timeout: 90_000, intervals: [2_000, 5_000] });
  await runs.getByRole('link', { name: 'Open analysis review' }).first().click();
  await expect(page.getByRole('heading', { level: 2, name: 'Analysis review' })).toBeVisible();

  // Facts exist only once a Manager has executed the run's jobs; the platform project starts none.
  const streams = page.getByRole('list', { name: 'Fact streams of the run' });
  await expect(streams).toBeVisible({ timeout: 15_000 });
  let play = page.getByRole('button', { name: /^Play evidence:/ }).first();
  const buttons = streams.getByRole('button', { name: /^Show facts of / });
  for (let i = 0; i < (await buttons.count()) && !(await play.isVisible()); i++) {
    await buttons.nth(i).click();
    await play.waitFor({ state: 'visible', timeout: 3_000 }).catch(() => undefined);
  }
  if (!(await play.isVisible())) {
    testInfo.annotations.push({ type: 'skipped', description: 'The run has no facts: no Manager executes jobs in the platform project.' });
    test.skip(true, 'The run has no facts: no Manager executes jobs in the platform project.');
  }

  await play.click();
  const dialog = page.getByRole('dialog');
  const title = (await dialog.getByRole('heading').first().textContent()) ?? '';
  const range = /(\d+:\d{2})(?:\.\d{3})?–(\d+:\d{2})(?:\.\d{3})?$/.exec(title);
  expect(range, `fact range in the dialog title "${title}"`).toBeTruthy();
  const factStart = clockToSeconds(range![1]);
  const factEnd = clockToSeconds(range![2]);

  const status = dialog.getByRole('status');
  await expect(status).toContainText('The fact is happening now.', { timeout: 60_000 });

  // The recording starts at match time 0:00, so media time equals match time within one frame.
  const position = await page.evaluate(() => {
    const playing = Array.from(document.querySelectorAll('video')).find((v) => v.currentSrc && !v.paused);
    return playing
      ? { time: playing.currentTime - (playing.seekable.length > 0 ? playing.seekable.start(0) : 0), src: playing.currentSrc }
      : undefined;
  });
  expect(position, 'a playing video element').toBeDefined();
  expect(position!.time).toBeGreaterThanOrEqual(factStart - FRAME_SECONDS);
  expect(position!.time).toBeLessThanOrEqual(factEnd + FRAME_SECONDS);

  // FR-022: the development object store grants no cross-origin access to the played media to another origin.
  for (const origin of ['https://other-origin.example', 'null']) {
    const response = await page.request.get(position!.src, { headers: { Origin: origin, Range: 'bytes=0-0' } });
    expect(response.status(), `media request with Origin ${origin}`).toBeLessThan(500);
    expect(response.headers()['access-control-allow-origin'], `Access-Control-Allow-Origin for Origin ${origin}`).toBeUndefined();
  }
});
