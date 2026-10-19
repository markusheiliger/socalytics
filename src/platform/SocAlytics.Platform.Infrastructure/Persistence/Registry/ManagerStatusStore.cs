using Dapper;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Infrastructure.Persistence.Registry;

internal sealed class ManagerStatusStore(IDbSession session) : IManagerStatusStore
{
    private const string Columns =
        "registration_id AS RegistrationId, operating_state AS OperatingState, paused_until AS PausedUntil, " +
        "runtime_unavailable_reason AS RuntimeUnavailableReason, active_analysts AS ActiveAnalysts, " +
        "max_analysts AS MaxAnalysts, manager_version AS ManagerVersion, received_at AS ReceivedAt, " +
        "interval_seconds AS IntervalSeconds";

    public async Task UpsertAsync(
        Guid registrationId, ManagerStatusReport report, DateTimeOffset receivedAt, int intervalSeconds,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.analyst_manager_status (registration_id, operating_state, paused_until, " +
            "runtime_unavailable_reason, active_analysts, max_analysts, manager_version, received_at, interval_seconds) " +
            "VALUES (@registrationId, @state, @pausedUntil, @reason, @active, @max, @version, @receivedAt, @intervalSeconds) " +
            "ON CONFLICT (registration_id) DO UPDATE SET operating_state = EXCLUDED.operating_state, " +
            "paused_until = EXCLUDED.paused_until, runtime_unavailable_reason = EXCLUDED.runtime_unavailable_reason, " +
            "active_analysts = EXCLUDED.active_analysts, max_analysts = EXCLUDED.max_analysts, " +
            "manager_version = EXCLUDED.manager_version, received_at = EXCLUDED.received_at, " +
            "interval_seconds = EXCLUDED.interval_seconds",
            new
            {
                registrationId,
                state = report.OperatingState.ToApiName(),
                pausedUntil = report.PausedUntil,
                reason = report.RuntimeUnavailableReason,
                active = report.ActiveAnalysts,
                max = report.MaxAnalysts,
                version = report.ManagerVersion,
                receivedAt,
                intervalSeconds,
            },
            session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<StoredManagerStatus?> FindAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.analyst_manager_status WHERE registration_id = @registrationId",
            new { registrationId }, session.Transaction, cancellationToken: cancellationToken));
        return row?.ToStored();
    }

    public async Task<string?> FindVersionAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT manager_version FROM socalytics.analyst_manager_status WHERE registration_id = @registrationId",
            new { registrationId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, StoredManagerStatus>> FindManyAsync(
        IReadOnlyCollection<Guid> registrationIds, CancellationToken cancellationToken)
    {
        if (registrationIds.Count == 0)
        {
            return new Dictionary<Guid, StoredManagerStatus>();
        }

        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<StatusRow>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.analyst_manager_status WHERE registration_id = ANY(@ids)",
            new { ids = registrationIds.ToArray() }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToStored()).ToDictionary(s => s.RegistrationId);
    }

    public async Task<int> DeleteAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.analyst_manager_status WHERE registration_id = @registrationId",
            new { registrationId }, session.Transaction, cancellationToken: cancellationToken));
    }

    private sealed record StatusRow(
        Guid RegistrationId, string OperatingState, DateTime? PausedUntil, string? RuntimeUnavailableReason,
        int ActiveAnalysts, int MaxAnalysts, string ManagerVersion, DateTime ReceivedAt, int IntervalSeconds)
    {
        public StoredManagerStatus ToStored()
        {
            ManagerOperatingStateExtensions.TryParse(OperatingState, out var state);
            DateTimeOffset? paused = PausedUntil is { } p ? new DateTimeOffset(DateTime.SpecifyKind(p, DateTimeKind.Utc)) : null;
            return new StoredManagerStatus(
                RegistrationId,
                ManagerStatusReport.Restore(state, paused, RuntimeUnavailableReason, ActiveAnalysts, MaxAnalysts, ManagerVersion),
                new DateTimeOffset(DateTime.SpecifyKind(ReceivedAt, DateTimeKind.Utc)),
                IntervalSeconds);
        }
    }
}
