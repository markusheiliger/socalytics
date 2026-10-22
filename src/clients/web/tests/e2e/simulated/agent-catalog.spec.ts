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

test.describe('Enablement', () => {
  const secondVersion = (version = '0.2.0') =>
    ({ schemaVersion: '1.0.0', kind: 'agent-version', agentId: AGENT, version, group: 'specialist', displayName: 'Tactics Specialist', artifacts: {} });
  const dialog = (page: Page) => page.getByRole('dialog');
  const alertOf = (page: Page, text: string) => dialog(page).getByRole('alert').filter({ hasText: text });
  const enablementOf = (api: SimulatedApi) => api.catalogEnablements.find((e) => e.agentId === AGENT)!.enabledVersion;

  async function startChange(page: Page, version: string) {
    await page.getByLabel('New enabled version').selectOption(version);
    await page.getByRole('button', { name: 'Change enabled version' }).click();
    await expect(dialog(page)).toBeVisible();
  }

  test('enables a second approved version after the confirmation (acceptance 1, SC-006)', async ({ page, api }) => {
    api.seedCatalogAgentVersion(secondVersion(), { state: 'approved' });
    await open(page, `/agent-catalog/agents/${AGENT}`);
    await startChange(page, '0.2.0');
    await expect(dialog(page)).toContainText('Currently enabled: 0.1.0');
    await expect(dialog(page)).toContainText('New enabled version: 0.2.0');
    await expect(dialog(page)).toContainText('Only questions submitted afterwards use the change.');
    await expect(dialog(page)).toContainText('keep their pinned versions');
    await expectNoAxeViolations(page);

    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(dialog(page).getByRole('status')).toContainText('The enablement was changed.');
    await dialog(page).getByRole('button', { name: 'Close' }).click();
    await expect(dialog(page)).toHaveCount(0);

    expect(enablementOf(api)).toBe('0.2.0');
    await expect(page.getByText('Enabled version: 0.2.0')).toBeVisible();
    await expect(page.getByText(/^Changed .* by /)).toBeVisible();
    await expect(page.getByRole('list', { name: 'Agent versions' }).getByText('enabled', { exact: true })).toHaveCount(1);

    await open(page, '/agent-catalog');
    const row = page.getByRole('row').filter({ has: page.getByRole('link', { name: `Show the agent ${AGENT}` }) });
    await expect(row.getByRole('cell').nth(5)).toHaveText('Offered in triage conversations: 0.2.0');
  });

  test('enables an approved version from its own page', async ({ page, api }) => {
    api.seedCatalogAgentVersion(secondVersion(), { state: 'approved' });
    await open(page, `/agent-catalog/agents/${AGENT}/versions/0.2.0`);
    await expect(page.getByText('This version is not enabled.')).toBeVisible();
    await page.getByRole('button', { name: 'Enable', exact: true }).click();
    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(dialog(page).getByRole('status')).toBeVisible();
    expect(enablementOf(api)).toBe('0.2.0');
  });

  test('disables the agent and enables it again (acceptance 2, SC-006)', async ({ page, api }) => {
    await open(page, `/agent-catalog/agents/${AGENT}`);
    await page.getByRole('button', { name: 'Disable', exact: true }).click();
    await expect(dialog(page)).toContainText('New enabled version: none (the agent is disabled)');
    await expect(dialog(page)).toContainText('Members can no longer add or ask the agent until a version is enabled again.');
    await expectNoAxeViolations(page);
    await dialog(page).getByRole('button', { name: 'Disable agent' }).click();
    await expect(dialog(page).getByRole('status')).toBeVisible();
    await dialog(page).getByRole('button', { name: 'Close' }).click();

    expect(enablementOf(api)).toBeNull();
    await expect(page.getByText('No version is enabled.')).toBeVisible();
    await open(page, '/agent-catalog');
    const row = page.getByRole('row').filter({ has: page.getByRole('link', { name: `Show the agent ${AGENT}` }) });
    await expect(row.getByRole('cell').nth(5)).toHaveText('Not offered: no enabled version');

    await open(page, `/agent-catalog/agents/${AGENT}/versions/0.1.0`);
    await page.getByRole('button', { name: 'Enable', exact: true }).click();
    await expect(dialog(page)).toContainText('Currently enabled: none');
    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(dialog(page).getByRole('status')).toBeVisible();
    expect(enablementOf(api)).toBe('0.1.0');
  });

  test('cancelling the confirmation sends nothing and changes nothing (acceptance 3)', async ({ page, api }) => {
    api.seedCatalogAgentVersion(secondVersion(), { state: 'approved' });
    await open(page, `/agent-catalog/agents/${AGENT}`);
    const puts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'PUT' && request.url().includes('/agent-catalog/enablements/')) puts.push(request.url());
    });
    await startChange(page, '0.2.0');
    await dialog(page).getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog(page)).toHaveCount(0);
    await page.getByRole('button', { name: 'Disable', exact: true }).click();
    await dialog(page).getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog(page)).toHaveCount(0);

    expect(puts).toEqual([]);
    expect(enablementOf(api)).toBe('0.1.0');
    await expect(page.getByText('Enabled version: 0.1.0')).toBeVisible();
  });

  test('explains the refusal of a version that became a draft (acceptance 4)', async ({ page, api }) => {
    const version = api.seedCatalogAgentVersion(secondVersion(), { state: 'approved' });
    await open(page, `/agent-catalog/agents/${AGENT}`);
    await startChange(page, '0.2.0');
    version.state = 'draft';
    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(alertOf(page, 'This version cannot be enabled.')).toBeVisible();
    await expect(alertOf(page, 'The version is not approved.')).toBeVisible();
    await expect(dialog(page).getByRole('button', { name: 'Enable version' })).toHaveCount(0);
    await expectNoAxeViolations(page);
    expect(enablementOf(api)).toBe('0.1.0');
  });

  test('explains the refusal of a retired version (acceptance 4)', async ({ page, api }) => {
    const version = api.seedCatalogAgentVersion(secondVersion(), { state: 'approved' });
    await open(page, `/agent-catalog/agents/${AGENT}`);
    await startChange(page, '0.2.0');
    version.state = 'retired';
    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(alertOf(page, 'This version cannot be enabled.')).toBeVisible();
    await expect(alertOf(page, 'The version is retired.')).toBeVisible();
    expect(enablementOf(api)).toBe('0.1.0');
  });

  test('explains the refusal of the simulated provider (acceptance 4)', async ({ page, api }) => {
    api.seedCatalogAgentVersion(secondVersion(), { state: 'approved' });
    await open(page, `/agent-catalog/agents/${AGENT}`);
    api.refuseCatalogNext('setAgentEnablement', { status: 409, code: 'agent-version-not-enableable', extra: { reason: 'simulated-provider' } });
    await startChange(page, '0.2.0');
    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(alertOf(page, 'This version cannot be enabled.')).toBeVisible();
    await expect(alertOf(page, 'which is permitted only in Development and Test')).toBeVisible();
    expect(enablementOf(api)).toBe('0.1.0');
  });

  test('states the Coach Agent note and explains the refusal (acceptance 5)', async ({ page, api }) => {
    api.seedCatalogAgentVersion(
      {
        schemaVersion: '1.0.0',
        kind: 'agent-version',
        agentId: 'coach-jurgen-klopp',
        version: '1.0.0',
        group: 'coach',
        displayName: 'Coach Agent',
        disclosure: 'A simulation of a coaching philosophy.',
        artifacts: {},
      },
      { state: 'approved' },
    );
    await open(page, '/agent-catalog/agents/coach-jurgen-klopp/versions/1.0.0');
    await page.getByRole('button', { name: 'Enable', exact: true }).click();
    await expect(dialog(page)).toContainText('The platform may refuse while Coach Agents are not available in conversations.');
    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(alertOf(page, 'The platform does not support enabling Coach Agents yet.')).toBeVisible();
    expect(api.catalogEnablements.find((e) => e.agentId === 'coach-jurgen-klopp')!.enabledVersion).toBeNull();
  });

  test('says that the enablement changed and never repeats the change (acceptance 6)', async ({ page, api }) => {
    api.seedCatalogAgentVersion(secondVersion(), { state: 'approved' });
    await open(page, `/agent-catalog/agents/${AGENT}`);
    await startChange(page, '0.2.0');

    // Another session changes the enablement after this page loaded it.
    api.changeCatalogBehindTheScenes({ agentId: AGENT });
    const puts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'PUT' && request.url().includes('/agent-catalog/enablements/')) puts.push(request.url());
    });
    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(alertOf(page, 'has changed since you loaded it')).toBeVisible();
    await expect(dialog(page)).toContainText('Currently enabled: 0.1.0');
    await expect(dialog(page).getByRole('button', { name: 'Enable version' })).toHaveCount(0);
    await expectNoAxeViolations(page);

    expect(puts).toHaveLength(1);
    expect(enablementOf(api)).toBe('0.1.0');
  });
});

