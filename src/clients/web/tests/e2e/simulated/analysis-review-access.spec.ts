import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard, expectAllReachableByKeyboard } from '../support/keyboard';
import {
  analysisRunFixture,
  match,
  problem,
  seededProvenance,
  sessionFor,
  team,
  type SeededAnalysisRun,
  type SeededFact,
  type SeededFactStream,
  type SimulatedApi,
} from '../support/simulatedApi';

const digest = (c: string) => `sha256:${c.repeat(64)}`;
const memberId = '00000000-0000-4000-8000-000000000201';
const factsRequest = /\/api\/v1\/analysis\/runs\/[^/]+\/facts\?/;

function seedRun(api: SimulatedApi, overrides: Partial<SeededAnalysisRun>): SeededAnalysisRun {
  const run = api.seedAnalysisRun(overrides);
  api.recordingSetVersions.push({
    id: run.recordingSetVersionId,
    matchId: run.matchId,
    teamId: run.teamId,
    finalizedAt: new Date(),
    members: [{ position: 0, recordingVersionId: memberId, recordingContentDigest: digest('1'), timelineMappingId: memberId, timelineMappingDigest: digest('b') }],
  } as never);
  return run;
}

const fact = (index: number): SeededFact => ({
  acceptedResultId: `00000000-0000-4000-8000-0000000007${String(index).padStart(2, '0')}`,
  ordinal: 0,
  kind: `pass-${index}`,
  startMs: index * 10_000,
  endMs: index * 10_000 + 10_000,
  confidence: 0.5 + index / 20,
  payload: { player: `p${index}`, detail: { x: index, y: 2 } },
});

function stream(overrides: Partial<SeededFactStream> = {}): SeededFactStream {
  const [base] = analysisRunFixture().streams;
  return { ...base, provenance: seededProvenance, ...overrides };
}

const reviewUrl = (runId: string, query = '') => `/matches/${match.id}/analysis-runs/${runId}${query}`;

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
});

test.describe('Access', () => {
  for (const role of ['ClubAdmin', 'Coach', 'Viewer'] as const) {
    test(`a ${role} of the team sees the review link and opens the address`, async ({ page, api }) => {
      api.signInAs(role === 'ClubAdmin' ? 'Coach' : role);
      if (role === 'ClubAdmin') {
        // The simulated platform authorizes analysis reads by team role; the session alone is a Club Admin's.
        const admin = (route: { fulfill: (o: object) => Promise<void> }) =>
          route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(sessionFor('ClubAdmin')) });
        api.on('GET', '/session', admin);
      }
      const run = seedRun(api, { streams: [stream({ facts: [fact(0)] })] });

      await page.goto(`/matches/${match.id}`);
      await activateWithKeyboard(page.getByRole('link', { name: 'Open analysis review' }));
      await expect(page).toHaveURL(new RegExp(`/matches/${match.id}/analysis-runs/${run.runId}$`));
      await expect(page.getByRole('region', { name: 'Run details' })).toBeVisible();

      await page.goto(reviewUrl(run.runId, '?stream=detections'));
      await expect(page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li')).toHaveCount(1);
    });
  }

  test('a member without a team role gets the not-permitted view and no analysis request', async ({ page, api }) => {
    api.signInAs('Registrar');
    const run = seedRun(api, { streams: [stream({ facts: [fact(0)] })] });
    await page.goto(reviewUrl(run.runId));

    await expect(page.getByText('You are not permitted to open this page.')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Run details' })).toHaveCount(0);
    expect(api.requests.filter((r) => r.path.startsWith('/analysis/'))).toEqual([]);
  });

  test('shows the not-found view at the next refresh after the Viewer loses the team role', async ({ page, api }) => {
    api.signInAs('Viewer');
    const run = seedRun(api, {
      state: 'Running',
      terminatedAt: null,
      streams: [stream({ facts: [fact(0)], coverage: [{ startMs: 0, endMs: 90_000, state: 'missing' }] })],
    });
    await page.goto(reviewUrl(run.runId, '?stream=detections'));
    await expect(page.getByRole('region', { name: 'Run details' })).toBeVisible();

    api.on('GET', `/analysis/runs/${run.runId}`, (route) =>
      route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify(problem(404, 'Not found')) }),
    );
    await expect(page.getByText('This page does not exist.')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByRole('region', { name: 'Run details' })).toHaveCount(0);
    await expect(page.getByRole('list', { name: 'Facts of the selected stream' })).toHaveCount(0);
  });
});

