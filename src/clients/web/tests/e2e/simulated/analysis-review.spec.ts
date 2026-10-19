import { activateWithKeyboard } from '../support/keyboard';
import { expect, test } from '../support/fixtures';
import { match, team, type SeededAnalysisRun, type SeededNode, type SimulatedApi } from '../support/simulatedApi';

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
