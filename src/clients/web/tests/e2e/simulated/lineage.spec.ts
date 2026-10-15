import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { match, team, type SimulatedApi } from '../support/simulatedApi';

const digest = (c: string) => `sha256:${c.repeat(64)}`;
const id = (n: number) => `00000000-0000-4000-8000-0000000002${String(n).padStart(2, '0')}`;

function seed(api: SimulatedApi) {
  api.recordingVersions.push({
    id: id(1),
    matchId: match.id,
    teamId: match.teamId,
    uploadSessionId: id(50),
    recording: { displayName: 'Camera 1', description: null, contentType: 'video/mp4' },
    totalSizeBytes: 1024,
    partSizeBytes: 1024,
    partCount: 1,
    contentDigest: digest('1'),
    createdAt: new Date(),
  } as never);
  api.timelineMappings.push({
    id: id(71),
    recordingVersionId: id(1),
    matchId: match.id,
    spans: [{ mediaStartSeconds: 0, mediaEndSeconds: 60, matchStartSeconds: 600 }],
    digest: digest('b'),
    createdAt: new Date(Date.now() - 60_000),
  } as never);
  api.recordingSetVersions.push({
    id: id(90),
    matchId: match.id,
    teamId: match.teamId,
    finalizedAt: new Date(),
    members: [{ position: 0, recordingVersionId: id(1), recordingContentDigest: digest('1'), timelineMappingId: id(71), timelineMappingDigest: digest('b') }],
  } as never);
}

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
  seed(api);
});

for (const role of ['Coach', 'Viewer'] as const) {
  test(`shows the lineage without media or change actions as ${role} (scenarios 1, 4)`, async ({ page, api }) => {
    api.signInAs(role);
    await page.goto(`/matches/${match.id}`);
    const section = page.getByRole('region', { name: 'Recording lineage' });
    await expect(section.getByRole('list', { name: 'Uploaded versions in the lineage' })).toContainText(digest('1'));
    await expect(section.getByRole('list', { name: 'Lineage timeline mappings' })).toContainText('00:00–01:00');
    await expect(section.getByRole('list', { name: 'Lineage recording set versions' })).toContainText('1. Camera 1');
    await expect(section.getByRole('button')).toHaveCount(0);
    await expect(page.locator('video, audio')).toHaveCount(0);
    await expectNoAxeViolations(page);
  });
}

test('lets a Coach revise a mapping and keeps the earlier one (scenario 2)', async ({ page, api }) => {
  api.signInAs('Coach');
  api.on('POST', `/matches/${match.id}/recording-versions/${id(1)}/timeline-mappings`, async (route, body) => {
    const created = {
      id: id(72),
      recordingVersionId: id(1),
      matchId: match.id,
      spans: (body as { spans: unknown[] }).spans,
      digest: digest('c'),
      createdAt: new Date(),
    };
    api.timelineMappings.push(created as never);
    await route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
  });
  await page.goto(`/matches/${match.id}`);
  await page.getByRole('link', { name: 'Open Camera 1' }).click();
  await expect(page).toHaveURL(new RegExp(`/recordings/${id(1)}$`));
  await page.getByRole('button', { name: 'Revise mapping' }).click();
  await expectNoAxeViolations(page);
  const start = page.getByLabel('Match time at media start (Span 1)');
  await start.fill('10:30');
  await page.getByRole('button', { name: 'Add revised mapping' }).click();
  await expect(page.getByText(/Mapping digest/)).toHaveCount(2);
  await expect(page.getByText(new RegExp(digest('b')))).toBeVisible();
  expect(api.timelineMappings).toHaveLength(2);
  expect(api.recordingSetVersions).toHaveLength(1);
});

test('offers a Viewer no revision (scenario 4)', async ({ page, api }) => {
  api.signInAs('Viewer');
  await page.goto(`/matches/${match.id}/recordings/${id(1)}`);
  await expect(page.getByText(digest('1'))).toBeVisible();
  await expect(page.getByRole('button', { name: 'Revise mapping' })).toHaveCount(0);
});

test('shows the platform reason next to the span and creates nothing (scenario 5)', async ({ page, api }) => {
  api.signInAs('Coach');
  api.on('POST', `/matches/${match.id}/recording-versions/${id(1)}/timeline-mappings`, (route) =>
    route.fulfill({
      status: 400,
      contentType: 'application/problem+json',
      body: JSON.stringify({ title: 'Bad', status: 400, code: 'validation-failed', errors: [{ field: 'timelineMapping.spans[0]', code: 'timeline-mapping-invalid' }] }),
    }),
  );
  await page.goto(`/matches/${match.id}/recordings/${id(1)}`);
  await page.getByRole('button', { name: 'Revise mapping' }).click();
  await page.getByRole('button', { name: 'Add revised mapping' }).click();
  await expect(page.getByText('The platform refused this span.')).toBeVisible();
  expect(api.timelineMappings).toHaveLength(1);
});
