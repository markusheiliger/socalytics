import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { analysisRun, match, team, type SimulatedApi } from '../support/simulatedApi';

const digest = (c: string) => `sha256:${c.repeat(64)}`;
const id = (n: number) => `00000000-0000-4000-8000-0000000004${String(n).padStart(2, '0')}`;

const OLD_RUN = id(31);
const MID_RUN = id(32);
const NEW_RUN = id(33);

function addSet(api: SimulatedApi, setId: string, finalizedAt: Date) {
  api.recordingSetVersions.push({
    id: setId,
    matchId: match.id,
    teamId: match.teamId,
    finalizedAt,
    members: [{ position: 0, recordingVersionId: id(1), recordingContentDigest: digest('1'), timelineMappingId: id(71), timelineMappingDigest: digest('b') }],
  } as never);
}

function addRun(api: SimulatedApi, runId: string, setId: string, overrides: Record<string, unknown> = {}) {
  api.analysisRuns.push({ ...analysisRun, runId, recordingSetVersionId: setId, state: 'Completed', ...overrides } as never);
}

/** Two older recording sets with an unfinished run each; the caller adds the newest set's run. */
function seed(api: SimulatedApi) {
  const hour = 3_600_000;
  const base = Date.now() - 6 * hour;
  addSet(api, id(90), new Date(base));
  addSet(api, id(91), new Date(base + hour));
  addSet(api, id(92), new Date(base + 2 * hour));
  addRun(api, OLD_RUN, id(90), { state: 'Running', runningAt: new Date(base), isCurrent: true });
  addRun(api, MID_RUN, id(91), { state: 'Running', runningAt: new Date(base + hour) });
}

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
});

const marker = (page: import('@playwright/test').Page, text: string | RegExp) => page.getByRole('status').filter({ hasText: text });

for (const role of ['Coach', 'Viewer'] as const) {
  test(`shows the current and superseded markers and the superseded cancellation to a ${role} (FR-019–FR-021, SC-006)`, async ({ page, api }) => {
    seed(api);
    // The older run was cancelled by a member, then superseded; the middle run was cancelled by supersession; the newest is current.
    api.analysisRuns[0].state = 'Cancelled';
    api.analysisRuns[0].terminatedAt = new Date();
    api.analysisRuns[0].cancelledBy = { memberId: id(5), accountName: 'coach' } as never;
    api.analysisRuns[0].cancellationReason = 'Wrong set';
    api.analysisRuns[0].cancellationCause = 'member';
    addRun(api, NEW_RUN, id(92), { state: 'Running', runningAt: new Date() });
    api.supersedeAnalysisRun(OLD_RUN, MID_RUN);
    api.supersedeAnalysisRun(MID_RUN, NEW_RUN);
    api.signInAs(role);
    await page.goto(`/matches/${match.id}`);

    const both = marker(page, 'Cancelled · by coach');
    await expect(both).toHaveCount(1);
    await expect(both).toContainText('Wrong set');
    await expect(both).toContainText('Superseded by the run of the recording set finalized');

    const superseded = marker(page, 'Cancelled (superseded)');
    await expect(superseded).toHaveCount(1);
    await expect(superseded).toContainText('Superseded by the run of the recording set finalized');
    await expect(superseded).not.toContainText('by coach');
    await expect(superseded).not.toContainText('Reason');

    const current = marker(page, 'Current run');
    await expect(current).toHaveCount(1);
    await expect(current).toContainText('State: Running');
    await expect(current).not.toContainText('Superseded by');

    // Superseded runs stay listed.
    await expect(page.getByRole('link', { name: 'Open analysis review' })).toHaveCount(3);
    await expect(page.getByRole('button', { name: 'Cancel run', exact: true })).toHaveCount(role === 'Coach' ? 1 : 0);
    await expectNoAxeViolations(page);
  });
}

test('shows runs without markers as before', async ({ page, api }) => {
  addSet(api, id(90), new Date());
  addRun(api, OLD_RUN, id(90));
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await expect(marker(page, 'State: Completed')).toBeVisible();
  await expect(page.getByText('Current run')).toHaveCount(0);
  await expect(page.getByText(/Superseded by/)).toHaveCount(0);
});

test('updates the markers at the next refresh while a run is unfinished, without a reload (FR-019, FR-020)', async ({ page, api }) => {
  const base = Date.now() - 2 * 3_600_000;
  addSet(api, id(90), new Date(base));
  addSet(api, id(91), new Date());
  addRun(api, OLD_RUN, id(90), { state: 'Running', runningAt: new Date(base), isCurrent: true });
  api.signInAs('Coach');
  await page.clock.install();
  await page.goto(`/matches/${match.id}`);
  await expect(marker(page, 'Current run')).toContainText('State: Running');

  addRun(api, NEW_RUN, id(91), { state: 'Requested' });
  api.supersedeAnalysisRun(OLD_RUN, NEW_RUN);
  await page.clock.fastForward(10_500);

  await expect(marker(page, 'Cancelled (superseded)')).toContainText('Superseded by the run of the recording set finalized');
  await expect(marker(page, 'Current run')).toContainText('State: Requested');
  await expect(page.getByText('Cancelled (superseded)')).not.toContainText('by coach');
  await expectNoAxeViolations(page);
});