test.describe('Accessibility', () => {
  test.beforeEach(({ api }) => api.signInAs('Viewer'));

  test('has no WCAG 2.2 AA violations in the loading state', async ({ page, api }) => {
    api.factPageSize = 2;
    const run = seedRun(api, { streams: [stream({ facts: [fact(0)] })] });
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    await page.route(factsRequest, async (route) => {
      await gate;
      await route.fallback();
    });
    await page.goto(reviewUrl(run.runId, '?stream=detections'));
    await expect(page.getByText('Loading facts…')).toBeVisible();
    await expectNoAxeViolations(page);
    release();
    await expect(page.getByRole('list', { name: 'Facts of the selected stream' })).toBeVisible();
  });

  test('has no violations with facts, and every control is reachable by keyboard with visible focus', async ({ page, api }) => {
    api.factPageSize = 2;
    const run = seedRun(api, {
      streams: [
        stream({
          facts: [0, 1, 2, 3, 4].map(fact),
          coverage: [
            { startMs: 0, endMs: 30_000, state: 'accepted' },
            { startMs: 30_000, endMs: 90_000, state: 'missing' },
          ],
        }),
      ],
    });
    await page.goto(`/matches/${match.id}`);
    await activateWithKeyboard(page.getByRole('link', { name: 'Open analysis review' }));
    await activateWithKeyboard(page.getByRole('button', { name: 'Show facts of detections' }));
    const facts = page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li');
    await expect(facts).toHaveCount(2);
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, page.locator('main'));

    await page.getByRole('textbox', { name: 'From' }).focus();
    await page.getByRole('textbox', { name: 'From' }).fill('0:10');
    await page.getByRole('textbox', { name: 'To' }).fill('0:50');
    await activateWithKeyboard(page.getByRole('button', { name: 'Apply range' }));
    await expect(page).toHaveURL(new RegExp(`/analysis-runs/${run.runId}\\?stream=detections&fromMs=10000&toMs=50000$`));
    // The address changes before the filtered page renders, and the unfiltered page also has two facts. Without
    // these waits the test activated the control of the previous selection, which is replaced when the filtered
    // request returns (reproduced with a 5000 ms delay on that request: the list was unmounted while loading).
    await expect(facts.first()).toHaveAttribute('aria-label', /: pass-1$/);
    await expect(page.getByRole('button', { name: 'Next page' })).toBeEnabled();
    await expect(facts).toHaveCount(2);

    await activateWithKeyboard(page.getByRole('button', { name: 'Next page' }));
    await expect(facts).toHaveCount(2);
    await activateWithKeyboard(page.getByRole('button', { name: 'Previous page' }));
    await expect(facts.first()).toHaveAttribute('aria-label', /: pass-1$/);

    await activateWithKeyboard(facts.first().getByRole('button', { name: 'Show full payload' }));
    await expect(page.getByLabel('Full payload of the fact')).toBeVisible();
    await activateWithKeyboard(page.getByRole('button', { name: /Show full digest/ }).first());
    await expect(page.getByRole('group', { name: 'Full digest' }).first()).toBeVisible();
    await expectNoAxeViolations(page);
    await expectAllReachableByKeyboard(page, page.locator('main'));
  });

  test('has no violations in the empty, incomplete, capability-not-in-run, and error states', async ({ page, api }) => {
    const empty = seedRun(api, { streams: [stream()] });
    await page.goto(reviewUrl(empty.runId, '?stream=detections'));
    await expect(page.getByText('The analysis is complete and produced no facts.')).toBeVisible();
    await expectNoAxeViolations(page);

    const coverage: SeededFactStream['coverage'] = [
      { startMs: 0, endMs: 30_000, state: 'accepted' },
      { startMs: 30_000, endMs: 90_000, state: 'failed' },
    ];
    const incomplete = seedRun(api, { state: 'PartiallyCompleted', streams: [stream({ facts: [], coverage })] });
    await page.goto(reviewUrl(incomplete.runId, '?stream=detections'));
    await expect(page.getByText('The analysis is incomplete. Some windows are not accepted.')).toBeVisible();
    await expectNoAxeViolations(page);

    await page.goto(reviewUrl(empty.runId, '?stream=retired'));
    await expect(page.getByText('The chosen capability is not part of this run.')).toBeVisible();
    await expectNoAxeViolations(page);

    await page.route(factsRequest, (route) =>
      route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify(problem(500, 'Server error')) }),
    );
    await page.goto(reviewUrl(empty.runId, '?stream=detections'));
    await expect(page.getByRole('alert').filter({ hasText: 'The analysis could not be loaded.' })).toBeVisible();
    await expectNoAxeViolations(page);
  });
});

