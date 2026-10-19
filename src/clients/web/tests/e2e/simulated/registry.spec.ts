import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { problem, type RegistryImage, type RegistryProfile, type RegistryWorkflow, type SimulatedApi } from '../support/simulatedApi';

const hex = (c: string) => `sha-256:${c.repeat(64)}`;
const TRACKER_IMAGE = hex('a');
const POSSESSION_IMAGE = hex('b');
const MODEL = hex('c');
const UNKNOWN_IMAGE = hex('f');

const image = (digest: string, id: string, version: string, tier: string, scope: string, manifest: Record<string, unknown>): RegistryImage => ({
  imageDigest: digest,
  repository: `registry.example/analysts/${id}`,
  manifestDigest: hex('e'),
  capability: { id, version, tier, executionScope: scope },
  modelDigest: MODEL,
  manifest,
  source: `registry.example/analysts/${id}@${digest}`,
  registeredAt: new Date('2026-10-10T12:00:00Z'),
  registeredBy: null,
});

const content = (capabilityId: string, capabilityVersion: string): RegistryProfile['content'] => ({
  capabilityId,
  capabilityVersion,
  modelDigest: MODEL,
  runtimeConstraints: ['linux/amd64'],
  accelerators: [],
  resources: { cpu: '2' },
  maxAttempts: 3,
  leaseSeconds: 120,
  staleTimeoutSeconds: 240,
  executionTimeoutSeconds: 900,
  videoPolicy: 'required',
  inputSchemaVersions: {},
  outputSchemaVersion: '1.0.0',
});

const profile = (
  profileId: string,
  version: number,
  state: RegistryProfile['state'],
  imageDigest: string,
  capabilityId: string,
  capabilityVersion: string,
): RegistryProfile => ({
  profileId,
  version,
  profileDigest: hex(String(version + 1)),
  imageDigest,
  state,
  content: content(capabilityId, capabilityVersion),
  createdAt: new Date('2026-10-11T12:00:00Z'),
  createdBy: '00000000-0000-4000-8000-000000000021',
  rowVersion: 1,
});

const node = (nodeKey: string, profileId: string, profileVersion: number) => ({
  nodeKey,
  profileId,
  profileVersion,
  profileDigest: hex('d'),
});

function seed(api: SimulatedApi, options: { withDefault: boolean }) {
  api.seedImage(
    image(TRACKER_IMAGE, 'ball-tracking', '1.0.0', 'low-level', 'segment', {
      manifestVersion: '1.0.0',
      videoAccess: 'required',
      compatibleProfiles: ['standard'],
      output: { schemaVersion: '1.0.0' },
    }),
  );
  api.seedImage(image(POSSESSION_IMAGE, 'possession', '2.1.0', 'experimental', 'match', { manifestVersion: '1.0.0' }));
  api.seedProfile(profile('tracker', 1, 'approved', TRACKER_IMAGE, 'ball-tracking', '1.0.0'));
  api.seedProfile(profile('tracker', 2, 'draft', TRACKER_IMAGE, 'ball-tracking', '1.0.0'));
  api.seedProfile(profile('possession-std', 1, 'retired', POSSESSION_IMAGE, 'possession', '2.1.0'));
  const definition = (version: string): RegistryWorkflow => ({
    workflowId: 'match-analysis',
    workflowVersion: version,
    definitionDigest: hex('9'),
    definition: {
      requestedCapabilities: ['possession'],
      completionPolicy: 'all-requested-capabilities',
      segmentationPolicy: { version: 1, durationSeconds: 300, digest: hex('8') },
      nodes: [node('track', 'tracker', 1), node('poss', 'possession-std', 1), node('summary', 'tracker', 1)],
      edges: [
        { upstreamNodeKey: 'track', dependentNodeKey: 'poss', kind: 'required' },
        {
          upstreamNodeKey: 'poss',
          dependentNodeKey: 'summary',
          kind: 'conditional',
          predicate: { kind: 'capabilityRequested', capabilityId: 'possession' },
        },
        { upstreamNodeKey: 'summary', dependentNodeKey: 'track', kind: 'sideways' },
      ],
    },
    createdAt: new Date('2026-10-12T12:00:00Z'),
    createdBy: null,
  });
  api.seedWorkflow(definition('1.0.0'));
  api.seedWorkflow(definition('1.1.0'));
  if (options.withDefault) api.seedDefault({ workflowId: 'match-analysis', workflowVersion: '1.0.0' });
}

