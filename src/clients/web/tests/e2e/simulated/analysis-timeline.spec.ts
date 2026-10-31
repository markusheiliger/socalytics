import { expect, test } from '../support/fixtures';
import {
  coverageMatchEndMs,
  match,
  team,
  timelineRunFixture,
  type SeededAnalysisRun,
  type SeededFact,
  type SimulatedApi,
} from '../support/simulatedApi';

const digest = (c: string) => `sha256:${c.repeat(64)}`;
const memberId = '00000000-0000-4000-8000-000000000201';

const firstLane = 'Timeline of pass-detection 1.0.0 (passes-first-half)';
const secondLane = 'Timeline of pass-detection 1.0.0 (passes-second-half)';
const shotsLane = 'Timeline of shot-detection 1.0.0 (shots)';

/** Registers the finalized recording set the match view lists the seeded run under. */
function registerRecordingSet(api: SimulatedApi, run: SeededAnalysisRun): void {
  api.recordingSetVersions.push({
    id: run.recordingSetVersionId,
    matchId: run.matchId,
    teamId: run.teamId,
    finalizedAt: new Date(),
    members: [{ position: 0, recordingVersionId: memberId, recordingContentDigest: digest('1'), timelineMappingId: memberId, timelineMappingDigest: digest('b') }],
  } as never);
}

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
  api.signInAs('Viewer');
});

function binGroup(lane: ReturnType<import('@playwright/test').Page['getByRole']>) {
  return lane.getByRole('group', { name: /^Bins of/ });
}

