import type { Locator, Page } from '@playwright/test';
import { expect, test } from '../support/fixtures';
import { activateWithKeyboard } from '../support/keyboard';
import {
  analysisRunFixture,
  seededProvenance,
  match,
  problem,
  sessionFor,
  team,
  type SeededAnalysisRun,
  type SeededFactStream,
  type SeededNode,
  type SeededInputCoverage,
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

const alignmentRegion = (page: Page) => page.getByRole('region', { name: 'Alignment by capability and scope', exact: true });
const alignmentItems = (page: Page) => alignmentRegion(page).getByRole('list', { name: 'Stream alignment' }).locator(':scope > li');
const escapeRegExp = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const streamItem = (page: Page, name: 'Left run' | 'Right run', key: string) =>
  side(page, name).getByRole('listitem', { name: `${key} of the ${name}`, exact: true });
const wording = /\b(better|worse|improved|regress\w*|more accurate|identical|same inputs)\b/i;

function mkNode(
  nodeKey: string,
  capabilityId: string,
  executionScope: string,
  inputCoverage?: SeededInputCoverage[] | null,
  capabilityVersion = '1.0.0',
): SeededNode {
  return {
    nodeKey,
    capabilityId,
    capabilityVersion,
    tier: 'LowLevel',
    executionScope,
    state: 'Completed',
    blockedByNodeKey: null,
    jobs: { waiting: 0, ready: 0, claimed: 0, completed: 1, failed: 0, blocked: 0 },
    inputCoverage,
  };
}

function mkStream(
  nodeKey: string,
  capabilityId: string,
  executionScope: string,
  overrides: Partial<SeededFactStream> = {},
  workflowVersion = '1',
): SeededFactStream {
  return {
    nodeKey,
    capabilityId,
    capabilityVersion: '1.0.0',
    schemaVersion: '1.0.0',
    outputType: 'foundational-facts',
    executionScope,
    required: true,
    provenance: { ...seededProvenance, workflow: { id: 'default', version: workflowVersion } },
    facts: [],
    coverage: [{ startMs: 0, endMs: 90_000, state: 'accepted' }],
    coverageEndMs: 90_000,
    ...overrides,
  };
}

/** Seeds two runs from stream/node lists; every stream must have a node of the same key, capability, and scope. */
function seedPair(
  api: SimulatedApi,
  left: { streams: SeededFactStream[]; nodes?: SeededNode[]; workflowVersion?: string },
  right: { streams: SeededFactStream[]; nodes?: SeededNode[]; workflowVersion?: string },
) {
  const toRun = (input: typeof left) =>
    seedRun(api, {
      workflowVersion: input.workflowVersion ?? '1',
      streams: input.streams,
      nodes: input.nodes ?? input.streams.map((s) => mkNode(s.nodeKey, s.capabilityId, s.executionScope, undefined, s.capabilityVersion)),
    });
  return { leftRun: toRun(left), rightRun: toRun(right) };
}

async function openPair(page: Page, left: SeededAnalysisRun, right: SeededAnalysisRun) {
  await page.goto(`${comparisonPath}?left=${left.runId}&right=${right.runId}`);
  await expect(page.getByRole('region', { name: 'Comparison', exact: true })).toBeVisible();
}

/** SC-002: every stream of each side is accounted for exactly once in the alignment and in its own side's list. */
async function expectEveryStreamOnce(page: Page, left: SeededFactStream[], right: SeededFactStream[], aligned: number) {
  for (const s of left) {
    const key = escapeRegExp(s.nodeKey);
    await expect(alignmentItems(page).filter({ hasText: new RegExp(`^(Left only: ${key} \\(|Left ${key} aligned with right )`) })).toHaveCount(1);
    await expect(streamItem(page, 'Left run', s.nodeKey)).toHaveCount(1);
  }
  for (const s of right) {
    const key = escapeRegExp(s.nodeKey);
    await expect(
      alignmentItems(page).filter({ hasText: new RegExp(`^(Right only: ${key} \\(|Left .+ aligned with right ${key} \\()`) }),
    ).toHaveCount(1);
    await expect(streamItem(page, 'Right run', s.nodeKey)).toHaveCount(1);
  }
  await expect(alignmentItems(page)).toHaveCount(left.length + right.length - aligned);
  await expect(alignmentItems(page).filter({ hasText: /aligned with right/ })).toHaveCount(aligned);
}

test.describe('Stream alignment and honest differences', () => {
  test('aligns uniquely despite renamed nodes and shows every changed field with its original values', async ({ page, api }) => {
    api.signInAs('Coach');
    const changedDigests = {
      analystProfile: { id: 'profile-detector-2', digest: `sha256:${'1'.repeat(64)}` },
      imageDigest: `sha256:${'2'.repeat(64)}`,
      modelDigest: null,
      workflow: { id: 'default', version: '2' },
    };
    const leftP = mkStream('detections', 'player-detection', 'Segment');
    const leftQ = mkStream('tracks', 'ball-tracking', 'Match', { outputType: 'experimental-kind' });
    const rightP = mkStream('detections-renamed', 'player-detection', 'Segment', {
      capabilityVersion: '1.1.0',
      schemaVersion: '2.0.0',
      outputType: 'soccer-concepts',
      provenance: changedDigests,
    });
    const rightQ = mkStream('tracks', 'ball-tracking', 'Match', { outputType: 'experimental-kind' }, '2');
    // The right run lists its streams in the opposite order; pairing must not depend on position.
    const { leftRun, rightRun } = seedPair(api, { streams: [leftP, leftQ] }, { streams: [rightQ, rightP], workflowVersion: '2' });

    await openPair(page, leftRun, rightRun);
    await expectEveryStreamOnce(page, [leftP, leftQ], [rightQ, rightP], 2);

    const items = alignmentItems(page);
    await expect(items.nth(0)).toContainText('Left detections aligned with right detections-renamed (capability player-detection, scope Segment)');
    await expect(items.nth(1)).toContainText('Left tracks aligned with right tracks (capability ball-tracking, scope Match)');
    const differences = items.nth(0).getByRole('listitem').filter({ hasText: /^(Node|Capability version|Schema version|Output type|Analyst profile|Image digest|Model digest)/ });
    await expect(differences).toHaveText([
      'Node: left detections, right detections-renamed',
      'Capability version: left 1.0.0, right 1.1.0',
      'Schema version: left 1.0.0, right 2.0.0',
      'Output type: left foundational-facts, right soccer-concepts',
      'Analyst profile ID: left profile-detector, right profile-detector-2',
      `Analyst profile digest: left ${seededProvenance.analystProfile.digest}, right ${changedDigests.analystProfile.digest}`,
      `Image digest: left ${seededProvenance.imageDigest}, right ${changedDigests.imageDigest}`,
      `Model digest: left ${seededProvenance.modelDigest}, right not reported`,
    ]);
    await expect(items.nth(1)).toContainText('No differences in node, versions, output type, profile, image, or model digest.');

    // The renamed stream keeps its own original values on its own side; the absent model digest is not invented.
    await expect(field(streamItem(page, 'Left run', 'detections'), 'Schema version')).toHaveText('1.0.0');
    await expect(field(streamItem(page, 'Right run', 'detections-renamed'), 'Schema version')).toHaveText('2.0.0');
    await expect(streamItem(page, 'Left run', 'detections').getByText('Model digest', { exact: true })).toHaveCount(1);
    await expect(streamItem(page, 'Right run', 'detections-renamed').getByText('Model digest', { exact: true })).toHaveCount(0);
    // An unknown output type stays raw.
    await expect(field(streamItem(page, 'Left run', 'tracks'), 'Output type')).toHaveText('experimental-kind');
    await expect(field(streamItem(page, 'Right run', 'tracks'), 'Output type')).toHaveText('experimental-kind');

    // Different recording sets and workflows are pair context, not stream equivalence.
    const pair = page.getByRole('region', { name: 'Pair context', exact: true });
    await expect(pair).toContainText(`left ${leftRun.recordingSetVersionId}, right ${rightRun.recordingSetVersionId}`);
    await expect(pair).toContainText('The runs used different workflows: left default 1, right default 2.');

    await expect(page.getByText(/never shows that two runs had equal full inputs or that their outputs mean the same/)).toBeVisible();
    await expect(alignmentRegion(page)).not.toContainText(wording);
    await expect(alignmentRegion(page)).toContainText('does not mean the streams are equivalent');
  });

  test('never guesses a pairing for left-only, right-only, repeated, or collision-like groups', async ({ page, api }) => {
    api.signInAs('Viewer');
    const left = [
      mkStream('l-uniq', 'detect', 'Segment', { schemaVersion: '1.0.1' }),
      mkStream('l-rep1', 'track', 'Segment'),
      mkStream('l-rep2', 'track', 'Segment'),
      mkStream('l-only', 'left-only', 'Match'),
      mkStream('l-both1', 'pose', 'Match'),
      mkStream('l-both2', 'pose', 'Match'),
      mkStream('l-coll', 'a:b', 'c'),
    ];
    const right = [
      mkStream('r-both2', 'pose', 'Match'),
      mkStream('r-both1', 'pose', 'Match'),
      mkStream('r-rep', 'track', 'Segment'),
      mkStream('r-uniq', 'detect', 'Segment', { schemaVersion: '1.0.2' }),
      mkStream('r-only', 'right-only', 'Match'),
      mkStream('r-coll', 'a', 'b:c'),
    ];
    const { leftRun, rightRun } = seedPair(api, { streams: left }, { streams: right });

    await openPair(page, leftRun, rightRun);
    await expectEveryStreamOnce(page, left, right, 1);

    const items = alignmentItems(page);
    await expect(items.filter({ hasText: /aligned with right/ })).toContainText('Left l-uniq aligned with right r-uniq');
    for (const key of ['l-rep1', 'l-rep2', 'l-both1', 'l-both2']) {
      await expect(items.filter({ hasText: new RegExp(`^Left only: ${key} \\(`) })).toContainText('More than one stream shares this capability and scope');
    }
    for (const key of ['r-rep', 'r-both1', 'r-both2']) {
      await expect(items.filter({ hasText: new RegExp(`^Right only: ${key} \\(`) })).toContainText('More than one stream shares this capability and scope');
    }
    await expect(alignmentRegion(page).getByText(/More than one stream shares this capability and scope/)).toHaveCount(7);
    await expect(alignmentRegion(page).getByText(/The other side has no stream of this capability and scope/)).toHaveCount(4);
    for (const [key, kind] of [['l-only', 'Left'], ['r-only', 'Right'], ['l-coll', 'Left'], ['r-coll', 'Right']] as const) {
      const item = items.filter({ hasText: new RegExp(`^${kind} only: ${key} \\(`) });
      await expect(item).toContainText('No counterpart values are shown.');
      // No synthetic zero side: an unmatched stream carries no counts or differences.
      await expect(item).not.toContainText(/facts|output coverage|Differences/);
    }
    // Only the single uniquely aligned pair carries a differences list.
    await expect(alignmentRegion(page).getByText('Differences', { exact: true })).toHaveCount(1);
    await expect(items.filter({ hasText: /aligned with right/ })).toContainText('Schema version: left 1.0.1, right 1.0.2');
    await expect(field(streamItem(page, 'Left run', 'l-uniq'), 'Schema version')).toHaveText('1.0.1');
    await expect(field(streamItem(page, 'Right run', 'r-uniq'), 'Schema version')).toHaveText('1.0.2');
    await expect(alignmentRegion(page)).not.toContainText(wording);
  });

  test('shows changed, missing, and unsupported input dependency reports by explicit upstream key only', async ({ page, api }) => {
    api.signInAs('Viewer');
    const counts = { accepted: 4, failed: 0, blocked: 0, skipped: 0, missing: 0 };
    const leftStreams = [mkStream('fuse', 'fusion', 'Match'), mkStream('quiet', 'ball-tracking', 'Match'), mkStream('lonely', 'pose', 'Match')];
    const rightStreams = [mkStream('fuse', 'fusion', 'Match'), mkStream('quiet', 'ball-tracking', 'Match'), mkStream('lonely', 'pose', 'Match')];
    const leftNodes = [
      mkNode('fuse', 'fusion', 'Match', [
        { upstreamNodeKey: 'frames', coverageMode: 'complete', ...counts },
        { upstreamNodeKey: 'tracks', coverageMode: 'partial', minimumAcceptedFraction: 0.5, ...counts },
        { upstreamNodeKey: 'old-input', coverageMode: 'complete', ...counts },
      ]),
      mkNode('quiet', 'ball-tracking', 'Match', null),
      mkNode('lonely', 'pose', 'Match', []),
    ];
    const rightNodes = [
      mkNode('fuse', 'fusion', 'Match', [
        { upstreamNodeKey: 'new-input', coverageMode: 'complete', ...counts },
        { upstreamNodeKey: 'tracks', coverageMode: 'quorum', minimumAcceptedFraction: 0.8, ...counts, accepted: 2, missing: 2 },
        { upstreamNodeKey: 'frames', coverageMode: 'complete', ...counts },
      ]),
      mkNode('quiet', 'ball-tracking', 'Match', []),
      mkNode('lonely', 'pose', 'Match', null),
    ];
    const { leftRun, rightRun } = seedPair(api, { streams: leftStreams, nodes: leftNodes }, { streams: rightStreams, nodes: rightNodes });

    await openPair(page, leftRun, rightRun);
    await expectEveryStreamOnce(page, leftStreams, rightStreams, 3);

    const fuse = alignmentItems(page).nth(0);
    await expect(fuse).toContainText('Dependency frames: same reported policy and counts on both sides.');
    await expect(fuse).toContainText('Dependency tracks: differs in policy, minimum accepted fraction, accepted count, missing count.');
    await expect(fuse).toContainText('Left: policy partial, minimum accepted fraction 0.5; accepted 4, missing 0, failed 0, blocked 0, skipped 0.');
    await expect(fuse).toContainText(
      'Right: policy quorum (unsupported policy, shown as reported), minimum accepted fraction 0.8; accepted 2, missing 2, failed 0, blocked 0, skipped 0.',
    );
    await expect(fuse).toContainText('Dependency old-input: reported only by the left run.');
    await expect(fuse).toContainText('Dependency new-input: reported only by the right run.');
    await expect(fuse).toContainText('Unchanged reports do not show equal full input snapshots.');
    // Dependencies are never merged across keys: old-input and new-input stay separate and unpaired.
    await expect(fuse.getByText(/Dependency (old-input|new-input): differs/)).toHaveCount(0);

    // Not reported on one side and a reported empty list on the other are different from zero coverage.
    for (const index of [1, 2]) {
      await expect(alignmentItems(page).nth(index)).toContainText('not reported is not zero coverage');
      await expect(alignmentItems(page).nth(index)).toContainText('Neither side reported dependencies.');
    }
    await expect(field(streamItem(page, 'Left run', 'quiet'), 'Input coverage of this node')).toContainText('Input coverage was not reported for this node.');
    await expect(field(streamItem(page, 'Right run', 'quiet'), 'Input coverage of this node')).toContainText('reported an empty dependency list');
    await expect(field(streamItem(page, 'Left run', 'lonely'), 'Input coverage of this node')).toContainText('reported an empty dependency list');
    await expect(field(streamItem(page, 'Right run', 'lonely'), 'Input coverage of this node')).toContainText('Input coverage was not reported for this node.');

    // Each side keeps its own original report.
    await expect(field(streamItem(page, 'Left run', 'fuse'), 'Input coverage of this node')).toContainText(
      'Dependency tracks: policy partial, minimum accepted fraction 0.5; accepted 4',
    );
    await expect(field(streamItem(page, 'Right run', 'fuse'), 'Input coverage of this node')).toContainText(
      'Dependency tracks: policy quorum (unsupported policy, shown as reported), minimum accepted fraction 0.8; accepted 2, missing 2',
    );
    await expect(alignmentRegion(page)).not.toContainText(wording);
  });

  test('keeps a stream with an absent optional model digest aligned with one that reports it', async ({ page, api }) => {
    api.signInAs('Viewer');
    const withModel = mkStream('model-node', 'player-detection', 'Segment');
    const withoutModel = mkStream('model-node', 'player-detection', 'Segment', {
      provenance: { ...seededProvenance, workflow: { id: 'default', version: '1' }, modelDigest: null },
    });
    const { leftRun, rightRun } = seedPair(api, { streams: [withModel] }, { streams: [withoutModel] });

    await openPair(page, leftRun, rightRun);
    await expectEveryStreamOnce(page, [withModel], [withoutModel], 1);
    await expect(alignmentItems(page).nth(0)).toContainText(`Model digest: left ${seededProvenance.modelDigest}, right not reported`);
    await expect(streamItem(page, 'Right run', 'model-node').getByText('Model digest', { exact: true })).toHaveCount(0);
  });

  const malformed: [string, (streams: ReturnType<typeof entries>) => unknown][] = [
    ['a repeated node identity', (items) => [...items, items[0]]],
    ['a stream whose provenance workflow contradicts its run', (items) => items.map((i) => ({ ...i, provenance: { ...i.provenance, workflow: { id: 'default', version: '9' } } }))],
    ['a stream without a node in its run', (items) => [...items, { ...items[0], nodeKey: 'ghost-node' }]],
    ['a negative coverage count', (items) => items.map((i) => ({ ...i, coverage: { ...i.coverage, missing: -1 } }))],
    ['a missing stream collection', () => undefined],
  ];

  function entries(run: SeededAnalysisRun) {
    return run.streams.map((s) => ({
      nodeKey: s.nodeKey,
      capability: { id: s.capabilityId, version: s.capabilityVersion },
      schemaVersion: s.schemaVersion,
      outputType: s.outputType,
      executionScope: s.executionScope,
      required: s.required,
      factCount: s.facts.length,
      provenance: s.provenance,
      coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
    }));
  }

  for (const [name, mutate] of malformed) {
    test(`refuses to show any comparison for ${name}`, async ({ page, api }) => {
      api.signInAs('Viewer');
      const leftStream = mkStream('detections', 'player-detection', 'Segment');
      const { leftRun, rightRun } = seedPair(api, { streams: [leftStream] }, { streams: [mkStream('detections', 'player-detection', 'Segment')] });
      await page.route(`**/api/v1/analysis/runs/${rightRun.runId}/fact-streams*`, (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            run: { runId: rightRun.runId, teamId: rightRun.teamId, matchId: rightRun.matchId, state: rightRun.state },
            items: mutate(entries(rightRun)),
          }),
        }),
      );

      await page.goto(`${comparisonPath}?left=${leftRun.runId}&right=${rightRun.runId}`);
      await expect(page.getByText('The platform answered with data that does not fit this comparison, so nothing is shown.')).toBeVisible();
      await expect(page.getByRole('region', { name: 'Comparison', exact: true })).toHaveCount(0);
      await expect(alignmentRegion(page)).toHaveCount(0);
      await expect(side(page, 'Left run')).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Try loading the comparison again' })).toBeVisible();
    });
  }
});
