using Dapper;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration;

internal sealed class AgentInvocationStore(IDbSession session) : IAgentInvocationStore
{
    private const string InvocationSelect =
        """
        SELECT id AS Id, conversation_id AS ConversationId, team_id AS TeamId, match_id AS MatchId,
               analysis_run_id AS AnalysisRunId, owner_member_id AS OwnerMemberId, agent_id AS AgentId, route AS Route,
               question AS Question, idempotency_key AS IdempotencyKey, request_hash AS RequestHash,
               correlation_id AS CorrelationId, policy_version AS PolicyVersion, execution_policy::text AS ExecutionPolicy,
               state AS State, attempts_used AS AttemptsUsed, current_attempt_id AS CurrentAttemptId,
               ready_occurrence AS ReadyOccurrence, last_notified_at AS LastNotifiedAt, failure_reason AS FailureReason,
               submitted_at AS SubmittedAt, ended_at AS EndedAt, progress_version AS ProgressVersion, version AS Version,
               agent_version AS AgentVersion, agent_version_digest AS AgentVersionDigest
        FROM socalytics.agent_invocations
        """;

    private const string TurnSelect =
        """
        SELECT i.id AS InvocationId, i.agent_id AS AgentId, i.question AS Question, i.state AS State,
               i.failure_reason AS FailureReason, i.submitted_at AS SubmittedAt,
               a.attempt_id AS AdviceAttemptId, a.advice AS Advice, a.execution_references::text AS ExecutionReferences,
               a.accepted_at AS AcceptedAt
        FROM socalytics.agent_invocations i
        LEFT JOIN socalytics.agent_accepted_advice a ON a.invocation_id = i.id
        """;

    private sealed class InvocationRow
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public Guid TeamId { get; set; }
        public Guid MatchId { get; set; }
        public Guid AnalysisRunId { get; set; }
        public Guid OwnerMemberId { get; set; }
        public string AgentId { get; set; } = "";
        public string Route { get; set; } = "";
        public string Question { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public Guid CorrelationId { get; set; }
        public string PolicyVersion { get; set; } = "";
        public string ExecutionPolicy { get; set; } = "";
        public string State { get; set; } = "";
        public int AttemptsUsed { get; set; }
        public Guid? CurrentAttemptId { get; set; }
        public int ReadyOccurrence { get; set; }
        public DateTime? LastNotifiedAt { get; set; }
        public string? FailureReason { get; set; }
        public DateTime SubmittedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public long ProgressVersion { get; set; }
        public long Version { get; set; }
        public string? AgentVersion { get; set; }
        public string? AgentVersionDigest { get; set; }
    }

    private sealed class TurnRow
    {
        public Guid InvocationId { get; set; }
        public string AgentId { get; set; } = "";
        public string Question { get; set; } = "";
        public string State { get; set; } = "";
        public string? FailureReason { get; set; }
        public DateTime SubmittedAt { get; set; }
        public Guid? AdviceAttemptId { get; set; }
        public string? Advice { get; set; }
        public string? ExecutionReferences { get; set; }
        public DateTime? AcceptedAt { get; set; }
    }

    private sealed class LineageRow
    {
        public int Sequence { get; set; }
        public string Kind { get; set; } = "";
        public Guid? AttemptId { get; set; }
        public string Payload { get; set; } = "";
        public DateTime RecordedAt { get; set; }
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static AgentFailureReason? ParseReason(string? value) =>
        AgentPersistedNames.TryParseFailureReason(value, out var parsed) ? parsed : null;

    private static AgentInvocationRecord ToRecord(InvocationRow r)
    {
        _ = AgentPersistedNames.TryParseRoute(r.Route, out var route);
        _ = AgentPersistedNames.TryParseInvocationState(r.State, out var state);
        return new AgentInvocationRecord(
            r.Id, r.ConversationId, r.TeamId, r.MatchId, r.AnalysisRunId, r.OwnerMemberId, r.AgentId, route, r.Question,
            r.IdempotencyKey, r.RequestHash, r.CorrelationId, r.PolicyVersion, r.ExecutionPolicy, state, r.AttemptsUsed,
            r.CurrentAttemptId, r.ReadyOccurrence, Utc(r.LastNotifiedAt), ParseReason(r.FailureReason), Utc(r.SubmittedAt),
            Utc(r.EndedAt), r.ProgressVersion, r.Version, r.AgentVersion, r.AgentVersionDigest);
    }

    private static AgentAcceptedAdviceRecord? ToAdvice(Guid invocationId, TurnRow r) =>
        r is { Advice: { } text, AdviceAttemptId: { } attemptId, AcceptedAt: { } acceptedAt }
            ? new AgentAcceptedAdviceRecord(invocationId, attemptId, text, r.ExecutionReferences ?? "{}", Utc(acceptedAt))
            : null;

