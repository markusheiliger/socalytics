import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import type { SimulatedApi } from '../support/simulatedApi';

const SET = 'coach-philosophy-dimensions';
const COACH = 'coach-jurgen-klopp';
const OTHER_COACH = 'coach-pep-guardiola';
const DISCLOSURE = 'A simulation of a coaching philosophy, not the coach.';

async function open(page: Page, path: string) {
  await page.goto(path);
  await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible();
  await expect(page.getByText(/^Loading/)).toHaveCount(0);
}

const textbox = (page: Page, name: RegExp) => page.getByRole('textbox', { name });
const register = (page: Page) => page.getByRole('button', { name: 'Register', exact: true }).click();
const alertWith = (page: Page, text: string | RegExp) => page.getByRole('alert').filter({ hasText: text });
const dialog = (page: Page) => page.getByRole('dialog');

function seedSet(api: SimulatedApi) {
  return api.seedCatalogArtifact({
    schemaVersion: '1.0.0',
    kind: 'dimension-set',
    id: SET,
    version: '1.0.0',
    dimensions: [
      { id: 'pressing', meaning: 'How hard the team presses.', acceptedEvidence: ['get_match'] },
      { id: 'possession', meaning: 'How much the team keeps the ball.', acceptedEvidence: ['get_match'] },
    ],
  });
}

function profileDocument(id: string, pressing: number, possession: number) {
  return {
    schemaVersion: '1.0.0',
    kind: 'philosophy-profile',
    id,
    version: '1.0.0',
    dimensionSet: { id: SET, version: '1.0.0' },
    weights: {
      pressing: { weight: pressing, rationale: 'Pressing rationale.', provenance: 'Pressing provenance.' },
      possession: { weight: possession, rationale: 'Possession rationale.', provenance: 'Possession provenance.' },
    },
    author: 'Author A',
    reviewers: { architecture: ['Reviewer Arch'], footballDomain: ['Reviewer Football'] },
  };
}

/** Seeds an approved Coach Agent version whose profile is the given one, so that new profiles have distances to it. */
function seedOtherCoach(api: SimulatedApi) {
  const profile = api.seedCatalogArtifact(profileDocument('guardiola-profile', 55, 45));
  api.seedCatalogAgentVersion(
    {
      schemaVersion: '1.0.0',
      kind: 'agent-version',
      agentId: OTHER_COACH,
      version: '1.0.0',
      group: 'coach',
      displayName: 'Guardiola simulation',
      disclosure: DISCLOSURE,
      artifacts: { philosophyProfile: { id: profile.id, version: profile.version, digest: profile.digest } },
    },
    { state: 'approved' },
  );
}

async function fillRow(page: Page, id: string, weight: number) {
  await page.getByRole('spinbutton', { name: new RegExp(`^Weight of ${id}`) }).fill(String(weight));
  await textbox(page, new RegExp(`^Rationale of ${id}`)).fill(`${id} rationale`);
  await textbox(page, new RegExp(`^Provenance of ${id}`)).fill(`${id} provenance`);
}

test.beforeEach(async ({ api }) => {
  api.signInAs('ClubAdmin');
});

