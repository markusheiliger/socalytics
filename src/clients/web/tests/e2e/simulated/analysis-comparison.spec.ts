import type { Locator, Page } from '@playwright/test';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard } from '../support/keyboard';
import {
  analysisRunFixture,
  match,
  problem,
  sessionFor,
  team,
  type SeededAnalysisRun,
  type SimulatedApi,
} from '../support/simulatedApi';

const comparisonPath = `/matches/${match.id}/analysis-comparison`;
const reviewPath = (runId: string) => `/matches/${match.id}/analysis-runs/${runId}`;
const runsListPath = `/matches/${match.id}/analysis-runs`;

/** Seeds a run together with its exact recording set; runs that share `recordingSetVersionId` share one set. */
function seedRun(api: SimulatedApi, overrides: Partial<SeededAnalysisRun> = {}): SeededAnalysisRun {
  const run = api.seedAnalysisRun(overrides);
  if (!api.recordingSetVersions.some((set) => set.id === run.recordingSetVersionId)) api.seedRecordingSet(run);
  return run;
}

/** A current run plus one superseded terminal run of an older recording set. */
function seedCurrentAndSuperseded(api: SimulatedApi) {
  const current = seedRun(api, { isCurrent: true });
  const superseded = seedRun(api, {
    isCurrent: false,
    supersededBy: {
      runId: current.runId,
      recordingSetVersionId: current.recordingSetVersionId,
      recordingSetFinalizedAt: new Date(),
    },
    supersededAt: new Date(),
  });
  return { current, superseded };
}

const side = (page: Page, name: 'Left run' | 'Right run') => page.getByRole('region', { name, exact: true });
const field = (scope: Locator, name: string) =>
  scope.locator('dt', { hasText: new RegExp(`^${name}$`) }).locator('xpath=following-sibling::dd');
const reviewLink = (page: Page, name: 'Left run' | 'Right run') =>
  page.getByRole('link', { name: `Open the detailed review of the ${name}` });

async function choose(page: Page, left: string, right: string) {
  await page.getByLabel('Left run').selectOption({ value: left });
  await page.getByLabel('Right run').selectOption({ value: right });
  await activateWithKeyboard(page.getByRole('button', { name: 'Compare the runs' }));
}

async function expectIdentities(page: Page, left: SeededAnalysisRun, right: SeededAnalysisRun) {
  await expect(page).toHaveURL(new RegExp(`left=${left.runId}&right=${right.runId}$`));
  await expect(field(side(page, 'Left run'), 'Run ID')).toHaveText(left.runId);
  await expect(field(side(page, 'Right run'), 'Run ID')).toHaveText(right.runId);
  await expect(field(side(page, 'Left run'), 'Recording set')).toHaveText(left.recordingSetVersionId);
  await expect(field(side(page, 'Right run'), 'Recording set')).toHaveText(right.recordingSetVersionId);
  await expect(reviewLink(page, 'Left run')).toHaveAttribute('href', reviewPath(left.runId));
  await expect(reviewLink(page, 'Right run')).toHaveAttribute('href', reviewPath(right.runId));
}

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
});