test.describe('Browse', () => {
  test('a Club Admin reaches the overview in two steps and sees the default workflow (acceptance 1, 2, SC-001)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto('/');
    await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Analyst Registry', exact: true }).click();
    await expect(page).toHaveURL(/\/registry$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Analyst Registry' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'match-analysis version 1.0.0' })).toBeVisible();
    await expect(page.getByText('Changed ', { exact: false })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Analyst images' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Analyst profiles' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Workflows' })).toBeVisible();
    await expectNoAxeViolations(page);
  });

  test('without a default workflow the overview says so (acceptance 2)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: false });
    await page.goto('/registry');
    await expect(
      page.getByText('No default workflow is set; analysis runs cannot be created until one is.'),
    ).toBeVisible();
    await expect(page.getByRole('link', { name: 'match-analysis version 1.0.0' })).toHaveCount(0);
  });

  test('images are filtered by capability id and opened with their manifest text (acceptance 3, 4)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto('/registry/images');
    const links = page.getByRole('list', { name: 'Analyst images' }).getByRole('link');
    await expect(links).toHaveText(['ball-tracking 1.0.0', 'possession 2.1.0']);
    await page.getByRole('textbox', { name: 'Capability' }).fill('possession');
    await page.getByRole('button', { name: 'Apply filter' }).click();
    await expect(page).toHaveURL(/capabilityId=possession/);
    await expect(links).toHaveText(['possession 2.1.0']);
    await page.getByRole('button', { name: 'Clear filter' }).click();
    await expect(links).toHaveCount(2);

    await links.first().click();
    await expect(page.getByRole('heading', { level: 2, name: 'Analyst image' })).toBeVisible();
    await expect(page.getByText('registry.example/analysts/ball-tracking', { exact: true })).toBeVisible();
    await expect(page.getByText('Development seed')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Stored manifest' })).toHaveAttribute('aria-expanded', 'false');
    await page.getByRole('button', { name: 'Stored manifest' }).click();
    await expect(page.locator('pre')).toContainText('"manifestVersion": "1.0.0"');
    await expectNoAxeViolations(page);
  });

  test('an unknown tier and missing manifest fields are shown raw or as not declared (acceptance 7)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto(`/registry/images/${encodeURIComponent(POSSESSION_IMAGE)}`);
    await expect(page.getByText('experimental', { exact: true })).toBeVisible();
    await expect(page.getByText('not declared').first()).toBeVisible();
  });

  test('profiles are filtered by state and a profile version is opened (acceptance 5)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto('/registry/profiles');
    const links = page.getByRole('list', { name: 'Analyst profile versions' }).getByRole('link');
    await expect(links).toHaveCount(3);
    await page.getByLabel('State').selectOption('draft');
    await page.getByRole('button', { name: 'Apply filters' }).click();
    await expect(page).toHaveURL(/state=draft/);
    await expect(links).toHaveText(['Version 2 – Draft']);

    await links.first().click();
    await expect(page.getByRole('heading', { level: 3, name: 'tracker version 2' })).toBeVisible();
    await expect(page.getByText('Draft', { exact: true })).toBeVisible();
    await expect(page.getByText('Maximum attempts')).toBeVisible();
    await expect(page.getByText('Created at', { exact: true })).toBeVisible();
    await expect(page.getByText('Created by', { exact: true })).toBeVisible();
    await expectNoAxeViolations(page);
  });

  test('a workflow version shows its nodes and edges, with raw values for an unknown edge kind (acceptance 6, 7)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto('/registry/workflows');
    const list = page.getByRole('list', { name: 'Workflow versions' });
    await expect(list.getByRole('link')).toHaveText(['match-analysis version 1.0.0', 'match-analysis version 1.1.0']);
    await expect(list.getByText('Default', { exact: true })).toHaveCount(1);

    await list.getByRole('link', { name: 'match-analysis version 1.0.0' }).click();
    await expect(page.getByText('This is the default workflow version.')).toBeVisible();
    const nodes = page.getByRole('list', { name: 'Nodes' });
    await expect(nodes.getByRole('listitem')).toHaveCount(3);
    await expect(nodes.getByRole('link', { name: 'tracker version 1' }).first()).toBeVisible();
    const edges = page.getByRole('list', { name: 'Edges' });
    await expect(edges.getByRole('listitem')).toHaveCount(3);
    await expect(edges).toContainText('track → poss (Required)');
    await expect(edges).toContainText('poss → summary (Conditional)');
    await expect(edges).toContainText('only when capability possession is requested');
    await expect(edges).toContainText('summary → track (sideways)');
    await expectNoAxeViolations(page);

    await nodes.getByRole('link', { name: 'possession-std version 1' }).click();
    await expect(page.getByRole('heading', { level: 3, name: 'possession-std version 1' })).toBeVisible();
    await expect(page.getByText('Retired', { exact: true })).toBeVisible();
  });

  test('an unknown digest shows the not-found view (acceptance 7)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto(`/registry/images/${encodeURIComponent(UNKNOWN_IMAGE)}`);
    await expect(page.getByRole('alert')).toHaveText('This Analyst image was not found.');
    await expect(page.getByRole('link', { name: 'Back to the images' })).toBeVisible();
  });
});

