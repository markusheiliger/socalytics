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

// The Development and Test seed registers and enables version 0.1.0 of the two direct Specialist Agents.
const AGENT = 'specialist-tactics';
const PROMPT = `${AGENT}-prompt`;

// This spec changes the enabled version of a specialist, so it runs after every other platform project.
test('browses the seeded catalog, registers, approves, and enables agent version 0.2.0, and sees it offered (FR-023)', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(ADMIN_ACCOUNT);
  await page.getByLabel('Password').fill(ADMIN_CHANGED_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();

  // Browse the seeded catalog: the specialist is offered with its seeded version.
  await page.goto('/agent-catalog');
  const table = page.getByRole('table', { name: 'Catalog agents' });
  const row = table.getByRole('row').filter({ has: page.getByRole('link', { name: `Show the agent ${AGENT}` }) });
  await expect(row.getByRole('cell').nth(5)).toHaveText('Offered in triage conversations: 0.1.0', { timeout: 15_000 });
  await page.getByRole('link', { name: `Show the agent ${AGENT}` }).click();
  await expect(page.getByRole('heading', { level: 2, name: `Agent ${AGENT}` })).toBeVisible();
  await expect(page.getByText('Enabled version: 0.1.0')).toBeVisible();
  await expect(page.getByRole('list', { name: 'Agent versions' }).getByRole('link', { name: '0.1.0 (approved)' })).toBeVisible();

  // Register prompt template 0.1.1 from the seeded prompt.
  await page.goto(`/agent-catalog/artifacts/prompt-templates/new?fromId=${PROMPT}&fromVersion=0.1.0`);
  await expect(page.getByRole('textbox', { name: /^Identifier/ })).toHaveValue(PROMPT);
  await expect(page.getByRole('textbox', { name: /^Version/ })).toHaveValue('0.1.1');
  await page.getByRole('textbox', { name: /^Prompt text/ }).fill('Revised: provide decision support; the human coach decides.');
  await page.getByRole('button', { name: 'Register', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/agent-catalog/artifacts/prompt-templates/${PROMPT}/versions/0\\.1\\.1$`));
  await expect(page.getByRole('heading', { level: 2, name: `${PROMPT} version 0.1.1` })).toBeVisible();

  // Register agent version 0.2.0 from 0.1.0 with the new prompt.
  await page.goto(`/agent-catalog/agents/${AGENT}/versions/new?from=0.1.0`);
  await expect(page.getByLabel(/^Prompt template/)).toHaveValue(`${PROMPT}@0.1.0`);
  await page.getByRole('textbox', { name: /^Version/ }).fill('0.2.0');
  await page.getByLabel(/^Prompt template/).selectOption(`${PROMPT}@0.1.1`);
  await page.getByRole('button', { name: 'Register', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/agent-catalog/agents/${AGENT}/versions/0\\.2\\.0$`));
  await expect(page.getByRole('heading', { level: 2, name: `${AGENT} version 0.2.0` })).toBeVisible();
  await expect(page.locator('dl').first().getByText('draft', { exact: true })).toBeVisible();

  // Approve it; the member registered it, so the approval is a self-approval.
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  const approve = page.getByRole('dialog');
  await expect(approve).toContainText('The approval is recorded as a self-approval.');
  await approve.getByRole('button', { name: 'Approve version' }).click();
  await expect(approve.getByRole('status')).toBeVisible();
  await approve.getByRole('button', { name: 'Close' }).click();
  await expect(page.locator('dl').first().getByText('approved', { exact: true })).toBeVisible();
  await expect(page.getByText('The approval was a self-approval')).toBeVisible();

  // Enable it.
  await page.getByRole('button', { name: 'Enable', exact: true }).click();
  const enable = page.getByRole('dialog');
  await enable.getByRole('button', { name: 'Enable version' }).click();
  await expect(enable.getByRole('status')).toContainText('The enablement was changed.');
  await enable.getByRole('button', { name: 'Close' }).click();

  // Triage conversations offer the enabled version.
  await page.goto('/agent-catalog');
  const updated = page.getByRole('table', { name: 'Catalog agents' }).getByRole('row').filter({ has: page.getByRole('link', { name: `Show the agent ${AGENT}` }) });
  await expect(updated.getByRole('cell').nth(5)).toHaveText('Offered in triage conversations: 0.2.0', { timeout: 15_000 });
});