test.describe('Selection and navigation', () => {
  for (const role of ['ClubAdmin', 'Coach', 'Viewer'] as const) {
    test(`a ${role} enters from the match, compares two runs, and follows both exact review links`, async ({ page, api }) => {
      // The simulated platform authorizes analysis reads by team role; the session alone is a Club Admin's.
      api.signInAs(role === 'ClubAdmin' ? 'Coach' : role);
      if (role === 'ClubAdmin') {
        api.on('GET', '/session', (route) =>
          route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(sessionFor('ClubAdmin')) }),
        );
      }
      const { current, superseded } = seedCurrentAndSuperseded(api);

      await page.goto(`/matches/${match.id}`);
      await activateWithKeyboard(page.getByRole('link', { name: 'Compare analysis runs' }));
      await expect(page).toHaveURL(new RegExp(`${comparisonPath}$`));
      await expect(page.getByRole('region', { name: 'Comparison', exact: true })).toHaveCount(0);
      await expect(page.getByLabel('Left run')).toHaveValue('');
      await expect(page.getByLabel('Right run')).toHaveValue('');

      await choose(page, superseded.runId, current.runId);
      await expectIdentities(page, superseded, current);

      await activateWithKeyboard(reviewLink(page, 'Left run'));
      await expect(page).toHaveURL(new RegExp(`${reviewPath(superseded.runId)}$`));
      await expect(page.getByRole('region', { name: 'Run details' })).toBeVisible();
      await page.goBack();
      await expectIdentities(page, superseded, current);

      await activateWithKeyboard(reviewLink(page, 'Right run'));
      await expect(page).toHaveURL(new RegExp(`${reviewPath(current.runId)}$`));
      await expect(page.getByRole('region', { name: 'Run details' })).toBeVisible();
    });

    test(`a ${role} enters from a terminal review with the left run fixed and chooses the right run`, async ({ page, api }) => {
      api.signInAs(role === 'ClubAdmin' ? 'Coach' : role);
      if (role === 'ClubAdmin') {
        api.on('GET', '/session', (route) =>
          route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(sessionFor('ClubAdmin')) }),
        );
      }
      const { current, superseded } = seedCurrentAndSuperseded(api);

      await page.goto(reviewPath(superseded.runId));
      await activateWithKeyboard(page.getByRole('link', { name: 'Compare this run with another run' }));
      await expect(page).toHaveURL(new RegExp(`${comparisonPath}\\?left=${superseded.runId}$`));
      await expect(page.getByLabel('Left run')).toHaveValue(superseded.runId);
      await expect(page.getByLabel('Right run')).toHaveValue('');
      await expect(page.getByRole('region', { name: 'Comparison', exact: true })).toHaveCount(0);

      await page.getByLabel('Right run').selectOption({ value: current.runId });
      await activateWithKeyboard(page.getByRole('button', { name: 'Compare the runs' }));
      await expectIdentities(page, superseded, current);

      await activateWithKeyboard(reviewLink(page, 'Right run'));
      await expect(page).toHaveURL(new RegExp(`${reviewPath(current.runId)}$`));
      await activateWithKeyboard(page.getByRole('link', { name: 'Compare this run with another run' }));
      await expect(page).toHaveURL(new RegExp(`${comparisonPath}\\?left=${current.runId}$`));
      await expect(page.getByLabel('Left run')).toHaveValue(current.runId);
    });
  }

  test('keeps current and superseded attribution distinct for each side', async ({ page, api }) => {
    api.signInAs('Viewer');
    const { current, superseded } = seedCurrentAndSuperseded(api);

    await page.goto(`${comparisonPath}?left=${superseded.runId}&right=${current.runId}`);
    const left = side(page, 'Left run');
    const right = side(page, 'Right run');
    await expect(field(left, 'Run ID')).toHaveText(superseded.runId);
    await expect(field(left, 'Current run')).toHaveText('No');
    await expect(field(left, 'Superseded by')).toContainText(current.runId);
    await expect(field(left, 'Superseded by')).toContainText(current.recordingSetVersionId);
    await expect(field(left, 'Superseded at')).toBeVisible();
    await expect(field(left, 'Recording set')).toHaveText(superseded.recordingSetVersionId);

    await expect(field(right, 'Run ID')).toHaveText(current.runId);
    await expect(field(right, 'Current run')).toHaveText('Yes');
    await expect(field(right, 'Superseded by')).toHaveCount(0);
    await expect(field(right, 'Recording set')).toHaveText(current.recordingSetVersionId);

    // The same pair in the other order keeps each identity with its own side.
    await page.goto(`${comparisonPath}?left=${current.runId}&right=${superseded.runId}`);
    await expect(field(side(page, 'Left run'), 'Run ID')).toHaveText(current.runId);
    await expect(field(side(page, 'Left run'), 'Current run')).toHaveText('Yes');
    await expect(field(side(page, 'Right run'), 'Run ID')).toHaveText(superseded.runId);
    await expect(field(side(page, 'Right run'), 'Superseded by')).toContainText(current.runId);
  });

  test('keeps two runs of the same recording set distinct', async ({ page, api }) => {
    api.signInAs('Coach');
    const first = seedRun(api);
    const [base] = analysisRunFixture().streams;
    const second = seedRun(api, {
      recordingSetVersionId: first.recordingSetVersionId,
      workflowVersion: '2',
      streams: [{ ...base, provenance: { ...base.provenance, workflow: { id: 'default', version: '2' } } }],
    });

    await page.goto(comparisonPath);
    await choose(page, first.runId, second.runId);
    await expectIdentities(page, first, second);
    await expect(field(side(page, 'Left run'), 'Recording set')).toHaveText(first.recordingSetVersionId);
    await expect(field(side(page, 'Right run'), 'Recording set')).toHaveText(first.recordingSetVersionId);
    await expect(field(side(page, 'Left run'), 'Workflow').first()).toContainText('default 1');
    await expect(field(side(page, 'Right run'), 'Workflow').first()).toContainText('default 2');
    await expect(field(side(page, 'Left run'), 'History')).toHaveText('Not the current run and not superseded');
    await expect(field(side(page, 'Right run'), 'History')).toHaveText('Not the current run and not superseded');
  });

  test('offers two older terminal runs while the current run is still running', async ({ page, api }) => {
    api.signInAs('Viewer');
    const olderA = seedRun(api);
    const olderB = seedRun(api);
    const running = seedRun(api, { state: 'Running', terminatedAt: null, isCurrent: true });

    await page.goto(comparisonPath);
    const left = page.getByLabel('Left run');
    await expect(left.locator(`option[value="${olderA.runId}"]`)).toHaveCount(1);
    await expect(left.locator(`option[value="${olderB.runId}"]`)).toHaveCount(1);
    await expect(left.locator(`option[value="${running.runId}"]`)).toHaveCount(0);
    await expect(page.getByText('All 3 run(s) of this match are loaded.')).toBeVisible();

    await choose(page, olderA.runId, olderB.runId);
    await expectIdentities(page, olderA, olderB);
    await expect(field(side(page, 'Left run'), 'Current run')).toHaveText('No');
    await expect(field(side(page, 'Right run'), 'Current run')).toHaveText('No');
  });

  test('does not invent a pair when fewer than two runs are eligible', async ({ page, api }) => {
    api.signInAs('Viewer');
    const only = seedRun(api);
    seedRun(api, { state: 'Running', terminatedAt: null, isCurrent: true });

    await page.goto(comparisonPath);
    await expect(
      page.getByText('All runs of this match are loaded, and 1 of them can be compared. Two finished runs are needed.'),
    ).toBeVisible();
    await expect(page.getByLabel('Left run')).toHaveValue('');
    await expect(page.getByLabel('Right run')).toHaveValue('');
    await expect(page.getByLabel('Left run').locator('option')).toHaveCount(2);
    await expect(page.getByLabel('Left run').locator(`option[value="${only.runId}"]`)).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Compare the runs' })).toBeDisabled();
    await expect(page.getByRole('region', { name: 'Comparison', exact: true })).toHaveCount(0);
  });

  test('reaches older runs beyond twenty pages with load-more and never preselects a pair', async ({ page, api }) => {
    api.signInAs('Viewer');
    api.analysisRunPageSize = 1;
    // The list is newest first as seeded here: many unfinished runs, then the two older finished ones.
    for (let i = 0; i < 22; i++) seedRun(api, { state: 'Running', terminatedAt: null });
    const olderA = seedRun(api);
    const olderB = seedRun(api);
    const listReads = () => api.requests.filter((r) => r.path === runsListPath).length;

    await page.goto(comparisonPath);
    await expect(page.getByText('1 run(s) loaded, 0 of them finished.')).toBeVisible();
    await expect(page.getByText(/Two finished runs are needed/)).toHaveCount(0);
    await expect(page.getByLabel('Left run')).toHaveValue('');
    await expect(page.getByLabel('Right run')).toHaveValue('');
    expect(listReads()).toBe(1);

    for (let pages = 1; pages < 24; pages++) {
      await page.getByRole('button', { name: 'Load older runs' }).click();
      await expect.poll(listReads).toBe(pages + 1);
    }
    await expect(page.getByText('All 24 run(s) of this match are loaded.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Load older runs' })).toHaveCount(0);
    await expect(page.getByLabel('Left run')).toHaveValue('');

    await choose(page, olderA.runId, olderB.runId);
    await expectIdentities(page, olderA, olderB);
  });

  test('shows a paging failure, keeps the loaded runs, and continues after a retry', async ({ page, api }) => {
    api.signInAs('Viewer');
    api.analysisRunPageSize = 1;
    seedRun(api, { state: 'Running', terminatedAt: null });
    const olderA = seedRun(api);
    const olderB = seedRun(api);

    let reads = 0;
    await page.route(`**/api/v1${runsListPath}*`, (route) => {
      reads += 1;
      if (reads !== 2) return route.fallback();
      return route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify(problem(500, 'Simulated failure')) });
    });

    await page.goto(comparisonPath);
    await expect(page.getByText('1 run(s) loaded, 0 of them finished.')).toBeVisible();
    await page.getByRole('button', { name: 'Load older runs' }).click();
    await expect(page.getByRole('button', { name: 'Retry loading runs' })).toBeVisible();
    await expect(page.getByLabel('Left run').locator(`option[value="${olderA.runId}"]`)).toHaveCount(0);
    await expect(page.getByText(/Two finished runs are needed/)).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Compare the runs' })).toBeDisabled();

    await page.getByRole('button', { name: 'Retry loading runs' }).click();
    await expect(page.getByLabel('Left run').locator(`option[value="${olderA.runId}"]`)).toHaveCount(1);
    await page.getByRole('button', { name: 'Load older runs' }).click();
    await expect(page.getByText('All 3 run(s) of this match are loaded.')).toBeVisible();

    await choose(page, olderA.runId, olderB.runId);
    await expectIdentities(page, olderA, olderB);
  });
});
