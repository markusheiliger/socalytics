import { createOperationsAdapter } from '../../../../src/platform/adapters/operationsAdapter';
import { header, ids, ok, problem, setup } from './support';

const messageId = '00000000-0000-4000-8000-000000000301';

const message = {
  messageId,
  messageType: 'analysis.ready-work',
  contractVersion: '1',
  subject: '<b>subject</b>',
  state: 'failed',
  attemptCount: 3,
  publicationRound: 2,
  lastAttemptAt: '2026-10-20T10:00:00Z',
  nextAttemptAt: null,
  failureCategory: 'mystery',
  createdAt: '2026-10-20T09:00:00Z',
  publishedAt: null,
  ageSeconds: 3600,
  publicationLagSeconds: null,
  runId: ids.run,
  logicalJobId: null,
  capabilityId: 'cap',
};

const summary = {
  workflowDefinition: { state: 'unresolvable', category: 'missing', since: '2026-10-20T08:00:00Z' },
  runsByState: { Failed: 1, Weird: 2, Running: 4, Requested: 3 },
  runsWithBlockedNodes: 5,
  staleAttempts: 0,
  rejectedNotifications: 2,
  outboxPending: 7,
  outboxFailed: 1,
  oldestPendingAgeSeconds: null,
  observedAt: '2026-10-21T05:00:00Z',
};

