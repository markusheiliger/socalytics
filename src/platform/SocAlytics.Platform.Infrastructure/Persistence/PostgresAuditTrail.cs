using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal sealed class PostgresAuditTrail(
    IDbSession session,
    PlatformDataSource platformDataSource,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IAuditTrail
{
    // Later features add their detail keys to this set in their own tasks.
    private static readonly HashSet<string> AllowedDetailKeys = new(StringComparer.Ordinal)
    {
        "role",
        "previousRole",
        "purpose",
        "targetAccountId",
        "endReason",
        "recoveryId",
        "fromState",
        "toState",
    };

    private const string InsertSql =
        """
        INSERT INTO socalytics.security_audit_event
            (id, occurred_at, event_type, action, outcome, actor_kind, actor_account_id, session_id,
             resource_type, resource_id, team_id, reason_code, details, correlation_id)
        VALUES
            (@id, @occurred_at, @event_type, @action, @outcome, @actor_kind, @actor_account_id, @session_id,
             @resource_type, @resource_id, @team_id, @reason_code, @details, @correlation_id)
        """;

    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var transaction = session.RequireTransaction();
        var details = SerializeDetails(auditEvent);
        var connection = await session.GetConnectionAsync(cancellationToken);

        await InsertAsync(connection, transaction, auditEvent, details, cancellationToken);
    }

    public async Task RecordIndependentAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var details = SerializeDetails(auditEvent);

        if (!platformDataSource.TryGetDataSource(out var dataSource))
        {
            throw new InvalidOperationException("The platform database connection is not configured.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertAsync(connection, transaction, auditEvent, details, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string SerializeDetails(AuditEvent auditEvent)
    {
        var details = auditEvent.Details;
        if (details is null || details.Count == 0)
        {
            return "{}";
        }

        foreach (var key in details.Keys)
        {
            if (!AllowedDetailKeys.Contains(key))
            {
                throw new ArgumentException($"The audit detail key '{key}' is not allowed.", nameof(auditEvent));
            }
        }

        return JsonSerializer.Serialize(details);
    }

    private async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditEvent auditEvent,
        string details,
        CancellationToken cancellationToken)
    {
        var actor = auditEvent.ActorOverride;
        var actorKind = actor?.Kind ?? requestContext.ActorKind;
        var actorAccountId = actor is null ? requestContext.MemberAccountId : actor.AccountId;
        var sessionId = actor is null ? requestContext.SessionId : null;

        await using var command = new NpgsqlCommand(InsertSql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("occurred_at", timeProvider.GetUtcNow().UtcDateTime);
        command.Parameters.AddWithValue("event_type", auditEvent.EventType);
        command.Parameters.AddWithValue("action", auditEvent.Action);
        command.Parameters.AddWithValue("outcome", auditEvent.Outcome.ToWireValue());
        command.Parameters.AddWithValue("actor_kind", ToWireValue(actorKind));
        command.Parameters.AddWithValue("actor_account_id", NpgsqlDbType.Uuid, (object?)actorAccountId ?? DBNull.Value);
        command.Parameters.AddWithValue("session_id", NpgsqlDbType.Uuid, (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("resource_type", auditEvent.Resource.Type);
        command.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, (object?)auditEvent.Resource.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("team_id", NpgsqlDbType.Uuid, (object?)auditEvent.TeamId ?? DBNull.Value);
        command.Parameters.AddWithValue("reason_code", NpgsqlDbType.Text, (object?)auditEvent.ReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue("details", NpgsqlDbType.Jsonb, details);
        command.Parameters.AddWithValue("correlation_id", requestContext.CorrelationId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToWireValue(AuditActorKind kind) => kind switch
    {
        AuditActorKind.Member => "member",
        AuditActorKind.System => "system",
        AuditActorKind.Anonymous => "anonymous",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
