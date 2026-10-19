import { expectNoAxeViolations } from '../support/axe';
import { expect, test } from '../support/fixtures';
import { match, team } from '../support/simulatedApi';

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
    await expect(section.getByText('You have no triage conversations for this match yet.')).toBeVisible();
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