describe('operationsAdapter', () => {
  it('maps the summary: numbers, dates, nulls, and ordered run states', async () => {
    const { api, calls } = setup(() => ok(200, summary));
    const result = await createOperationsAdapter(api).summary();
    expect(calls[0].url).toContain('/api/v1/operations/analysis/summary');
    expect(result.workflowDefinition).toEqual({ state: 'unresolvable', category: 'missing', since: new Date('2026-10-20T08:00:00Z') });
    expect(result.runsByState).toEqual([
      { state: 'Requested', count: 3 },
      { state: 'Running', count: 4 },
      { state: 'Failed', count: 1 },
      { state: 'Weird', count: 2 },
    ]);
    expect(result.runsWithBlockedNodes).toBe(5);
    expect(result.outboxPending).toBe(7);
    expect(result.oldestPendingAgeSeconds).toBeUndefined();
    expect(result.observedAt).toEqual(new Date('2026-10-21T05:00:00Z'));
  });

  it('keeps a numeric oldest pending age and an absent workflow category', async () => {
    const { api } = setup(() =>
      ok(200, { ...summary, oldestPendingAgeSeconds: 90, workflowDefinition: { state: 'resolvable' }, runsByState: {} }),
    );
    const result = await createOperationsAdapter(api).summary();
    expect(result.oldestPendingAgeSeconds).toBe(90);
    expect(result.workflowDefinition).toEqual({ state: 'resolvable', category: undefined, since: undefined });
    expect(result.runsByState).toEqual([]);
  });

  it('refuses a summary without counts as incompatible', async () => {
    const { api } = setup(() => ok(200, { ...summary, outboxPending: null }));
    await expect(createOperationsAdapter(api).summary()).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('lists outbox messages without unset filters, the page size, or a token', async () => {
    const { api, calls } = setup(() => ok(200, { items: [message], continuationToken: null }));
    const page = await createOperationsAdapter(api).listOutboxMessages({});
    const url = new URL(calls[0].url);
    expect(url.pathname).toBe('/api/v1/operations/outbox-messages');
    expect([...url.searchParams.keys()]).toEqual([]);
    expect(page.continuationToken).toBeUndefined();
    expect(page.items[0]).toEqual({
      messageId,
      messageType: 'analysis.ready-work',
      contractVersion: '1',
      subject: '<b>subject</b>',
      state: 'failed',
      attemptCount: 3,
      publicationRound: 2,
      lastAttemptAt: new Date('2026-10-20T10:00:00Z'),
      nextAttemptAt: undefined,
      failureCategory: 'mystery',
      createdAt: new Date('2026-10-20T09:00:00Z'),
      publishedAt: undefined,
      ageSeconds: 3600,
      publicationLagSeconds: undefined,
      runId: ids.run,
      logicalJobId: undefined,
      capabilityId: 'cap',
    });
  });

  it('sends the set filters and the continuation token but never the page size', async () => {
    const { api, calls } = setup(() => ok(200, { items: [], continuationToken: 'next' }));
    const page = await createOperationsAdapter(api).listOutboxMessages(
      { state: 'failed', messageType: 'analysis.ready-work' },
      'tok',
    );
    const params = new URL(calls[0].url).searchParams;
    expect(Object.fromEntries(params)).toEqual({ state: 'failed', messageType: 'analysis.ready-work', continuationToken: 'tok' });
    expect(params.has('pageSize')).toBe(false);
    expect(page).toEqual({ items: [], continuationToken: 'next' });
  });

  it('reads one outbox message by identifier', async () => {
    const { api, calls } = setup(() => ok(200, message));
    const result = await createOperationsAdapter(api).getOutboxMessage(messageId);
    expect(new URL(calls[0].url).pathname).toBe(`/api/v1/operations/outbox-messages/${messageId}`);
    expect(result.messageId).toBe(messageId);
  });

  it('lists notification receipts with the outcome and token only when set', async () => {
    const receipt = {
      messageId,
      messageType: 'matches.recordings-finalized',
      recordingSetVersionId: ids.version,
      outcome: 'rejected',
      rejectionCategory: null,
      runId: null,
      receivedAt: '2026-10-20T11:00:00Z',
    };
    const none = setup(() => ok(200, { items: [receipt], continuationToken: 'n' }));
    const first = await createOperationsAdapter(none.api).listNotificationReceipts({});
    const url = new URL(none.calls[0].url);
    expect(url.pathname).toBe('/api/v1/operations/analysis/notification-receipts');
    expect([...url.searchParams.keys()]).toEqual([]);
    expect(first.continuationToken).toBe('n');
    expect(first.items[0]).toEqual({
      messageId,
      messageType: 'matches.recordings-finalized',
      recordingSetVersionId: ids.version,
      outcome: 'rejected',
      rejectionCategory: undefined,
      runId: undefined,
      receivedAt: new Date('2026-10-20T11:00:00Z'),
    });
    const set = setup(() => ok(200, { items: [] }));
    await createOperationsAdapter(set.api).listNotificationReceipts({ outcome: 'rejected' }, 'tok');
    expect(Object.fromEntries(new URL(set.calls[0].url).searchParams)).toEqual({ outcome: 'rejected', continuationToken: 'tok' });
  });

  it('reconciles with a body-less post and the antiforgery header only', async () => {
    const { api, calls } = setup(() =>
      ok(200, { readyJobsRepublished: 2, notificationsRepublished: 0, startedAt: '2026-10-21T05:00:00Z', completedAt: '2026-10-21T05:00:01Z' }),
    );
    const result = await createOperationsAdapter(api).reconcile();
    expect(calls[0].init.method).toBe('POST');
    expect(new URL(calls[0].url).pathname).toBe('/api/v1/operations/analysis/reconciliations');
    expect(calls[0].init.body).toBeUndefined();
    expect(header(calls[0], 'Idempotency-Key')).toBeUndefined();
    expect(header(calls[0], 'If-Match')).toBeUndefined();
    expect(result).toEqual({
      readyJobsRepublished: 2,
      notificationsRepublished: 0,
      startedAt: new Date('2026-10-21T05:00:00Z'),
      completedAt: new Date('2026-10-21T05:00:01Z'),
    });
  });

  it('classifies the failures of the views', async () => {
    const forbidden = setup(() => problem(403, 'forbidden'));
    await expect(createOperationsAdapter(forbidden.api).summary()).rejects.toMatchObject({ kind: 'notPermitted', status: 403 });
    const missing = setup(() => problem(404, 'outbox-message-not-found'));
    await expect(createOperationsAdapter(missing.api).getOutboxMessage(messageId)).rejects.toMatchObject({ status: 404 });
    const token = setup(() => problem(400, 'validation-failed', [{ field: 'continuationToken', code: 'invalid' }]));
    await expect(createOperationsAdapter(token.api).listOutboxMessages({}, 'bad')).rejects.toMatchObject({
      kind: 'programmingError',
      fieldErrors: [{ field: 'continuationToken', code: 'invalid' }],
    });
    const unavailable = setup(
      () =>
        new Response(JSON.stringify({ status: 503, code: 'unavailable' }), {
          status: 503,
          headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '0' },
        }),
    );
    await expect(createOperationsAdapter(unavailable.api).reconcile()).rejects.toMatchObject({ kind: 'unreachable' });
  });
});