test.describe('Timeline density', () => {
  test('shows one lane per stream over a shared axis with extra-time labels (scenarios 1, 5)', async ({ page, api }) => {
    const run = api.seedTimelineRun();
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const timeline = page.getByRole('region', { name: 'Timeline', exact: true });
    await expect(timeline.getByRole('region', { name: /^Timeline of / })).toHaveCount(3);
    const first = page.getByRole('region', { name: firstLane, exact: true });
    const second = page.getByRole('region', { name: secondLane, exact: true });
    const shots = page.getByRole('region', { name: shotsLane, exact: true });
    await expect(first.getByRole('heading', { name: 'pass-detection 1.0.0' })).toBeVisible();
    await expect(first.getByText('passes-first-half', { exact: true })).toBeVisible();
    await expect(second.getByText('passes-second-half', { exact: true })).toBeVisible();
    await expect(shots.getByRole('heading', { name: 'shot-detection 1.0.0' })).toBeVisible();

    // One shared axis: the default zoom covers the 120 minutes, so the bins of every lane use the same match clock.
    await expect(page.getByText('Current zoom: 5 minutes per bin')).toBeVisible();
    await expect(page.getByText('Showing 0:00–150:00', { exact: true })).toBeVisible();
    for (const lane of [first, second, shots]) await expect(binGroup(lane).getByRole('button')).toHaveCount(30);

    // Match-clock labels beyond 90 minutes.
    await expect(binGroup(first).getByRole('button', { name: /^from 100:00 to 105:00 \(end excluded\): /, exact: false })).toBeVisible();
    await expect(page.getByText('100:00', { exact: true })).toBeAttached();
    await expect(page.getByText('125:00', { exact: true })).toBeAttached();

    await expect(first.getByText('90 facts reported')).toBeVisible();
    await expect(second.getByText('45 facts reported')).toBeVisible();
    await expect(shots.getByText('12 facts reported')).toBeVisible();
  });

  test('counts facts per bin and the bins agree with the reported total (scenarios 2, SC)', async ({ page, api }) => {
    const run = api.seedTimelineRun();
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const first = binGroup(page.getByRole('region', { name: firstLane, exact: true }));
    await expect(first.getByRole('button', { name: 'from 0:00 to 5:00 (end excluded): Facts present, 25', exact: true })).toBeVisible();
    await expect(first.getByRole('button', { name: 'from 5:00 to 10:00 (end excluded): Facts present, 25', exact: true })).toBeVisible();
    await expect(first.getByRole('button', { name: 'from 10:00 to 15:00 (end excluded): Facts present, 1', exact: true })).toBeVisible();
    await expect(first.getByRole('button', { name: 'from 85:00 to 90:00 (end excluded): Analysed, no facts', exact: true })).toBeVisible();

    const second = binGroup(page.getByRole('region', { name: secondLane, exact: true }));
    await expect(second.getByRole('button', { name: 'from 45:00 to 50:00 (end excluded): Facts present, 25', exact: true })).toBeVisible();

    // The lane of the third stream has no fact that touches a bin boundary, so its bins add up to the reported 12.
    const shots = binGroup(page.getByRole('region', { name: shotsLane, exact: true }));
    await expect(shots.getByRole('button', { name: /: Facts present, \d+$/ })).not.toHaveCount(0);
    const names = await shots.getByRole('button').evaluateAll((buttons) => buttons.map((b) => b.getAttribute('aria-label') ?? ''));
    const total = names.reduce((sum, name) => sum + Number(/: Facts present, (\d+)$/.exec(name)?.[1] ?? 0), 0);
    expect(total).toBe(12);
    await expect(page.getByRole('region', { name: shotsLane, exact: true }).getByText('Partial', { exact: true })).toHaveCount(0);
  });

  test('changes bin size and the visible extent with zoom and extent buttons (scenario 3)', async ({ page, api }) => {
    const run = api.seedTimelineRun();
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const first = binGroup(page.getByRole('region', { name: firstLane, exact: true }));
    await expect(page.getByText('Showing 0:00–150:00', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Earlier', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Later', exact: true })).toBeDisabled();

    await page.getByRole('button', { name: '1 minute', exact: true }).click();
    await expect(page.getByRole('button', { name: '1 minute', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: '5 minutes', exact: true })).toHaveAttribute('aria-pressed', 'false');
    await expect(page.getByText('Current zoom: 1 minute per bin')).toBeVisible();
    await expect(page.getByText('Showing 0:00–30:00', { exact: true })).toBeVisible();
    await expect(first.getByRole('button', { name: 'from 0:00 to 1:00 (end excluded): Facts present, 5', exact: true })).toBeVisible();

    // The extent moves by half its width without leaving the zoom level.
    await page.getByRole('button', { name: 'Later', exact: true }).click();
    await expect(page.getByText('Showing 15:00–45:00', { exact: true })).toBeVisible();
    await expect(page.getByText('Current zoom: 1 minute per bin')).toBeVisible();
    await expect(first.getByRole('button', { name: /^from 15:00 to 16:00 \(end excluded\): /, exact: false })).toBeVisible();
    await page.getByRole('button', { name: 'Earlier', exact: true }).click();
    await expect(page.getByText('Showing 0:00–30:00', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: '10 seconds', exact: true }).click();
    await expect(page.getByText('Showing 0:00–5:00', { exact: true })).toBeVisible();
    await expect(first.getByRole('button', { name: 'from 0:00 to 0:10 (end excluded): Facts present, 1', exact: true })).toBeVisible();
    await expect(first.getByRole('button', { name: 'from 0:10 to 0:20 (end excluded): Facts present, 1', exact: true })).toBeVisible();
    await expect(page.getByRole('status').filter({ hasText: 'Zoom changed to 10 seconds per bin.' })).toBeVisible();
  });

  test('states partial density and never presents it as complete (scenario 4)', async ({ page, api }) => {
    const [base] = timelineRunFixture().streams;
    const [firstNode] = timelineRunFixture().nodes;
    const fact = (index: number, startMs: number): SeededFact => ({
      acceptedResultId: `00000000-0000-4000-8000-${String(index).padStart(12, '0')}`,
      ordinal: 0,
      kind: `event-${index}`,
      startMs,
      endMs: startMs + 5_000,
      confidence: 0.5,
      payload: { index },
    });
    // 5 000 facts fill the lane's loading bound within the first hour; the last 100 facts lie beyond it.
    const facts = [
      ...Array.from({ length: 5_000 }, (_, i) => fact(i, i * 720)),
      ...Array.from({ length: 100 }, (_, i) => fact(5_000 + i, 6_000_000 + i * 10_000)),
    ];
    const run = api.seedTimelineRun({ nodes: [firstNode], requestedCapabilities: ['pass-detection'], streams: [{ ...base, facts }] });
    api.factPageSize = 500;
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const lane = page.getByRole('region', { name: firstLane, exact: true });
    await expect(lane.getByText('5100 facts reported')).toBeVisible();
    await expect(lane.getByText('Partial', { exact: true })).toBeVisible();
    await expect(lane.getByText('Based on 5000 of 5100 facts')).toBeVisible();

    const bins = binGroup(lane);
    await expect(bins.getByRole('button', { name: /^from 0:00 to 5:00 \(end excluded\): Facts present, \d+$/ })).toBeVisible();
    // Beyond the loading bound the bins say so instead of showing a zero.
    await expect(bins.getByRole('button', { name: 'from 100:00 to 105:00 (end excluded): Facts beyond the loading bound not shown', exact: true })).toBeVisible();
    await expect(bins.getByRole('button', { name: /^from 100:00 to 105:00 .*, 0$/ })).toHaveCount(0);
  });
});