    private static AgentTurnRecord ToTurn(TurnRow r)
    {
        _ = AgentPersistedNames.TryParseInvocationState(r.State, out var state);
        return new AgentTurnRecord(
            r.InvocationId, r.AgentId, r.Question, state, ParseReason(r.FailureReason), Utc(r.SubmittedAt),
            ToAdvice(r.InvocationId, r));
    }

    public async Task<AgentInvocationRecord?> FindByKeyAsync(
        Guid ownerMemberId, Guid conversationId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<InvocationRow>(new CommandDefinition(
            InvocationSelect +
            " WHERE owner_member_id = @ownerMemberId AND conversation_id = @conversationId AND idempotency_key = @idempotencyKey",
            new { ownerMemberId, conversationId, idempotencyKey }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<AgentInvocationRecord?> FindAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<InvocationRow>(new CommandDefinition(
            InvocationSelect + " WHERE id = @invocationId", new { invocationId }, session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<AgentInvocationRecord> InsertAsync(NewAgentInvocation invocation, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_invocations
                (id, conversation_id, team_id, match_id, analysis_run_id, owner_member_id, agent_id, route, question,
                 idempotency_key, request_hash, correlation_id, policy_version, execution_policy, state, attempts_used,
                 ready_occurrence, submitted_at, agent_version, agent_version_digest)
            VALUES (@Id, @ConversationId, @TeamId, @MatchId, @AnalysisRunId, @OwnerMemberId, @AgentId, @Route, @Question,
                    @IdempotencyKey, @RequestHash, @CorrelationId, @PolicyVersion, @ExecutionPolicy::jsonb, 'queued', 0,
                    1, @SubmittedAt, @AgentVersion, @AgentVersionDigest)
            """,
            new
            {
                invocation.Id,
                invocation.ConversationId,
                invocation.TeamId,
                invocation.MatchId,
                invocation.AnalysisRunId,
                invocation.OwnerMemberId,
                invocation.AgentId,
                Route = AgentPersistedNames.ToPersistedName(invocation.Route),
                invocation.Question,
                invocation.IdempotencyKey,
                invocation.RequestHash,
                invocation.CorrelationId,
                invocation.PolicyVersion,
                ExecutionPolicy = invocation.ExecutionPolicyJson,
                invocation.SubmittedAt,
                invocation.AgentVersion,
                invocation.AgentVersionDigest,
            },
            transaction,
            cancellationToken: cancellationToken));
        var row = await connection.QuerySingleAsync<InvocationRow>(new CommandDefinition(
            InvocationSelect + " WHERE id = @Id", new { invocation.Id }, transaction, cancellationToken: cancellationToken));
        return ToRecord(row);
    }

    public async Task<AgentInvocationRecord?> LockAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<InvocationRow>(new CommandDefinition(
            InvocationSelect + " WHERE id = @invocationId FOR UPDATE", new { invocationId }, transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<int> CountUnfinishedAsync(Guid ownerMemberId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM socalytics.agent_invocations WHERE owner_member_id = @ownerMemberId AND state IN ('queued','running')",
            new { ownerMemberId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<long> CountQueuedAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM socalytics.agent_invocations WHERE state = 'queued'",
            transaction: session.Transaction, cancellationToken: cancellationToken));
    }

    public Task<VersionedWriteResult> TransitionAsync(
        Guid invocationId, AgentInvocationState expectedState, long expectedVersion, AgentInvocationState newState,
        AgentFailureReason? failureReason, DateTimeOffset? endedAt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        return VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                """
                UPDATE socalytics.agent_invocations
                SET state = @newState, failure_reason = @failureReason, ended_at = @endedAt,
                    progress_version = progress_version + 1
                WHERE id = @invocationId AND state = @expectedState AND version = @expectedVersion
                RETURNING version
                """,
                new
                {
                    invocationId,
                    expectedState = AgentPersistedNames.ToPersistedName(expectedState),
                    expectedVersion,
                    newState = AgentPersistedNames.ToPersistedName(newState),
                    failureReason = failureReason is { } reason ? AgentPersistedNames.ToPersistedName(reason) : null,
                    endedAt,
                },
                transaction,
                cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.agent_invocations WHERE id = @invocationId",
                new { invocationId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task SetCurrentAttemptAsync(Guid invocationId, Guid? attemptId, int attemptsUsed, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.agent_invocations
            SET current_attempt_id = @attemptId, attempts_used = @attemptsUsed, progress_version = progress_version + 1
            WHERE id = @invocationId
            """,
            new { invocationId, attemptId, attemptsUsed }, transaction, cancellationToken: cancellationToken));
    }

    public async Task SetReadyOccurrenceAsync(Guid invocationId, int readyOccurrence, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.agent_invocations SET ready_occurrence = @readyOccurrence WHERE id = @invocationId",
            new { invocationId, readyOccurrence }, transaction, cancellationToken: cancellationToken));
    }

    public async Task SetLastNotifiedAsync(Guid invocationId, DateTimeOffset notifiedAt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.agent_invocations SET last_notified_at = @notifiedAt WHERE id = @invocationId",
            new { invocationId, notifiedAt }, transaction, cancellationToken: cancellationToken));
    }

