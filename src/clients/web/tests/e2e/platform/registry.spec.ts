import { expect, test as base } from '../support/fixtures';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD]);
  },
});

const run = Date.now().toString(36);
// The development seed (Development and Test) registers this workflow as the default; the AppHost has no OCI registry.
const SEEDED_WORKFLOW = 'development-default';
const SEEDED_SEGMENT_IMAGE_DIGEST = `sha256:${'3'.repeat(64)}`;
const newWorkflowVersion = `e2e-${run}`;

// This spec changes the default workflow, so it runs last in the chain of platform projects.
test('browses the seeded Registry, versions a profile and a workflow, makes it the default, and explains references (FR-023)', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  // Browse the seeded default workflow and its nodes.
  await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Analyst Registry', exact: true }).click();
  await expect(page).toHaveURL(/\/registry$/);
  await page.getByRole('link', { name: `${SEEDED_WORKFLOW} version 1` }).click();
  await expect(page.getByText('This is the default workflow version.')).toBeVisible();
  const seededNodes = page.getByRole('list', { name: 'Nodes' }).getByRole('listitem');
  await expect(seededNodes).toHaveCount(3);
  await expect(seededNodes.filter({ hasText: 'dev-segment-node' })).toHaveCount(1);

  // Create a profile version from the seeded segment declaration with one execution value changed.
  await page.goto('/registry/images');
  await page.getByRole('list', { name: 'Analyst images' }).getByRole('link', { name: /^dev-segment-capability/ }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Analyst image' })).toBeVisible();
  await page.getByRole('link', { name: 'New profile version' }).click();
  await expect(page.getByLabel('Profile id')).toHaveValue('dev-profile');
  await page.getByLabel('Maximum attempts').fill('3');
  await page.getByLabel('Lease (seconds)').fill('30');
  await page.getByLabel('Stale timeout (seconds)').fill('90');
  await page.getByLabel('Execution timeout (seconds)').fill('7200');
  await page.getByRole('button', { name: 'Create profile version' }).click();
  await expect(page.getByText('Profile version created')).toBeVisible();
  await page.getByRole('link', { name: 'Show the profile version' }).click();
  await expect(page).toHaveURL(/\/registry\/profiles\/dev-profile\/versions\/\d+$/);
  const profileVersion = /\/versions\/(\d+)$/.exec(page.url())![1];

  // Approve it.
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  const approve = page.getByRole('dialog', { name: 'Approve profile version' });
  await expect(approve).toContainText(`dev-profile version ${profileVersion}`);
  await approve.getByRole('button', { name: 'Approve version' }).click();
  await expect(approve).toContainText('Profile version approved.');
  await approve.getByRole('button', { name: 'Close' }).click();

  // Define a workflow version equivalent to the seeded one that uses the new profile version.
  await page.goto(`/registry/workflows/${SEEDED_WORKFLOW}/versions/1`);
  await page.getByRole('link', { name: 'New version from this' }).click();
  await expect(page.getByText(`Started from ${SEEDED_WORKFLOW} version 1.`)).toBeVisible();
  await page.getByLabel('Workflow version').fill(newWorkflowVersion);
  const segmentNodeIndex = (await page.getByLabel(/^Node key \d+$/).evaluateAll((inputs) => inputs.map((i) => (i as HTMLInputElement).value))).indexOf(
    'dev-segment-node',
  );
  expect(segmentNodeIndex, 'the segment node is part of the started draft').toBeGreaterThanOrEqual(0);
  await page.getByLabel(`Approved profile version ${segmentNodeIndex + 1}`).selectOption({ label: `dev-profile version ${profileVersion}` });
  await page.getByRole('button', { name: 'Define workflow version' }).click();
  await expect(page.getByText('Workflow version defined')).toBeVisible();

  // Make it the default.
  await page.getByRole('link', { name: 'Show the workflow version' }).click();
  await expect(page).toHaveURL(new RegExp(`/registry/workflows/${SEEDED_WORKFLOW}/versions/${newWorkflowVersion}$`));
  await page.getByRole('button', { name: 'Make default' }).click();
  const makeDefault = page.getByRole('dialog', { name: 'Make workflow version the default' });
  await makeDefault.getByRole('button', { name: 'Make default' }).click();
  await expect(makeDefault).toContainText('The workflow version is now the default.');
  await makeDefault.getByRole('button', { name: 'Close' }).click();
  await page.goto('/registry');
  await expect(page.getByRole('link', { name: `${SEEDED_WORKFLOW} version ${newWorkflowVersion}` })).toBeVisible();

  // A digest reference of a seeded declaration is already registered; the platform contacts no image registry.
  await page.goto('/registry/images/register');
  await page.getByLabel('Image reference').fill(`registry.example/analysts/dev-segment@${SEEDED_SEGMENT_IMAGE_DIGEST}`);
  await page.getByRole('button', { name: 'Register image' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Analyst image' })).toBeVisible();
  await expect(page.getByText('The image was already registered, nothing changed.')).toBeVisible();

  // A tag reference is explained.
  await page.goto('/registry/images/register');
  await page.getByLabel('Image reference').fill('registry.example/analysts/dev-segment:latest');
  await page.getByRole('button', { name: 'Register image' }).click();
  await expect(page.getByText('The reference must contain @sha256: followed by 64 hexadecimal characters.')).toBeVisible();
});
