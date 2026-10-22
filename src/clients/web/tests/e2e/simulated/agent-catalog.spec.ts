import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import type { SimulatedApi } from '../support/simulatedApi';

const NOT_FOUND = 'This catalog record was not found.';
const AGENT = 'specialist-tactics';

async function open(page: Page, path: string) {
  await page.goto(path);
  await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible();
  await expect(page.getByText(/^Loading/)).toHaveCount(0);
}

const versionDigest = (api: SimulatedApi, agentId: string) => api.catalogVersions.find((v) => v.agentId === agentId)!.digest;

test.beforeEach(async ({ api }) => {
  api.signInAs('ClubAdmin');
  // The seed offers the two enabled specialists only.
  api.agents = api.agents.filter((a) => ['specialist-tactics', 'specialist-video-analysis'].includes(a.agentId));
});

test.describe('Overview', () => {
  test('lists every catalog agent with the two offered specialists (acceptance 2)', async ({ page }) => {
    await open(page, '/agent-catalog');
    const table = page.getByRole('table', { name: 'Catalog agents' });
    await expect(table.getByRole('row')).toHaveCount(10);

    const tactics = table.getByRole('row').filter({ has: page.getByRole('link', { name: `Show the agent ${AGENT}` }) });
    await expect(tactics.getByRole('cell').nth(0)).toHaveText('Specialist Agent');
    await expect(tactics.getByRole('cell').nth(2)).toHaveText('Tactics Specialist');
    await expect(tactics.getByRole('cell').nth(3)).toHaveText('0.1.0');
    await expect(tactics.getByRole('cell').nth(4)).toHaveText('0 draft, 1 approved, 0 retired');
    await expect(tactics.getByRole('cell').nth(5)).toHaveText('Offered in triage conversations: 0.1.0');

    const offered = table.getByRole('cell', { name: /^Offered in triage conversations/ });
    await expect(offered).toHaveCount(2);
    await expect(table.getByRole('cell', { name: 'Not offered: no enabled version' })).toHaveCount(4);
    await expect(table.getByRole('cell', { name: 'Not available in conversations yet' })).toHaveCount(3);
    await expect(table.getByRole('cell', { name: 'none', exact: true })).toHaveCount(7);
    await expectNoAxeViolations(page);
  });

  test('opens an agent from the overview', async ({ page }) => {
    await open(page, '/agent-catalog');
    await page.getByRole('link', { name: `Show the agent ${AGENT}` }).click();
    await expect(page.getByRole('heading', { level: 2, name: `Agent ${AGENT}` })).toBeVisible();
  });
});