test.describe('Coach Agent artifacts', () => {
  test('registers a dimension set and shows the stored record (US5 acceptance 3)', async ({ page, api }) => {
    await open(page, '/agent-catalog/artifacts/dimension-sets/new');
    await textbox(page, /^Identifier(?! of)/).fill(SET);
    await expect(textbox(page, /^Version/)).toHaveValue('0.1.0');
    await textbox(page, /^Version/).fill('1.0.0');

    await page.getByRole('button', { name: 'Add dimension', exact: true }).click();
    await textbox(page, /^Identifier of dimension 1/).fill('pressing');
    await textbox(page, /^Meaning of dimension 1/).fill('How hard the team presses.');
    await page.getByRole('group', { name: /^Accepted evidence tools of dimension 1/ }).getByRole('checkbox', { name: 'get_match' }).check();
    await expectNoAxeViolations(page);
    await register(page);

    await expect(page).toHaveURL(new RegExp(`/agent-catalog/artifacts/dimension-sets/${SET}/versions/1\\.0\\.0$`));
    await expect(page.getByRole('heading', { level: 2, name: `${SET} version 1.0.0` })).toBeVisible();
    const stored = api.catalogArtifacts.find((a) => a.id === SET && a.version === '1.0.0')!;
    expect(stored.document.dimensions).toEqual([{ id: 'pressing', meaning: 'How hard the team presses.', acceptedEvidence: ['get_match'] }]);
  });

  test('a 99-point profile shows the running-total hint and the located refusal, then 100 is stored with its distances (US5 acceptance 2, 3)', async ({ page, api }) => {
    seedSet(api);
    seedOtherCoach(api);
    await open(page, '/agent-catalog/artifacts/philosophy-profiles/new');
    await textbox(page, /^Identifier/).fill('klopp-profile');
    await textbox(page, /^Version/).fill('1.0.0');

    await page.getByRole('combobox', { name: /^Dimension set version/ }).click();
    await page.getByRole('option', { name: `${SET} 1.0.0` }).click();
    await expect(page.getByRole('group', { name: 'Dimension pressing' })).toBeVisible();
    await expect(page.getByRole('group', { name: 'Dimension possession' })).toBeVisible();

    await fillRow(page, 'pressing', 50);
    await fillRow(page, 'possession', 49);
    await textbox(page, /^Author/).fill('Author A');
    await textbox(page, /^Architecture reviewer 1/).fill('Reviewer Arch');
    await textbox(page, /^Football domain reviewer 1/).fill('Reviewer Football');

    await expect(page.getByRole('status').filter({ hasText: 'Total of the weights: 99 of 100' })).toBeVisible();
    await expect(page.getByText('The weights must total 100', { exact: true })).toBeVisible();
    await register(page);

    await expect(alertWith(page, 'The platform refused the registration')).toBeVisible();
    await expect(alertWith(page, 'The weights must total 100.')).toBeVisible();
    await expect(page.getByRole('spinbutton', { name: /^Weight of pressing/ })).toHaveValue('50');
    await expect(page.getByRole('spinbutton', { name: /^Weight of possession/ })).toHaveValue('49');
    await expect(page).toHaveURL(/\/philosophy-profiles\/new/);
    expect(api.catalogArtifacts.some((a) => a.id === 'klopp-profile')).toBe(false);
    await expectNoAxeViolations(page);

    await page.getByRole('spinbutton', { name: /^Weight of possession/ }).fill('50');
    await expect(page.getByRole('status').filter({ hasText: 'Total of the weights: 100 of 100' })).toBeVisible();
    await expect(page.getByText('The weights must total 100', { exact: true })).toHaveCount(0);
    await register(page);

    await expect(page).toHaveURL(/\/philosophy-profiles\/klopp-profile\/versions\/1\.0\.0$/);
    await expect(page.getByRole('heading', { level: 2, name: 'klopp-profile version 1.0.0' })).toBeVisible();
    expect(api.catalogArtifacts.some((a) => a.id === 'klopp-profile' && a.version === '1.0.0')).toBe(true);
    const distances = page.getByRole('list', { name: 'Distances to other profiles' });
    await expect(distances).toContainText(`${OTHER_COACH} 1.0.0 with profile guardiola-profile 1.0.0: 0.05`);
    await expect(distances).toContainText('below 0.10: differentiation review required');
  });

  test('shows a stored 100-point profile with its table, reviewers, and marked distances (US5 acceptance 1)', async ({ page, api }) => {
    seedSet(api);
    seedOtherCoach(api);
    api.seedCatalogArtifact(profileDocument('klopp-profile', 50, 50));
    await open(page, '/agent-catalog/artifacts/philosophy-profiles/klopp-profile/versions/1.0.0');

    await expect(page.getByRole('link', { name: `${SET} 1.0.0` })).toBeVisible();
    const table = page.getByRole('table', { name: 'Weights' });
    await expect(table.getByRole('row')).toHaveCount(3);
    const pressing = table.getByRole('row').filter({ has: page.getByRole('rowheader', { name: 'pressing' }) });
    await expect(pressing).toContainText('How hard the team presses.');
    await expect(pressing).toContainText('50');
    await expect(pressing).toContainText('Pressing rationale.');
    await expect(pressing).toContainText('Pressing provenance.');
    await expect(page.getByText('Author A', { exact: true })).toBeVisible();
    await expect(page.getByText('Reviewer Arch', { exact: true })).toBeVisible();
    await expect(page.getByText('Reviewer Football', { exact: true })).toBeVisible();
    await expect(page.getByRole('list', { name: 'Distances to other profiles' })).toContainText('below 0.10: differentiation review required');
    await expectNoAxeViolations(page);
  });

  test('refuses a modifier policy with the bound 4 next to the bound and keeps the entries (US5 acceptance 3)', async ({ page, api }) => {
    await open(page, '/agent-catalog/artifacts/modifier-policies/new');
    await textbox(page, /^Identifier/).fill('bounded-modifiers');
    await textbox(page, /^Version/).fill('1.0.0');
    await page.getByRole('spinbutton', { name: /^Per-dimension bound/ }).fill('4');
    await page.getByRole('spinbutton', { name: /^Total positive movement/ }).fill('5');
    await register(page);

    await expect(alertWith(page, 'The per-dimension bound or the total positive movement exceeds the allowed bounds.')).toHaveCount(1);
    await expect(page.getByRole('spinbutton', { name: /^Per-dimension bound/ })).toHaveValue('4');
    await expect(page.getByRole('spinbutton', { name: /^Total positive movement/ })).toHaveValue('5');
    await expect(page).toHaveURL(/\/modifier-policies\/new/);
    expect(api.catalogArtifacts.some((a) => a.id === 'bounded-modifiers')).toBe(false);

    await page.getByRole('spinbutton', { name: /^Per-dimension bound/ }).fill('2');
    await register(page);
    await expect(page).toHaveURL(/\/modifier-policies\/bounded-modifiers\/versions\/1\.0\.0$/);
    expect(api.catalogArtifacts.find((a) => a.id === 'bounded-modifiers')!.document.perDimensionBound).toBe(2);
  });
});

