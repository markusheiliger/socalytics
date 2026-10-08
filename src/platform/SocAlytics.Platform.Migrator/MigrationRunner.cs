using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Logging;
using Npgsql;
using SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;

namespace SocAlytics.Platform.Migrator;

internal sealed class MigrationRunner(
    ILogger logger,
    MigratorOptions options,
    MigrationCatalog catalog,
    string connectionString)
{
    private const string AuthenticationFailure = "28P01";

    public async Task<MigratorExitCode> RunAsync(CancellationToken cancellationToken)
    {
        var applicationName = $"socalytics-migrator-{Guid.NewGuid():N}";
        NpgsqlConnection? lockConnection = null;
        try
        {
            MigratorLog.RunStarted(logger, catalog.Scripts.Count);

            var sessionConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Options = "-c role=socalytics_migrator",
            }.ConnectionString;

            lockConnection = await ConnectAsync(sessionConnectionString, cancellationToken);
            if (lockConnection is null)
            {
                return Fail("database-unavailable", MigratorExitCode.DatabaseUnavailable);
            }

            await using var held = await MigrationLock.AcquireAsync(
                lockConnection,
                options.LockWaitTimeout,
                () => MigratorLog.WaitingForLock(logger),
                cancellationToken);
            if (!held.Acquired)
            {
                return Fail("lock-timeout", MigratorExitCode.LockTimeout);
            }

            MigratorLog.LockAcquired(logger);

            var history = await MigrationHistoryStore.ReadEntriesAsync(lockConnection, null, cancellationToken);
            var state = MigrationStateEvaluator.Evaluate(catalog, history);
            if (state.UnknownAppliedIdentities.Count > 0)
            {
                MigratorLog.UnknownApplied(logger, string.Join(", ", state.UnknownAppliedIdentities));
            }

            if (state.Kind == MigrationStateKind.ChecksumMismatch)
            {
                return Fail("checksum-mismatch", MigratorExitCode.ChecksumMismatch, state.Identity);
            }

            if (state.Kind == MigrationStateKind.SequenceConflict)
            {
                return Fail("sequence-conflict", MigratorExitCode.SequenceConflict, state.Identity);
            }

            var applied = 0;
            if (state.PendingScripts.Count > 0)
            {
                var dbUpConnectionString = new NpgsqlConnectionStringBuilder(sessionConnectionString)
                {
                    ApplicationName = applicationName,
                }.ConnectionString;

                var upgrader = DeployChanges.To
                    .PostgresqlDatabase(dbUpConnectionString)
                    .WithScripts(new PendingMigrationScriptProvider(state.PendingScripts))
                    .JournalTo((connectionManager, log) => new SocAlyticsHistoryJournal(connectionManager, log))
                    .WithTransactionPerScript()
                    .WithVariablesDisabled()
                    .WithExecutionTimeout(options.ScriptTimeout)
                    .LogToNowhere()
                    .Build();

                upgrader.ScriptExecuted += (_, args) =>
                {
                    applied++;
                    if (args is ScriptExecutedEventArgs executed)
                    {
                        MigratorLog.Applied(logger, executed.Script.Name);
                    }
                };

                foreach (var script in state.PendingScripts)
                {
                    MigratorLog.Applying(logger, script.Identity);
                }

                var result = await RunUpgradeAsync(upgrader, lockConnection, applicationName, cancellationToken);
                if (!result.Successful)
                {
                    var sqlState = (result.Error as PostgresException)?.SqlState
                        ?? (result.Error?.InnerException as PostgresException)?.SqlState;
                    return Fail(
                        "migration-failed",
                        MigratorExitCode.MigrationFailed,
                        result.ErrorScript?.Name,
                        sqlState,
                        result.Error?.GetType().Name);
                }
            }

            MigratorLog.RunFinished(logger, applied);
            return MigratorExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            return Fail("cancelled", MigratorExitCode.Cancelled);
        }
        catch (NpgsqlException ex)
        {
            return Fail(
                "database-unavailable",
                MigratorExitCode.DatabaseUnavailable,
                sqlState: (ex as PostgresException)?.SqlState,
                exceptionType: ex.GetType().Name);
        }
        finally
        {
            if (lockConnection is not null)
            {
                await lockConnection.DisposeAsync();
            }
        }
    }

    private static async Task<DatabaseUpgradeResult> RunUpgradeAsync(
        UpgradeEngine upgrader,
        NpgsqlConnection lockConnection,
        string applicationName,
        CancellationToken cancellationToken)
    {
        var upgrade = Task.Run(upgrader.PerformUpgrade, CancellationToken.None);
        try
        {
            return await upgrade.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await using var command = new NpgsqlCommand(MigrationHistorySql.CancelBackendsByApplicationName, lockConnection);
                command.Parameters.AddWithValue("ApplicationName", applicationName);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (NpgsqlException)
            {
                // Closing the DbUp connections rolls the in-flight script back anyway.
            }

            // Wait for the rollback to finish before the lock is released.
            await upgrade.ContinueWith(_ => { }, TaskScheduler.Default);
            throw;
        }
    }

    private async Task<NpgsqlConnection?> ConnectAsync(string sessionConnectionString, CancellationToken cancellationToken)
    {
        var start = TimeProvider.System.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            var connection = new NpgsqlConnection(sessionConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or IOException)
            {
                await connection.DisposeAsync();
                if (ex is PostgresException { SqlState: AuthenticationFailure })
                {
                    return null;
                }

                var remaining = options.ConnectTimeout - TimeProvider.System.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero)
                {
                    return null;
                }

                await Task.Delay(remaining < delay ? remaining : delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
            }
        }
    }

    private MigratorExitCode Fail(
        string category,
        MigratorExitCode code,
        string? identity = null,
        string? sqlState = null,
        string? exceptionType = null)
    {
        MigratorLog.Failed(logger, category, identity, sqlState, exceptionType);

        return code;
    }
}