test.describe('Register image', () => {
  const reference = (c: string) => `registry.example/analysts/ball-tracking@sha256:${c.repeat(64)}`;
  const submit = async (page: import('@playwright/test').Page, value: string) => {
    await page.getByLabel('Image reference').fill(value);
    await page.getByRole('button', { name: 'Register image' }).click();
  };

  test('a tag reference is explained without a request (acceptance 1)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    const posts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST') posts.push(request.url());
    });
    await page.goto('/registry/images');
    await page.getByRole('link', { name: 'Register an image' }).click();
    await expect(page).toHaveURL(/\/registry\/images\/register$/);
    await submit(page, 'registry.example/analysts/ball-tracking:latest');
    await expect(page.getByText('The reference must contain @sha256: followed by 64 hexadecimal characters.')).toBeVisible();
    expect(posts).toEqual([]);
    await expectNoAxeViolations(page);
  });

  test('each scripted refusal shows its message and details and keeps the reference (acceptance 2)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    const refusals: [string, string, string][] = [
      ['reference-not-allowed', '1', 'The reference points to a registry or repository that is not allowed.'],
      ['image-not-found', '2', 'The image was not found in the registry.'],
      ['digest-mismatch', '3', 'The registry returned content that does not match the digest.'],
      ['manifest-missing', '4', 'The image carries no Analyst manifest.'],
      ['manifest-invalid', '5', "The image's Analyst manifest is invalid."],
      ['labels-mismatch', '6', 'The image labels do not match its Analyst manifest.'],
      ['unsupported-media-type', '7', 'The image uses a media type that is not supported.'],
    ];
    for (const [code, digit] of refusals) {
      api.imageRefusals.set(reference(digit), { code, errors: [{ field: 'reference', code }] });
    }
    await page.goto('/registry/images/register');
    for (const [code, digit, message] of refusals) {
      await submit(page, reference(digit));
      const alert = page.getByRole('alert').filter({ hasText: 'The platform refused the' });
      await expect(alert).toContainText(message);
      await expect(alert).toContainText(`reference: ${code}`);
      await expect(alert).not.toContainText('Try again later.');
      await expect(page.getByLabel('Image reference')).toHaveValue(reference(digit));
    }
    await expectNoAxeViolations(page);
  });

  test('an unavailable image registry is refused with "try again later" (acceptance 2)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    api.imageRefusals.set(reference('8'), { code: 'registry-unavailable' });
    await page.goto('/registry/images/register');
    await submit(page, reference('8'));
    const alert = page.getByRole('alert').filter({ hasText: 'The platform refused the' });
    await expect(alert).toContainText('The image registry is unavailable.');
    await expect(alert).toContainText('Try again later.');
    await expect(page.getByLabel('Image reference')).toHaveValue(reference('8'));
  });

  test('a new registration opens the declaration and a repeated one says nothing changed (acceptance 3, 4, SC-003)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    const offered = hex('1');
    api.registryCatalog.set(offered, image(offered, 'passing', '3.0.0', 'low-level', 'segment', { manifestVersion: '1.0.0' }));
    await page.goto('/registry/images/register');
    await submit(page, reference('1'));
    await expect(page).toHaveURL(new RegExp(`/registry/images/${encodeURIComponent(offered)}$`));
    await expect(page.getByRole('heading', { level: 2, name: 'Analyst image' })).toBeVisible();
    await expect(page.getByText('registry.example/analysts/passing', { exact: true })).toBeVisible();
    await expect(page.getByText('The image was already registered, nothing changed.')).toHaveCount(0);

    await page.goto('/registry/images/register');
    await submit(page, reference('1'));
    await expect(page.getByRole('heading', { level: 2, name: 'Analyst image' })).toBeVisible();
    await expect(page.getByText('The image was already registered, nothing changed.')).toBeVisible();
  });
});