const mixedLane = 'Timeline of mixed-detection 1.0.0 (coverage-mixed)';
const uncoveredLane = 'Timeline of uncovered-detection 1.0.0 (no-coverage)';
const emptyLane = 'Timeline of empty-detection 1.0.0 (complete-empty)';
const absentLane = 'Timeline of absent-detection 1.0.0 (not-in-run)';
const failingLane = 'Timeline of failing-detection 1.0.0 (load-failure)';

test.describe('Timeline coverage', () => {
  test('shows four distinct bin states and never infers zero (scenarios 1-4)', async ({ page, api }) => {
    const run = api.seedCoverageRun();
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    // The 30 minutes of this run default to one-minute bins; five-minute bins match the windows.
    await page.getByRole('button', { name: '5 minutes', exact: true }).click();
    await expect(page.getByText('Current zoom: 5 minutes per bin')).toBeVisible();

    const lane = page.getByRole('region', { name: mixedLane, exact: true });
    const bins = binGroup(lane);
    await expect(bins.getByRole('button', { name: 'from 0:00 to 5:00 (end excluded): Facts present, 5', exact: true })).toBeVisible();
    await expect(bins.getByRole('button', { name: 'from 5:00 to 10:00 (end excluded): Analysed, no facts', exact: true })).toBeVisible();
    await expect(bins.getByRole('button', { name: 'from 10:00 to 15:00 (end excluded): Not analysed', exact: true })).toBeVisible();
    await expect(bins.getByRole('button', { name: 'from 15:00 to 20:00 (end excluded): Not analysed', exact: true })).toBeVisible();
    await expect(bins.getByRole('button', { name: 'from 20:00 to 25:00 (end excluded): No reported coverage', exact: true })).toBeVisible();

    // Each window is drawn with its state as text.
    const windows = lane.getByRole('list', { name: 'Coverage windows of mixed-detection 1.0.0 (coverage-mixed)' });
    await expect(windows.getByRole('listitem', { name: /^from 0:00 to 10:00 \(end excluded\): Accepted$/ })).toBeAttached();
    await expect(windows.getByRole('listitem', { name: /^from 10:00 to 15:00 \(end excluded\): Failed$/ })).toBeAttached();
    await expect(windows.getByRole('listitem', { name: /^from 15:00 to 20:00 \(end excluded\): Skipped$/ })).toBeAttached();

    // A zero count appears only where the window is accepted and nothing was found.
    await expect(bins.getByRole('button', { name: /^from (1[05]|20):00 .*, 0$/ })).toHaveCount(0);
    await expect(page.getByRole('region', { name: emptyLane, exact: true }).getByText('The analysis is complete and produced no facts.')).toBeVisible();
  });

  test('says a running run is in progress and refreshes the lane within 15 seconds keeping zoom (scenario 5)', async ({ page, api }) => {
    const run = api.seedCoverageRun({
      state: 'Running',
      terminatedAt: null,
      steps: [
        (r) => {
          const empty = r.streams.find((s) => s.nodeKey === 'complete-empty');
          empty?.facts.push({
            acceptedResultId: '00000000-0000-4000-8000-000000009001',
            ordinal: 0,
            kind: 'late-event',
            startMs: 720_000,
            endMs: 725_000,
            confidence: 0.5,
            payload: {},
          });
        },
      ],
    });
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const empty = page.getByRole('region', { name: emptyLane, exact: true });
    await expect(empty.getByText('The analysis is still in progress; no facts yet.')).toBeVisible();
    await expect(binGroup(empty)).toHaveCount(0);
    await expect(empty.getByText(/ 0$/)).toHaveCount(0);

    await page.getByRole('button', { name: '1 minute', exact: true }).click();
    await expect(page.getByText('Current zoom: 1 minute per bin')).toBeVisible();

    const bin = binGroup(empty).getByRole('button', { name: 'from 12:00 to 13:00 (end excluded): Facts present, 1', exact: true });
    await expect(bin).toBeVisible({ timeout: 15_000 });
    await expect(empty.getByText('The analysis is still in progress; no facts yet.')).toHaveCount(0);
    await expect(page.getByRole('button', { name: '1 minute', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByText('Current zoom: 1 minute per bin')).toBeVisible();
    await expect(page.getByText(`Showing 0:00–${coverageMatchEndMs / 60_000}:00`, { exact: true })).toBeVisible();
  });

  test('shows every explicit lane state without density (scenarios 5-7)', async ({ page, api }) => {
    const run = api.seedCoverageRun();
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const uncovered = page.getByRole('region', { name: uncoveredLane, exact: true });
    await expect(uncovered.getByText('The platform reports no coverage windows for this selection.')).toBeVisible();
    const empty = page.getByRole('region', { name: emptyLane, exact: true });
    await expect(empty.getByText('The analysis is complete and produced no facts.')).toBeVisible();
    const absent = page.getByRole('region', { name: absentLane, exact: true });
    await expect(absent.getByText('The chosen capability is not part of this run.')).toBeVisible();
    const failing = page.getByRole('region', { name: failingLane, exact: true });
    await expect(failing.getByText('The timeline of this stream could not be loaded.')).toBeVisible();
    await expect(failing.getByRole('button', { name: 'Reload the timeline', exact: true })).toBeVisible();

    for (const name of [uncoveredLane, emptyLane, absentLane, failingLane]) {
      await expect(binGroup(page.getByRole('region', { name, exact: true }))).toHaveCount(0);
    }
    await expect(binGroup(page.getByRole('region', { name: mixedLane, exact: true })).getByRole('button')).toHaveCount(30);
  });
});

const factList = (page: import('@playwright/test').Page) => page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li');
const selectionGroup = (lane: import('@playwright/test').Locator) => lane.getByRole('group', { name: 'Selection', exact: true });

test.describe('Timeline jump', () => {
  test('selects a bin, jumps to exactly the overlapping facts, and returns keeping zoom and extent (scenarios 1-4)', async ({ page, api }) => {
    const run = api.seedTimelineRun();
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    // Zoom and extent are changed first so that the return has something to keep.
    await page.getByRole('button', { name: '1 minute', exact: true }).click();
    await page.getByRole('button', { name: 'Later', exact: true }).click();
    await expect(page.getByText('Showing 15:00–45:00', { exact: true })).toBeVisible();

    // Interaction 1: select the bin; interaction 2: show its facts.
    const lane = page.getByRole('region', { name: firstLane, exact: true });
    await binGroup(lane).getByRole('button', { name: 'from 15:00 to 16:00 (end excluded): Facts present, 1', exact: true }).click();
    const selection = selectionGroup(lane);
    await expect(selection.getByText('15:00–16:00', { exact: false })).toBeVisible();
    await expect(selection.getByText('1 facts', { exact: true })).toBeVisible();
    await selection.getByRole('button', { name: /^Show facts of passes-first-half for / }).click();

    // The address carries the stream and the range; the timeline is closed and the list shows the overlapping fact only.
    await expect(page).toHaveURL(/[?&]stream=passes-first-half&fromMs=900000&toMs=960000$/);
    await expect(page.getByRole('region', { name: 'Timeline', exact: true })).toHaveCount(0);
    await expect(factList(page)).toHaveCount(1);
    await expect(factList(page).first()).toHaveAccessibleName('from 15:20 to 15:25 (end excluded): event-1-51');

    // Back to the timeline keeps zoom and extent and reads the lanes afresh.
    await page.getByRole('button', { name: 'Back to timeline', exact: true }).click();
    await expect(page.getByText('Current zoom: 1 minute per bin')).toBeVisible();
    await expect(page.getByText('Showing 15:00–45:00', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: '1 minute', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(binGroup(page.getByRole('region', { name: firstLane, exact: true })).getByRole('button', { name: 'from 15:00 to 16:00 (end excluded): Facts present, 1', exact: true })).toBeVisible();
  });

  test('lists exactly the facts of a selected span across pages (scenarios 2, 3)', async ({ page, api }) => {
    const run = api.seedTimelineRun();
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const lane = page.getByRole('region', { name: firstLane, exact: true });
    const bins = binGroup(lane);
    // Select the first bin, then extend the span to the second with shift: 10 minutes hold 50 facts.
    await bins.getByRole('button', { name: 'from 0:00 to 5:00 (end excluded): Facts present, 25', exact: true }).click();
    await bins.getByRole('button', { name: 'from 5:00 to 10:00 (end excluded): Facts present, 25', exact: true }).click({ modifiers: ['Shift'] });
    const selection = selectionGroup(lane);
    await expect(selection.getByText('0:00–10:00', { exact: false })).toBeVisible();
    await expect(selection.getByText('50 facts', { exact: true })).toBeVisible();
    await selection.getByRole('button', { name: /^Show facts of passes-first-half for / }).click();

    await expect(page).toHaveURL(/[?&]stream=passes-first-half&fromMs=0&toMs=600000$/);
    await expect(factList(page)).toHaveCount(20);
    await page.getByRole('button', { name: 'Next page', exact: true }).click();
    await expect(factList(page)).toHaveCount(20);
    await page.getByRole('button', { name: 'Next page', exact: true }).click();
    await expect(factList(page)).toHaveCount(10);
    await expect(page.getByRole('button', { name: 'Next page', exact: true })).toBeDisabled();
    // The last fact starting before 10:00 is the 50th of the dense opening.
    await expect(factList(page).last()).toHaveAccessibleName(/: event-1-49$/);
  });

  test('lands on the explicit fact list state for a not-analysed range (scenario 5)', async ({ page, api }) => {
    const run = api.seedCoverageRun();
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);
    await page.getByRole('button', { name: '5 minutes', exact: true }).click();

    const lane = page.getByRole('region', { name: mixedLane, exact: true });
    await binGroup(lane).getByRole('button', { name: 'from 10:00 to 15:00 (end excluded): Not analysed', exact: true }).click();
    const selection = selectionGroup(lane);
    await expect(selection.getByText('Not analysed', { exact: true })).toBeVisible();
    await expect(selection.getByText(/^\d+ facts$/)).toHaveCount(0);
    await selection.getByRole('button', { name: /^Show facts of coverage-mixed for / }).click();

    await expect(page).toHaveURL(/[?&]stream=coverage-mixed&fromMs=600000&toMs=900000$/);
    await expect(factList(page)).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Facts', exact: true }).getByText('No facts overlap this range.')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Coverage', exact: true })).toContainText('10:00–15:00: Failed');
  });

  test('marks the selection summary of a partial lane as partial or not loaded (scenario 6)', async ({ page, api }) => {
    const [base] = timelineRunFixture().streams;
    const [firstNode] = timelineRunFixture().nodes;
    const fact = (index: number, startMs: number): SeededFact => ({
      acceptedResultId: `00000000-0000-4000-8000-${String(index).padStart(12, '0')}`,
      ordinal: 0,
      kind: `event-${index}`,
      startMs,
      endMs: startMs + 5_000,
      confidence: 0.5,
      payload: { index },
    });
    const facts = [
      ...Array.from({ length: 5_000 }, (_, i) => fact(i, i * 720)),
      ...Array.from({ length: 100 }, (_, i) => fact(5_000 + i, 6_000_000 + i * 10_000)),
    ];
    const run = api.seedTimelineRun({ nodes: [firstNode], requestedCapabilities: ['pass-detection'], streams: [{ ...base, facts }] });
    api.factPageSize = 500;
    registerRecordingSet(api, run);
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const lane = page.getByRole('region', { name: firstLane, exact: true });
    const bins = binGroup(lane);
    await expect(lane.getByText('Partial', { exact: true })).toBeVisible();
    const selection = selectionGroup(lane);

    await bins.getByRole('button', { name: /^from 0:00 to 5:00 \(end excluded\): Facts present, \d+$/ }).click();
    await expect(selection.getByText(/^At least \d+ facts, based on loaded facts$/)).toBeVisible();

    await bins.getByRole('button', { name: 'from 100:00 to 105:00 (end excluded): Facts beyond the loading bound not shown', exact: true }).click();
    await expect(selection.getByText('Facts beyond the loading bound not shown', { exact: true })).toBeVisible();
    await expect(selection.getByText(/At least/)).toHaveCount(0);
  });
});
