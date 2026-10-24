using System.Text.Json;
using Dapper;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration;

internal sealed class AgentConversationStore(IDbSession session, IOutbox outbox) : IAgentConversationStore
{
    public const string PurgeSubject = "agents.conversations.purge";

    private const string ConversationSelect =
        """
        SELECT id AS Id, team_id AS TeamId, match_id AS MatchId, analysis_run_id AS AnalysisRunId,
               owner_member_id AS OwnerMemberId, state AS State, idempotency_key AS IdempotencyKey,
               request_hash AS RequestHash, created_at AS CreatedAt, version AS Version, archived_at AS ArchivedAt
        FROM socalytics.agent_conversations
        """;

    private sealed record ConversationRow(
        Guid Id, Guid TeamId, Guid MatchId, Guid AnalysisRunId, Guid OwnerMemberId, string State,
        string IdempotencyKey, string RequestHash, DateTime CreatedAt, long Version, DateTime? ArchivedAt);

    private sealed record VersionStateRow(long Version, string State);

    private sealed record TeamVersionRow(Guid TeamId, long Version);

    private sealed record TombstoneRow(Guid ConversationId, Guid TeamId, Guid DeletedByMemberId, DateTime DeletedAt, int InvocationsSuppressed);

    private sealed record PurgeRow(Guid ConversationId, string Copy, string State, string? Outcome, int Attempts, DateTime NextAttemptAt);

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
            [.. participants[r.Id].Select(p => new AgentParticipantRecord(p.AgentId, p.AddedByMemberId, Utc(p.AddedAt)))],
            r.ArchivedAt is { } archivedAt ? Utc(archivedAt) : null))];
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
        string state,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ConversationRow>(new CommandDefinition(
            ConversationSelect + """

             WHERE owner_member_id = @ownerMemberId AND match_id = @matchId AND state = @state
               AND (@hasAfter = false OR (created_at, id) < (@afterCreated, @afterId))
             ORDER BY created_at DESC, id DESC
             LIMIT @limit
             """,
            new
            {
                ownerMemberId,
                matchId,
                state,
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

    public async Task<ConversationTransitionResult> ArchiveAsync(
        Guid conversationId, long expectedVersion, DateTimeOffset archivedAt, CancellationToken cancellationToken) =>
        await TransitionAsync(
            conversationId, expectedVersion, "active",
            "state = 'archived', archived_at = @at", new { at = archivedAt }, cancellationToken);

    public async Task<ConversationTransitionResult> UnarchiveAsync(
        Guid conversationId, long expectedVersion, CancellationToken cancellationToken) =>
        await TransitionAsync(
            conversationId, expectedVersion, "archived",
            "state = 'active', archived_at = NULL", new { }, cancellationToken);

    private async Task<ConversationTransitionResult> TransitionAsync(
        Guid conversationId, long expectedVersion, string fromState, string assignments, object extra, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var parameters = new DynamicParameters(extra);
        parameters.Add("conversationId", conversationId);
        parameters.Add("expectedVersion", expectedVersion);
        parameters.Add("fromState", fromState);
        var version = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            $"UPDATE socalytics.agent_conversations SET {assignments} " +
            "WHERE id = @conversationId AND version = @expectedVersion AND state = @fromState RETURNING version",
            parameters, transaction, cancellationToken: cancellationToken));
        return version is { } applied
            ? new ConversationTransitionResult(ConversationTransitionOutcome.Applied, applied)
            : await ClassifyFailureAsync(conversationId, expectedVersion, cancellationToken);
    }

    private async Task<ConversationTransitionResult> ClassifyFailureAsync(
        Guid conversationId, long expectedVersion, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<VersionStateRow>(new CommandDefinition(
            "SELECT version AS Version, state AS State FROM socalytics.agent_conversations WHERE id = @conversationId",
            new { conversationId }, session.Transaction, cancellationToken: cancellationToken));
        if (current is not { } row)
        {
            return new ConversationTransitionResult(ConversationTransitionOutcome.NotFound, null);
        }

        return row.Version != expectedVersion
            ? new ConversationTransitionResult(ConversationTransitionOutcome.VersionConflict, row.Version)
            : new ConversationTransitionResult(ConversationTransitionOutcome.InvalidState, row.Version);
    }

    public async Task<ConversationTransitionResult> DeleteAsync(
        NewAgentConversationDeletion deletion, long expectedVersion, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var updated = await connection.QuerySingleOrDefaultAsync<TeamVersionRow>(new CommandDefinition(
            """
            UPDATE socalytics.agent_conversations SET state = 'deleted'
            WHERE id = @ConversationId AND version = @expectedVersion AND state IN ('active', 'archived')
            RETURNING team_id AS TeamId, version AS Version
            """,
            new { deletion.ConversationId, expectedVersion }, transaction, cancellationToken: cancellationToken));
        if (updated is not { } row)
        {
            return await ClassifyFailureAsync(deletion.ConversationId, expectedVersion, cancellationToken);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_conversation_tombstones
                (conversation_id, team_id, deleted_by_member_id, deleted_at, invocations_suppressed)
            VALUES (@ConversationId, @teamId, @DeletedByMemberId, @DeletedAt, @InvocationsSuppressed)
            """,
            new { deletion.ConversationId, teamId = row.TeamId, deletion.DeletedByMemberId, deletion.DeletedAt, deletion.InvocationsSuppressed },
            transaction, cancellationToken: cancellationToken));
        await EnsurePurgeRecordsAsync(deletion.ConversationId, deletion.DeletedAt, cancellationToken);

        var payload = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1.0.0",
            conversationId = deletion.ConversationId,
            requestedAt = deletion.DeletedAt,
        });
        await outbox.EnqueueAsync(
            new OutboxMessageDraft(
                Guid.CreateVersion7(), OutboxMessageTypes.AgentConversationPurge, "1.0.0", PurgeSubject, payload, deletion.ConversationId),
            cancellationToken);
        return new ConversationTransitionResult(ConversationTransitionOutcome.Applied, row.Version);
    }

    public async Task<bool> ReassertDeletedAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.agent_conversations SET state = 'deleted' WHERE id = @conversationId AND state <> 'deleted'",
            new { conversationId }, transaction, cancellationToken: cancellationToken));
        return changed > 0;
    }

    public async Task<AgentConversationTombstoneRecord?> FindTombstoneAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TombstoneRow>(new CommandDefinition(
            TombstoneSelect + " WHERE conversation_id = @conversationId",
            new { conversationId }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToTombstone(row);
    }

    private const string TombstoneSelect =
        """
        SELECT conversation_id AS ConversationId, team_id AS TeamId, deleted_by_member_id AS DeletedByMemberId,
               deleted_at AS DeletedAt, invocations_suppressed AS InvocationsSuppressed
        FROM socalytics.agent_conversation_tombstones
        """;

    private static AgentConversationTombstoneRecord ToTombstone(TombstoneRow r) =>
        new(r.ConversationId, r.TeamId, r.DeletedByMemberId, Utc(r.DeletedAt), r.InvocationsSuppressed);

    public async Task EnsurePurgeRecordsAsync(Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_conversation_purges (conversation_id, copy, state, attempts, next_attempt_at, updated_at)
            SELECT t.conversation_id, c.copy, 'pending', 0, @now, @now
            FROM socalytics.agent_conversation_tombstones t
            CROSS JOIN (VALUES ('primary'), ('index')) AS c (copy)
            WHERE t.conversation_id = @conversationId
            ON CONFLICT (conversation_id, copy) DO NOTHING
            """,
            new { conversationId, now }, transaction, cancellationToken: cancellationToken));
    }

    public async Task EnsureMissingPurgeRecordsAsync(int limit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_conversation_purges (conversation_id, copy, state, attempts, next_attempt_at, updated_at)
            SELECT t.conversation_id, c.copy, 'pending', 0, @now, @now
            FROM (SELECT conversation_id FROM socalytics.agent_conversation_tombstones t
                  WHERE (SELECT count(*) FROM socalytics.agent_conversation_purges p WHERE p.conversation_id = t.conversation_id) < 2
                  ORDER BY deleted_at, conversation_id LIMIT @limit) t
            CROSS JOIN (VALUES ('primary'), ('index')) AS c (copy)
            ON CONFLICT (conversation_id, copy) DO NOTHING
            """,
            new { limit, now }, transaction, cancellationToken: cancellationToken));
    }

    private const string PurgeSelect =
        """
        SELECT conversation_id AS ConversationId, copy AS Copy, state AS State, outcome AS Outcome,
               attempts AS Attempts, next_attempt_at AS NextAttemptAt
        FROM socalytics.agent_conversation_purges
        """;

    private async Task<IReadOnlyList<AgentPurgeRecord>> ClaimPurgesAsync(
        string condition, object parameters, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PurgeRow>(new CommandDefinition(
            $"{PurgeSelect} WHERE {condition} ORDER BY next_attempt_at, conversation_id, copy LIMIT @limit FOR UPDATE SKIP LOCKED",
            parameters, transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new AgentPurgeRecord(r.ConversationId, r.Copy, r.State, r.Outcome, r.Attempts, Utc(r.NextAttemptAt)))];
    }

    public Task<IReadOnlyList<AgentPurgeRecord>> ClaimDuePurgesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        ClaimPurgesAsync("state = 'pending' AND next_attempt_at <= @now", new { now, limit }, cancellationToken);

    public Task<IReadOnlyList<AgentPurgeRecord>> ClaimUnresolvedPurgesAsync(int limit, CancellationToken cancellationToken) =>
        ClaimPurgesAsync("state = 'unresolved'", new { limit }, cancellationToken);

    public async Task CompletePurgeAsync(
        Guid conversationId, string copy, string outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.agent_conversation_purges
            SET state = 'completed', outcome = @outcome, completed_at = @now, updated_at = @now
            WHERE conversation_id = @conversationId AND copy = @copy AND state IN ('pending', 'unresolved')
            """,
            new { conversationId, copy, outcome, now }, transaction, cancellationToken: cancellationToken));
    }

    public async Task RecordPurgeFailureAsync(
        Guid conversationId, string copy, DateTimeOffset now, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.agent_conversation_purges
            SET attempts = attempts + 1, next_attempt_at = @nextAttemptAt, updated_at = @now
            WHERE conversation_id = @conversationId AND copy = @copy AND state = 'pending'
            """,
            new { conversationId, copy, now, nextAttemptAt }, transaction, cancellationToken: cancellationToken));
    }

    public async Task MarkPurgeUnresolvedAsync(
        Guid conversationId, string copy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.agent_conversation_purges
            SET state = 'unresolved', attempts = attempts + 1, updated_at = @now
            WHERE conversation_id = @conversationId AND copy = @copy AND state = 'pending'
            """,
            new { conversationId, copy, now }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<int> PurgePrimaryAsync(Guid conversationId, DateTimeOffset purgedAt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            WITH invocation_ids AS (
                SELECT id FROM socalytics.agent_invocations WHERE conversation_id = @conversationId),
            advice AS (
                DELETE FROM socalytics.agent_accepted_advice
                WHERE invocation_id IN (SELECT id FROM invocation_ids) RETURNING 1),
            lineage AS (
                DELETE FROM socalytics.agent_lineage_entries
                WHERE invocation_id IN (SELECT id FROM invocation_ids) RETURNING 1),
            operations AS (
                DELETE FROM socalytics.agent_workload_operations
                WHERE invocation_id IN (SELECT id FROM invocation_ids) RETURNING 1),
            blanked AS (
                UPDATE socalytics.agent_invocations SET question = '', purged_at = @purgedAt
                WHERE conversation_id = @conversationId AND purged_at IS NULL RETURNING 1)
            SELECT (SELECT count(*) FROM advice) + (SELECT count(*) FROM lineage)
                 + (SELECT count(*) FROM operations) + (SELECT count(*) FROM blanked)
            """,
            new { conversationId, purgedAt }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AgentConversationTombstoneRecord>> ListInconsistentTombstonesAsync(
        int limit, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TombstoneRow>(new CommandDefinition(
            """
            SELECT t.conversation_id AS ConversationId, t.team_id AS TeamId, t.deleted_by_member_id AS DeletedByMemberId,
                   t.deleted_at AS DeletedAt, t.invocations_suppressed AS InvocationsSuppressed
            FROM socalytics.agent_conversation_tombstones t
            JOIN socalytics.agent_conversations c ON c.id = t.conversation_id
            WHERE c.state <> 'deleted'
               OR EXISTS (SELECT 1 FROM socalytics.agent_invocations i
                          WHERE i.conversation_id = t.conversation_id AND i.state IN ('queued', 'running'))
               OR EXISTS (SELECT 1 FROM socalytics.agent_attempts a
                          JOIN socalytics.agent_invocations i ON i.id = a.invocation_id
                          WHERE i.conversation_id = t.conversation_id AND a.state IN ('claimed', 'active'))
            ORDER BY t.deleted_at, t.conversation_id
            LIMIT @limit
            """,
            new { limit }, session.Transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(ToTombstone)];
    }
}