test.describe('Profiles', () => {
  const fillContent = async (page: import('@playwright/test').Page, cpu: string) => {
    await page.getByLabel('Runtime constraints').fill('linux/amd64');
    await page.getByLabel('Resources').fill(`cpu=${cpu}`);
    await page.getByLabel('Maximum attempts').fill('3');
    await page.getByLabel('Lease (seconds)').fill('120');
    await page.getByLabel('Stale timeout (seconds)').fill('240');
    await page.getByLabel('Execution timeout (seconds)').fill('900');
  };
  const openForm = async (page: import('@playwright/test').Page) => {
    await page.goto(`/registry/images/${encodeURIComponent(TRACKER_IMAGE)}`);
    await page.getByRole('link', { name: 'New profile version' }).click();
    await expect(page).toHaveURL(/\/registry\/profiles\/new\?image=/);
    await expect(page.getByLabel('Profile id')).toHaveValue('tracker');
  };
  const submit = (page: import('@playwright/test').Page) => page.getByRole('button', { name: 'Create profile version' }).click();

  test('a draft is created from an image and identical content says it already existed (acceptance 1, 2, SC-003)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await openForm(page);
    await fillContent(page, '4');
    await submit(page);
    await expect(page.getByText('Profile version created')).toBeVisible();
    expect(api.registryProfiles.filter((p) => p.profileId === 'tracker')).toHaveLength(3);
    await expectNoAxeViolations(page);

    await submit(page);
    await expect(page.getByText('This already existed, no new version was created.')).toBeVisible();
    expect(api.registryProfiles.filter((p) => p.profileId === 'tracker')).toHaveLength(3);
  });

  test('contradictions with the declaration are shown and the input is kept (acceptance 3)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await openForm(page);
    await fillContent(page, '4');
    api.registryImages[0].capability.version = '9.9.9';
    await submit(page);
    const alert = page.getByRole('alert').filter({ hasText: 'The platform refused the' });
    await expect(alert).toContainText("The profile content contradicts the image's capability declaration.");
    await expect(alert).toContainText('capabilityVersion: differs');
    await expect(page.getByLabel('Resources')).toHaveValue('cpu=4');
    expect(api.registryProfiles).toHaveLength(3);
  });

  test('approving a draft the member created adds the self-approval note (acceptance 4, 5)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await openForm(page);
    await fillContent(page, '4');
    await submit(page);
    await page.getByRole('link', { name: 'Show the profile version' }).click();
    await expect(page).toHaveURL(/\/registry\/profiles\/tracker\/versions\/3$/);
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Approve profile version' });
    await expect(dialog).toContainText('tracker version 3');
    await expect(dialog).toContainText(TRACKER_IMAGE);
    await expect(dialog).toContainText(MODEL);
    await expect(dialog).toContainText('You created this profile version. You are approving your own work.');
    await expectNoAxeViolations(page);
    await dialog.getByRole('button', { name: 'Approve version' }).click();
    await expect(dialog).toContainText('Profile version approved.');
    expect(api.registryProfiles.find((p) => p.profileId === 'tracker' && p.version === 3)?.state).toBe('approved');
  });

  test('cancelling a confirmation sends no request (acceptance 4)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    const posts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST') posts.push(request.url());
    });
    await page.goto('/registry/profiles/tracker/versions/2');
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Approve profile version' });
    await expect(dialog).not.toContainText('You are approving your own work.');
    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toBeHidden();
    expect(posts).toEqual([]);
    expect(api.registryProfiles.find((p) => p.profileId === 'tracker' && p.version === 2)?.state).toBe('draft');
  });

  test('a version used by the default workflow cannot be retired, an unused one can (acceptance 6)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto('/registry/profiles/tracker/versions/1');
    await page.getByRole('button', { name: 'Retire', exact: true }).click();
    let dialog = page.getByRole('dialog', { name: 'Retire profile version' });
    await dialog.getByRole('button', { name: 'Retire version' }).click();
    await expect(dialog).toContainText('The profile version is used by the default workflow and cannot be retired.');
    await dialog.getByRole('button', { name: 'Close' }).click();
    expect(api.registryProfiles.find((p) => p.profileId === 'tracker' && p.version === 1)?.state).toBe('approved');

    await page.goto('/registry/profiles/tracker/versions/2');
    await page.getByRole('button', { name: 'Retire', exact: true }).click();
    dialog = page.getByRole('dialog', { name: 'Retire profile version' });
    await dialog.getByRole('button', { name: 'Retire version' }).click();
    await expect(dialog).toContainText('Profile version retired.');
    expect(api.registryProfiles.find((p) => p.profileId === 'tracker' && p.version === 2)?.state).toBe('retired');
  });

  test('a stale approval shows the "has changed" message (acceptance 7)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto('/registry/profiles/tracker/versions/2');
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Approve profile version' });
    const stored = api.registryProfiles.find((p) => p.profileId === 'tracker' && p.version === 2);
    if (stored) stored.rowVersion += 1;
    await dialog.getByRole('button', { name: 'Approve version' }).click();
    await expect(dialog).toContainText('The record has changed');
    await expect(dialog).toContainText('Current state: Draft');
    expect(stored?.state).toBe('draft');
  });
});

