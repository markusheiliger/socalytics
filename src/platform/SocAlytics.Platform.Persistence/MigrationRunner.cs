using System.Data;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

internal sealed class MigrationRunner(BootstrapConnectionSource bootstrap, MigrationCatalog catalog) : IMigrationRunner
{
    // Serializes concurrent startups so only one instance applies migrations at a time.
    private const long AdvisoryLockKey = 0x536F63416C79_01;

    private const string EnsureHistorySql = """
        CREATE SCHEMA IF NOT EXISTS socalytics_migrations;
        CREATE TABLE IF NOT EXISTS socalytics_migrations.history (
            module text NOT NULL,
            sequence integer NOT NULL,
            script_identity text NOT NULL,
            checksum text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT history_pkey PRIMARY KEY (module, script_identity),
            CONSTRAINT history_module_sequence_key UNIQUE (module, sequence)
        );
        """;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var lockConnection = await bootstrap.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(lockConnection, "SELECT pg_advisory_lock(@key)", cancellationToken, ("key", AdvisoryLockKey)).ConfigureAwait(false);
            await ExecuteAsync(lockConnection, ModuleRoleBootstrap.BuildSql(), cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(lockConnection, EnsureHistorySql, cancellationToken).ConfigureAwait(false);

            await VerifyChecksumsAsync(lockConnection, cancellationToken).ConfigureAwait(false);

            // DbUp is synchronous; a cancelled token takes effect between scripts.
            await Task.Run(() => Apply(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MigrationException(MigrationFailureKind.Infrastructure, Describe("Migration infrastructure failed", exception));
        }
    }

    private async Task VerifyChecksumsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var expected = catalog.Migrations.ToDictionary(m => (m.Module.Key, m.Identity));

        await using var command = new NpgsqlCommand(
            "SELECT module, sequence, script_identity, checksum FROM socalytics_migrations.history", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var module = reader.GetString(0);
            var identity = reader.GetString(2);

            if (expected.TryGetValue((module, identity), out var descriptor)
                && (descriptor.Checksum != reader.GetString(3) || descriptor.Sequence != reader.GetInt32(1)))
            {
                throw new MigrationException(
                    MigrationFailureKind.ChecksumConflict,
                    $"Applied migration '{identity}' of module '{module}' no longer matches its registered script. Applied migrations are immutable; add a new migration instead.",
                    module,
                    identity);
            }
        }
    }

    private void Apply(CancellationToken cancellationToken)
    {
        var scripts = catalog.Migrations
            .Select(m => new SqlScript(ScriptName.Format(m), ModuleRoleBootstrap.WrapMigration(m.Module, m.Script)))
            .ToArray();

        var engine = DeployChanges.To
            .PostgresqlDatabase(new RunnerConnectionManager(bootstrap))
            .WithScripts(new FixedScriptProvider(scripts))
            .WithTransactionPerScript()
            .JournalTo(new HistoryJournal(bootstrap, catalog))
            .LogTo(new SilentLog())
            .Build();

        if (!engine.IsUpgradeRequired())
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = engine.PerformUpgrade();

        if (!result.Successful)
        {
            var parsed = result.ErrorScript is null ? null : ScriptName.Parse(result.ErrorScript.Name);
            throw new MigrationException(
                MigrationFailureKind.ScriptFailed,
                parsed is null
                    ? Describe("Migration failed", result.Error)
                    : Describe($"Migration '{parsed.Value.Identity}' of module '{parsed.Value.Module}' failed and was rolled back", result.Error),
                parsed?.Module,
                parsed?.Identity);
        }
    }

    // Only the PostgreSQL SQLSTATE is surfaced; server messages can echo SQL fragments or data.
    private static string Describe(string prefix, Exception? exception) =>
        exception is PostgresException { SqlState: { } state } ? $"{prefix} (SQLSTATE {state})." : $"{prefix}.";

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static class ScriptName
    {
        public static string Format(MigrationDescriptor m) => $"{m.Module.Order:D2}/{m.Module.Key}/{m.Sequence:D6}/{m.Identity}";

        public static (string Module, int Sequence, string Identity)? Parse(string name)
        {
            var parts = name.Split('/', 4);
            return parts.Length == 4 && int.TryParse(parts[2], out var sequence) ? (parts[1], sequence, parts[3]) : null;
        }
    }

    private sealed class FixedScriptProvider(IReadOnlyList<SqlScript> scripts) : IScriptProvider
    {
        public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager) => scripts;
    }

    private sealed class RunnerConnectionManager(BootstrapConnectionSource bootstrap)
        : DatabaseConnectionManager(_ => bootstrap.CreateConnection())
    {
        // Migration scripts run as one command so PostgreSQL parses them, not DbUp's splitter.
        public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents) => [scriptContents];
    }

    private sealed class SilentLog : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }

        public void LogDebug(string format, params object[] args) { }

        public void LogInformation(string format, params object[] args) { }

        public void LogWarning(string format, params object[] args) { }

        public void LogError(string format, params object[] args) { }

        public void LogError(Exception ex, string format, params object[] args) { }
    }

    private sealed class HistoryJournal(BootstrapConnectionSource bootstrap, MigrationCatalog catalog) : IJournal
    {
        public string[] GetExecutedScripts()
        {
            using var connection = bootstrap.CreateConnection();
            connection.Open();
            using var command = new NpgsqlCommand(
                "SELECT module, sequence, script_identity FROM socalytics_migrations.history", connection);
            using var reader = command.ExecuteReader();

            var names = new List<string>();
            while (reader.Read())
            {
                var module = reader.GetString(0);
                var applied = catalog.Migrations.FirstOrDefault(m => m.Module.Key == module && m.Identity == reader.GetString(2));
                if (applied is not null)
                {
                    names.Add(ScriptName.Format(applied));
                }
            }

            return [.. names];
        }

        public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
        {
            var parsed = ScriptName.Parse(script.Name)
                ?? throw new InvalidOperationException("Unexpected migration script name.");
            var descriptor = catalog.Migrations.Single(m => ScriptName.Format(m) == script.Name);

            // The factory command enlists in the script's transaction, so the record commits atomically with it.
            using var command = dbCommandFactory();
            command.CommandText =
                "INSERT INTO socalytics_migrations.history (module, sequence, script_identity, checksum) VALUES (@module, @sequence, @identity, @checksum)";
            AddParameter(command, "module", parsed.Module);
            AddParameter(command, "sequence", parsed.Sequence);
            AddParameter(command, "identity", parsed.Identity);
            AddParameter(command, "checksum", descriptor.Checksum);
            command.ExecuteNonQuery();
        }

        public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
        {
            // The runner creates the history table before DbUp starts.
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