    public async Task TouchProgressAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.agent_invocations SET progress_version = progress_version + 1 WHERE id = @invocationId",
            new { invocationId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AgentInvocationRef>> ListQueuedNotifiedBeforeAsync(
        DateTimeOffset notifiedBefore, int batchSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AgentInvocationRef>(new CommandDefinition(
            """
            SELECT id AS InvocationId, ready_occurrence AS ReadyOccurrence
            FROM socalytics.agent_invocations
            WHERE state = 'queued' AND (last_notified_at IS NULL OR last_notified_at < @notifiedBefore)
            ORDER BY last_notified_at NULLS FIRST, submitted_at, id
            LIMIT @batchSize
            """,
            new { notifiedBefore, batchSize }, session.Transaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> AppendLineageAsync(NewAgentLineageEntry entry, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_lineage_entries (invocation_id, sequence, kind, attempt_id, payload, recorded_at)
            SELECT @InvocationId, COALESCE(MAX(sequence), 0) + 1, @Kind, @AttemptId, @Payload::jsonb, @RecordedAt
            FROM socalytics.agent_lineage_entries WHERE invocation_id = @InvocationId
            RETURNING sequence
            """,
            new { entry.InvocationId, entry.Kind, entry.AttemptId, Payload = entry.PayloadJson, entry.RecordedAt },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task InsertAdviceAsync(NewAgentAcceptedAdvice advice, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_accepted_advice (invocation_id, attempt_id, advice, execution_references, accepted_at)
            VALUES (@InvocationId, @AttemptId, @Advice, @References::jsonb, @AcceptedAt)
            """,
            new { advice.InvocationId, advice.AttemptId, advice.Advice, References = advice.ExecutionReferencesJson, advice.AcceptedAt },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AgentTurnRecord>> ListTurnsAsync(
        Guid conversationId, AgentTurnCursor? after, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var cursorFilter = after is null ? "" : " AND (i.submitted_at, i.id) > (@afterSubmittedAt, @afterInvocationId)";
        var rows = await connection.QueryAsync<TurnRow>(new CommandDefinition(
            TurnSelect + " WHERE i.conversation_id = @conversationId" + cursorFilter +
            " ORDER BY i.submitted_at, i.id LIMIT @limit",
            new
            {
                conversationId,
                afterSubmittedAt = after?.SubmittedAt ?? default,
                afterInvocationId = after?.InvocationId ?? Guid.Empty,
                limit,
            },
            session.Transaction,
            cancellationToken: cancellationToken));
        return rows.Select(ToTurn).ToList();
    }

    public async Task<IReadOnlyList<AgentTurnRecord>> ListSucceededTurnsBeforeAsync(
        Guid conversationId, DateTimeOffset submittedBefore, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TurnRow>(new CommandDefinition(
            TurnSelect +
            " WHERE i.conversation_id = @conversationId AND i.state = 'succeeded' AND i.submitted_at < @submittedBefore" +
            " ORDER BY i.submitted_at DESC, i.id DESC LIMIT @limit",
            new { conversationId, submittedBefore, limit }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToTurn).ToList();
    }

    public async Task<AgentInvocationLineage> ReadLineageAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var entries = await connection.QueryAsync<LineageRow>(new CommandDefinition(
            """
            SELECT sequence AS Sequence, kind AS Kind, attempt_id AS AttemptId, payload::text AS Payload,
                   recorded_at AS RecordedAt
            FROM socalytics.agent_lineage_entries WHERE invocation_id = @invocationId ORDER BY sequence
            """,
            new { invocationId }, session.Transaction, cancellationToken: cancellationToken));
        var advice = await connection.QuerySingleOrDefaultAsync<TurnRow>(new CommandDefinition(
            """
            SELECT attempt_id AS AdviceAttemptId, advice AS Advice, execution_references::text AS ExecutionReferences,
                   accepted_at AS AcceptedAt
            FROM socalytics.agent_accepted_advice WHERE invocation_id = @invocationId
            """,
            new { invocationId }, session.Transaction, cancellationToken: cancellationToken));
        return new AgentInvocationLineage(
            entries.Select(e => new AgentLineageEntryRecord(e.Sequence, e.Kind, e.AttemptId, e.Payload, Utc(e.RecordedAt))).ToList(),
            advice is null ? null : ToAdvice(invocationId, advice));
    }
}