test.describe('Workflows', () => {
  const SEGMENT_DIGEST = `sha256:${'8'.repeat(64)}`;
  type Page = import('@playwright/test').Page;
  const submit = (page: Page) => page.getByRole('button', { name: 'Define workflow version' }).click();
  const fillSegmentation = async (page: Page) => {
    await page.getByLabel('Segment duration (seconds)').fill('300');
    await page.getByLabel('Digest', { exact: true }).fill(SEGMENT_DIGEST);
  };
  const versionPage = (id: string, version: string) => `/registry/workflows/${id}/versions/${version}`;
  const alertWith = (page: Page) => page.getByRole('alert').filter({ hasText: 'The platform refused the' });

  test('a version is defined from approved profiles only (acceptance 1, SC-002)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: false });
    await page.goto('/registry/workflows');
    await page.getByRole('link', { name: 'Define workflow version' }).click();
    await expect(page).toHaveURL(/\/registry\/workflows\/new$/);
    await page.getByRole('button', { name: 'Add node' }).click();
    const choices = page.getByLabel('Approved profile version 1').locator('option');
    await expect(choices).toHaveText(['Choose a profile version', 'tracker version 1']);
    await page.getByLabel('Node key 1').fill('track');
    await page.getByLabel('Approved profile version 1').selectOption({ label: 'tracker version 1' });
    await expect(page.getByRole('checkbox', { name: 'ball-tracking' })).toBeChecked();
    await expect(page.getByLabel('Completion policy')).toHaveValue('all-requested-capabilities');
    await page.getByLabel('Workflow id').fill('match-analysis');
    await page.getByLabel('Workflow version').fill('2.0.0');
    await fillSegmentation(page);
    await expectNoAxeViolations(page);
    await submit(page);
    await expect(page.getByText('Workflow version defined')).toBeVisible();
    const stored = api.registryWorkflows.find((w) => w.workflowVersion === '2.0.0');
    expect(stored?.definition.nodes.map((n) => n.nodeKey)).toEqual(['track']);
    expect(stored?.definition.requestedCapabilities).toEqual(['ball-tracking']);
    expect(stored?.definition.completionPolicy).toBe('all-requested-capabilities');
  });

  test('a version started from an existing one flags unapproved nodes until they are replaced (acceptance 2)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto(`${versionPage('match-analysis', '1.0.0')}`);
    await page.getByRole('link', { name: 'New version from this' }).click();
    await expect(page.getByText('Started from match-analysis version 1.0.0.')).toBeVisible();
    await expect(page.getByLabel('Workflow id')).toHaveValue('match-analysis');
    await expect(page.getByLabel('Workflow version')).toHaveValue('');
    await expect(page.getByText('The profile version of node poss is no longer approved. Choose an approved profile version.')).toBeVisible();
    await expect(page.getByLabel('Node key 3')).toHaveValue('track');

    const posts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST') posts.push(request.url());
    });
    await page.getByLabel('Workflow version').fill('2.0.0');
    await fillSegmentation(page);
    await submit(page);
    await expect(page.getByRole('alert').filter({ hasText: 'node poss is no longer approved' })).toBeVisible();
    expect(posts).toEqual([]);
    expect(api.registryWorkflows).toHaveLength(2);

    await page.getByLabel('Approved profile version 1').selectOption({ label: 'tracker version 1' });
    await submit(page);
    await expect(page.getByText('Workflow version defined')).toBeVisible();
    expect(api.registryWorkflows.map((w) => w.workflowVersion)).toContain('2.0.0');
  });

  test('each scripted violation is explained and the input is kept (acceptance 3, SC-003)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    api.on('POST', '/registry/workflows', (route) =>
      route.fulfill({
        status: 422,
        contentType: 'application/problem+json',
        body: JSON.stringify(
          problem(422, 'Unprocessable', {
            code: 'workflow-invalid',
            errors: [
              { field: 'nodes.track', code: 'profile-not-approved' },
              { field: 'workflow', code: 'missing-upstream' },
              { field: 'workflow', code: 'schema-incompatible' },
              { field: 'workflow', code: 'cycle' },
              { field: 'workflow', code: 'unknown-completion-policy' },
              { field: 'workflow', code: 'invalid-segmentation-policy' },
            ],
          }),
        ),
      }),
    );
    await page.goto('/registry/workflows/new?fromId=match-analysis&fromVersion=1.0.0');
    await page.getByLabel('Workflow version').fill('2.0.0');
    await page.getByLabel('Approved profile version 1').selectOption({ label: 'tracker version 1' });
    await fillSegmentation(page);
    await submit(page);
    const alert = alertWith(page);
    for (const message of [
      'Node track: the profile version is not approved.',
      'A required upstream dependency is missing in the workflow.',
      'An upstream output schema is not compatible with the dependent node.',
      'The workflow edges form a cycle.',
      'The completion policy is not known to the platform.',
      'The segmentation policy is invalid.',
    ]) {
      await expect(alert).toContainText(message);
    }
    await expect(page.getByLabel('Workflow version')).toHaveValue('2.0.0');
    await expect(page.getByLabel('Node key 3')).toHaveValue('track');
    await expectNoAxeViolations(page);
  });

  test('an existing version identifier is explained (acceptance 3)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto('/registry/workflows/new?fromId=match-analysis&fromVersion=1.0.0');
    await page.getByLabel('Workflow version').fill('1.1.0');
    await page.getByLabel('Approved profile version 1').selectOption({ label: 'tracker version 1' });
    await fillSegmentation(page);
    await submit(page);
    await expect(alertWith(page)).toContainText('This workflow version already exists. Choose another version.');
    expect(api.registryWorkflows).toHaveLength(2);
  });

  test('a stored version becomes the default after the confirmation states its consequence (acceptance 4, SC-006)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto(versionPage('match-analysis', '1.1.0'));
    await page.getByRole('button', { name: 'Make default', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Make workflow version the default' });
    await expect(dialog).toContainText('The current default is match-analysis version 1.0.0.');
    await expect(dialog).toContainText('Only analysis runs created afterwards use it; existing runs keep their snapshot.');
    expect(api.registryDefault?.workflowVersion).toBe('1.0.0');
    await expectNoAxeViolations(page);
    await dialog.getByRole('button', { name: 'Make default' }).click();
    await expect(dialog).toContainText('The workflow version is now the default.');
    expect(api.registryDefault?.workflowVersion).toBe('1.1.0');
    await dialog.getByRole('button', { name: 'Close' }).click();

    await page.goto('/registry');
    await expect(page.getByRole('link', { name: 'match-analysis version 1.1.0' })).toBeVisible();
    await expect(page.getByText('Changed ', { exact: false })).toBeVisible();
  });

  test('a default can be set when none exists, without a loaded default (acceptance 7)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: false });
    await page.goto(versionPage('match-analysis', '1.0.0'));
    await page.getByRole('button', { name: 'Make default', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Make workflow version the default' });
    await expect(dialog).toContainText('No default workflow exists yet. Once this version is the default, new analysis runs can be created.');
    await dialog.getByRole('button', { name: 'Make default' }).click();
    await expect(dialog).toContainText('The workflow version is now the default.');
    expect(api.registryDefault?.workflowVersion).toBe('1.0.0');
    const put = api.requests.filter((r) => r.method === 'PUT' && r.path === '/registry/default-workflow');
    expect(put).toHaveLength(1);
    expect(put[0].headers['if-match']).toBeUndefined();
  });

  test('a version the platform cannot resolve does not become the default (acceptance 5)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto(versionPage('match-analysis', '1.1.0'));
    await page.getByRole('button', { name: 'Make default', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Make workflow version the default' });
    api.registryWorkflows.splice(
      api.registryWorkflows.findIndex((w) => w.workflowVersion === '1.1.0'),
      1,
    );
    await dialog.getByRole('button', { name: 'Make default' }).click();
    await expect(dialog).toContainText('The workflow version cannot be resolved and cannot become the default.');
    expect(api.registryDefault?.workflowVersion).toBe('1.0.0');
  });

  test('a default changed meanwhile shows the "has changed" message and the current default (acceptance 6, SC-006)', async ({ page, api }) => {
    api.signInAs('ClubAdmin');
    seed(api, { withDefault: true });
    await page.goto(versionPage('match-analysis', '1.1.0'));
    await page.getByRole('button', { name: 'Make default', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Make workflow version the default' });
    api.seedWorkflow({ ...api.registryWorkflows[0], workflowVersion: '1.2.0' });
    api.seedDefault({ workflowId: 'match-analysis', workflowVersion: '1.2.0' }, 2);
    await dialog.getByRole('button', { name: 'Make default' }).click();
    await expect(dialog).toContainText('The record has changed');
    await expect(dialog).toContainText('Current default: match-analysis version 1.2.0');
    expect(api.registryDefault?.workflowVersion).toBe('1.2.0');
  });
});
