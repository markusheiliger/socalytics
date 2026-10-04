using System.Data;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Transactions;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Applies the registered migrations with DbUp: full checksum preflight, deterministic module and
/// sequence order, one transaction per script that also records the history row.
/// </summary>
public sealed class MigrationOrchestrator
{
    private readonly MigrationCatalog _catalog;
    private readonly BootstrapConnectionSource _bootstrap;

    internal MigrationOrchestrator(MigrationCatalog catalog, BootstrapConnectionSource bootstrap)
    {
        _catalog = catalog;
        _bootstrap = bootstrap;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        List<AppliedMigration> applied;
        try
        {
            await EnsureRolesAsync(cancellationToken);
            await EnsureHistoryAsync(cancellationToken);
            applied = await ReadHistoryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.IO.IOException)
        {
            throw new MigrationFailedException($"Migration history could not be prepared{SqlStateSuffix(ex)}.");
        }

        Preflight(applied);

        var appliedNames = applied.Select(a => Name(a.Module, a.Identity)).ToHashSet(StringComparer.Ordinal);
        var pending = _catalog.Migrations.Where(m => !appliedNames.Contains(Name(m.Module.Key, m.Identity))).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(_bootstrap.ConnectionString)
            .WithScripts(new CatalogScriptProvider(pending))
            .WithTransactionPerScript()
            .JournalTo(new NoopJournal())
            .LogToNowhere()
            .Build();

        var result = await Task.Run(upgrader.PerformUpgrade, cancellationToken);
        if (!result.Successful)
        {
            var failed = result.ErrorScript?.Name;
            var target = failed is null ? "a migration" : $"migration '{failed}'";
            throw new MigrationFailedException($"Applying {target} failed{SqlStateSuffix(result.Error)}.");
        }
    }

    private void Preflight(List<AppliedMigration> applied)
    {
        var byName = _catalog.Migrations.ToDictionary(m => Name(m.Module.Key, m.Identity), StringComparer.Ordinal);
        foreach (var entry in applied)
        {
            if (byName.TryGetValue(Name(entry.Module, entry.Identity), out var registered)
                && (!string.Equals(registered.Checksum, entry.Checksum, StringComparison.Ordinal)
                    || registered.Sequence != entry.Sequence))
            {
                throw new MigrationFailedException(
                    $"Checksum conflict: applied migration '{entry.Module}/{entry.Identity}' no longer matches its registered script.");
            }
        }
    }

    private async Task EnsureRolesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _bootstrap.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RoleBootstrapSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Local/test role bootstrap: role names derive only from the fixed module keys. Owners may create their
    // schema; runtime roles get DML only on objects their owner creates and no access to peer schemas.
    private static readonly string RoleBootstrapSql = BuildRoleBootstrapSql();

    private static string BuildRoleBootstrapSql()
    {
        var sql = new System.Text.StringBuilder("SELECT pg_advisory_xact_lock(hashtext('socalytics_role_bootstrap'));\n");
        foreach (var module in PersistenceModuleKey.All)
        {
            foreach (var role in new[] { module.OwnerRole, module.RuntimeRole })
            {
                sql.Append($"DO $$ BEGIN CREATE ROLE \"{role}\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE; EXCEPTION WHEN duplicate_object THEN NULL; END $$;\n");
            }

            sql.Append($"DO $$ BEGIN EXECUTE format('GRANT CREATE ON DATABASE %I TO %I', current_database(), '{module.OwnerRole}'); END $$;\n");
            sql.Append($"ALTER DEFAULT PRIVILEGES FOR ROLE \"{module.OwnerRole}\" GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO \"{module.RuntimeRole}\";\n");
            sql.Append($"ALTER DEFAULT PRIVILEGES FOR ROLE \"{module.OwnerRole}\" GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO \"{module.RuntimeRole}\";\n");
        }

        return sql.ToString();
    }

    private async Task EnsureHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _bootstrap.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA IF NOT EXISTS socalytics_migrations;
            CREATE TABLE IF NOT EXISTS socalytics_migrations.history (
                module text NOT NULL,
                sequence integer NOT NULL,
                identity text NOT NULL,
                checksum text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT history_pkey PRIMARY KEY (module, identity),
                CONSTRAINT history_module_sequence_key UNIQUE (module, sequence)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<AppliedMigration>> ReadHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _bootstrap.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT module, sequence, identity, checksum FROM socalytics_migrations.history";
        var rows = new List<AppliedMigration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AppliedMigration(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    private static string Name(string module, string identity) => $"{module}/{identity}";

    private static string SqlStateSuffix(Exception? ex) =>
        ex is PostgresException { SqlState: { } state } ? $" (SQLSTATE {state})" : string.Empty;

    private sealed record AppliedMigration(string Module, int Sequence, string Identity, string Checksum);

    private sealed class CatalogScriptProvider(IReadOnlyList<MigrationDescriptor> pending) : IScriptProvider
    {
        public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager) =>
            pending.Select(m => new SqlScript(Name(m.Module.Key, m.Identity), WithHistoryInsert(m)));
    }

    // The history row is part of the script text so DbUp's per-script transaction commits both together.
    private sealed class NoopJournal : IJournal
    {
        public string[] GetExecutedScripts() => [];

        public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
        {
        }

        public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
        {
        }
    }

    private static string WithHistoryInsert(MigrationDescriptor m) =>
        $"SET LOCAL ROLE \"{m.Module.OwnerRole}\";\n"
        + m.Script + "\n;\n"
        + "RESET ROLE;\n"
        + "INSERT INTO socalytics_migrations.history (module, sequence, identity, checksum) VALUES ("
        + $"{Literal(m.Module.Key)}, {m.Sequence}, {Literal(m.Identity)}, {Literal(m.Checksum)});\n";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
