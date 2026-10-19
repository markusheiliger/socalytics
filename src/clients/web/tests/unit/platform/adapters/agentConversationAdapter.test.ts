import { createAgentConversationAdapter, TriageFailure } from '../../../../src/platform/adapters/agentConversationAdapter';
import { bodyOf, header, ids, ok, problem, setup } from './support';

const conversationId = '00000000-0000-4000-8000-000000000301';
const invocationId = '00000000-0000-4000-8000-000000000401';

const conversation = {
  conversationId,
  matchId: ids.match,
  analysisRunId: ids.run,
  teamId: ids.team,
  analysisRunIsCurrent: true,
  state: 'active',
  participants: [{ agentId: 'specialist-tactics', addedAt: '2026-10-01T12:00:00Z' }],
  createdAt: '2026-10-01T12:00:00Z',
  version: 3,
};

const withHeaders = (status: number, body: unknown, headers: Record<string, string>) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

const turn = {
  invocationId,
  agentId: 'specialist-tactics',
  question: 'Why?',
  state: 'succeeded',
  failureReason: null,
  submittedAt: '2026-10-01T12:00:00Z',
  endedAt: '2026-10-01T12:00:09Z',
  advice: 'Press higher.',
};

async function outcomeOf(promise: Promise<unknown>) {
  const error = await promise.then(
    () => undefined,
    (e: unknown) => e,
  );
  expect(error).toBeInstanceOf(TriageFailure);
  return (error as TriageFailure).outcome;
}

