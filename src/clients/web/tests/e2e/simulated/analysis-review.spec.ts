import { activateWithKeyboard } from '../support/keyboard';
import { expect, test } from '../support/fixtures';
import {
  analysisRunFixture,
  match,
  problem,
  seededProvenance,
  team,
  type SeededAnalysisRun,
  type SeededFact,
  type SeededFactStream,
  type SeededNode,
  type SimulatedApi,
} from '../support/simulatedApi';

const digest = (c: string) => `sha256:${c.repeat(64)}`;
const memberId = '00000000-0000-4000-8000-000000000201';

function node(overrides: Partial<SeededNode>): SeededNode {
  return {
    nodeKey: 'detections',
    capabilityId: 'player-detection',
    capabilityVersion: '1.0.0',
    tier: 'LowLevel',
    executionScope: 'Segment',
    state: 'Completed',
    blockedByNodeKey: null,
    jobs: { waiting: 0, ready: 0, claimed: 0, completed: 0, failed: 0, blocked: 0 },
    ...overrides,
  };
}

/** Seeds a run together with the finalized recording set the match view lists it under. */
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

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
  api.signInAs('Viewer');
});

test.describe('Run status', () => {
  test('opens a completed run from the match by keyboard and shows run details and node counts (scenarios 1, 2)', async ({ page, api }) => {
    const run = seedRun(api, {
      nodes: [node({ jobs: { waiting: 0, ready: 0, claimed: 0, completed: 3, failed: 0, blocked: 0 } })],
    });
    await page.goto(`/matches/${match.id}`);
    await activateWithKeyboard(page.getByRole('link', { name: 'Open analysis review' }));
    await expect(page).toHaveURL(new RegExp(`/matches/${match.id}/analysis-runs/${run.runId}$`));

    const details = page.getByRole('region', { name: 'Run details' });
    await expect(details.locator('dl').first().locator('div').first().locator('dd')).toHaveText('Completed');
    await expect(details).toContainText('default 1');
    await expect(details).not.toContainText('Failure reason');

    const entry = page.getByRole('list', { name: 'Capability nodes of the run' }).locator(':scope > li').first();
    await expect(entry).toContainText('detections');
    await expect(entry).toContainText('player-detection 1.0.0');
    await expect(entry).toContainText('Low level');
    await expect(entry).toContainText('Completed: 3');
    await expect(entry).toContainText('Failed: 0');
  });

  test('shows the failure reason and the failed node of a failed run (scenario 3)', async ({ page, api }) => {
    const run = seedRun(api, {
      state: 'Failed',
      failureReason: 'A required capability failed',
      nodes: [
        node({ state: 'Failed', jobs: { waiting: 0, ready: 0, claimed: 0, completed: 1, failed: 2, blocked: 0 } }),
        node({
          nodeKey: 'events',
          capabilityId: 'event-detection',
          tier: 'HighLevel',
          executionScope: 'Match',
          state: 'Blocked',
          blockedByNodeKey: 'detections',
          jobs: { waiting: 0, ready: 0, claimed: 0, completed: 0, failed: 0, blocked: 1 },
        }),
      ],
    });
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const details = page.getByRole('region', { name: 'Run details' });
    await expect(details).toContainText('Failed');
    await expect(details).toContainText('A required capability failed');

    const nodes = page.getByRole('list', { name: 'Capability nodes of the run' }).locator(':scope > li');
    await expect(nodes).toHaveCount(2);
    await expect(nodes.nth(0)).toContainText('Failed: 2');
    await expect(nodes.nth(1)).toContainText('Blocked by');
    await expect(nodes.nth(1)).toContainText('detections');
    await expect(nodes.nth(1)).toContainText('Blocked: 1');
  });

  test('shows a running run and its state change within 15 seconds without a reload (scenario 4, SC-004)', async ({ page, api }) => {
    const run = seedRun(api, {
      state: 'Running',
      terminatedAt: null,
      nodes: [node({ state: 'Active', jobs: { waiting: 0, ready: 0, claimed: 1, completed: 1, failed: 0, blocked: 0 } })],
      steps: [
        (r) => {
          r.state = 'Completed';
          r.terminatedAt = new Date();
          r.nodes[0].state = 'Completed';
          r.nodes[0].jobs = { waiting: 0, ready: 0, claimed: 0, completed: 2, failed: 0, blocked: 0 };
        },
      ],
    });
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);

    const details = page.getByRole('region', { name: 'Run details' });
    const entry = page.getByRole('list', { name: 'Capability nodes of the run' }).locator(':scope > li').first();
    await expect(details).toContainText('Running');
    await expect(entry).toContainText('Active');
    await expect(entry).toContainText('Claimed: 1');

    await expect(details).toContainText('Completed', { timeout: 15_000 });
    await expect(entry).toContainText('Completed: 2', { timeout: 15_000 });
    await expect(entry).toContainText('Claimed: 0');
    await expect(page.getByRole('status').filter({ hasText: 'The run is now Completed.' })).toBeVisible();
  });

  test('shows the not-found view for an unknown run (scenario 5)', async ({ page }) => {
    await page.goto(`/matches/${match.id}/analysis-runs/00000000-0000-4000-8000-00000000ffff`);
    await expect(page.getByText('This page does not exist.')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Run details' })).toHaveCount(0);
  });

  test('shows the not-found view for a run of another match (scenario 5)', async ({ page, api }) => {
    const otherMatch = { ...match, id: '00000000-0000-4000-8000-000000000022', opponent: { name: 'Other FC' } };
    api.matches.push(otherMatch);
    const run = seedRun(api, { matchId: otherMatch.id as string });

    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}`);
    await expect(page.getByText('This page does not exist.')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Run details' })).toHaveCount(0);
  });
});

const fact = (index: number): SeededFact => ({
  acceptedResultId: `00000000-0000-4000-8000-0000000007${String(index).padStart(2, '0')}`,
  ordinal: 0,
  kind: `pass-${index}`,
  startMs: index * 10_000,
  endMs: index * 10_000 + 10_000,
  confidence: 0.5 + index / 20,
  payload: { player: `p${index}`, note: '<b>bold</b>', detail: { x: index, y: 2 } },
});

function seedFactRun(api: SimulatedApi): SeededAnalysisRun {
  const [base] = analysisRunFixture().streams;
  api.factPageSize = 2;
  return seedRun(api, {
    streams: [
      {
        ...base,
        facts: [0, 1, 2, 3, 4].map(fact),
        coverage: [
          { startMs: 0, endMs: 30_000, state: 'accepted' },
          { startMs: 30_000, endMs: 90_000, state: 'missing' },
        ],
      },
      {
        ...base,
        nodeKey: 'events',
        capabilityId: 'event-detection',
        outputType: 'soccer-concepts',
        facts: [fact(0)],
        provenance: { ...seededProvenance, modelDigest: null },
      },
    ],
  });
}

test.describe('Fact browsing', () => {
  test('pages through a stream, applies and clears a range, and keeps the selection on reload (scenarios 1–5, SC-001, SC-002)', async ({ page, api }) => {
    const run = seedFactRun(api);
    const factRequests: string[] = [];
    page.on('request', (request) => {
      if (/\/analysis\/runs\/[^/]+\/facts\?/.test(request.url())) factRequests.push(request.url());
    });

    // Two navigation steps from the match view: open the review, select the stream.
    await page.goto(`/matches/${match.id}`);
    await page.getByRole('link', { name: 'Open analysis review' }).click();
    const streamList = page.getByRole('list', { name: 'Fact streams of the run' });
    await expect(streamList.locator(':scope > li')).toHaveCount(2);
    const detections = streamList.locator(':scope > li').first();
    await expect(detections).toContainText('player-detection 1.0.0');
    await expect(detections).toContainText('Facts5');
    await expect(detections).toContainText('Accepted: 1');
    await expect(detections).toContainText('Missing: 1');
    await page.getByRole('button', { name: 'Show facts of detections' }).click();

    const facts = page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li');
    const expectKinds = async (kinds: string[]) => {
      await expect(facts).toHaveCount(kinds.length);
      for (const [index, kind] of kinds.entries()) {
        await expect(facts.nth(index)).toHaveAttribute('aria-label', new RegExp(`: ${kind}$`));
      }
    };
    await expectKinds(['pass-0', 'pass-1']);
    await expect(page.getByRole('button', { name: 'Previous page' })).toBeDisabled();
    await expect(page.getByText('This is the first page.')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Coverage' })).toContainText('0:30–1:30: Missing');

    await page.getByRole('button', { name: 'Next page' }).click();
    await expectKinds(['pass-2', 'pass-3']);
    await page.getByRole('button', { name: 'Next page' }).click();
    await expectKinds(['pass-4']);
    await expect(page.getByRole('button', { name: 'Next page' })).toBeDisabled();
    await expect(page.getByText('This is the last page.')).toBeVisible();
    await page.getByRole('button', { name: 'Previous page' }).click();
    await expectKinds(['pass-2', 'pass-3']);
    await page.getByRole('button', { name: 'Previous page' }).click();
    await expectKinds(['pass-0', 'pass-1']);

    await facts.first().getByRole('button', { name: 'Show full payload' }).click();
    const payload = page.getByLabel('Full payload of the fact');
    await expect(payload).toContainText('"player": "p0"');
    await expect(payload).toContainText('<b>bold</b>');
    await expect(payload.locator('b')).toHaveCount(0);

    // Invalid range: explained at the field, no request.
    const requestsBefore = factRequests.length;
    const from = page.getByRole('textbox', { name: 'From' });
    await from.fill('abc');
    await page.getByRole('button', { name: 'Apply range' }).click();
    await expect(page.getByText('Enter a time as m:ss or m:ss.SSS.')).toBeVisible();
    await page.getByRole('textbox', { name: 'To' }).fill('0:05');
    await from.fill('0:20');
    await page.getByRole('button', { name: 'Apply range' }).click();
    await expect(page.getByText('The end must be after the start.')).toBeVisible();
    expect(factRequests.length).toBe(requestsBefore);

    // Range 0:20–0:40 keeps only overlapping facts and clips the coverage.
    await page.getByRole('textbox', { name: 'To' }).fill('0:40');
    await page.getByRole('button', { name: 'Apply range' }).click();
    await expect(page).toHaveURL(new RegExp(`/analysis-runs/${run.runId}\\?stream=detections&fromMs=20000&toMs=40000$`));
    await expectKinds(['pass-2', 'pass-3']);
    const coverage = page.getByRole('region', { name: 'Coverage' });
    await expect(coverage).toContainText('0:20–0:30: Accepted');
    await expect(coverage).toContainText('0:30–0:40: Missing');
    await expect(coverage).not.toContainText('1:30');

    await page.reload();
    await expect(page.getByRole('textbox', { name: 'From' })).toHaveValue('0:20');
    await expect(page.getByRole('textbox', { name: 'To' })).toHaveValue('0:40');
    await expectKinds(['pass-2', 'pass-3']);

    await page.getByRole('button', { name: 'Clear range' }).click();
    await expect(page).toHaveURL(new RegExp(`/analysis-runs/${run.runId}\\?stream=detections$`));
    await expectKinds(['pass-0', 'pass-1']);
  });

  test('shows provenance with and without a model digest (scenario 6)', async ({ page, api }) => {
    const run = seedFactRun(api);

    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`);
    const provenance = page.getByRole('region', { name: 'Provenance' });
    await expect(provenance).toContainText('default 1');
    await expect(provenance).toContainText('profile-detector');
    await expect(provenance).toContainText('Image digest');
    await expect(provenance).toContainText('Model digest');
    await expect(provenance).toContainText(`sha256:${'c'.repeat(12)}`);
    await expect(provenance).not.toContainText('c'.repeat(64));

    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=events`);
    const without = page.getByRole('region', { name: 'Provenance' });
    await expect(without).toContainText('Image digest');
    await expect(without).not.toContainText('Model digest');
  });
});

const factsRequest = /\/api\/v1\/analysis\/runs\/[^/]+\/facts\?/;

function stream(overrides: Partial<SeededFactStream> = {}): SeededFactStream {
  const [base] = analysisRunFixture().streams;
  return { ...base, ...overrides };
}

test.describe('Explicit states', () => {
  test('explains a running run without facts as in progress (scenario 1)', async ({ page, api }) => {
    const run = seedRun(api, {
      state: 'Running',
      terminatedAt: null,
      streams: [stream({ coverage: [{ startMs: 0, endMs: 90_000, state: 'missing' }] })],
    });
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`);

    await expect(page.getByRole('region', { name: 'Facts', exact: true }).getByText('The analysis is still in progress; no facts yet.')).toBeVisible();
    await expect(page.getByRole('list', { name: 'Facts of the selected stream' })).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Coverage', exact: true })).toContainText('0:00–1:30: Missing');
  });

  test('marks facts of a partially completed run as incomplete and lists the non-accepted windows (scenarios 2, 3)', async ({ page, api }) => {
    const coverage: SeededFactStream['coverage'] = [
      { startMs: 0, endMs: 30_000, state: 'accepted' },
      { startMs: 30_000, endMs: 60_000, state: 'failed' },
      { startMs: 60_000, endMs: 90_000, state: 'blocked' },
    ];
    const run = seedRun(api, {
      state: 'PartiallyCompleted',
      streams: [
        stream({ facts: [fact(0), fact(1)], coverage }),
        stream({ nodeKey: 'events', capabilityId: 'event-detection', outputType: 'soccer-concepts', facts: [], coverage }),
      ],
    });

    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`);
    await expect(page.getByText('These results are incomplete.')).toBeVisible();
    await expect(page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li')).toHaveCount(2);
    const all = page.getByRole('region', { name: 'Coverage', exact: true });
    await expect(all).toContainText('0:30–1:00: Failed');
    await expect(all).toContainText('1:00–1:30: Blocked');

    await page.getByRole('button', { name: 'Show facts of events' }).click();
    await expect(page.getByRole('region', { name: 'Facts', exact: true }).getByText('The analysis is incomplete. Some windows are not accepted.')).toBeVisible();
    const windows = page.getByRole('region', { name: 'Windows that are not accepted' });
    await expect(windows).toContainText('0:30–1:00: Failed');
    await expect(windows).toContainText('1:00–1:30: Blocked');
    await expect(windows).not.toContainText('Accepted');
  });

  test('says a complete run without facts produced none, and a range without facts overlaps none (scenarios 1, 3)', async ({ page, api }) => {
    const empty = seedRun(api, { streams: [stream()] });
    await page.goto(`/matches/${match.id}/analysis-runs/${empty.runId}?stream=detections`);
    await expect(page.getByRole('region', { name: 'Facts', exact: true }).getByText('The analysis is complete and produced no facts.')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Coverage', exact: true })).toContainText('0:00–1:30: Accepted');

    const withFacts = seedRun(api, { streams: [stream({ facts: [fact(0)] })] });
    await page.goto(`/matches/${match.id}/analysis-runs/${withFacts.runId}?stream=detections&fromMs=50000&toMs=60000`);
    await expect(page.getByText('No facts overlap this range.')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Coverage', exact: true })).toContainText('0:50–1:00: Accepted');
    await expect(page.getByRole('region', { name: 'Facts', exact: true }).getByText('The analysis is complete and produced no facts.')).toHaveCount(0);
  });

  test('explains an outdated stream address and keeps the stream list (scenario 4)', async ({ page, api }) => {
    const run = seedRun(api, { streams: [stream({ facts: [fact(0)] })] });
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=retired`);

    await expect(page.getByText('The chosen capability is not part of this run.')).toBeVisible();
    await expect(page.getByRole('list', { name: 'Fact streams of the run' }).locator(':scope > li')).toHaveCount(1);
    await expect(page.getByRole('region', { name: 'Facts', exact: true })).toHaveCount(0);
  });

  test('restarts at the first page with a note when the platform rejects a continuation (scenario 4)', async ({ page, api }) => {
    api.factPageSize = 2;
    const run = seedRun(api, { streams: [stream({ facts: [0, 1, 2, 3, 4].map(fact) })] });
    await page.route(/\/api\/v1\/analysis\/runs\/[^/]+\/facts\?.*continuationToken=/, (route) =>
      route.fulfill({
        status: 400,
        contentType: 'application/problem+json',
        body: JSON.stringify(problem(400, 'Validation failed', { errors: [{ field: 'continuationToken', code: 'invalid' }] })),
      }),
    );
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`);
    const facts = page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li');
    await expect(facts).toHaveCount(2);

    await page.getByRole('button', { name: 'Next page' }).click();
    await expect(page.getByText('The platform no longer accepts the position in the list, so the list restarted at the first page.')).toBeVisible();
    await expect(facts.first()).toHaveAttribute('aria-label', /: pass-0$/);
    await expect(page.getByRole('button', { name: 'Previous page' })).toBeDisabled();
  });

  test('shows a platform error with a reload that recovers (scenario 4)', async ({ page, api }) => {
    const run = seedRun(api, { streams: [stream({ facts: [fact(0)] })] });
    // The timeline's lane load reads the same route, so every request fails until the reload instead of only the first.
    let failing = true;
    await page.route(factsRequest, (route) => {
      if (!failing) return route.fallback();
      return route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify(problem(500, 'Server error')) });
    });
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`);

    await expect(page.getByRole('alert').filter({ hasText: 'The analysis could not be loaded.' })).toBeVisible();
    failing = false;
    await page.getByRole('button', { name: 'Reload', exact: true }).click();
    await expect(page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li')).toHaveCount(1);
    await expect(page.getByText('The analysis could not be loaded.')).toHaveCount(0);
  });

  test('offers refreshed results of a running run instead of changing the page (scenarios 5, 6)', async ({ page, api }) => {
    const run = seedRun(api, {
      state: 'Running',
      terminatedAt: null,
      streams: [
        stream({
          facts: [fact(0)],
          coverage: [
            { startMs: 0, endMs: 30_000, state: 'accepted' },
            { startMs: 30_000, endMs: 90_000, state: 'missing' },
          ],
        }),
      ],
      steps: [
        (r) => {
          r.streams[0].facts.push(fact(1));
          r.streams[0].coverage = [
            { startMs: 0, endMs: 30_000, state: 'accepted' },
            { startMs: 30_000, endMs: 60_000, state: 'accepted' },
            { startMs: 60_000, endMs: 90_000, state: 'missing' },
          ];
        },
      ],
    });
    await page.goto(`/matches/${match.id}/analysis-runs/${run.runId}?stream=detections`);
    const facts = page.getByRole('list', { name: 'Facts of the selected stream' }).locator(':scope > li');
    await expect(facts).toHaveCount(1);
    const entry = page.getByRole('list', { name: 'Fact streams of the run' }).locator(':scope > li').first();
    await expect(entry).toContainText('Facts1');
    await expect(entry).toContainText('Accepted: 1');

    await expect(entry).toContainText('Facts2', { timeout: 30_000 });
    await expect(entry).toContainText('Accepted: 2');
    await expect(page.getByRole('status').filter({ hasText: 'New results are available for this stream.' })).toBeVisible();
    await expect(facts).toHaveCount(1);
    await expect(page.getByRole('region', { name: 'Coverage', exact: true })).toContainText('0:30–1:30: Missing');

    await page.getByRole('button', { name: 'Reload the facts' }).click();
    await expect(facts).toHaveCount(2);
    await expect(page.getByRole('region', { name: 'Coverage', exact: true })).toContainText('0:30–1:00: Accepted');
    await expect(page.getByText('New results are available for this stream.')).toHaveCount(0);
  });
});
