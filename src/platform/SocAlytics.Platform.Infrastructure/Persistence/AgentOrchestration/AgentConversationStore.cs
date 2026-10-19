using Dapper;
using SocAlytics.Platform.Application.AgentOrchestration;

namespace SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration;

internal sealed class AgentConversationStore(IDbSession session) : IAgentConversationStore
{
    private const string ConversationSelect =
        """
        SELECT id AS Id, team_id AS TeamId, match_id AS MatchId, analysis_run_id AS AnalysisRunId,
               owner_member_id AS OwnerMemberId, state AS State, idempotency_key AS IdempotencyKey,
               request_hash AS RequestHash, created_at AS CreatedAt, version AS Version
        FROM socalytics.agent_conversations
        """;

    private sealed record ConversationRow(
        Guid Id, Guid TeamId, Guid MatchId, Guid AnalysisRunId, Guid OwnerMemberId, string State,
        string IdempotencyKey, string RequestHash, DateTime CreatedAt, long Version);

    private sealed record ParticipantRow(Guid ConversationId, string AgentId, Guid AddedByMemberId, DateTime AddedAt);

    private static DateTimeOffset Utc(DateTime v) => new(DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private async Task<AgentConversationRecord[]> ToRecordsAsync(IReadOnlyList<ConversationRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var connection = await session.GetConnectionAsync(cancellationToken);
        var participants = (await connection.QueryAsync<ParticipantRow>(new CommandDefinition(
            """
            SELECT conversation_id AS ConversationId, agent_id AS AgentId, added_by_member_id AS AddedByMemberId, added_at AS AddedAt
            FROM socalytics.agent_conversation_participants
            WHERE conversation_id = ANY(@ids)
            ORDER BY conversation_id, agent_id
            """,
            new { ids = rows.Select(r => r.Id).ToArray() },
            session.Transaction,
            cancellationToken: cancellationToken))).ToLookup(p => p.ConversationId);

        return [.. rows.Select(r => new AgentConversationRecord(
            r.Id, r.TeamId, r.MatchId, r.AnalysisRunId, r.OwnerMemberId, r.State, r.IdempotencyKey, r.RequestHash,
            Utc(r.CreatedAt), r.Version,
            [.. participants[r.Id].Select(p => new AgentParticipantRecord(p.AgentId, p.AddedByMemberId, Utc(p.AddedAt)))]))];
    }

    public async Task<AgentConversationRecord?> FindByOwnerAndKeyAsync(
        Guid ownerMemberId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ConversationRow>(new CommandDefinition(
            ConversationSelect + " WHERE owner_member_id = @ownerMemberId AND idempotency_key = @idempotencyKey",
            new { ownerMemberId, idempotencyKey },
            session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : (await ToRecordsAsync([row], cancellationToken))[0];
    }

    public async Task InsertAsync(NewAgentConversation conversation, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_conversations
                (id, team_id, match_id, analysis_run_id, owner_member_id, state, idempotency_key, request_hash, created_at)
            VALUES (@Id, @TeamId, @MatchId, @AnalysisRunId, @OwnerMemberId, 'active', @IdempotencyKey, @RequestHash, @CreatedAt)
            """,
            new
            {
                conversation.Id,
                conversation.TeamId,
                conversation.MatchId,
                conversation.AnalysisRunId,
                conversation.OwnerMemberId,
                conversation.IdempotencyKey,
                conversation.RequestHash,
                conversation.CreatedAt,
            },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var agentId in conversation.ParticipantAgentIds)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO socalytics.agent_conversation_participants (conversation_id, agent_id, added_by_member_id, added_at)
                VALUES (@id, @agentId, @ownerMemberId, @addedAt)
                """,
                new { id = conversation.Id, agentId, ownerMemberId = conversation.OwnerMemberId, addedAt = conversation.CreatedAt },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<AgentConversationRecord?> LockByIdAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ConversationRow>(new CommandDefinition(
            ConversationSelect + " WHERE id = @conversationId FOR UPDATE",
            new { conversationId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : (await ToRecordsAsync([row], cancellationToken))[0];
    }

    public async Task<AddParticipantResult> TryAddParticipantAsync(
        Guid conversationId,
        long expectedVersion,
        string agentId,
        Guid addedByMemberId,
        DateTimeOffset addedAt,
        CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_conversation_participants (conversation_id, agent_id, added_by_member_id, added_at)
            SELECT c.id, @agentId, @addedByMemberId, @addedAt
            FROM socalytics.agent_conversations c
            WHERE c.id = @conversationId AND c.version = @expectedVersion
            ON CONFLICT (conversation_id, agent_id) DO NOTHING
            """,
            new { conversationId, expectedVersion, agentId, addedByMemberId, addedAt },
            transaction,
            cancellationToken: cancellationToken));

        var current = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT version FROM socalytics.agent_conversations WHERE id = @conversationId",
            new { conversationId },
            transaction,
            cancellationToken: cancellationToken));
        if (current is null)
        {
            return new AddParticipantResult(AddParticipantOutcome.NotFound, null);
        }

        if (inserted > 0)
        {
            return new AddParticipantResult(AddParticipantOutcome.Added, current);
        }

        return current != expectedVersion
            ? new AddParticipantResult(AddParticipantOutcome.VersionConflict, current)
            : new AddParticipantResult(AddParticipantOutcome.AlreadyParticipant, current);
    }

    public async Task<AgentConversationPage> ListByOwnerAndMatchAsync(
        Guid ownerMemberId,
        Guid matchId,
        AgentConversationPageKey? after,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ConversationRow>(new CommandDefinition(
            ConversationSelect + """

             WHERE owner_member_id = @ownerMemberId AND match_id = @matchId
               AND (@hasAfter = false OR (created_at, id) < (@afterCreated, @afterId))
             ORDER BY created_at DESC, id DESC
             LIMIT @limit
             """,
            new
            {
                ownerMemberId,
                matchId,
                hasAfter = after is not null,
                afterCreated = after?.CreatedAt ?? DateTimeOffset.MinValue,
                afterId = after?.Id ?? Guid.Empty,
                limit = pageSize + 1,
            },
            session.Transaction,
            cancellationToken: cancellationToken))).ToList();

        var items = await ToRecordsAsync([.. rows.Take(pageSize)], cancellationToken);
        return new AgentConversationPage(items, rows.Count > pageSize);
    }

    public async Task<bool> IsCurrentRunAsync(Guid matchId, Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<bool>(new CommandDefinition(
            """
            SELECT coalesce(
                (SELECT r.id
                 FROM socalytics.analysis_runs r
                 WHERE r.match_id = @matchId
                   AND r.recording_set_version_id =
                       (SELECT s.id FROM socalytics.recording_set_versions s
                        WHERE s.match_id = @matchId
                        ORDER BY s.finalized_at DESC, s.id DESC
                        LIMIT 1)
                 ORDER BY r.created_at DESC, r.id DESC
                 LIMIT 1) = @runId,
                false)
            """,
            new { matchId, runId },
            session.Transaction,
            cancellationToken: cancellationToken));
    }
}