test.describe('Agent', () => {
  test('shows the baseline, enablement, and versions with the enabled marker (acceptance 3)', async ({ page }) => {
    await open(page, `/agent-catalog/agents/${AGENT}`);
    const baseline = page.getByRole('list', { name: 'Permitted-tool baseline' });
    await expect(baseline.getByRole('listitem').first()).toBeVisible();
    await expect(baseline.getByText('get_match', { exact: true })).toBeVisible();
    await expect(page.getByText('Enabled version: 0.1.0')).toBeVisible();

    const versions = page.getByRole('list', { name: 'Agent versions' });
    await expect(versions.getByRole('link', { name: '0.1.0 (approved)' })).toBeVisible();
    await expect(versions.getByText('enabled', { exact: true })).toBeVisible();
    await expect(versions.getByText(/^Registered /)).toBeVisible();
    await expectNoAxeViolations(page);
  });

  test('filters the versions by state (acceptance 3)', async ({ page, api }) => {
    api.seedCatalogAgentVersion(
      { schemaVersion: '1.0.0', kind: 'agent-version', agentId: AGENT, version: '0.2.0', group: 'specialist', displayName: 'Tactics Specialist', artifacts: {} },
      { state: 'draft' },
    );
    await open(page, `/agent-catalog/agents/${AGENT}`);
    const versions = page.getByRole('list', { name: 'Agent versions' });
    await expect(versions.getByRole('link')).toHaveCount(2);

    await page.getByLabel('State', { exact: true }).selectOption('draft');
    await expect(versions.getByRole('link')).toHaveCount(1);
    await expect(versions.getByRole('link', { name: '0.2.0 (draft)' })).toBeVisible();
    await expect(page).toHaveURL(/state=draft/);

    await page.getByLabel('State', { exact: true }).selectOption('retired');
    await expect(page.getByText('No versions are in this state.')).toBeVisible();

    await page.getByLabel('State', { exact: true }).selectOption('');
    await expect(versions.getByRole('link')).toHaveCount(2);
  });

  test('shows an agent without versions with the way to register one', async ({ page }) => {
    await open(page, '/agent-catalog/agents/specialist-fitness');
    await expect(page.getByText('No versions are registered for this agent.')).toBeVisible();
    await expect(page.getByText('No version is enabled.')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Register a version' })).toBeVisible();
  });
});

test.describe('Agent version', () => {
  test('shows every field with digests shortened and the artifact links (acceptance 4)', async ({ page, api }) => {
    await open(page, `/agent-catalog/agents/${AGENT}/versions/0.1.0`);
    await expect(page.getByRole('heading', { level: 2, name: `${AGENT} version 0.1.0` })).toBeVisible();
    const fields = page.locator('dl').first();
    await expect(fields.getByText('approved', { exact: true })).toBeVisible();
    await expect(fields.getByText('Tactics Specialist', { exact: true })).toBeVisible();

    const digest = versionDigest(api, AGENT);
    await expect(fields.locator('code').first()).toHaveText(`${digest.slice(0, 'sha-256:'.length + 12)}`);

    const artifacts = page.getByRole('heading', { level: 3, name: 'Artifact references' }).locator('xpath=following-sibling::ul[1]');
    await expect(artifacts.getByRole('listitem')).toHaveCount(4);
    for (const label of [
      `Prompt template ${AGENT}-prompt version 0.1.0`,
      'Model selection simulated-model version 0.1.0',
      `Tool policy ${AGENT}-tools version 0.1.0`,
      `Safety policy ${AGENT}-safety version 0.1.0`,
    ]) {
      await expect(artifacts.getByRole('link', { name: label })).toBeVisible();
    }

    await expect(page.getByText('Registered by Development seed')).toBeVisible();
    await expect(page.getByText('Approved by Development seed')).toBeVisible();
    await expect(page.getByText('This version is enabled.')).toBeVisible();
    await expect(page.getByText('The approval was a self-approval')).toHaveCount(0);
    await expect(page.getByRole('heading', { level: 3, name: 'Profile distances' })).toHaveCount(0);
    await expectNoAxeViolations(page);
  });

  test('reveals the full digest and copies it (acceptance 4, FR-010)', async ({ page, context, api }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await open(page, `/agent-catalog/agents/${AGENT}/versions/0.1.0`);
    const digest = versionDigest(api, AGENT);

    await page.getByRole('button', { name: 'Show full digest' }).first().click();
    await expect(page.getByLabel('Full digest').first()).toHaveText(digest);

    await page.getByRole('button', { name: 'Copy digest' }).first().click();
    await expect(page.getByText('Digest copied').first()).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(digest);
  });

  test('follows an artifact link to the artifact (acceptance 4)', async ({ page }) => {
    await open(page, `/agent-catalog/agents/${AGENT}/versions/0.1.0`);
    await page.getByRole('link', { name: `Prompt template ${AGENT}-prompt version 0.1.0` }).click();
    await expect(page.getByRole('heading', { level: 2, name: `${AGENT}-prompt version 0.1.0` })).toBeVisible();
  });
});

test.describe('Artifacts', () => {
  test('lists the records of a kind', async ({ page }) => {
    await open(page, '/agent-catalog/artifacts/prompt-templates');
    await expect(page.getByRole('link', { name: `${AGENT}-prompt version 0.1.0` })).toBeVisible();
    await expect(page.getByRole('link', { name: 'specialist-video-analysis-prompt version 0.1.0' })).toBeVisible();
  });

  test('shows a prompt template with every field and the text verbatim (acceptance 5, FR-021)', async ({ page, api }) => {
    const text = api.catalogArtifacts.find((a) => a.id === `${AGENT}-prompt`)!.document.text as string;
    await open(page, `/agent-catalog/artifacts/prompt-templates/${AGENT}-prompt/versions/0.1.0`);
    const fields = page.locator('dl').nth(1);
    await expect(fields.getByText('Specialist Agent', { exact: true })).toBeVisible();
    await expect(fields.getByText('Agent response, version 1', { exact: false })).toBeVisible();
    await expect(fields.getByText('No slots are declared.')).toBeVisible();
    await expect(page.getByText(/^Registered by /).first()).toBeVisible();

    const prompt = page.getByLabel('Prompt text', { exact: true });
    await expect(prompt).toHaveText(text);
    expect(await prompt.evaluate((el) => el.tagName)).toBe('PRE');
    expect(await prompt.evaluate((el) => getComputedStyle(el).whiteSpace)).toBe('pre-wrap');
    await expect(prompt).toContainText('Provide decision support; the human coach decides.');
    await expectNoAxeViolations(page);
  });

  test('shows a prompt text with markup as plain text (FR-021)', async ({ page, api }) => {
    const markup = '<img src=x onerror="window.__pwned=1">\n  indented **line**';
    api.seedCatalogArtifact({
      schemaVersion: '1.0.0',
      kind: 'prompt-template',
      id: 'markup-prompt',
      version: '1.0.0',
      group: 'specialist',
      responseContract: 'agent-response/1',
      text: markup,
      slots: [],
    });
    await open(page, '/agent-catalog/artifacts/prompt-templates/markup-prompt/versions/1.0.0');
    await expect(page.getByLabel('Prompt text', { exact: true })).toHaveText(markup);
    await expect(page.locator('main img')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined();
  });

  test('shows a model selection with every field (acceptance 6)', async ({ page }) => {
    await open(page, '/agent-catalog/artifacts/model-selections/simulated-model/versions/0.1.0');
    const fields = page.locator('dl').nth(1);
    await expect(fields.getByText('Simulated (Development and Test only)')).toBeVisible();
    await expect(fields.getByText('Provider')).toBeVisible();
    await expect(fields.getByText('Model', { exact: true })).toBeVisible();
    await expect(fields.getByText('Deployment or configuration name')).toBeVisible();
    await expect(fields.getByText('Generation parameters')).toBeVisible();
    await expect(fields.getByText('simulated', { exact: true })).toHaveCount(2);
    await expectNoAxeViolations(page);
  });

  test('shows a tool policy with the permitted tools as a list (acceptance 6)', async ({ page, api }) => {
    const tools = api.catalogArtifacts.find((a) => a.id === `${AGENT}-tools`)!.document.tools as string[];
    await open(page, `/agent-catalog/artifacts/tool-policies/${AGENT}-tools/versions/0.1.0`);
    const list = page.getByRole('list').filter({ hasText: tools[0] }).last();
    await expect(list.getByRole('listitem')).toHaveCount(tools.length);
    for (const tool of tools) await expect(list.getByText(tool, { exact: true })).toBeVisible();
    await expectNoAxeViolations(page);
  });

  test('shows a safety policy with routes, boundaries, categories, and rules in words (acceptance 6)', async ({ page }) => {
    await open(page, `/agent-catalog/artifacts/safety-policies/${AGENT}-safety/versions/0.1.0`);
    const fields = page.locator('dl').nth(1);
    await expect(fields.getByText('User to Specialist Agent')).toBeVisible();
    await expect(fields.getByText('Coach Agent to Specialist Agent')).toBeVisible();
    await expect(page.getByLabel('Hard boundaries', { exact: true })).toHaveText('Never reveal raw recordings or unauthorized team data.');
    for (const word of ['Medical', 'Safeguarding', 'Welfare', 'Privacy', 'Raw data access', 'Unauthorized data']) {
      await expect(fields.getByText(word, { exact: true }).first()).toBeVisible();
    }
    for (const word of ['Evidence citation', 'Tool allowlist only', 'No raw data access']) {
      await expect(fields.getByText(word, { exact: true })).toBeVisible();
    }
    await expectNoAxeViolations(page);
  });

  test('shows a kind without a dedicated view as its stored document', async ({ page, api }) => {
    api.seedCatalogArtifact({ schemaVersion: '1.0.0', kind: 'modifier-policy', id: 'bounded-modifiers', version: '1.0.0', perDimensionBound: 2 });
    await open(page, '/agent-catalog/artifacts/modifier-policies/bounded-modifiers/versions/1.0.0');
    await expect(page.getByText('This kind has no dedicated view yet.')).toBeVisible();
    await expect(page.getByLabel('Stored document')).toContainText('"perDimensionBound": 2');
  });
});

test.describe('Baseline', () => {
  test('shows the tool allowlist and the agents of the baseline', async ({ page }) => {
    await open(page, '/agent-catalog/baseline');
    await expect(page.getByRole('heading', { level: 2, name: 'Catalog baseline' })).toBeVisible();
    await expect(page.getByRole('heading', { level: 3, name: 'Tool allowlist' })).toBeVisible();
    await expectNoAxeViolations(page);
  });
});

test.describe('Not found', () => {
  const addresses = [
    '/agent-catalog/agents/unknown-agent',
    `/agent-catalog/agents/${AGENT}/versions/9.9.9`,
    '/agent-catalog/artifacts/unknown-kind',
    '/agent-catalog/artifacts/prompt-templates/missing-prompt/versions/1.0.0',
    '/agent-catalog/artifacts/prompt-templates/Not_Kebab/versions/1.0.0',
    `/agent-catalog/artifacts/prompt-templates/${AGENT}-prompt/versions/not-a-version`,
    '/agent-catalog/artifacts/unknown-kind/some-id/versions/1.0.0',
  ];
  for (const path of addresses) {
    test(`${path} shows the not-found view (acceptance 7)`, async ({ page }) => {
      await page.goto(path);
      await expect(page.getByRole('alert')).toContainText(NOT_FOUND);
      await expectNoAxeViolations(page);
    });
  }

  test('an invalid artifact address sends no request to the catalog (FR-006)', async ({ page }) => {
    const requests: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/v1/agent-catalog/artifacts/')) requests.push(request.url());
    });
    await page.goto('/agent-catalog/artifacts/prompt-templates/Not_Kebab/versions/1.0.0');
    await expect(page.getByRole('alert')).toContainText(NOT_FOUND);
    await page.goto(`/agent-catalog/artifacts/prompt-templates/${AGENT}-prompt/versions/one`);
    await expect(page.getByRole('alert')).toContainText(NOT_FOUND);
    expect(requests).toEqual([]);
  });
});
