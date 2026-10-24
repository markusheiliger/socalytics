import type { Page, Route } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { expectAllReachableByKeyboard, activateWithKeyboard } from '../support/keyboard';
import { agentRefusals, match, team, type SimulatedApi } from '../support/simulatedApi';

const connectionRequired = /A connection is required/;
const QUESTION = 'Confidential pressing question';
const ADVICE = `Simulated advice for: ${QUESTION}`;

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
});

const conversationUrl = (id: string) => `/matches/${match.id}/triage/${id}`;
const history = (page: Page) => page.getByRole('list', { name: 'Questions and advice' });

test.describe('Who may start and who may read', () => {
  for (const role of ['ClubAdmin', 'Coach'] as const) {
    test(`a ${role} sees the start action`, async ({ page, api }) => {
      api.signInAs(role);
      api.seedEligibleRun();
      await page.goto(`/matches/${match.id}`);
      const section = page.getByRole('region', { name: 'Triage conversations' });
      await expect(section.getByRole('button', { name: 'Start triage conversation' })).toBeVisible();
    });
  }

  test('a Viewer sees the section without the start action', async ({ page, api }) => {
    api.signInAs('Viewer');
    api.seedEligibleRun();
    await page.goto(`/matches/${match.id}`);
    const section = page.getByRole('region', { name: 'Triage conversations' });
    await expect(section.getByText('You have no active triage conversations for this match.')).toBeVisible();
    await expect(section.getByRole('button', { name: 'Start triage conversation' })).toHaveCount(0);
  });

  test('a member without a team role gets the not-permitted view for the match and a conversation', async ({ page, api }) => {
    api.signInAs('Registrar');
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    for (const url of [`/matches/${match.id}`, conversationUrl(conversation.conversationId)]) {
      await page.goto(url);
      await expect(page.getByRole('alert').filter({ hasText: /not permitted|not allowed|permission/i })).toBeVisible();
      await expect(page.getByText(QUESTION)).toHaveCount(0);
      await expect(page.getByRole('region', { name: 'Triage conversations' })).toHaveCount(0);
    }
  });

  test('a conversation owned by another member shows the not-found view', async ({ page, api }) => {
    api.signInAs('Coach');
    const conversation = api.seedAgentConversation({ ownerRole: 'ClubAdmin', participants: ['specialist-tactics'] });
    api.seedAgentTurn(conversation.conversationId, { question: QUESTION, script: [{ state: 'succeeded' }] });
    await page.goto(conversationUrl(conversation.conversationId));
    await expect(page.getByText('This page does not exist.')).toBeVisible();
    await expect(page.getByText(QUESTION)).toHaveCount(0);
    await expectNoAxeViolations(page);
  });
});

test.describe('Accessibility of the triage states', () => {
  test('the match section has no violations with and without conversations', async ({ page, api }) => {
    api.signInAs('Coach');
    api.seedEligibleRun();
    await page.goto(`/matches/${match.id}`);
    const section = page.getByRole('region', { name: 'Triage conversations' });
    await expect(section.getByText('You have no active triage conversations for this match.')).toBeVisible();
    await expectNoAxeViolations(page);

    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    api.supersedeRun(conversation.conversationId);
    await page.reload();
    await expect(section.getByRole('listitem')).toHaveCount(1);
    await expectNoAxeViolations(page);
  });

  test('the conversation view has no violations in the pending, succeeded, failed, lineage-open, and superseded states', async ({ page, api }) => {
    api.signInAs('Coach');
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    api.seedAgentTurn(conversation.conversationId, { question: 'Pending question', script: [{ state: 'accepted', holdReads: 1000 }] });
    api.seedAgentTurn(conversation.conversationId, { question: QUESTION, script: [{ state: 'succeeded' }] });
    api.seedAgentTurn(conversation.conversationId, {
      question: 'Failed question',
      script: [{ state: 'failed', failureReason: 'execution-failed' }],
    });
    api.supersedeRun(conversation.conversationId);

    await page.goto(conversationUrl(conversation.conversationId));
    const list = history(page);
    await expect(list).toContainText('Waiting for the agent');
    await expect(list).toContainText(ADVICE);
    await expect(list).toContainText('No advice: the agent could not answer this question.');
    await expect(page.getByText("This conversation stays bound to an analysis run that is no longer the match's current run.")).toBeVisible();
    await expectNoAxeViolations(page);

    const turn = list.getByRole('listitem', { name: /^Question to/ }).filter({ hasText: QUESTION });
    await turn.getByRole('button', { name: 'Show lineage' }).click();
    await expect(turn.getByRole('region', { name: 'Lineage' })).toBeVisible();
    await expectNoAxeViolations(page);
  });

  test('the conversation view has no violations in the refused and read-only states', async ({ page, api }) => {
    api.signInAs('Coach');
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    await page.goto(conversationUrl(conversation.conversationId));
    await expect(page.getByLabel('Your question')).toBeVisible();

    api.refuseNext('submit', agentRefusals.tooManyUnfinished);
    await page.getByLabel('Your question').fill('Refused question');
    await page.getByRole('button', { name: 'Send question' }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'Too many of your questions are still being answered.' })).toBeVisible();
    await expectNoAxeViolations(page);

    api.signInAs('Viewer');
    const own = api.seedAgentConversation({ ownerRole: 'Viewer', participants: ['specialist-tactics'] });
    await page.goto(conversationUrl(own.conversationId));
    await expect(page.getByText('You can read this conversation, but only Club Admins and Coaches of the team can ask questions.')).toBeVisible();
    await expectNoAxeViolations(page);
  });
});

