using System.Data;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>Applies pending module migrations in deterministic order, recording each in the shared history.</summary>
public interface IMigrationRunner
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
}

internal sealed class MigrationRunner(BootstrapConnectionSource bootstrap, MigrationCatalog catalog, string bootstrapConnectionString)
    : IMigrationRunner
{
    internal const string HistorySchema = "socalytics_migrations";

    // Arbitrary constant key serialising concurrent migrators on one database.
    private const long AdvisoryLockKey = 0x534F43_4D4947;

    private const string EnsureHistorySql = $"""
        CREATE SCHEMA IF NOT EXISTS {HistorySchema};
        CREATE TABLE IF NOT EXISTS {HistorySchema}.history (
            module_key text NOT NULL,
            sequence integer NOT NULL,
            script_name text NOT NULL,
            checksum char(64) NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT history_module_script_key UNIQUE (module_key, script_name),
            CONSTRAINT history_module_sequence_key UNIQUE (module_key, sequence)
        );
        """;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var lockConnection = await bootstrap.OpenAsync(cancellationToken);
            await ExecuteAsync(lockConnection, "SELECT pg_advisory_lock(@k)", cancellationToken, ("k", AdvisoryLockKey));
            try
            {
                await ExecuteAsync(lockConnection, EnsureHistorySql, cancellationToken);
                await ExecuteAsync(lockConnection, RoleBootstrap.Sql, cancellationToken);
                var pending = await PreflightAsync(lockConnection, cancellationToken);
                if (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Run(() => Apply(pending), CancellationToken.None);
                }
            }
            finally
            {
                await ExecuteAsync(lockConnection, "SELECT pg_advisory_unlock(@k)", CancellationToken.None, ("k", AdvisoryLockKey));
            }
        }
        catch (MigrationFailedException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MigrationFailedException("Database migration failed because the database could not be reached or queried.");
        }
    }

    // Validates every applied script against the embedded checksums before any new work runs.
    private async Task<List<MigrationDescriptor>> PreflightAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var applied = new Dictionary<(string, string), string>();
        await using (var command = new NpgsqlCommand(
            $"SELECT module_key, script_name, checksum FROM {HistorySchema}.history", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                applied[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2).Trim();
            }
        }

        var pending = new List<MigrationDescriptor>();
        foreach (var migration in catalog.Migrations)
        {
            if (!applied.TryGetValue((migration.Module.Name, migration.ScriptName), out var checksum))
            {
                pending.Add(migration);
            }
            else if (!string.Equals(checksum, migration.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationChecksumConflictException(migration.Module.Name, migration.ScriptName);
            }
        }

        return pending;
    }

    private void Apply(List<MigrationDescriptor> pending)
    {
        var byName = pending.ToDictionary(m => Key(pending, m));
        var engine = DeployChanges.To
            .PostgresqlDatabase(bootstrapConnectionString)
            .WithScripts(new PendingScriptProvider(pending))
            .JournalTo(new HistoryJournal(byName))
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .LogToNowhere()
            .Build();

        var result = engine.PerformUpgrade();
        if (!result.Successful)
        {
            var failed = result.ErrorScript?.Name is { } name && byName.TryGetValue(name, out var d) ? d : null;
            throw new MigrationFailedException(
                failed is null
                    ? "Database migration failed."
                    : $"Migration '{failed.ScriptName}' of module '{failed.Module}' failed and was rolled back.")
            {
                Module = failed?.Module.Name,
                ScriptName = failed?.ScriptName,
            };
        }
    }

    // DbUp sorts scripts by name, so a zero-padded position preserves the catalog order.
    private static string Key(List<MigrationDescriptor> pending, MigrationDescriptor m) =>
        $"{pending.IndexOf(m):D6}:{m.Module.Name}/{m.ScriptName}";

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class PendingScriptProvider(List<MigrationDescriptor> pending) : IScriptProvider
    {
        public IEnumerable<SqlScript> GetScripts(DbUp.Engine.Transactions.IConnectionManager connectionManager) =>
            pending.Select(m => new SqlScript(Key(pending, m), System.Text.Encoding.UTF8.GetString(m.Content.Span)));
    }

    // Only pending scripts are supplied, so the journal reports nothing as executed and records each
    // success through the command factory of the script's own transaction.
    private sealed class HistoryJournal(Dictionary<string, MigrationDescriptor> byName) : IJournal
    {
        public string[] GetExecutedScripts() => [];

        public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
        {
        }

        public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
        {
            var m = byName[script.Name];
            // A script may leave SET LOCAL ROLE active; history is written with the migrator's own role.
            using var command = dbCommandFactory();
            command.CommandText =
                $"RESET ROLE; INSERT INTO {HistorySchema}.history (module_key, sequence, script_name, checksum) VALUES (@m, @s, @n, @c)";
            AddParameter(command, "m", m.Module.Name);
            AddParameter(command, "s", m.Sequence);
            AddParameter(command, "n", m.ScriptName);
            AddParameter(command, "c", m.Checksum);
            command.ExecuteNonQuery();
        }

        private static void AddParameter(IDbCommand command, string name, object value)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }
    }
}
