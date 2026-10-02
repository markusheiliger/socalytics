using System.Data;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>Applies registered module migrations. Resolved by the host; owns the privileged connection use.</summary>
public interface IMigrationOrchestrator
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Migration failure carrying only the module, identity, and a sanitized reason.</summary>
public sealed class MigrationException(string message) : Exception(message);

internal sealed class MigrationOrchestrator(BootstrapConnectionSource bootstrap, MigrationCatalogProvider provider)
    : IMigrationOrchestrator
{
    public Task MigrateAsync(CancellationToken cancellationToken = default) =>
        MigrationRunner.RunAsync(bootstrap.ConnectionString, provider.Catalog, cancellationToken);
}

internal static class MigrationRunner
{
    private const string HistoryTable = "socalytics_migrations.history";

    public static async Task RunAsync(string connectionString, MigrationCatalog catalog, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Serialises concurrent starters for the lifetime of this session.
            await ExecuteAsync(connection, "SELECT pg_advisory_lock(hashtext('socalytics_migrations'))", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, EnsureHistorySql, cancellationToken).ConfigureAwait(false);

            var applied = await ReadHistoryAsync(connection, cancellationToken).ConfigureAwait(false);

            // Full preflight: no pending script may run while any applied script conflicts.
            foreach (var migration in catalog.Migrations)
            {
                if (applied.TryGetValue((migration.Module.Schema, migration.Identity), out var checksum)
                    && !string.Equals(checksum, migration.Checksum, StringComparison.Ordinal))
                {
                    throw new MigrationException(
                        $"Migration '{migration.Identity}' of module '{migration.Module}' conflicts with its applied checksum.");
                }
            }

            var pending = catalog.Migrations
                .Where(m => !applied.ContainsKey((m.Module.Schema, m.Identity)))
                .ToList();
            if (pending.Count == 0)
            {
                return;
            }

            var journal = new HistoryJournal(pending);
            var scripts = pending.Select(m => new SqlScript(journal.NameOf(m), m.Script)).ToList();
            var result = await Task.Run(
                () => DeployChanges.To
                    .PostgresqlDatabase(connectionString)
                    .WithScripts(scripts)
                    .WithTransactionPerScript()
                    .JournalTo(journal)
                    .LogToNowhere()
                    .Build()
                    .PerformUpgrade(),
                cancellationToken).ConfigureAwait(false);

            if (!result.Successful)
            {
                var failed = result.ErrorScript is null ? null : journal.Find(result.ErrorScript.Name);
                var reason = result.Error is PostgresException pg ? $" (SQLSTATE {pg.SqlState})" : string.Empty;
                throw new MigrationException(failed is null
                    ? $"Migration execution failed{reason}."
                    : $"Migration '{failed.Identity}' of module '{failed.Module}' failed and was rolled back{reason}.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The original message may embed connection details, so only the SQLSTATE survives.
            var state = exception is PostgresException pg ? $" (SQLSTATE {pg.SqlState})" : string.Empty;
            throw new MigrationException($"Migration orchestration failed{state}.");
        }
    }

    private const string EnsureHistorySql = """
        CREATE SCHEMA IF NOT EXISTS socalytics_migrations;
        CREATE TABLE IF NOT EXISTS socalytics_migrations.history (
            module text NOT NULL,
            sequence integer NOT NULL,
            identity text NOT NULL,
            checksum text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (module, identity),
            UNIQUE (module, sequence)
        );
        """;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<(string Module, string Identity), string>> ReadHistoryAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<(string, string), string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT module, identity, checksum FROM {HistoryTable}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return rows;
    }

    /// <summary>Writes history rows inside the script's own transaction.</summary>
    private sealed class HistoryJournal : IJournal
    {
        private readonly Dictionary<string, MigrationDescriptor> byName;

        public HistoryJournal(IEnumerable<MigrationDescriptor> pending) =>
            byName = pending.ToDictionary(NameOf, StringComparer.Ordinal);

        public string NameOf(MigrationDescriptor migration) =>
            $"{migration.Module.Order:D2}_{migration.Module.Schema}/{migration.Sequence:D10}_{migration.Identity}";

        public MigrationDescriptor? Find(string name) => byName.GetValueOrDefault(name);

        public string[] GetExecutedScripts() => [];

        public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
        {
        }

        public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
        {
            var migration = byName[script.Name];
            using var command = dbCommandFactory();
            command.CommandText =
                $"INSERT INTO {HistoryTable} (module, sequence, identity, checksum) VALUES (@module, @sequence, @identity, @checksum)";
            AddParameter(command, "module", migration.Module.Schema);
            AddParameter(command, "sequence", migration.Sequence);
            AddParameter(command, "identity", migration.Identity);
            AddParameter(command, "checksum", migration.Checksum);
            command.ExecuteNonQuery();
        }

        private static void AddParameter(IDbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