test.describe('Registration', () => {
  const PROMPT = `${AGENT}-prompt`;
  const textbox = (page: Page, name: RegExp) => page.getByRole('textbox', { name });
  const register = (page: Page) => page.getByRole('button', { name: 'Register', exact: true }).click();
  const fromPrompt = `/agent-catalog/artifacts/prompt-templates/new?fromId=${PROMPT}&fromVersion=0.1.0`;
  const refusalAlert = (page: Page, text: string | RegExp) => page.getByRole('alert').filter({ hasText: text });

  test('registers a prompt template from the seeded one with the proposed 0.1.1 (acceptance 2, 7; SC-003)', async ({ page, api }) => {
    await open(page, fromPrompt);
    await expect(textbox(page, /^Identifier/)).toHaveValue(PROMPT);
    await expect(textbox(page, /^Version/)).toHaveValue('0.1.1');
    await expect(textbox(page, /^Prompt text/)).toContainText('Provide decision support');
    await expectNoAxeViolations(page);

    await textbox(page, /^Prompt text/).fill('Revised: provide decision support; the human coach decides.');
    await register(page);
    await expect(page).toHaveURL(new RegExp(`/agent-catalog/artifacts/prompt-templates/${PROMPT}/versions/0\\.1\\.1$`));
    await expect(page.getByRole('heading', { level: 2, name: `${PROMPT} version 0.1.1` })).toBeVisible();
    const stored = api.catalogArtifacts.find((a) => a.id === PROMPT && a.version === '0.1.1')!;
    expect(stored.document.text).toBe('Revised: provide decision support; the human coach decides.');
    expect(stored.registeredBy).not.toBe('system:seed');
  });

  test('proposes 0.1.0 for a new identifier (acceptance 7)', async ({ page }) => {
    await open(page, '/agent-catalog/artifacts/prompt-templates/new');
    await textbox(page, /^Identifier/).fill('fresh-prompt');
    await expect(textbox(page, /^Version/)).toHaveValue('0.1.0');
  });

  test('refuses a tool outside the allowlist next to the tool and keeps the entries (acceptance 3)', async ({ page, api }) => {
    api.seedCatalogArtifact({ schemaVersion: '1.0.0', kind: 'tool-policy', id: 'legacy-tools', version: '1.0.0', tools: ['get_match', 'bogus_tool'] });
    await open(page, '/agent-catalog/artifacts/tool-policies/new?fromId=legacy-tools&fromVersion=1.0.0');
    await expect(textbox(page, /^Version/)).toHaveValue('1.0.1');
    await register(page);

    await expect(refusalAlert(page, 'The platform refused the registration')).toBeVisible();
    const tool = page.getByRole('group', { name: 'Permitted tools' }).locator('div').filter({ has: page.getByRole('checkbox', { name: 'bogus_tool' }) }).last();
    await expect(tool.getByRole('alert')).toContainText('is not on the tool allowlist');
    await expect(page.getByRole('checkbox', { name: 'get_match' })).toBeChecked();
    await expect(page.getByRole('checkbox', { name: 'bogus_tool' })).toBeChecked();
    await expect(page).toHaveURL(/\/artifacts\/tool-policies\/new/);
    expect(api.catalogArtifacts.some((a) => a.id === 'legacy-tools' && a.version === '1.0.1')).toBe(false);
  });

  test('refuses an out-of-baseline tool next to the tool list and hints it beforehand (acceptance 1, 3)', async ({ page, api }) => {
    await open(page, '/agent-catalog/artifacts/tool-policies/new?agentId=specialist-video-analysis');
    await textbox(page, /^Identifier/).fill('video-tools');
    const outside = page.getByRole('checkbox', { name: 'get_player_stats' });
    await outside.check();
    await page.getByRole('checkbox', { name: 'get_match' }).check();
    await expect(page.getByText('Outside the baseline of specialist-video-analysis')).toHaveCount(1);
    api.refuseCatalogNext('registerAgentCatalogArtifact', {
      status: 400,
      code: 'agent-catalog-validation-failed',
      extra: { violations: [{ code: 'tool-outside-baseline', pointer: '/tools/0', detail: 'get_player_stats' }] },
    });
    await register(page);

    const tool = page.getByRole('group', { name: 'Permitted tools' }).locator('div').filter({ has: outside }).last();
    await expect(tool.getByRole('alert')).toContainText('outside the permitted-tool baseline of this agent');
    await expect(outside).toBeChecked();
    await expect(page.getByRole('checkbox', { name: 'get_match' })).toBeChecked();
    expect(api.catalogArtifacts.some((a) => a.id === 'video-tools')).toBe(false);
  });

  test('refuses a model selection with an invalid name next to the field (acceptance 3)', async ({ page, api }) => {
    await open(page, '/agent-catalog/artifacts/model-selections/new');
    await textbox(page, /^Identifier/).fill('bad-model');
    await textbox(page, /^Model/).fill('bad model!');
    await textbox(page, /^Deployment/).fill('deployment-1');
    api.refuseCatalogNext('registerAgentCatalogArtifact', {
      status: 400,
      code: 'agent-catalog-validation-failed',
      extra: { violations: [{ code: 'schema-violation', pointer: '/model', detail: 'The name does not match the pattern.' }] },
    });
    await register(page);

    await expect(refusalAlert(page, 'The record does not match its format')).toHaveCount(1);
    await expect(textbox(page, /^Model/)).toHaveValue('bad model!');
    await expect(textbox(page, /^Deployment/)).toHaveValue('deployment-1');
    await expect(textbox(page, /^Identifier/)).toHaveValue('bad-model');
    await expect(page.getByText('Problems without a field')).toHaveCount(0);
    expect(api.catalogArtifacts.some((a) => a.id === 'bad-model')).toBe(false);
  });

  test('lists a violation without a known location in the summary above the form (acceptance 3)', async ({ page, api }) => {
    await open(page, fromPrompt);
    api.refuseCatalogNext('registerAgentCatalogArtifact', {
      status: 400,
      code: 'agent-catalog-validation-failed',
      extra: { violations: [{ code: 'schema-violation', pointer: '', detail: 'The record is not an object of this kind.' }] },
    });
    await register(page);

    const summary = refusalAlert(page, 'Problems without a field');
    await expect(summary).toBeVisible();
    await expect(summary).toContainText('The record does not match its format');
    await expect(textbox(page, /^Prompt text/)).toContainText('Provide decision support');
    await expectNoAxeViolations(page);
  });

  test('says "already registered, nothing changed" for equal content (acceptance 4)', async ({ page, api }) => {
    await open(page, fromPrompt);
    await textbox(page, /^Version/).fill('0.1.0');
    const before = api.catalogArtifacts.length;
    await register(page);

    await expect(page.getByRole('status').filter({ hasText: 'Already registered, nothing changed.' })).toBeVisible();
    await page.getByRole('link', { name: 'Show the stored record' }).click();
    await expect(page.getByRole('heading', { level: 2, name: `${PROMPT} version 0.1.0` })).toBeVisible();
    expect(api.catalogArtifacts).toHaveLength(before);
  });

  test('explains that an existing version with different content never changes (acceptance 4)', async ({ page, api }) => {
    await open(page, fromPrompt);
    await textbox(page, /^Version/).fill('0.1.0');
    await textbox(page, /^Prompt text/).fill('Different content for the same version.');
    const before = api.catalogArtifacts.length;
    await register(page);

    await expect(refusalAlert(page, 'registered versions never change')).toBeVisible();
    await expect(textbox(page, /^Prompt text/)).toHaveValue('Different content for the same version.');
    expect(api.catalogArtifacts).toHaveLength(before);
    expect(api.catalogArtifacts.find((a) => a.id === PROMPT && a.version === '0.1.0')!.document.text).not.toBe('Different content for the same version.');
  });

  test('registers agent version 0.2.0 from 0.1.0 and shows it as a draft (acceptance 5, 6; SC-003)', async ({ page, api }) => {
    const prompt = api.seedCatalogArtifact({
      schemaVersion: '1.0.0',
      kind: 'prompt-template',
      id: PROMPT,
      version: '0.1.1',
      group: 'specialist',
      responseContract: 'agent-response/1',
      text: 'Revised prompt.',
      slots: [],
    });
    await open(page, `/agent-catalog/agents/${AGENT}/versions/new?from=0.1.0`);
    await expect(textbox(page, /^Display name/)).toHaveValue('Tactics Specialist');
    await expect(page.getByLabel(/^Prompt template/)).toHaveValue(`${PROMPT}@0.1.0`);
    await expect(textbox(page, /^Version/)).toHaveValue('0.1.1');

    await textbox(page, /^Version/).fill('0.2.0');
    await page.getByLabel(/^Prompt template/).selectOption(`${PROMPT}@0.1.1`);
    await register(page);

    await expect(page).toHaveURL(new RegExp(`/agent-catalog/agents/${AGENT}/versions/0\\.2\\.0$`));
    await expect(page.getByRole('heading', { level: 2, name: `${AGENT} version 0.2.0` })).toBeVisible();
    await expect(page.locator('dl').first().getByText('draft', { exact: true })).toBeVisible();
    const stored = api.catalogVersions.find((v) => v.agentId === AGENT && v.version === '0.2.0')!;
    expect(stored.state).toBe('draft');
    expect(stored.document.artifacts.promptTemplate).toMatchObject({ id: PROMPT, version: '0.1.1', digest: prompt.digest });
    await expect(page.locator('dl').first().locator('code').first()).toHaveText(stored.digest.slice(0, 'sha-256:'.length + 12));
  });
});