async function analysisInBrowserStorage(page: Page): Promise<{ cacheApi: string[]; text: string }> {
  return page.evaluate(async () => {
    const cacheApi: string[] = [];
    for (const name of await caches.keys()) {
      cacheApi.push(...(await (await caches.open(name)).keys()).map((request) => new URL(request.url).pathname).filter((p) => p.startsWith('/api/')));
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

test.describe('Online only', () => {
  test.beforeEach(({ api }) => api.signInAs('Viewer'));

  test('shows connection required without earlier analysis data and recovers when the connection returns', async ({ page, api, context }) => {
    api.factPageSize = 2;
    const run = seedRun(api, { streams: [stream({ facts: [0, 1, 2].map(fact) })] });
    await page.goto(reviewUrl(run.runId, '?stream=detections'));
    await expect(page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li')).toHaveCount(2);

    await context.setOffline(true);
    await expect(page.getByRole('alert').filter({ hasText: 'A connection is required to review analysis results.' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Run details' })).toHaveCount(0);
    await expect(page.getByRole('list', { name: 'Fact streams of the run' })).toHaveCount(0);
    await expect(page.getByRole('list', { name: 'Facts of the selected stream' })).toHaveCount(0);
    await expect(page.getByText('pass-0')).toHaveCount(0);
    await expectNoAxeViolations(page);

    await context.setOffline(false);
    await expect(page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li')).toHaveCount(2, { timeout: 15_000 });
  });

  test('leaves no analysis data in Cache Storage, localStorage, sessionStorage, or IndexedDB', async ({ page, api }) => {
    api.factPageSize = 2;
    const run = seedRun(api, { streams: [stream({ facts: [0, 1, 2].map(fact) })] });
    await page.goto(`/matches/${match.id}`);
    await page.getByRole('link', { name: 'Open analysis review' }).click();
    await page.getByRole('button', { name: 'Show facts of detections' }).click();
    await page.getByRole('button', { name: 'Next page' }).click();
    await page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li').first().getByRole('button', { name: 'Show full payload' }).click();
    await expect(page.getByLabel('Full payload of the fact')).toBeVisible();

    const stored = await analysisInBrowserStorage(page);
    expect(stored.cacheApi).toEqual([]);
    for (const marker of [run.runId, 'pass-', 'player-detection', digest('c').slice(7, 27), '"player"']) {
      expect(stored.text, `storage holds ${marker}`).not.toContain(marker);
    }
  });
});
