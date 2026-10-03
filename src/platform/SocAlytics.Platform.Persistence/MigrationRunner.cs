using DbUp;
using DbUp.Engine;
using DbUp.Engine.Transactions;
using DbUp.Engine.Output;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>Applies registered migrations after a full checksum preflight, one transaction per script.</summary>
internal sealed class MigrationRunner(PersistenceDataSource source, MigrationCatalog catalog)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AppliedMigration> applied;
        await using (var connection = await source.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext('socalytics_migrations'))", connection);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await MigrationJournal.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
                applied = await MigrationJournal.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
                Preflight(applied);
                Apply(applied);
            }
            finally
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('socalytics_migrations'))", connection);
                await unlock.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void Preflight(IReadOnlyList<AppliedMigration> applied)
    {
        var registered = catalog.Migrations.ToDictionary(MigrationJournal.ScriptKey);
        foreach (var row in applied.OrderBy(a => a.Module, StringComparer.Ordinal).ThenBy(a => a.Sequence))
        {
            if (registered.TryGetValue(row.Module + "/" + row.ScriptName, out var migration)
                && !string.Equals(migration.Checksum, row.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationException(
                    $"Migration '{row.ScriptName}' of module '{row.Module}' was changed after it was applied.");
            }
        }
    }

    private void Apply(IReadOnlyList<AppliedMigration> applied)
    {
        var executed = applied.Select(a => a.Module + "/" + a.ScriptName).ToArray();
        var byName = catalog.Migrations.ToDictionary(MigrationJournal.ScriptKey);
        var scripts = catalog.Migrations
            .Select((m, index) => new SqlScript(
                MigrationJournal.ScriptKey(m), m.Sql, new SqlScriptOptions { RunGroupOrder = index }))
            .ToList();

        var engine = DeployChanges.To
            .PostgresqlDatabase(source.ConnectionString)
            .WithScripts(new FixedScriptProvider(scripts))
            .WithTransactionPerScript()
            .JournalTo(new MigrationJournal(byName, executed))
            .LogTo(new NullUpgradeLog())
            .Build();

        var result = engine.PerformUpgrade();
        if (!result.Successful)
        {
            var name = result.ErrorScript?.Name;
            throw new MigrationException(name is null
                ? "Migration orchestration failed."
                : $"Migration '{name}' failed and was rolled back.");
        }
    }

    private sealed class FixedScriptProvider(IEnumerable<SqlScript> scripts) : IScriptProvider
    {
        public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager) => scripts;
    }

    private sealed class NullUpgradeLog : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }
        public void LogDebug(string format, params object[] args) { }
        public void LogInformation(string format, params object[] args) { }
        public void LogWarning(string format, params object[] args) { }
        public void LogError(string format, params object[] args) { }
        public void LogError(Exception ex, string format, params object[] args) { }
    }
}