describe('agentConversationAdapter', () => {
  it('lists the enabled agents with raw groups', async () => {
    const { api, calls } = setup(() =>
      ok(200, { agents: [{ agentId: 'specialist-tactics', group: 'specialist', domain: 'Tactics' }, { agentId: 'x', group: 'coach', domain: 'D' }] }),
    );
    const agents = await createAgentConversationAdapter(api).listAgents();
    expect(calls[0].url).toContain('/api/v1/agent-catalog/agents');
    expect(agents[0].group).toMatchObject({ known: true, recognized: 'specialist' });
    expect(agents[1].group).toEqual({ value: 'coach', known: false });
  });

  it('lists the conversations of a match in platform order', async () => {
    const { api, calls } = setup(() => ok(200, { conversations: [conversation, { ...conversation, conversationId: invocationId }] }));
    const list = await createAgentConversationAdapter(api).listConversations(ids.match);
    expect(calls[0].url).toContain(`/api/v1/matches/${ids.match}/agent-conversations`);
    expect(list.map((c) => c.conversationId)).toEqual([conversationId, invocationId]);
    expect(list[0]).toMatchObject({ analysisRunIsCurrent: true, version: 3, participants: [{ agentId: 'specialist-tactics' }] });
  });

  it('creates a conversation with the caller key', async () => {
    const { api, calls } = setup(() => withHeaders(201, conversation, { ETag: '"3"' }));
    const result = await createAgentConversationAdapter(api).createConversation(ids.match, ids.run, 'key-1');
    expect(header(calls[0], 'Idempotency-Key')).toBe('key-1');
    expect(bodyOf(calls[0])).toEqual({ analysisRunId: ids.run });
    expect(result).toMatchObject({ conversationId, etag: '"3"' });
  });

  it('refuses a conversation without identifiers as incompatible', async () => {
    const { api } = setup(() => ok(200, { ...conversation, teamId: null }));
    const outcome = await outcomeOf(createAgentConversationAdapter(api).createConversation(ids.match, ids.run, 'k'));
    expect(outcome).toEqual({ kind: 'platform', failureKind: 'incompatible' });
  });

  it('reads a conversation conditionally', async () => {
    let status = 200;
    const { api, calls } = setup(() => (status === 304 ? new Response(null, { status: 304 }) : withHeaders(200, conversation, { ETag: '"e1"' })));
    const adapter = createAgentConversationAdapter(api);
    const first = await adapter.getConversation(conversationId);
    expect(first).toMatchObject({ kind: 'changed', etag: '"e1"', value: { etag: '"e1"' } });
    status = 304;
    expect(await adapter.getConversation(conversationId, '"e1"')).toEqual({ kind: 'unchanged' });
    expect(header(calls[1], 'If-None-Match')).toBe('"e1"');
  });

  it('adds a participant with If-Match', async () => {
    const { api, calls } = setup(() => withHeaders(200, conversation, { ETag: '"4"' }));
    await createAgentConversationAdapter(api).addParticipant(conversationId, 'specialist-fitness', 3);
    expect(calls[0].url).toContain(`/agent-conversations/${conversationId}/participants`);
    expect(header(calls[0], 'If-Match')).toBe('"3"');
    expect(bodyOf(calls[0])).toEqual({ agentId: 'specialist-fitness' });
  });

  it('submits a question and reads the accepted answer', async () => {
    const { api, calls } = setup(() =>
      withHeaders(202, { conversationId, invocationId, statusLocation: `/api/v1/agent-invocations/${invocationId}`, retryAfterSeconds: 3 }, {}),
    );
    const result = await createAgentConversationAdapter(api).submitQuestion(conversationId, 'specialist-tactics', 'Why?', 'key-2');
    expect(header(calls[0], 'Idempotency-Key')).toBe('key-2');
    expect(bodyOf(calls[0])).toEqual({ agentId: 'specialist-tactics', question: 'Why?' });
    expect(result).toMatchObject({ invocationId, retryAfterSeconds: 3 });
  });

  it('maps turns with unknown raw values and an empty evidence list', async () => {
    const { api, calls } = setup(() =>
      withHeaders(200, { turns: [turn, { ...turn, invocationId: conversationId, state: 'mystery', failureReason: 'odd', advice: null }], nextCursor: 'c2' }, { ETag: '"t1"' }),
    );
    const page = await createAgentConversationAdapter(api).listTurns(conversationId, 'c1');
    expect(calls[0].url).toContain('cursor=c1');
    expect(page).toMatchObject({ kind: 'changed', etag: '"t1"' });
    if (page.kind !== 'changed') throw new Error('expected changed');
    expect(page.value.nextCursor).toBe('c2');
    expect(page.value.turns[0]).toMatchObject({ state: { recognized: 'succeeded' }, advice: { text: 'Press higher.', evidenceReferences: [] } });
    expect(page.value.turns[1].state).toEqual({ value: 'mystery', known: false });
    expect(page.value.turns[1].failureReason).toEqual({ value: 'odd', known: false });
    expect(page.value.turns[1].advice).toBeUndefined();
  });

  it('refuses a turn without an invocation id as incompatible', async () => {
    const { api } = setup(() => withHeaders(200, { turns: [{ ...turn, invocationId: null }] }, { ETag: '"t"' }));
    expect(await outcomeOf(createAgentConversationAdapter(api).listTurns(conversationId))).toEqual({ kind: 'platform', failureKind: 'incompatible' });
  });

  it('reads an invocation status with Retry-After and 304', async () => {
    let status = 200;
    const { api } = setup(() =>
      status === 304
        ? new Response(null, { status: 304 })
        : withHeaders(200, { invocationId, conversationId, agentId: 'specialist-tactics', route: 'r', state: 'running', attemptCount: 1 }, { ETag: '"s1"', 'Retry-After': '5' }),
    );
    const adapter = createAgentConversationAdapter(api);
    const first = await adapter.getInvocationStatus(invocationId);
    expect(first).toMatchObject({ kind: 'changed', etag: '"s1"', retryAfterSeconds: 5, value: { state: { value: 'running', recognized: 'pending' }, retryAfterSeconds: 5 } });
    status = 304;
    expect(await adapter.getInvocationStatus(invocationId, '"s1"')).toEqual({ kind: 'unchanged' });
  });

  it('maps lineage entries in sequence order with execution references', async () => {
    const { api, calls } = setup(() =>
      withHeaders(
        200,
        {
          invocationId,
          policyVersion: 'p1',
          entries: [
            { sequence: 2, kind: 'completed', attemptId: ids.run, attemptNumber: 1, recordedAt: '2026-10-01T12:00:09Z', details: { note: 'done' } },
            { sequence: 1, kind: 'new-kind', recordedAt: '2026-10-01T12:00:00Z', details: {} },
          ],
          advice: { advice: 'Press higher.', attemptId: ids.run, acceptedAt: '2026-10-01T12:00:09Z', executionReferences: { runtime: 'hermes', model: 'm1' } },
        },
        { ETag: '"l1"' },
      ),
    );
    const result = await createAgentConversationAdapter(api).getLineage(invocationId);
    expect(calls[0].url).toContain(`/agent-invocations/${invocationId}/lineage`);
    if (result.kind !== 'changed') throw new Error('expected changed');
    expect(result.value.entries.map((e) => e.sequence)).toEqual([1, 2]);
    expect(result.value.entries[0].kind).toEqual({ value: 'new-kind', known: false });
    expect(result.value.entries[1]).toMatchObject({ kind: { recognized: 'completed' }, attemptNumber: 1, details: { note: 'done' } });
    expect(result.value.advice).toMatchObject({ evidenceReferences: [], executionReferences: { runtime: 'hermes', model: 'm1' } });
  });

  describe('refusals', () => {
    const submit = (api: ReturnType<typeof setup>['api']) =>
      createAgentConversationAdapter(api).submitQuestion(conversationId, 'specialist-tactics', 'Why?', 'k');

    it.each([
      [429, 'too-many-unfinished-invocations', 'tooManyUnfinished'],
      [409, 'agent-not-available', 'agentNotAvailable'],
      [409, 'agent-not-participant', 'agentNotParticipant'],
      [409, 'participant-exists', 'participantExists'],
      [409, 'analysis-run-not-eligible', 'runNotEligible'],
      [403, 'forbidden', 'notPermitted'],
      [404, 'not-found', 'notFound'],
      [412, 'precondition-failed', 'staleVersion'],
    ])('translates %i %s to %s', async (status, code, kind) => {
      const { api } = setup(() => problem(status, code));
      expect((await outcomeOf(submit(api))).kind).toBe(kind);
    });

    it('keeps the field errors of an invalid question', async () => {
      const { api } = setup(() => problem(400, 'validation', [{ field: 'question', code: 'too-long' }]));
      expect(await outcomeOf(submit(api))).toEqual({ kind: 'invalidQuestion', fieldErrors: [{ field: 'question', code: 'too-long' }] });
    });

    it('treats a network failure of a state-changing call as an unknown outcome', async () => {
      const { api } = setup(() => {
        throw new TypeError('network');
      });
      expect(await outcomeOf(submit(api))).toEqual({ kind: 'unknownOutcome' });
      expect(await outcomeOf(createAgentConversationAdapter(api).createConversation(ids.match, ids.run, 'k'))).toEqual({ kind: 'unknownOutcome' });
    });

    it('keeps a network failure of a read as the shared kind', async () => {
      const { api } = setup(() => {
        throw new TypeError('network');
      });
      expect(await outcomeOf(createAgentConversationAdapter(api).getConversation(conversationId))).toEqual({ kind: 'platform', failureKind: 'unreachable' });
    });

    it.each([
      [409, 'idempotency-key-reused'],
      [400, 'idempotency-key-missing'],
      [428, 'precondition-required'],
    ])('leaves %i %s as a programming error', async (status, code) => {
      const { api } = setup(() => problem(status, code));
      expect(await outcomeOf(submit(api))).toEqual({ kind: 'platform', failureKind: 'programmingError' });
    });

    it('leaves other failures as the shared kind', async () => {
      const { api } = setup(() => problem(500, 'boom'));
      expect(await outcomeOf(submit(api))).toEqual({ kind: 'platform', failureKind: 'unexpected' });
    });
  });

  it('never touches browser storage', async () => {
    const local = vi.spyOn(Storage.prototype, 'setItem');
    const { api } = setup(() => withHeaders(200, conversation, { ETag: '"3"' }));
    const adapter = createAgentConversationAdapter(api);
    await adapter.getConversation(conversationId);
    await adapter.addParticipant(conversationId, 'specialist-fitness', 3);
    expect(local).not.toHaveBeenCalled();
    local.mockRestore();
  });
});
