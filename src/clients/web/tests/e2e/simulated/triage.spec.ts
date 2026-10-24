import type { Page } from '@playwright/test';
import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { agentRefusals, match, team, type SimulatedApi } from '../support/simulatedApi';

test.beforeEach(async ({ api }) => {
  api.on('GET', `/teams/${team.id}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }));
});

test.describe('Ask and answer', () => {
  test('a Coach asks a specialist and reads the attributed advice without reload (SC-001, SC-002, SC-004)', async ({ page, api }) => {
    test.setTimeout(60_000);
    api.signInAs('Coach');
    const older = api.seedEligibleRun();
    older.createdAt = new Date(2026, 0, 1);
    const newest = api.seedEligibleRun();
    api.statusRetryAfterSeconds = 1;
    api.agentScript = [{ state: 'accepted', holdReads: 3 }, { state: 'running' }, { state: 'succeeded' }];

    const statusReads: { at: number; ifNoneMatch: string | undefined }[] = [];
    page.on('request', (request) => {
      if (request.method() === 'GET' && /\/api\/v1\/agent-invocations\/[^/]+$/.test(new URL(request.url()).pathname)) {
        statusReads.push({ at: Date.now(), ifNoneMatch: request.headers()['if-none-match'] });
      }
    });

    await page.goto(`/matches/${match.id}`);
    const section = page.getByRole('region', { name: 'Triage conversations' });
    await expect(section.getByText('You have no active triage conversations for this match.')).toBeVisible();
    await section.getByRole('button', { name: 'Start triage conversation' }).click();
    const dialog = page.getByRole('dialog', { name: 'Start triage conversation' });
    await expectNoAxeViolations(page);
    await dialog.getByRole('button', { name: 'Start triage conversation' }).click();

    await expect(page).toHaveURL(new RegExp(`/matches/${match.id}/triage/[^/]+$`));
    expect(api.agentConversations).toHaveLength(1);
    expect(api.agentConversations[0]?.analysisRunId).toBe(String(newest.runId));
    expect(api.agentConversations[0]?.analysisRunId).not.toBe(String(older.runId));

    await page.getByLabel('Agent to add').selectOption('specialist-tactics');
    await page.getByRole('button', { name: 'Add agent' }).click();
    await expect(page.getByRole('list', { name: 'Participating agents' })).toContainText('specialist-tactics');

    await page.getByLabel('Ask this agent').selectOption('specialist-tactics');
    await page.getByLabel('Your question').fill('How did we press in the second half?');
    await expectNoAxeViolations(page);
    const accepted = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().endsWith('/invocations'));
    await page.getByRole('button', { name: 'Send question' }).click();
    expect((await accepted).status()).toBe(202);
    const history = page.getByRole('list', { name: 'Questions and advice' });
    await expect(history).toContainText('How did we press in the second half?', { timeout: 1000 });
    await expect(history).toContainText('Waiting for the agent', { timeout: 1000 });

    const advice = history.getByRole('region', { name: 'Advice from Tactics specialist' });
    await expect(advice).toBeVisible({ timeout: 30_000 });
    await expect(advice).toContainText('Tactics specialist (specialist-tactics)');
    await expect(advice).toContainText('This is AI-generated advice for a coach');
    await expect(advice).toContainText('Simulated advice for: How did we press in the second half?');
    await expectNoAxeViolations(page);

    expect(statusReads.length).toBeGreaterThanOrEqual(4);
    expect(statusReads[0]?.ifNoneMatch).toBeUndefined();
    for (const read of statusReads.slice(1)) expect(read.ifNoneMatch).toBeTruthy();
    const gaps = statusReads.slice(1, 4).map((read, index) => read.at - (statusReads[index]?.at ?? 0));
    expect(gaps[1]).toBeGreaterThan(gaps[0] ?? 0);
    expect(gaps[2]).toBeGreaterThan(gaps[1] ?? 0);
  });

  const reasons = [
    ['attempts-exhausted', 'No advice: the agent did not finish after several attempts.'],
    ['execution-failed', 'No advice: the agent could not answer this question.'],
    ['scope-lost', 'No advice: access to this team was lost while the question was being answered.'],
  ] as const;

  for (const [failureReason, message] of reasons) {
    test(`shows the message of the failure reason ${failureReason} (SC-004)`, async ({ page, api }) => {
      api.signInAs('Coach');
      const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
      api.seedAgentTurn(conversation.conversationId, {
        script: [{ state: 'accepted' }, { state: 'failed', failureReason }],
      });
      await page.goto(`/matches/${match.id}/triage/${conversation.conversationId}`);
      await expect(page.getByRole('list', { name: 'Questions and advice' })).toContainText(message, { timeout: 15_000 });
      await expectNoAxeViolations(page);
    });
  }
});

test.describe('Return and context', () => {
  test('lists conversations newest first with run, participants, and the superseded marker, then shows the notice (FR-001, FR-003)', async ({ page, api }) => {
    api.signInAs('Coach');
    const first = api.seedAgentConversation({ participants: ['specialist-fitness'] });
    first.createdAt = new Date(2026, 5, 1, 10, 0).toISOString();
    api.supersedeRun(first.conversationId);
    const second = api.seedAgentConversation({ participants: ['specialist-tactics', 'specialist-scouting'] });
    second.createdAt = new Date(2026, 5, 2, 10, 0).toISOString();

    await page.goto(`/matches/${match.id}`);
    const list = page.getByRole('region', { name: 'Triage conversations' }).getByRole('list', { name: 'Your triage conversations for this match' });
    const items = list.getByRole('listitem');
    await expect(items).toHaveCount(2);
    await expect(items.nth(0)).toContainText('Participants: Tactics specialist, Scouting specialist');
    await expect(items.nth(0)).toContainText('Analysis run created');
    await expect(items.nth(0)).not.toContainText('No longer current run');
    await expect(items.nth(1)).toContainText('Participants: Fitness specialist');
    await expect(items.nth(1)).toContainText('No longer current run');

    await items.nth(1).getByRole('link').click();
    await expect(page).toHaveURL(new RegExp(`/matches/${match.id}/triage/${first.conversationId}$`));
    await expect(page.getByText("This conversation stays bound to an analysis run that is no longer the match's current run.")).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open the analysis review' })).toBeVisible();
    await expectNoAxeViolations(page);
  });

  test('loads three history pages with Show more and shows no turn twice (FR-004a)', async ({ page, api }) => {
    api.signInAs('Coach');
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    const total = 45;
    for (let index = 1; index <= total; index++) {
      api.seedAgentTurn(conversation.conversationId, {
        question: `Question ${String(index).padStart(2, '0')}`,
        script: [{ state: 'succeeded' }],
      });
    }

    await page.goto(`/matches/${match.id}/triage/${conversation.conversationId}`);
    const history = page.getByRole('list', { name: 'Questions and advice' });
    const turns = history.getByRole('listitem', { name: /^Question to/ });
    const showMore = page.getByRole('button', { name: 'Show more' });
    await expect(turns).toHaveCount(20);
    await showMore.click();
    await expect(turns).toHaveCount(40);
    await showMore.click();
    await expect(turns).toHaveCount(total);
    await expect(showMore).toHaveCount(0);

    const questions = await history.locator('[aria-label="Question"]').allTextContents();
    expect(new Set(questions).size).toBe(total);
    expect(questions).toEqual([...questions].sort());
    expect(questions[0]).toBe('Question 01');
    expect(questions.at(-1)).toBe(`Question ${total}`);
  });

  test('shows the lineage with ordered entries and execution references and refreshes it while the turn runs (FR-015)', async ({ page, api }) => {
    test.setTimeout(60_000);
    api.signInAs('Coach');
    api.statusRetryAfterSeconds = 1;
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    api.seedAgentTurn(conversation.conversationId, {
      question: 'Who pressed first?',
      script: [{ state: 'accepted', holdReads: 2 }, { state: 'running', holdReads: 2 }, { state: 'succeeded' }],
    });

    await page.goto(`/matches/${match.id}/triage/${conversation.conversationId}`);
    const turn = page.getByRole('list', { name: 'Questions and advice' }).getByRole('listitem', { name: /^Question to/ });
    await turn.getByRole('button', { name: 'Show lineage' }).click();
    const lineage = turn.getByRole('region', { name: 'Lineage' });
    const entries = lineage.getByRole('list', { name: 'Lineage entries' }).getByRole('listitem');
    await expect(entries).toHaveCount(1);
    await expect(entries.first()).toContainText('Question submitted');

    await expect(entries).toHaveCount(3, { timeout: 40_000 });
    await expect(entries.nth(0)).toContainText('Question submitted');
    await expect(entries.nth(1)).toContainText('Claimed by the agent');
    await expect(entries.nth(1)).toContainText('Attempt 1');
    await expect(entries.nth(2)).toContainText('Completed');

    const references = lineage.getByRole('list', { name: 'Execution references' });
    await expect(references).toContainText('Runtime: simulated-runtime');
    await expect(references).toContainText('Model: simulated-model');
    await expect(references).toContainText('Prompt reference: prompt/1');
    await expect(turn.getByRole('button', { name: 'Hide lineage' })).toBeVisible();
    await expectNoAxeViolations(page);
  });

  test('a Viewer who owns a conversation reads it without start, add, or submit controls (FR-024)', async ({ page, api }) => {
    api.signInAs('Viewer');
    const conversation = api.seedAgentConversation({ ownerRole: 'Viewer', participants: ['specialist-tactics'] });
    api.seedAgentTurn(conversation.conversationId, { question: 'Read only?', script: [{ state: 'succeeded' }] });

    await page.goto(`/matches/${match.id}`);
    const section = page.getByRole('region', { name: 'Triage conversations' });
    await expect(section.getByRole('listitem')).toHaveCount(1);
    await expect(section.getByRole('button', { name: 'Start triage conversation' })).toHaveCount(0);

    await section.getByRole('link').click();
    await expect(page.getByRole('list', { name: 'Questions and advice' })).toContainText('Read only?');
    await expect(page.getByText('You can read this conversation, but only Club Admins and Coaches of the team can ask questions.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send question' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add agent' })).toHaveCount(0);
    await expect(page.getByLabel('Your question')).toHaveCount(0);
    await expect(page.getByLabel('Agent to add')).toHaveCount(0);
    await expectNoAxeViolations(page);
  });
});

test.describe('Failures and refusals', () => {
  const openConversation = async (page: Page, api: SimulatedApi, participants = ['specialist-tactics']) => {
    api.signInAs('Coach');
    const conversation = api.seedAgentConversation({ participants });
    await page.goto(`/matches/${match.id}/triage/${conversation.conversationId}`);
    await expect(page.getByLabel('Your question')).toBeVisible();
    return conversation;
  };

  test('a network failure after acceptance followed by Send again leaves one turn and one idempotency key (SC-003)', async ({ page, api }) => {
    const conversation = await openConversation(page, api);
    const keys: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST' && request.url().endsWith('/invocations')) keys.push(request.headers()['idempotency-key'] ?? '');
    });
    api.dropNextResponse('submit');

    await page.getByLabel('Your question').fill('Who won the second balls?');
    await page.getByRole('button', { name: 'Send question' }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'We could not confirm that your question was received.' })).toBeVisible();
    await expect(page.getByLabel('Your question')).toHaveValue('Who won the second balls?');

    await page.getByRole('button', { name: 'Send again' }).click();
    const history = page.getByRole('list', { name: 'Questions and advice' });
    await expect(history).toContainText('Who won the second balls?');
    await expect(history.getByRole('listitem', { name: /^Question to/ })).toHaveCount(1);

    expect(keys).toHaveLength(2);
    expect(keys[0]).toBeTruthy();
    expect(keys[1]).toBe(keys[0]);
    expect([...api.agentInvocations.values()].filter((i) => i.conversationId === conversation.conversationId)).toHaveLength(1);
    expect([...api.idempotencyRecords.values()].filter((record) => record.fingerprint.startsWith('["submit"'))).toHaveLength(1);
  });

  const refusals = [
    ['tooManyUnfinished', agentRefusals.tooManyUnfinished, 'Too many of your questions are still being answered. Send this one when one of them has finished.'],
    ['agentNotAvailable', agentRefusals.agentNotAvailable, 'This agent is no longer available on this platform.'],
    ['agentNotParticipant', agentRefusals.agentNotParticipant, 'Add this agent to the conversation before asking it.'],
    ['runNotEligible', agentRefusals.runNotEligible, 'This analysis run cannot be used for triage.'],
    ['invalidQuestion (required)', agentRefusals.invalidQuestion, 'Enter a question.'],
    ['invalidQuestion (too long)', agentRefusals.questionTooLong, 'The question is too long. Shorten it to at most'],
  ] as const;

  for (const [name, refusal, message] of refusals) {
    test(`shows the message of the refusal ${name} and keeps the draft (SC-003)`, async ({ page, api }) => {
      await openConversation(page, api);
      api.refuseNext('submit', refusal);
      await page.getByLabel('Your question').fill('Keep this draft');
      await page.getByRole('button', { name: 'Send question' }).click();
      await expect(page.getByRole('alert').filter({ hasText: message })).toBeVisible();
      await expect(page.getByLabel('Your question')).toHaveValue('Keep this draft');
      expect(api.agentInvocations.size).toBe(0);
      await expectNoAxeViolations(page);
    });
  }

  test('a refused submission for lack of the right switches the conversation to read-only (SC-003)', async ({ page, api }) => {
    await openConversation(page, api);
    api.refuseNext('submit', agentRefusals.forbidden);
    await page.getByLabel('Your question').fill('Am I still allowed?');
    await page.getByRole('button', { name: 'Send question' }).click();
    await expect(page.getByText('You can read this conversation, but only Club Admins and Coaches of the team can ask questions.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send question' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add agent' })).toHaveCount(0);
  });

  test('a stale participant add reloads the conversation and a new choice succeeds (SC-003)', async ({ page, api }) => {
    const conversation = await openConversation(page, api);
    const reads: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'GET' && new URL(request.url()).pathname === `/api/v1/agent-conversations/${conversation.conversationId}`) {
        reads.push(request.url());
      }
    });
    const before = reads.length;
    conversation.version += 1;

    await page.getByLabel('Agent to add').selectOption('specialist-scouting');
    await page.getByRole('button', { name: 'Add agent' }).click();
    await expect(page.getByText('The conversation changed. It has been reloaded; choose again.')).toBeVisible();
    await expect.poll(() => reads.length).toBeGreaterThan(before);
    await expect(page.getByRole('list', { name: 'Participating agents' })).not.toContainText('specialist-scouting');

    await page.getByLabel('Agent to add').selectOption('specialist-scouting');
    await page.getByRole('button', { name: 'Add agent' }).click();
    await expect(page.getByRole('list', { name: 'Participating agents' })).toContainText('specialist-scouting');
  });

  test('revoking the owner while a turn is pending shows the not-found view and stops all status requests (FR-018)', async ({ page, api }) => {
    test.setTimeout(60_000);
    api.signInAs('Coach');
    api.statusRetryAfterSeconds = 1;
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    api.seedAgentTurn(conversation.conversationId, { script: [{ state: 'accepted', holdReads: 1000 }] });
    let statusReads = 0;
    page.on('request', (request) => {
      if (request.method() === 'GET' && /\/api\/v1\/agent-invocations\/[^/]+$/.test(new URL(request.url()).pathname)) statusReads++;
    });

    await page.goto(`/matches/${match.id}/triage/${conversation.conversationId}`);
    await expect(page.getByRole('list', { name: 'Questions and advice' })).toContainText('Waiting for the agent');
    await expect.poll(() => statusReads).toBeGreaterThan(0);

    conversation.ownerId = 'a-member-without-ownership';
    await expect(page.getByText('This page does not exist.')).toBeVisible({ timeout: 20_000 });
    const settled = statusReads;
    await page.waitForTimeout(3500);
    expect(statusReads).toBe(settled);
  });

  test('the leave confirmation appears for unsent input and not for pending turns (FR-019a)', async ({ page, api }) => {
    api.signInAs('Coach');
    const conversation = api.seedAgentConversation({ participants: ['specialist-tactics'] });
    api.seedAgentTurn(conversation.conversationId, { script: [{ state: 'accepted', holdReads: 1000 }] });
    const url = `/matches/${match.id}/triage/${conversation.conversationId}`;

    await page.goto(url);
    await expect(page.getByRole('list', { name: 'Questions and advice' })).toContainText('Waiting for the agent');
    await page.getByRole('link', { name: 'Back to match' }).click();
    await expect(page).toHaveURL(`/matches/${match.id}`);
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.goto(url);
    await page.getByLabel('Your question').fill('An unsent question');
    await page.getByRole('link', { name: 'Back to match' }).click();
    const dialog = page.getByRole('dialog', { name: 'Leave this conversation?' });
    await expect(dialog).toContainText('Leave this conversation? Your unsent question will be lost.');
    await expectNoAxeViolations(page);
    await dialog.getByRole('button', { name: 'Stay' }).click();
    await expect(page).toHaveURL(url);
    await expect(page.getByLabel('Your question')).toHaveValue('An unsent question');

    await page.getByRole('link', { name: 'Back to match' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Leave' }).click();
    await expect(page).toHaveURL(`/matches/${match.id}`);
  });
});
