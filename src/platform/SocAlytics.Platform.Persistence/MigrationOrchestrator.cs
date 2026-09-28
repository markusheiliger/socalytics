using System.Data;
using System.Text;
using Dapper;
using DbUp;
using DbUp.Engine;
using SocAlytics.Platform.Persistence.Connections;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Applies module-owned scripts in catalog order with a checksum-aware shared history.
/// Only startup orchestration should resolve this service.
/// </summary>
public sealed class MigrationOrchestrator(
    PersistenceOptions options,
    IEnumerable<IModuleMigrationContributor> contributors)
{
    public void Run()
    {
        var migrations = MigrationCatalog.Create(contributors).Migrations;

        try
        {
            using var connection = new NpgsqlBootstrapConnectionFactory(options).CreateConnection();
            connection.Open();
            connection.Execute("""
                CREATE SCHEMA IF NOT EXISTS socalytics_migrations;
                CREATE TABLE IF NOT EXISTS socalytics_migrations.history (
                    module_key text NOT NULL,
                    sequence integer NOT NULL,
                    script_identity text NOT NULL,
                    checksum text NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (module_key, script_identity),
                    UNIQUE (module_key, sequence)
                );
                """);

            var history = connection.Query<HistoryEntry>(
                "SELECT module_key AS ModuleKey, sequence AS Sequence, script_identity AS ScriptIdentity, checksum AS Checksum FROM socalytics_migrations.history")
                .ToDictionary(entry => (entry.ModuleKey, entry.ScriptIdentity));

            // Validate every applied script before allowing any new script to run.
            foreach (var migration in migrations)
            {
                if (history.TryGetValue((migration.ModuleKey.ToString(), migration.ScriptIdentity), out var applied) &&
                    (applied.Sequence != migration.Sequence || applied.Checksum != migration.Checksum))
                {
                    throw new MigrationConflictException(migration.ModuleKey, migration.ScriptIdentity);
                }
            }

            var pending = migrations
                .Where(migration => !history.ContainsKey((migration.ModuleKey.ToString(), migration.ScriptIdentity)))
                .ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            var scripts = pending.Select((migration, index) =>
                new SqlScript(index.ToString("D8"), new UTF8Encoding(false, true).GetString(migration.Content))).ToArray();
            var journal = new HistoryJournal(pending);
            var result = DeployChanges.To
                .PostgresqlDatabase(options.BootstrapConnectionString)
                .WithScripts(scripts)
                .WithScriptSorter(items => items)
                .JournalTo(journal)
                .WithTransactionPerScript()
                .LogToNowhere()
                .Build()
                .PerformUpgrade();

            if (!result.Successful)
            {
                var failed = journal.NextMigration;
                throw new MigrationFailedException(
                    failed is null
                        ? "Database migration failed."
                        : $"Database migration failed for module '{failed.ModuleKey}', script '{failed.ScriptIdentity}'.");
            }
        }
        catch (MigrationConflictException)
        {
            throw;
        }
        catch (MigrationFailedException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MigrationFailedException("Database migration failed.");
        }
    }

    private sealed class HistoryEntry
    {
        public string ModuleKey { get; set; } = string.Empty;
        public int Sequence { get; set; }
        public string ScriptIdentity { get; set; } = string.Empty;
        public string Checksum { get; set; } = string.Empty;
    }

    private sealed class HistoryJournal(MigrationDescriptor[] migrations) : IJournal
    {
        private int completed;

        public MigrationDescriptor? NextMigration => completed < migrations.Length ? migrations[completed] : null;

        public string[] GetExecutedScripts() => [];

        public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory) { }

        public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
        {
            var migration = migrations[int.Parse(script.Name, System.Globalization.CultureInfo.InvariantCulture)];
            using var command = dbCommandFactory();
            command.CommandText = """
                INSERT INTO socalytics_migrations.history (module_key, sequence, script_identity, checksum)
                VALUES (@module_key, @sequence, @script_identity, @checksum)
                """;
            AddParameter(command, "module_key", migration.ModuleKey.ToString());
            AddParameter(command, "sequence", migration.Sequence);
            AddParameter(command, "script_identity", migration.ScriptIdentity);
            AddParameter(command, "checksum", migration.Checksum);
            command.ExecuteNonQuery();
            completed++;
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

public sealed class MigrationConflictException(ModuleKey moduleKey, string scriptIdentity)
    : InvalidOperationException($"Migration checksum or sequence conflict for module '{moduleKey}', script '{scriptIdentity}'.");

internal sealed class MigrationFailedException(string message) : InvalidOperationException(message);
