import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard } from '../support/keyboard';
import {
  analysisRunFixture,
  FAKE_STORAGE_ORIGIN,
  match,
  team,
  type SeededAnalysisRun,
  type SimulatedApi,
  type SimulatedEvidencePart,
} from '../support/simulatedApi';
import { getSyntheticVideo } from '../support/syntheticVideo';

const FACT = { acceptedResultId: '00000000-0000-4000-8000-000000000701', ordinal: 0, startMs: 30_000, endMs: 32_000 };
const REQUESTED = { fromMs: 29_000, toMs: 33_000 };
const RECORDING = '00000000-0000-4000-8000-000000000801';
const PLAY = /^Play evidence:/;

const control = (action: string, body: unknown = {}) =>
  fetch(`${FAKE_STORAGE_ORIGIN}/__control/${action}`, { method: 'POST', body: JSON.stringify(body) });

async function seedMedia(key: string): Promise<void> {
  const video = getSyntheticVideo();
  await control('objects', { key, contentBase64: video.bytes().toString('base64'), contentType: video.mimeType });
}

/** A link into the fake store; `Expires` is the expiry the store enforces, `expiresAt` the one the platform reports. */
function available(
  startMs: number,
  endMs: number,
  key: string,
  offsets: [number, number],
  options: { expiresInMs?: number; version?: number } = {},
): SimulatedEvidencePart {
  const expiresInMs = options.expiresInMs ?? 3_600_000;
  const expiresAt = new Date(Date.now() + expiresInMs);
  const video = getSyntheticVideo();
  return {
    startMs,
    endMs,
    state: 'available',
    media: {
      url: `${FAKE_STORAGE_ORIGIN}/${key}?Expires=${Math.floor(expiresAt.getTime() / 1000) + 3600}&v=${options.version ?? 1}`,
      expiresAt: expiresAt.toISOString(),
      mediaType: 'video/mp4',
      mediaDigest: `sha-256:${'d'.repeat(64)}`,
      mediaSizeBytes: video.sizeBytes,
      startOffsetMs: offsets[0],
      endOffsetMs: offsets[1],
      recordingVersionId: RECORDING,
    },
  };
}

const resolution = (parts: SimulatedEvidencePart[], shortened = false) => ({
  resolution: { requested: REQUESTED, fact: { startMs: FACT.startMs, endMs: FACT.endMs }, shortened, parts },
});

function seedRun(api: SimulatedApi): SeededAnalysisRun {
  const [base] = analysisRunFixture().streams;
  return api.seedAnalysisRun({
    streams: [{ ...base, facts: [{ ...FACT, kind: 'pass', confidence: 0.9, payload: { player: 'p1' } }] }],
  });
}

