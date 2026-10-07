using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;

namespace SocAlytics.Platform.Infrastructure.Persistence.Readiness;

internal sealed partial class DatabaseReadinessHealthCheck(
    PlatformDataSource dataSource,
    UnknownAppliedMigrationsReporter reporter,
    ILogger<DatabaseReadinessHealthCheck> logger) : IHealthCheck
{
    private const string UndefinedTable = "42P01";
    private const string InsufficientPrivilege = "42501";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!dataSource.TryGetDataSource(out var source))
        {
            return HealthCheckResult.Unhealthy("configuration");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        try
        {
            await using var connection = await source.OpenConnectionAsync(budget.Token);
            var history = await MigrationHistoryStore.ReadEntriesAsync(connection, null, budget.Token);
            var state = MigrationStateEvaluator.Evaluate(MigrationCatalog.Platform, history);

            switch (state.Kind)
            {
                case MigrationStateKind.Current:
                    if (reporter.ShouldReport(state.UnknownAppliedIdentities))
                    {
                        LogUnknownAppliedMigrations(logger, string.Join(", ", state.UnknownAppliedIdentities));
                    }

                    return HealthCheckResult.Healthy();
                case MigrationStateKind.Pending:
                    return HealthCheckResult.Unhealthy("migration-state-not-current");
                default:
                    return HealthCheckResult.Unhealthy($"migration-state-conflict {state.Identity}");
            }
        }
        catch (PostgresException ex) when (ex.SqlState == UndefinedTable)
        {
            return HealthCheckResult.Unhealthy($"migration-state-not-current ({ex.SqlState})");
        }
        catch (PostgresException ex) when (ex.SqlState == InsufficientPrivilege)
        {
            return HealthCheckResult.Unhealthy($"database-access-denied ({ex.SqlState})");
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("database-unavailable");
        }
    }

    [LoggerMessage(EventId = 2000, EventName = "unknown-applied-migrations", Level = LogLevel.Warning,
        Message = "History contains migrations unknown to this host: {Identities}")]
    private static partial void LogUnknownAppliedMigrations(ILogger logger, string identities);
}