test.describe('Coach Agent version', () => {
  function seedCoachArtifacts(api: SimulatedApi) {
    seedSet(api);
    const profile = api.seedCatalogArtifact(profileDocument('klopp-profile', 50, 50));
    api.seedCatalogArtifact({
      schemaVersion: '1.0.0', kind: 'modifier-policy', id: 'bounded-modifiers', version: '1.0.0',
      dimensionSetMajor: 1, perDimensionBound: 2, maxPositiveMovement: 5, contextCategories: ['score-state'], budgetNeutral: true,
    });
    api.seedCatalogArtifact({
      schemaVersion: '1.0.0', kind: 'prompt-template', id: 'coach-prompt', version: '1.0.0', group: 'coach',
      responseContract: 'agent-response/1', text: 'Coach prompt.', slots: [], dimensionSetMajor: 1,
    });
    api.seedCatalogArtifact({ schemaVersion: '1.0.0', kind: 'tool-policy', id: 'coach-tools', version: '1.0.0', tools: [] });
    api.seedCatalogArtifact({
      schemaVersion: '1.0.0', kind: 'safety-policy', id: 'coach-safety', version: '1.0.0', group: 'coach',
      routes: [], hardBoundaries: ['Never decide for the coach.'],
    });
    return profile;
  }

  test('refuses a version without disclosure next to the field, then registers it as a draft and approves it with the acknowledgement (US5 acceptance 3, 4, 5; US4 acceptance 2)', async ({ page, api }) => {
    seedCoachArtifacts(api);
    seedOtherCoach(api);
    await open(page, `/agent-catalog/agents/${COACH}/versions/new`);
    await textbox(page, /^Version/).fill('1.0.0');
    await textbox(page, /^Display name/).fill('Klopp simulation');
    await page.getByLabel(/^Prompt template/).selectOption('coach-prompt@1.0.0');
    await page.getByLabel(/^Model selection/).selectOption('simulated-model@0.1.0');
    await page.getByLabel(/^Tool policy/).selectOption('coach-tools@1.0.0');
    await page.getByLabel(/^Safety policy/).selectOption('coach-safety@1.0.0');
    await expect(page.getByText('Listed first: matches the dimension set major version 1 of the prompt template')).toBeVisible();
    await expect(page.getByText(/^20 to 1,?000 characters/)).toBeVisible();
    await page.getByLabel(/^Philosophy profile/).selectOption('klopp-profile@1.0.0');
    await page.getByLabel(/^Modifier policy/).selectOption('bounded-modifiers@1.0.0');
    await register(page);

    await expect(alertWith(page, 'needs a simulation disclosure statement')).toHaveCount(1);
    await expect(textbox(page, /^Display name/)).toHaveValue('Klopp simulation');
    await expect(page).toHaveURL(/\/versions\/new/);
    expect(api.catalogVersions.some((v) => v.agentId === COACH)).toBe(false);

    await textbox(page, /^Disclosure statement/).fill(DISCLOSURE);
    await register(page);
    await expect(page).toHaveURL(new RegExp(`/agent-catalog/agents/${COACH}/versions/1\\.0\\.0$`));
    expect(api.catalogVersions.find((v) => v.agentId === COACH)!.state).toBe('draft');
    await expect(page.getByText(DISCLOSURE)).toBeVisible();
    await expect(page.getByRole('list', { name: 'Distances to other profiles' })).toContainText('below 0.10: differentiation review required');

    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(dialog(page)).toContainText('Profile distances below 0.10');
    await expect(dialog(page)).toContainText(`${OTHER_COACH} 1.0.0 with profile guardiola-profile 1.0.0: 0.05`);
    const confirm = dialog(page).getByRole('button', { name: 'Approve version' });
    await expect(confirm).toBeDisabled();
    await expectNoAxeViolations(page);
    await dialog(page).getByRole('checkbox', { name: 'I have reviewed the differentiation of these profiles' }).check();
    await expect(confirm).toBeEnabled();
    await confirm.click();
    await expect(dialog(page).getByRole('status')).toBeVisible();
    await dialog(page).getByRole('button', { name: 'Close' }).click();

    expect(api.catalogVersions.find((v) => v.agentId === COACH)).toMatchObject({ state: 'approved', differentiationAcknowledged: true });
    await expect(page.locator('dl').first().getByText('approved', { exact: true })).toBeVisible();
    await expect(page.getByText('The approval acknowledged a differentiation review.')).toBeVisible();
    await expect(page.getByText('Not available in conversations yet')).toBeVisible();
  });

  test('explains the refusal to enable an approved Coach Agent version (US2 acceptance 5)', async ({ page, api }) => {
    seedCoachArtifacts(api);
    api.seedCatalogAgentVersion(
      {
        schemaVersion: '1.0.0',
        kind: 'agent-version',
        agentId: COACH,
        version: '1.0.0',
        group: 'coach',
        displayName: 'Klopp simulation',
        disclosure: DISCLOSURE,
        artifacts: {},
      },
      { state: 'approved' },
    );
    await open(page, `/agent-catalog/agents/${COACH}/versions/1.0.0`);
    await expect(page.getByText('Not available in conversations yet')).toBeVisible();
    await page.getByRole('button', { name: 'Enable', exact: true }).click();
    await expect(dialog(page)).toContainText('The platform may refuse while Coach Agents are not available in conversations.');
    await dialog(page).getByRole('button', { name: 'Enable version' }).click();
    await expect(dialog(page).getByRole('alert').filter({ hasText: 'The platform does not support enabling Coach Agents yet.' })).toBeVisible();
    await expectNoAxeViolations(page);
    expect(api.catalogEnablements.find((e) => e.agentId === COACH)!.enabledVersion).toBeNull();
  });
});