async function openPlayer(page: Page, run: SeededAnalysisRun) {
  await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`);
  await page.getByRole('button', { name: PLAY }).click();
  return { dialog: page.getByRole('dialog'), status: page.getByRole('dialog').getByRole('status') };
}

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
  api.signInAs('Viewer');
  await control('reset');
  await seedMedia('evidence/a.mp4');
  await seedMedia('evidence/b.mp4');
});

test('plays a fact inside one segment with context, asking for 5 seconds on each side (scenario 1)', async ({ page, api }) => {
  const run = seedRun(api);
  api.scriptEvidence(resolution([available(29_000, 33_000, 'evidence/a.mp4', [1000, 5000])]));
  const { dialog, status } = await openPlayer(page, run);

  await expect(dialog).toContainText('Evidence: player-detection, 0:30–0:32');
  await expect(status).toContainText('Playing the evidence.');
  await expect(status).toContainText('The fact is happening now.');
  await expectNoAxeViolations(page);
  expect(api.evidenceRequests[0].body).toMatchObject({ fact: { acceptedResultId: FACT.acceptedResultId, ordinal: 0, contextBeforeMs: 5000, contextAfterMs: 5000 } });
  expect(api.evidenceRequests[0].body.retryFailed).toBeUndefined();
  await expect(status).toContainText('The evidence has ended.', { timeout: 15_000 });
  await expectNoAxeViolations(page);
});

test('plays a fact across two segments without member action (scenario 2)', async ({ page, api }) => {
  const run = seedRun(api);
  api.scriptEvidence(
    resolution([available(29_000, 31_000, 'evidence/a.mp4', [1000, 3000]), available(31_000, 33_000, 'evidence/b.mp4', [5000, 7000])]),
  );
  const { status } = await openPlayer(page, run);

  await expect(status).toContainText('Playing the evidence.');
  await expect(status).toContainText('The evidence has ended.', { timeout: 20_000 });
  const sources = await page.locator('video').evaluateAll((videos) => videos.map((v) => (v as HTMLVideoElement).currentSrc));
  expect(api.evidenceRequests).toHaveLength(1);
  expect(sources.length).toBe(2);
});

test('prepares a segment and plays it once available (scenario 3)', async ({ page, api }) => {
  const run = seedRun(api);
  api.scriptEvidence(
    resolution([{ startMs: 29_000, endMs: 33_000, state: 'materializing', retryAfterSeconds: 1 }]),
    resolution([available(29_000, 33_000, 'evidence/a.mp4', [1000, 5000])]),
  );
  const { status } = await openPlayer(page, run);

  await expect(status).toContainText('Preparing the video for 0:29–0:33. Next automatic attempt in 1 seconds.');
  await expectNoAxeViolations(page);
  await expect(status).toContainText('Playing the evidence.', { timeout: 10_000 });
  expect(api.evidenceRequests).toHaveLength(2);
});

test('explains failed and not-covered parts, continues past them, and retries failed parts only on request (scenario 4)', async ({ page, api }) => {
  const run = seedRun(api);
  const parts = [
    { startMs: 29_000, endMs: 30_000, state: 'failed', reason: 'segment-media-invalid' },
    { startMs: 30_000, endMs: 31_000, state: 'not-covered' },
    available(31_000, 33_000, 'evidence/b.mp4', [5000, 7000]),
  ];
  api.scriptEvidence(resolution(parts));
  const { dialog, status } = await openPlayer(page, run);

  await expect(status).toContainText('The video for 0:29–0:30 could not be prepared (segment-media-invalid).');
  await expectNoAxeViolations(page);
  await dialog.getByRole('button', { name: 'Try again' }).click();
  await expect.poll(() => api.evidenceRequests.length).toBe(2);
  expect(api.evidenceRequests[1].body.retryFailed).toBe(true);
  await expect(status).toContainText('could not be prepared');

  await dialog.getByRole('button', { name: 'Continue with the next playable part' }).click();
  await expect(status).toContainText('No recording covers 0:30–0:31.');
  await expect(dialog.getByRole('button', { name: 'Try again' })).toHaveCount(0);
  await expectNoAxeViolations(page);

  await dialog.getByRole('button', { name: 'Continue with the next playable part' }).click();
  await expect(status).toContainText('Playing the evidence.');
});

test('shows the shortened notice with the covered window (scenario 5)', async ({ page, api }) => {
  const run = seedRun(api);
  api.scriptEvidence(resolution([available(29_000, 33_000, 'evidence/a.mp4', [1000, 5000])], true));
  const { status } = await openPlayer(page, run);

  await expect(status).toContainText('The evidence was shortened to the covered window 0:29–0:33.');
  await expectNoAxeViolations(page);
});

test('renews a link that expires during playback and keeps playing (scenario 6)', async ({ page, api }) => {
  const run = seedRun(api);
  api.scriptEvidence(
    resolution([available(29_000, 33_000, 'evidence/a.mp4', [1000, 5000], { expiresInMs: 31_500 })]),
    resolution([available(29_000, 33_000, 'evidence/a.mp4', [1000, 5000], { version: 2 })]),
  );
  const { status } = await openPlayer(page, run);

  await expect.poll(() => api.evidenceRequests.length, { timeout: 10_000 }).toBe(2);
  await expect(status).toContainText('The evidence has ended.', { timeout: 20_000 });
  expect(api.evidenceRequests).toHaveLength(2);
});

test('re-resolves a link that stops working before its expiry and reports the segment as rematerializing (scenario 7)', async ({ page, api }) => {
  const run = seedRun(api);
  api.scriptEvidence(
    resolution([available(29_000, 33_000, 'evidence/purged.mp4', [1000, 5000])]),
    resolution([{ startMs: 29_000, endMs: 33_000, state: 'rematerializing', retryAfterSeconds: 60 }]),
  );
  const { status } = await openPlayer(page, run);

  await expect(status).toContainText('Preparing the video for 0:29–0:33.', { timeout: 15_000 });
  expect(api.evidenceRequests).toHaveLength(2);
  await expectNoAxeViolations(page);
});

test.describe('Refused resolutions', () => {
  test('explains a fact that was not found', async ({ page, api }) => {
    const run = seedRun(api);
    api.scriptEvidence({ status: 404 });
    const { dialog, status } = await openPlayer(page, run);

    await expect(status).toContainText('The evidence of this fact was not found.');
    await expect(dialog.getByRole('button', { name: 'Try again' })).toHaveCount(0);
    await expectNoAxeViolations(page);
  });

  test('explains rate limiting and offers Try again after the delay', async ({ page, api }) => {
    const run = seedRun(api);
    api.scriptEvidence({ status: 429, retryAfterSeconds: 1 }, resolution([available(29_000, 33_000, 'evidence/a.mp4', [1000, 5000])]));
    const { dialog, status } = await openPlayer(page, run);

    await expect(status).toContainText('Too many requests. Try again in 1 seconds.');
    await expectNoAxeViolations(page);
    await dialog.getByRole('button', { name: 'Try again' }).click();
    await expect(status).toContainText('Playing the evidence.');
  });
});

test('opens and closes by keyboard, keeps focus inside, and returns it to the button (FR-021)', async ({ page, api }) => {
  const run = seedRun(api);
  api.scriptEvidence(resolution([{ startMs: 29_000, endMs: 33_000, state: 'materializing', retryAfterSeconds: 60 }]));
  await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`);
  const button = page.getByRole('button', { name: PLAY });
  await activateWithKeyboard(button);

  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  for (let i = 0; i < 4; i++) {
    await page.keyboard.press('Tab');
    expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  }
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(button).toBeFocused();
});
