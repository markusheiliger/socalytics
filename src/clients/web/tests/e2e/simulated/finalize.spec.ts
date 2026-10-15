import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { analysisRun, match, team, type SimulatedApi } from '../support/simulatedApi';

const digest = (c: string) => `sha256:${c.repeat(64)}`;
const id = (n: number) => `00000000-0000-4000-8000-0000000001${String(n).padStart(2, '0')}`;

function seed(api: SimulatedApi, versions = 2) {
  for (let i = 1; i <= versions; i++) {
    api.recordingVersions.push({
      id: id(i),
      matchId: match.id,
      teamId: match.teamId,
      uploadSessionId: id(50 + i),
      recording: { displayName: `Camera ${i}`, description: null, contentType: 'video/mp4' },
      totalSizeBytes: 1024,
      partSizeBytes: 1024,
      partCount: 1,
      contentDigest: digest(String(i)),
      createdAt: new Date(),
    } as never);
    api.timelineMappings.push({
      id: id(70 + i),
      recordingVersionId: id(i),
      matchId: match.id,
      spans: [{ mediaStartSeconds: 0, mediaEndSeconds: 60, matchStartSeconds: 600 }],
      digest: digest('b'),
      createdAt: new Date(),
    } as never);
  }
}

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
});

test('finalizes two recordings in the chosen order with the newest mapping and shows the set and its run (scenarios 1, 2, 5)', async ({ page, api }) => {
  seed(api);
  api.signInAs('Coach');
  await page.goto(`/matches/${match.id}`);
  await page.getByRole('link', { name: 'Finalize recordings' }).click();
  await page.getByLabel('Include Camera 2').check();
  await page.getByLabel('Include Camera 1').check();
  await expect(page.getByLabel('Timeline mapping for Camera 1')).toHaveValue(id(71));
  await expectNoAxeViolations(page);
  await page.getByRole('button', { name: 'Review and finalize' }).click();
  await page.getByRole('button', { name: 'Finalize', exact: true }).click();

  await expect(page).toHaveURL(new RegExp(`/matches/${match.id}$`));
  const sets = page.getByRole('list', { name: 'Finalized recording sets' });
  await expect(sets.getByRole('listitem').first()).toContainText('2 recording(s)');
  expect(api.recordingSetVersions).toHaveLength(1);
  expect(api.recordingSetVersions[0].members?.map((m) => m.recordingVersionId)).toEqual([id(2), id(1)]);
  await expect(sets.getByRole('status')).toContainText('State:');
  expect(api.bodyContentTypes.every((type) => type.startsWith('application/json'))).toBe(true);
});

test('reuses the retry key when the request fails (scenario 3)', async ({ page, api }) => {
  seed(api, 1);
  api.signInAs('Coach');
  const keys: string[] = [];
  let attempts = 0;
  api.on('POST', `/matches/${match.id}/recording-set-versions`, (route) => {
    keys.push(route.request().headers()['idempotency-key']);
    return ++attempts === 1 ? route.abort('connectionreset') : route.fulfill({ status: 500, contentType: 'application/json', body: '{}' });
  });
  await page.goto(`/matches/${match.id}/finalize`);
  await page.getByLabel('Include Camera 1').check();
  await page.getByRole('button', { name: 'Review and finalize' }).click();
  await page.getByRole('button', { name: 'Finalize', exact: true }).click();
  await page.getByRole('button', { name: 'Try again' }).click();
  await expect.poll(() => keys.length).toBe(2);
  expect(keys[1]).toBe(keys[0]);
});

test('does not send an empty selection and shows the platform refusal (scenario 4)', async ({ page, api }) => {
  seed(api, 1);
  api.signInAs('Coach');
  api.on('POST', `/matches/${match.id}/recording-set-versions`, (route) =>
    route.fulfill({ status: 400, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Bad', status: 400, code: 'recording-set-too-large' }) }),
  );
  await page.goto(`/matches/${match.id}/finalize`);
  await expect(page.getByRole('button', { name: 'Review and finalize' })).toBeDisabled();
  await page.getByLabel('Include Camera 1').check();
  await page.getByRole('button', { name: 'Review and finalize' }).click();
  await page.getByRole('button', { name: 'Finalize', exact: true }).click();
  await expect(page.getByText(/The platform refused the input/)).toBeVisible();
  expect(api.recordingSetVersions).toHaveLength(0);
});

test('refreshes an unfinished run until it ends and shows no results (scenario 5)', async ({ page, api }) => {
  seed(api, 1);
  api.signInAs('Coach');
  api.recordingSetVersions.push({ id: id(90), matchId: match.id, teamId: match.teamId, finalizedAt: new Date(), members: [{ position: 0, recordingVersionId: id(1), recordingContentDigest: digest('1'), timelineMappingId: id(71), timelineMappingDigest: digest('b') }] } as never);
  api.analysisRuns.push({ ...analysisRun, recordingSetVersionId: id(90), state: 'Running' });
  await page.clock.install();
  await page.goto(`/matches/${match.id}`);
  await expect(page.getByRole('status').filter({ hasText: 'State: Running' })).toBeVisible();
  api.analysisRuns[0].state = 'Completed';
  await page.clock.fastForward(10_500);
  await expect(page.getByRole('status').filter({ hasText: 'State: Completed' })).toBeVisible();
  await expect(page.getByText(/result/i)).toHaveCount(0);
});

test('requires explicit confirmation for a selection identical to the newest set (scenario 6)', async ({ page, api }) => {
  seed(api, 1);
  api.signInAs('Coach');
  api.recordingSetVersions.push({ id: id(90), matchId: match.id, teamId: match.teamId, finalizedAt: new Date(), members: [{ position: 0, recordingVersionId: id(1), recordingContentDigest: digest('1'), timelineMappingId: id(71), timelineMappingDigest: digest('b') }] } as never);
  await page.goto(`/matches/${match.id}/finalize`);
  await page.getByLabel('Include Camera 1').check();
  await page.getByRole('button', { name: 'Review and finalize' }).click();
  await expect(page.getByRole('alert')).toContainText('starts another analysis');
  await page.getByRole('button', { name: 'Cancel' }).click();
  expect(api.recordingSetVersions).toHaveLength(1);
  await page.getByRole('button', { name: 'Review and finalize' }).click();
  await page.getByRole('button', { name: 'Finalize and start another analysis' }).click();
  await expect.poll(() => api.recordingSetVersions.length).toBe(2);
});