test.describe('Keyboard', () => {
  test('the whole flow works with the keyboard and shows visible focus', async ({ page, api }) => {
    test.setTimeout(60_000);
    api.signInAs('Coach');
    api.seedEligibleRun();
    api.agentScript = [{ state: 'succeeded' }];
    await page.goto(`/matches/${match.id}`);
    const section = page.getByRole('region', { name: 'Triage conversations' });
    await expect(section.getByRole('button', { name: 'Start triage conversation' })).toBeVisible();
    await expectAllReachableByKeyboard(page, section);

    await activateWithKeyboard(section.getByRole('button', { name: 'Start triage conversation' }));
    const dialog = page.getByRole('dialog', { name: 'Start triage conversation' });
    await expect(dialog).toBeVisible();
    await expectAllReachableByKeyboard(page, dialog);
    await activateWithKeyboard(dialog.getByRole('button', { name: 'Start triage conversation' }));
    await expect(page).toHaveURL(new RegExp(`/matches/${match.id}/triage/[^/]+$`));

    const agent = page.getByLabel('Agent to add');
    await expect(agent).toBeVisible();
    await agent.focus();
    await agent.selectOption('specialist-tactics');
    await activateWithKeyboard(page.getByRole('button', { name: 'Add agent' }));
    await expect(page.getByRole('list', { name: 'Participating agents' })).toContainText('specialist-tactics');

    const ask = page.getByLabel('Ask this agent');
    await ask.focus();
    await ask.selectOption('specialist-tactics');
    const question = page.getByLabel('Your question');
    await question.focus();
    await page.keyboard.type(QUESTION);
    await activateWithKeyboard(page.getByRole('button', { name: 'Send question' }));
    await expect(history(page)).toContainText(ADVICE, { timeout: 30_000 });

    const turn = history(page).getByRole('listitem', { name: /^Question to/ });
    await activateWithKeyboard(turn.getByRole('button', { name: 'Show lineage' }));
    await expect(turn.getByRole('region', { name: 'Lineage' })).toBeVisible();
    await expectAllReachableByKeyboard(page);
  });
});

async function startOnce(page: Page, api: SimulatedApi) {
  api.signInAs('Coach');
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
  await page.reload();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
}

test.describe('Online only', () => {
  test('offline shows the connection-required state without earlier conversation content', async ({ page, api }) => {
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    api.seedAgentTurn(conversation.conversationId, { question: QUESTION, script: [{ state: 'succeeded' }] });
    await startOnce(page, api);
    await page.goto(conversationUrl(conversation.conversationId));
    await expect(history(page)).toContainText(ADVICE);

    await page.context().setOffline(true);
    await page.route('**/api/v1/**', (route: Route) => route.abort('internetdisconnected'));
    for (const url of [conversationUrl(conversation.conversationId), `/matches/${match.id}`]) {
      await page.goto(url);
      await expect(page.getByText(connectionRequired).first()).toBeVisible();
      await expect(page.getByText(QUESTION)).toHaveCount(0);
      await expect(page.getByText(ADVICE)).toHaveCount(0);
    }
    await expectNoAxeViolations(page);
  });

  test('after a session Cache Storage, localStorage, sessionStorage, and IndexedDB hold no triage content', async ({ page, api }) => {
    await startOnce(page, api);
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    api.seedAgentTurn(conversation.conversationId, {
      question: QUESTION,
      script: [{ state: 'succeeded' }],
      lineage: [{ sequence: 1, kind: 'submitted', attemptId: null, attemptNumber: null, recordedAt: new Date().toISOString(), details: {} }],
    });
    await page.goto(conversationUrl(conversation.conversationId));
    await expect(history(page)).toContainText(ADVICE);
    const turn = history(page).getByRole('listitem', { name: /^Question to/ });
    await turn.getByRole('button', { name: 'Show lineage' }).click();
    await expect(turn.getByRole('region', { name: 'Lineage' })).toBeVisible();
    await page.getByLabel('Your question').fill('An unsent draft question');
    await page.getByRole('link', { name: 'Back to match' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Leave' }).click();
    await expect(page).toHaveURL(`/matches/${match.id}`);

    const stored = await page.evaluate(async () => {
      const entries: string[] = [];
      for (const name of await caches.keys()) {
        for (const request of await (await caches.open(name)).keys()) entries.push(`cache:${new URL(request.url).pathname}`);
      }
      for (const storage of [localStorage, sessionStorage]) {
        for (let i = 0; i < storage.length; i++) entries.push(`${storage.key(i)}=${storage.getItem(storage.key(i) ?? '')}`);
      }
      for (const info of (await indexedDB.databases?.()) ?? []) {
        if (!info.name) continue;
        const db = await new Promise<IDBDatabase>((resolve, reject) => {
          const open = indexedDB.open(info.name as string);
          open.onsuccess = () => resolve(open.result);
          open.onerror = () => reject(open.error);
        });
        for (const storeName of Array.from(db.objectStoreNames)) {
          const rows = await new Promise<unknown[]>((resolve, reject) => {
            const request = db.transaction(storeName, 'readonly').objectStore(storeName).getAll();
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
          });
          entries.push(`${info.name}/${storeName}=${JSON.stringify(rows)}`);
        }
        db.close();
      }
      return entries;
    });
    expect(stored.filter((entry) => entry.startsWith('cache:/api/'))).toEqual([]);
    expect(stored.filter((entry) => /confidential pressing|unsent draft|Simulated advice|submitted/i.test(entry))).toEqual([]);
  });
});
