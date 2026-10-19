import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import type { RegistryImage, RegistryProfile, RegistryWorkflow, SimulatedApi } from '../support/simulatedApi';

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
