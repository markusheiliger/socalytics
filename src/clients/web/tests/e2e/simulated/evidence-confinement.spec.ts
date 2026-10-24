import type { Page } from '@playwright/test';
import { expect, test } from '../support/fixtures';
import {
  analysisRunFixture,
  FAKE_STORAGE_ORIGIN,
  match,
  team,
  type SeededAnalysisRun,
  type SimulatedApi,
} from '../support/simulatedApi';
import { getSyntheticVideo } from '../support/syntheticVideo';

const FACT = { acceptedResultId: '00000000-0000-4000-8000-000000000701', ordinal: 0, startMs: 30_000, endMs: 32_000 };
const RECORDING = '00000000-0000-4000-8000-000000000801';
const KEY = 'evidence/confined.mp4';
const OTHER_ORIGIN = 'http://127.0.0.1:4179';
const PLAY = /^Play evidence:/;

const control = (action: string, body: unknown = {}) =>
  fetch(`${FAKE_STORAGE_ORIGIN}/__control/${action}`, { method: 'POST', body: JSON.stringify(body) });

const mediaUrl = () => `${FAKE_STORAGE_ORIGIN}/${KEY}?Expires=${Math.floor(Date.now() / 1000) + 7200}&v=1`;

function scriptPlayableEvidence(api: SimulatedApi) {
  const video = getSyntheticVideo();
  api.scriptEvidence({
    resolution: {
        requested: { fromMs: 29_000, toMs: 33_000 },
        fact: { startMs: FACT.startMs, endMs: FACT.endMs },
        shortened: false,
        parts: [
          {
            startMs: 29_000,
            endMs: 33_000,
            state: 'available',
            media: {
              url: mediaUrl(),
              expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
              mediaType: 'video/mp4',
              mediaDigest: `sha-256:${'d'.repeat(64)}`,
              mediaSizeBytes: video.sizeBytes,
              startOffsetMs: 1000,
              endOffsetMs: 5000,
              recordingVersionId: RECORDING,
            },
          },
        ],
    },
  });
}

function seedRun(api: SimulatedApi): SeededAnalysisRun {
  const [base] = analysisRunFixture().streams;
  return api.seedAnalysisRun({
    streams: [{ ...base, facts: [{ ...FACT, kind: 'pass', confidence: 0.9, payload: { player: 'p1' } }] }],
  });
}

const runUrl = (run: SeededAnalysisRun) => `/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`;

async function collectViolations(page: Page) {
  await page.addInitScript(() => {
    const seen: string[] = [];
    (window as unknown as { __violations: string[] }).__violations = seen;
    document.addEventListener('securitypolicyviolation', (event) => seen.push(`${event.violatedDirective}: ${event.blockedURI}`));
  });
  return () => page.evaluate(() => (window as unknown as { __violations: string[] }).__violations);
}

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
  api.signInAs('Viewer');
  await control('reset');
  const video = getSyntheticVideo();
  await control('objects', { key: KEY, contentBase64: video.bytes().toString('base64'), contentType: video.mimeType });
});

test('a page of another origin gets no CORS access to the media while the Web UI origin plays it (FR-022)', async ({ page, context, api }) => {
  const run = seedRun(api);
  scriptPlayableEvidence(api);
  const violations = await collectViolations(page);
  await page.goto(runUrl(run));
  await page.getByRole('button', { name: PLAY }).click();
  await expect(page.getByRole('dialog').getByRole('status')).toContainText('Playing the evidence.');
  await expect(page.getByRole('dialog').getByRole('status')).toContainText('The evidence has ended.', { timeout: 15_000 });
  expect(await violations()).toEqual([]);

  await context.route(`${OTHER_ORIGIN}/**`, (route) => route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>other</title>' }));
  const other = await context.newPage();
  await other.goto(`${OTHER_ORIGIN}/`);
  const outcomes = await other.evaluate(async (url) => {
    const attempt = (init?: RequestInit) =>
      fetch(url, { mode: 'cors', ...init }).then(
        (response) => `readable:${response.status}`,
        () => 'blocked',
      );
    return { plain: await attempt(), ranged: await attempt({ headers: { Range: 'bytes=0-99' } }) };
  }, mediaUrl());
  expect(outcomes).toEqual({ plain: 'blocked', ranged: 'blocked' });

  const sameOrigin = await page.evaluate(async (url) => (await fetch(url, { headers: { Range: 'bytes=0-99' } })).status, mediaUrl());
  expect(sameOrigin).toBe(206);
  await other.close();
});

test('an offline browser shows the connection-required state on "Play evidence"', async ({ page, api }) => {
  const run = seedRun(api);
  api.scriptEvidence({ abort: true });
  await page.goto(runUrl(run));
  await page.getByRole('button', { name: PLAY }).click();
  await expect(page.getByText(/A connection is required/).first()).toBeVisible();
  await expect(page.getByText('Evidence video')).toHaveCount(0);
});

test('after a playback session no cache or browser storage holds media, links, or resolutions (SC-006; FR-023)', async ({ page, api }) => {
  const run = seedRun(api);
  scriptPlayableEvidence(api);
  const violations = await collectViolations(page);
  await page.goto(runUrl(run));
  await page.getByRole('button', { name: PLAY }).click();
  const status = page.getByRole('dialog').getByRole('status');
  await expect(status).toContainText('Playing the evidence.');
  await expect(status).toContainText('The evidence has ended.', { timeout: 15_000 });
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(await violations()).toEqual([]);

  const stored = await page.evaluate(async () => {
    const entries: string[] = [];
    for (const name of await caches.keys()) {
      const cache = await caches.open(name);
      for (const request of await cache.keys()) {
        entries.push(`cache:${name}:${request.url}`);
      }
    }
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
      for (const storeName of Array.from(db.objectStoreNames)) {
        const rows = await new Promise<unknown[]>((resolve, reject) => {
          const request = db.transaction(storeName, 'readonly').objectStore(storeName).getAll();
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        entries.push(`${info.name}/${storeName}=${JSON.stringify(rows)}`);
      }
      db.close();
    }
    return entries;
  });

  const mediaMaterial = /127\.0\.0\.1:4176|evidence\/confined|Expires=|mediaDigest|resolveAnalysisEvidenceMedia|evidence-media|startOffsetMs/i;
  expect(stored.filter((entry) => mediaMaterial.test(entry)).map((entry) => entry.slice(0, 80))).toEqual([]);
});
